<#
.SYNOPSIS
Makes sure the pinned Inno Setup version is installed and returns the path of its ISCC.exe.

.DESCRIPTION
The release and package-smoke workflows build the installer with exactly this Inno Setup version. When ISCC.exe of
that version is not already under Program Files or Program Files (x86), the script installs it with Chocolatey
(`choco install innosetup --version <pinned>`); that package version checks the Inno Setup download against the
SHA-256 recorded in the package. Afterwards the script fails unless an ISCC.exe with the pinned file version is found.

With -GitHubEnv it also writes ISCC=<path> to the file named by GITHUB_ENV, for later workflow steps.

.EXAMPLE
pwsh eng/installer/get-inno-setup.ps1 -GitHubEnv
#>
[CmdletBinding()]
param(
    # Chocolatey package version, which is also the ISCC.exe file version it installs.
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '6.7.1',
    [switch] $GitHubEnv
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ISCC.exe itself carries no version resource, so the version is read from the Inno Setup files that do
# (the compiler DLL or the IDE), falling back to the version Inno Setup's own installer recorded.
function Get-InnoVersion([string] $directory) {
    foreach ($name in 'ISCmplr.dll', 'Compil32.exe', 'ISCC.exe') {
        $file = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $info = (Get-Item -LiteralPath $file).VersionInfo
        if ($info.FileMajorPart -gt 0) { return '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart }
        if ($info.ProductVersion -match '^(\d+\.\d+\.\d+)') { return $Matches[1] }
    }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                     'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
                     'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1') {
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($entry -and $entry.PSObject.Properties['DisplayVersion'] -and $entry.DisplayVersion -match '(\d+\.\d+\.\d+)') { return $Matches[1] }
    }
    # Chocolatey records the package version it installed (GitHub's Windows images install Inno Setup this way).
    $choco = Get-Command choco -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($choco) {
        $line = & $choco.Source list innosetup --exact --limit-output 2>$null | Where-Object { $_ -match '^innosetup\|' } | Select-Object -First 1
        if ($line -match '^innosetup\|(\d+\.\d+\.\d+)') { return $Matches[1] }
    }
    # Last resort: the compiler's own banner.
    $iscc = Join-Path $directory 'ISCC.exe'
    if (Test-Path -LiteralPath $iscc -PathType Leaf) {
        $banner = (& $iscc '/?' 2>&1 | Select-Object -First 5) -join ' '
        if ($banner -match '(\d+\.\d+\.\d+)') { return $Matches[1] }
    }
    return '0.0.0'
}

function Find-Iscc {
    foreach ($root in (@(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Select-Object -Unique)) {
        if (-not $root) { continue }
        $directory = Join-Path $root 'Inno Setup 6'
        $candidate = Join-Path $directory 'ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            [pscustomobject]@{ Path = $candidate; Version = Get-InnoVersion $directory }
        }
    }
}

function Select-Pinned {
    @(Find-Iscc | Where-Object Version -eq $Version) | Select-Object -First 1
}

$iscc = Select-Pinned
if (-not $iscc) {
    $choco = Get-Command choco -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $choco) {
        throw "Inno Setup $Version is not installed and Chocolatey is not available to install it."
    }
    # --allow-downgrade: a runner image may already carry a newer Inno Setup.
    & $choco.Source install innosetup --version $Version --allow-downgrade --no-progress -y
    if ($LASTEXITCODE -ne 0) { throw "Installing Inno Setup $Version failed (exit code $LASTEXITCODE)." }
    $iscc = Select-Pinned
}
if (-not $iscc) {
    $all = @(Find-Iscc)
    # Some Inno Setup builds carry no readable version anywhere; Chocolatey installed the pinned version above, so an
    # ISCC.exe whose version cannot be read is accepted with a warning. A different, readable version still fails.
    $unknown = @($all | Where-Object Version -eq '0.0.0') | Select-Object -First 1
    if ($unknown) {
        Write-Warning "The Inno Setup version could not be read; using $($unknown.Path) installed as Chocolatey package $Version."
        $chocoList = Get-Command choco -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($chocoList) { & $chocoList.Source list innosetup --exact 2>&1 | ForEach-Object { Write-Host "  choco: $_" } }
        $iscc = $unknown
    }
}
if (-not $iscc) {
    $found = @(Find-Iscc | ForEach-Object { "$($_.Path) ($($_.Version))" })
    $list = if ($found.Count -gt 0) { $found -join ', ' } else { 'none' }
    throw "ISCC.exe $Version was not found under Program Files or Program Files (x86). Found: $list."
}

Write-Host "Inno Setup $($iscc.Version): $($iscc.Path)"
if ($GitHubEnv) {
    if (-not $env:GITHUB_ENV) { throw '-GitHubEnv needs the GITHUB_ENV environment variable.' }
    "ISCC=$($iscc.Path)" | Add-Content -Encoding utf8 -LiteralPath $env:GITHUB_ENV
}
Write-Output $iscc.Path
