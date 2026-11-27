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

function Find-Iscc {
    foreach ($root in (@(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Select-Object -Unique)) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $info = (Get-Item -LiteralPath $candidate).VersionInfo
            [pscustomobject]@{
                Path    = $candidate
                Version = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
            }
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
