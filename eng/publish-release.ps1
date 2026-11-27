# Publishes the self-contained Soltex desktop app into artifacts/publish/<runtime> and compiles the
# per-user installer from that folder. The folder has the same layout as the release zip that
# eng/make-release.ps1 builds from an integrated package:
#   Soltex.exe, its DLLs, the .NET runtime and the language folders
#   TaskbarHelper\{x64,arm64}\SoltexTaskbar.dll   native taskbar helper (native/Soltex.TaskbarStyle)
#   soltex-rnnoise.dll                            RNNoise 0.2 next to each copy of the audio engine, here and in
#                                                 Soltex.Audio.Host\ (native/Soltex.Rnnoise)
#   Soltex.Audio.Host\                            audio helper that the Audio page starts
#   licenses\, THIRD-PARTY-NOTICES.txt, LICENSE.txt, README.txt
# The installer is artifacts/installer/Soltex-<version>-<runtime>-setup.exe with a SHA-256 record.
#
# The native parts need the pinned LLVM-MinGW toolchain; pass -Acquire to download and verify it.

[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props.
    [string] $Version,
    # The installer and the native helper's host build are x64 only.
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [switch] $SkipInstaller,
    # ISCC.exe to compile the installer with (CI passes the pinned one from eng/installer/get-inno-setup.ps1).
    # Without it the script looks in the usual Inno Setup 6 folders and on PATH.
    [string] $Iscc,
    # Download the pinned LLVM-MinGW toolchain for the native parts (taskbar helper, RNNoise) when it is missing.
    [switch] $Acquire,
    # Where the native build keeps that toolchain (passed to its -CacheRoot).
    [string] $ToolchainCacheRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime"
$installerDir = Join-Path $repoRoot 'artifacts\installer'
$appProject = Join-Path $repoRoot 'src\Soltex.App\Soltex.App.csproj'
$hostProject = Join-Path $repoRoot 'src\Soltex.Audio.Host.Cli\Soltex.Audio.Host.Cli.csproj'
$packageReadme = Join-Path $PSScriptRoot 'package\README.txt'

function Resolve-Tool {
    param([string[]] $Candidates, [string] $Name)

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

if (-not $Version) {
    $props = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw 'Directory.Build.props has no <Version>; pass -Version.'
    }
    $Version = $versionNode.InnerText.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') {
    throw "Version must look like 1.2.3 or 1.2.3-suffix; received '$Version'."
}
# Windows file version resources need a purely numeric version.
$numericVersion = ($Version -split '[-+]')[0]

if (-not (Test-Path -LiteralPath $packageReadme -PathType Leaf)) {
    throw "The end-user readme is missing: $packageReadme"
}

# A repo-local SDK, then DOTNET_ROOT, take precedence so the build does not
# depend on whichever runtime happens to be first on PATH. global.json pins the
# feature band, so an older host on PATH fails the restore rather than silently
# producing a different build.
$dotnet = Resolve-Tool -Name 'dotnet' -Candidates @(
    (Join-Path $repoRoot '.dotnet\dotnet.exe'),
    $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' } else { $null })
)
if (-not $dotnet) {
    throw 'Unable to locate a dotnet host. Install the .NET 10 SDK or place one in .dotnet\.'
}

Write-Host "Using dotnet: $dotnet"
& $dotnet --version | Write-Host
Write-Host "Version: $Version"

& (Join-Path $PSScriptRoot 'make-icon.ps1') | Write-Host

# The helper must exist before the app is published: Soltex.App.csproj includes it only when built.
$nativeArguments = @{}
if ($Acquire) { $nativeArguments.Acquire = $true }
if ($ToolchainCacheRoot) { $nativeArguments.CacheRoot = $ToolchainCacheRoot }
& (Join-Path $repoRoot 'native\Soltex.TaskbarStyle\build.ps1') @nativeArguments | Write-Host
# Likewise RNNoise: the engine project copies it next to Soltex.Audio.Engine.Core.dll only when it is built.
& (Join-Path $repoRoot 'native\Soltex.Rnnoise\build.ps1') @nativeArguments | Write-Host

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

function Invoke-Publish {
    param([string] $Project, [string] $Output)

    & $dotnet publish $Project `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        "-p:Version=$Version" `
        -p:DebugType=none `
        --output $Output
    if ($LASTEXITCODE -ne 0) {
        throw "Publish of $(Split-Path -Leaf $Project) failed with exit code $LASTEXITCODE."
    }
}

Invoke-Publish -Project $appProject -Output $publishDir
# Read the app's restore result now, before another restore can replace it.
$assetsPath = Join-Path $repoRoot 'src\Soltex.App\obj\project.assets.json'
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
Invoke-Publish -Project $hostProject -Output (Join-Path $publishDir 'Soltex.Audio.Host')

foreach ($required in @(
    'Soltex.exe',
    'THIRD-PARTY-NOTICES.txt',
    'TaskbarHelper\x64\SoltexTaskbar.dll',
    'TaskbarHelper\arm64\SoltexTaskbar.dll',
    'Soltex.Audio.Host\Soltex.Audio.Host.Service.exe',
    'soltex-rnnoise.dll',
    'Soltex.Audio.Host\soltex-rnnoise.dll'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required) -PathType Leaf)) {
        throw "The published app is incomplete; missing: $required"
    }
}

# Notices for the bundled .NET runtimes and for the runtime pieces linked into the taskbar helper.
$packageFolder = @($assets.packageFolders.PSObject.Properties.Name)[0]
$downloads = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies })
foreach ($runtimePackage in @("Microsoft.NETCore.App.Runtime.$Runtime", "Microsoft.WindowsDesktop.App.Runtime.$Runtime")) {
    $dependency = @($downloads | Where-Object name -eq $runtimePackage)[0]
    if ($null -eq $dependency -or $dependency.version -notmatch '^\[([^,]+),\s*\1\]$') {
        throw "Cannot resolve the license notices for $runtimePackage."
    }
    $runtimeVersion = $Matches[1]
    $runtimeFolder = Join-Path $packageFolder ($runtimePackage.ToLowerInvariant() + '\' + $runtimeVersion)
    $notices = @(Get-ChildItem -LiteralPath $runtimeFolder -File | Where-Object Name -in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'))
    if ($notices.Count -eq 0) {
        throw "Runtime license is missing: $runtimePackage."
    }
    $licenseFolder = Join-Path $publishDir "licenses\$runtimePackage\$runtimeVersion"
    New-Item -ItemType Directory -Path $licenseFolder -Force | Out-Null
    foreach ($notice in $notices) {
        Copy-Item -LiteralPath $notice.FullName -Destination $licenseFolder
    }
}
$helperLicenses = Join-Path $publishDir 'licenses\llvm-mingw-runtime'
New-Item -ItemType Directory -Path $helperLicenses -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot 'artifacts\native\taskbar\licenses\*') -Destination $helperLicenses
if (@(Get-ChildItem -LiteralPath $helperLicenses -File).Count -eq 0) {
    throw 'The taskbar helper license notices are missing.'
}
# RNNoise's BSD licence and authors (licenses\rnnoise); its runtime notices are the same files as the helper's.
foreach ($folder in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts\native\rnnoise\licenses') -Directory)) {
    $target = Join-Path $publishDir "licenses\$($folder.Name)"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $folder.FullName '*') -Destination $target -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'licenses\rnnoise\COPYING') -PathType Leaf)) {
    throw 'The RNNoise license notice is missing.'
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Destination (Join-Path $publishDir 'LICENSE.txt')
Copy-Item -LiteralPath $packageReadme -Destination (Join-Path $publishDir 'README.txt')

$publishedExe = Join-Path $publishDir 'Soltex.exe'
$publishedFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse)
$publishedBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
Write-Host "Published: $publishDir"
Write-Host "  files : $($publishedFiles.Count) ($publishedBytes bytes)"
Write-Host "  Soltex.exe sha256: $((Get-FileHash -LiteralPath $publishedExe -Algorithm SHA256).Hash)"

if ($SkipInstaller) {
    return
}

if ($Iscc) {
    if (-not (Test-Path -LiteralPath $Iscc -PathType Leaf)) { throw "ISCC.exe not found: $Iscc" }
    $isccPath = $Iscc
}
else {
    $isccPath = Resolve-Tool -Name 'ISCC' -Candidates @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
}
if (-not $isccPath) {
    throw 'Unable to locate ISCC.exe. Install Inno Setup 6, pass -Iscc <path>, or pass -SkipInstaller.'
}

Write-Host "Using Inno Setup: $isccPath"

$setup = Join-Path $installerDir "Soltex-$Version-$Runtime-setup.exe"

& $isccPath `
    "/DPublishDir=$publishDir" `
    "/DAppVersion=$Version" `
    "/DNumericVersion=$numericVersion" `
    "/DOutputDir=$installerDir" `
    (Join-Path $PSScriptRoot 'soltex.iss')
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $setup)) {
    throw "Expected installer was not produced: $setup"
}

$setupItem = Get-Item -LiteralPath $setup
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()

$checksumPath = "$setup.sha256"
"$setupHash  $($setupItem.Name)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host ''
Write-Host "Installer: $setup"
Write-Host "  bytes : $($setupItem.Length)"
Write-Host "  sha256: $setupHash"
Write-Host "  digest: $checksumPath"
Write-Host ''
Write-Host 'This installer is not code-signed. SmartScreen will warn on first run.'
