[CmdletBinding()]
param(
    [string]$PackageDirectory = $PSScriptRoot,
    [switch]$Desktop,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$executable = Join-Path $package 'Soltex.exe'
$identityPath = Join-Path $package 'BUILD.json'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or -not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
    throw 'Choose the complete extracted Soltex Windows package, not its source folder.'
}
$identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
if ($identity.ApplicationExecutable -ne 'Soltex.exe' -or $identity.ApplicationMode -ne 'Integrated' -or -not $identity.SelfContained) {
    throw 'This shortcut installer requires the integrated self-contained Soltex package.'
}
# Per-user launch entries only. No elevation, driver, startup or default audio changes.
$targets = @([Environment]::GetFolderPath('Programs'))
if ($Desktop) { $targets += [Environment]::GetFolderPath('DesktopDirectory') }
$shellObject = New-Object -ComObject WScript.Shell
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "Shortcut folder unavailable: $target" }
    $path = Join-Path $target 'Soltex Audio.lnk'
    $shortcut = $shellObject.CreateShortcut($path)
    if ((Test-Path -LiteralPath $path) -and $shortcut.Description -ne 'Soltex integrated audio mixer') {
        throw "Existing shortcut has different ownership and was preserved: $path"
    }
    $shortcut.TargetPath = $executable
    $shortcut.Arguments = '--mixer'
    $shortcut.WorkingDirectory = $package
    $shortcut.IconLocation = $executable + ',0'
    $shortcut.Description = 'Soltex integrated audio mixer'
    $shortcut.Save()
    $readback = $shellObject.CreateShortcut($path)
    if ($readback.TargetPath -ne $executable -or $readback.Arguments -ne '--mixer') { throw "Shortcut verification failed: $path" }
    Write-Output "Ready: $path"
}
if ($Launch) { Start-Process -FilePath $executable -ArgumentList '--mixer' -WorkingDirectory $package | Out-Null }
