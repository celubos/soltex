<#
.SYNOPSIS
Installs, upgrades, starts and uninstalls a Soltex setup.exe and checks each step. Meant for CI runners.

.DESCRIPTION
This changes the current Windows account: it installs Soltex for it, starts and quits it, and uninstalls it, so it
refuses to run outside GitHub Actions unless -AllowOnThisPc is passed. The account must not have Soltex installed.

Checks, in order:
  1. A first install with the sign-in task adds the files, the Start menu entry, the Installed apps entry and the
     sign-in (Run) value.
  2. Soltex.exe --quit returns 1 when Soltex is not running, and the installed app renders a page.
  3. Running setup again while Soltex runs (an upgrade) quits it first, and does not bring back a sign-in value
     that was removed in the meantime (as the app's own switch does).
  4. Soltex.exe --quit returns 0 only once the running copy has exited.
  5. Uninstalling while Soltex runs quits it first, then removes the files, the shortcut, the Installed apps entry
     and the sign-in value, and leaves no Soltex process behind.

.EXAMPLE
pwsh eng/installer/test-installer.ps1 -Setup artifacts\installer\Soltex-1.2.0-win-x64-setup.exe -Version 1.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Setup,
    # When given, the Installed apps entry must show this version.
    [string] $Version,
    # Scratch folder for the page render and its profile. Defaults to RUNNER_TEMP, then the user's temp folder.
    [string] $WorkDirectory,
    [switch] $AllowOnThisPc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowOnThisPc) {
    throw 'This installs and uninstalls Soltex for the current Windows account. It is meant for CI; pass -AllowOnThisPc to run it anyway.'
}
if (-not $WorkDirectory) { $WorkDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() } }

$setupPath = (Resolve-Path -LiteralPath $Setup).ProviderPath
$dir = Join-Path $env:LOCALAPPDATA 'Programs\Soltex'
$exe = Join-Path $dir 'Soltex.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Soltex.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E1F0C6A-5B3D-4A77-9F2E-1C4D6B8A0E52}_is1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$expectedRun = '"' + $exe + '" --tray'
# Exit codes of Soltex.exe --quit (src/Soltex.App/App.Instance.cs).
$quitExited = 0
$quitNotRunning = 1
$silent = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')

if (Test-Path -LiteralPath $uninstallKey) { throw 'Soltex is already installed for this account; these checks need an account without it.' }

# Start-Process joins arguments with spaces, so quote any that contain one.
function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory = $true)][string] $Value)
    if ($Value -match '\s') { return '"' + $Value + '"' }
    return $Value
}

function Invoke-Program {
    param([string] $FilePath, [string[]] $Arguments, [int] $TimeoutSeconds = 180)
    $quoted = @($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ })
    $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -PassThru
    # Reading Handle keeps the exit code available after the process ends.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        throw "$(Split-Path -Leaf $FilePath) $($Arguments -join ' ') did not finish within $TimeoutSeconds seconds."
    }
    $process.WaitForExit()
    return $process.ExitCode
}

function Test-SoltexListening {
    try { return @([IO.Directory]::GetFiles('\\.\pipe\') | Where-Object { $_ -like '*\Soltex.Suite.*' }).Count -gt 0 }
    catch { return $false }
}

# Starts the installed app in the notification area, as sign-in start does, and waits until it takes commands.
function Start-Soltex {
    $process = Start-Process -FilePath $exe -ArgumentList '--tray' -PassThru
    $null = $process.Handle
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-SoltexListening)) {
        if ($process.HasExited) { throw "Soltex.exe --tray ended at once (exit code $($process.ExitCode))." }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Soltex.exe --tray did not start listening within 60 seconds.' }
        Start-Sleep -Milliseconds 250
    }
    return $process
}

function Get-InstalledProcess {
    @(Get-Process -Name 'Soltex', 'Soltex.Audio.Host.Service' -ErrorAction SilentlyContinue | Where-Object {
        $path = $null
        try { $path = $_.Path } catch { $path = $null }
        $path -and $path.StartsWith($dir + '\', [StringComparison]::OrdinalIgnoreCase)
    })
}

function Get-RunValue {
    $item = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($null -eq $item -or $item.PSObject.Properties.Name -notcontains 'Soltex') { return $null }
    return $item.Soltex
}

function Assert-Installed {
    foreach ($file in 'Soltex.exe', 'LICENSE.txt', 'README.txt', 'TaskbarHelper\x64\SoltexTaskbar.dll', 'Soltex.Audio.Host\Soltex.Audio.Host.Service.exe') {
        if (-not (Test-Path -LiteralPath (Join-Path $dir $file) -PathType Leaf)) { throw "Not installed: $file" }
    }
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'The Start menu shortcut is missing.' }
    $entry = Get-ItemProperty -LiteralPath $uninstallKey
    if ($entry.DisplayName -ne 'Soltex') { throw 'Soltex is not listed in Installed apps.' }
    if ($Version -and $entry.DisplayVersion -ne $Version) { throw "Installed apps shows version '$($entry.DisplayVersion)', expected '$Version'." }
}

Write-Host '1. First install with the sign-in task'
$code = Invoke-Program $setupPath ($silent + '/TASKS=startup') 300
if ($code -ne 0) { throw "Setup exited with $code." }
Assert-Installed
if ((Get-RunValue) -ne $expectedRun) { throw 'The sign-in entry is missing or wrong after the first install.' }

Write-Host '2. --quit with nothing running, and a page render'
$code = Invoke-Program $exe @('--quit') 60
if ($code -ne $quitNotRunning) { throw "Soltex.exe --quit returned $code with no copy running; expected $quitNotRunning." }
$png = Join-Path $WorkDirectory 'installed-home.png'
$code = Invoke-Program $exe @('--render-smoke', $png, '--panel', 'my-pc', '--profile-dir', (Join-Path $WorkDirectory 'installed-profile')) 180
if ($code -ne 0 -or -not (Test-Path -LiteralPath $png -PathType Leaf)) { throw "The installed app did not render a page (exit code $code)." }

Write-Host '3. Upgrade while Soltex runs; sign-in start stays as the app left it'
# The app's switch removes this value when the user turns sign-in start off.
Remove-ItemProperty -LiteralPath $runKey -Name Soltex
$app = Start-Soltex
$code = Invoke-Program $setupPath $silent 300
if ($code -ne 0) { throw "The upgrade exited with $code (7 means Soltex could not be made to quit)." }
if (-not $app.WaitForExit(5000)) { throw 'The upgrade finished while the old Soltex was still running.' }
Assert-Installed
if ($null -ne (Get-RunValue)) { throw 'The upgrade turned sign-in start back on.' }

Write-Host '4. --quit with Soltex running'
$app = Start-Soltex
$code = Invoke-Program $exe @('--quit') 60
if ($code -ne $quitExited) { throw "Soltex.exe --quit returned $code with a copy running; expected $quitExited." }
# --quit returns only after the process has ended; allow a moment for this side to notice.
if (-not $app.WaitForExit(2000)) { throw 'Soltex.exe --quit returned before the running copy had exited.' }

Write-Host '5. Uninstall while Soltex runs'
# As the app's switch writes it when the user turns sign-in start on again.
Set-ItemProperty -LiteralPath $runKey -Name Soltex -Value $expectedRun
$app = Start-Soltex
$uninstaller = (Get-ItemProperty -LiteralPath $uninstallKey).UninstallString.Trim('"')
$code = Invoke-Program $uninstaller $silent 300
if ($code -ne 0) { throw "Uninstall exited with $code." }
# The uninstaller finishes from a copy in the temp folder, so wait for its results.
for ($i = 0; $i -lt 60 -and ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $uninstallKey)); $i++) { Start-Sleep -Seconds 1 }
if (-not $app.HasExited) { throw 'Uninstall left the running Soltex running.' }
$left = @(Get-InstalledProcess)
if ($left.Count -gt 0) { throw "Uninstall left Soltex processes running: $(($left | ForEach-Object { $_.ProcessName }) -join ', ')." }
if (Test-Path -LiteralPath $exe) { throw 'Uninstall left the program files.' }
if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left the Start menu shortcut.' }
if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall left the Installed apps entry.' }
if ($null -ne (Get-RunValue)) { throw 'Uninstall left the sign-in entry.' }

Write-Host 'Install, --quit, upgrade while running and uninstall while running all worked.'
