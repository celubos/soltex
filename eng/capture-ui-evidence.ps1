[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\visual',
    [string]$ValidationDirectory = 'artifacts\validation',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$SourceHeadSha,
    [string]$TestedCommitSha,
    [string]$DotnetPath,
    # A render that has not exited by then is stopped and reported, so a stuck render cannot hang CI.
    [ValidateRange(30, 1800)]
    [int]$RenderTimeoutSeconds = 180
)

# Renders every page of the built app in the light and dark themes through its --render-smoke switch,
# plus a few pages in a narrow window at 150 percent text, where the navigation pane is an icon rail.
# Checks each PNG and writes render-matrix.json with the file hashes. Requires a prior build of
# src\Soltex.App in the chosen configuration.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Assert-CommitSha {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    if ($Value -notmatch '^[0-9a-fA-F]{40}$') {
        throw "$Name must be a complete 40-character Git commit SHA; received '$Value'."
    }
}

# Start-Process joins arguments with spaces, so quote any that contain one.
function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -match '\s') {
        return '"' + $Value + '"'
    }
    return $Value
}

$checkedOutCommit = & git -C $repoRoot rev-parse HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the checked-out Git commit.'
}
$checkedOutCommit = $checkedOutCommit.Trim()

if ([string]::IsNullOrWhiteSpace($TestedCommitSha)) {
    $TestedCommitSha = $checkedOutCommit
}
if ([string]::IsNullOrWhiteSpace($SourceHeadSha)) {
    $SourceHeadSha = $TestedCommitSha
}

Assert-CommitSha -Name 'TestedCommitSha' -Value $TestedCommitSha
Assert-CommitSha -Name 'SourceHeadSha' -Value $SourceHeadSha

$checkedOutCommit = $checkedOutCommit.ToLowerInvariant()
$TestedCommitSha = $TestedCommitSha.ToLowerInvariant()
$SourceHeadSha = $SourceHeadSha.ToLowerInvariant()

if ($checkedOutCommit -ne $TestedCommitSha) {
    throw "The checked-out commit '$checkedOutCommit' does not match TestedCommitSha '$TestedCommitSha'."
}

if ($SourceHeadSha -ne $TestedCommitSha) {
    & git -C $repoRoot merge-base --is-ancestor $SourceHeadSha $TestedCommitSha
    if ($LASTEXITCODE -ne 0) {
        throw "SourceHeadSha '$SourceHeadSha' is not available as an ancestor of TestedCommitSha '$TestedCommitSha'."
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $repoDotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $repoDotnet -PathType Leaf) {
        $DotnetPath = $repoDotnet
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}

$resolvedDotnet = (Resolve-Path -LiteralPath $DotnetPath -ErrorAction Stop).Path
$outputRoot = Resolve-RepositoryPath -Path $OutputDirectory
$validationRoot = Resolve-RepositoryPath -Path $ValidationDirectory
$manifestPath = Join-Path $validationRoot 'render-matrix.json'
$projectPath = Join-Path $repoRoot 'src\Soltex.App\Soltex.App.csproj'

# Must match SuiteLaunchOptions.PanelNames in src\Soltex.App\SuiteLaunchOptions.cs.
$panels = @(
    'my-pc', 'customize', 'history', 'audio', 'audio-mic', 'audio-apps', 'audio-sonar', 'tools', 'settings',
    'search', 'taskbar', 'start', 'explorer', 'appearance', 'multitasking', 'desktop'
)
$themes = @('light', 'dark')
# Canonical capture size (RenderSmokeCapture in src\Soltex.App).
$canonicalWidth = 1280
$canonicalHeight = 820
# 900 px at 150 percent leaves a 600 px wide layout, so the navigation pane shows icons only.
$narrowPanels = @('my-pc', 'taskbar', 'audio-mic')
$narrowWidth = 900
$narrowHeight = 700
$narrowScale = 150

$matrix = @(
    foreach ($panel in $panels) {
        foreach ($theme in $themes) {
            [ordered]@{ id = "$panel-$theme"; panel = $panel; theme = $theme; file = "$panel-$theme.png"; width = $canonicalWidth; height = $canonicalHeight; scale = $null }
        }
    }
    foreach ($panel in $narrowPanels) {
        foreach ($theme in $themes) {
            [ordered]@{ id = "$panel-$theme-narrow"; panel = $panel; theme = $theme; file = "$panel-$theme-narrow.png"; width = $narrowWidth; height = $narrowHeight; scale = $narrowScale }
        }
    }
)

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $validationRoot | Out-Null

$reservedPaths = @($manifestPath)
foreach ($entry in $matrix) {
    $renderPath = Join-Path $outputRoot $entry.file
    $logPath = Join-Path $validationRoot ("render-{0}.log" -f $entry.id)
    $reservedPaths += $renderPath
    $reservedPaths += ($renderPath + '.error.txt')
    $reservedPaths += $logPath
    $reservedPaths += ($logPath + '.stderr')
}

$stalePaths = @($reservedPaths | Where-Object { Test-Path -LiteralPath $_ })
if ($stalePaths.Count -gt 0) {
    throw "Refusing to overwrite existing renders. Choose fresh output directories or remove these files first:`n$($stalePaths -join "`n")"
}

$artifacts = @()
Add-Type -AssemblyName System.Drawing

foreach ($entry in $matrix) {
    $renderPath = Join-Path $outputRoot $entry.file
    $errorPath = $renderPath + '.error.txt'
    $logPath = Join-Path $validationRoot ("render-{0}.log" -f $entry.id)
    $stderrPath = $logPath + '.stderr'

    Write-Host "Rendering page '$($entry.panel)' in the $($entry.theme) theme at $($entry.width)x$($entry.height)..."
    $arguments = @(
        'run', '--project', (ConvertTo-ProcessArgument $projectPath), '--configuration', $Configuration, '--no-build', '--',
        '--render-smoke', (ConvertTo-ProcessArgument $renderPath), '--panel', $entry.panel, '--theme', $entry.theme
    )
    if ($entry.width -ne $canonicalWidth -or $entry.height -ne $canonicalHeight) {
        $arguments += @('--size', ('{0}x{1}' -f $entry.width, $entry.height))
    }
    if ($null -ne $entry.scale) {
        $arguments += @('--scale', [string]$entry.scale)
    }
    $process = Start-Process -FilePath $resolvedDotnet -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $logPath -RedirectStandardError $stderrPath
    # Reading Handle keeps the exit code available after the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($RenderTimeoutSeconds * 1000)) {
        # dotnet run starts the app as a child process; stop the whole tree.
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw "Render '$($entry.id)' did not finish within $RenderTimeoutSeconds seconds. See '$logPath'."
    }
    $process.WaitForExit()
    $renderExitCode = $process.ExitCode
    foreach ($output in @($logPath, $stderrPath)) {
        if (Test-Path -LiteralPath $output -PathType Leaf) {
            Get-Content -LiteralPath $output | Write-Host
        }
    }

    if ($renderExitCode -ne 0) {
        if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
            Get-Content -LiteralPath $errorPath | Write-Host
        }
        throw "Render '$($entry.id)' failed with exit code $renderExitCode. See '$logPath'."
    }
    if (Test-Path -LiteralPath $errorPath -PathType Leaf) {
        Write-Host "Render error file: $errorPath"
        Get-Content -LiteralPath $errorPath | Write-Host
        throw "Render '$($entry.id)' produced an error file."
    }
    if (-not (Test-Path -LiteralPath $renderPath -PathType Leaf)) {
        throw "Render '$($entry.id)' did not create '$renderPath'."
    }

    $file = Get-Item -LiteralPath $renderPath
    if ($file.Length -le 0) {
        throw "Render '$($entry.id)' produced an empty PNG."
    }

    $image = [System.Drawing.Image]::FromFile($renderPath)
    try {
        if ($image.Width -ne $entry.width -or $image.Height -ne $entry.height) {
            throw "Render '$($entry.id)' is $($image.Width)x$($image.Height); expected $($entry.width)x$($entry.height)."
        }
    }
    finally {
        $image.Dispose()
    }

    $hash = Get-FileHash -LiteralPath $renderPath -Algorithm SHA256
    $artifacts += [ordered]@{
        id = $entry.id
        panel = $entry.panel
        theme = $entry.theme
        file = $entry.file
        width = $entry.width
        height = $entry.height
        scale_percent = $entry.scale
        bytes = $file.Length
        sha256 = $hash.Hash.ToLowerInvariant()
    }
}

[ordered]@{
    schema_version = 3
    source_head_sha = $SourceHeadSha
    tested_commit_sha = $TestedCommitSha
    captured_at_utc = (Get-Date).ToUniversalTime().ToString('o')
    configuration = $Configuration
    runtime = [ordered]@{
        framework = 'net10.0-windows10.0.19041.0'
        renderer = 'native WPF'
        process_render_mode = 'SoftwareOnly'
    }
    viewport = [ordered]@{
        width = $canonicalWidth
        height = $canonicalHeight
    }
    narrow_viewport = [ordered]@{
        width = $narrowWidth
        height = $narrowHeight
        scale_percent = $narrowScale
    }
    artifacts = $artifacts
} | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -LiteralPath $manifestPath

Write-Host "Rendered and checked $($artifacts.Count) pages ($($panels.Count) pages x $($themes.Count) themes, plus $($narrowPanels.Count) narrow pages x $($themes.Count) themes)."
Write-Host "Manifest: $manifestPath"
