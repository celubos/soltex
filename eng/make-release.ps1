<#
.SYNOPSIS
Builds the GitHub release zip from an integrated package.

.DESCRIPTION
Takes a package folder made by
    eng/build-audio.ps1 -Integrated -ArtifactRoot <folder outside the checkout>
and writes, to -OutputDirectory:
    Soltex-<version>-win-x64.zip   one top-level folder, Soltex-<version>-win-x64, holding the app
                                   (Soltex.exe, its DLLs, the .NET runtime and language folders),
                                   soltex-rnnoise.dll, TaskbarHelper\, Soltex.Audio.Host\, licenses\,
                                   THIRD-PARTY-NOTICES.txt, LICENSE.txt, README.txt
                                   (eng/package/README.txt), BUILD.json and SHA256.json
    SHA256SUMS.txt                 "<sha256>  <zip name>"

The developer tools (Soltex.Audio.Engine\, Soltex.Audio.Tool\, Soltex.Audio.Broker\),
OwnedAudioDeployment\, VERIFICATION.json, Install-Shortcuts.ps1 and the .md guides stay out.
Every shipped file is checked against the package's SHA256.json first; the zip carries a new
SHA256.json that lists exactly the shipped files. Existing outputs are never overwritten.
Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
.\eng\make-release.ps1 -PackageDirectory <artifact root>\package-<id> -OutputDirectory <release folder>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PackageDirectory,
    [Parameter(Mandatory = $true)][string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtime = 'win-x64'
$shippedFolders = @('TaskbarHelper', 'Soltex.Audio.Host', 'licenses')
$excludedFolders = @('Soltex.Audio.Engine', 'Soltex.Audio.Tool', 'Soltex.Audio.Broker', 'OwnedAudioDeployment')
# SHA256.json is rebuilt for the shipped files; README.txt comes from eng/package.
$excludedRootFiles = @('VERIFICATION.json', 'Install-Shortcuts.ps1', 'SHA256.json', 'README.txt')
$excludedExtensions = @('.md', '.ps1')
$utf8 = New-Object System.Text.UTF8Encoding -ArgumentList $false

function Get-BuildValue {
    param([object] $Build, [string] $Name)
    $property = $Build.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# A language folder holds only satellite resource assemblies (cs\, de\, zh-Hans\ ...).
function Test-LanguageFolder {
    param([IO.DirectoryInfo] $Folder)
    if ($Folder.Name -notmatch '^[a-z]{2,3}(-[A-Za-z0-9]+)*$') { return $false }
    $content = @(Get-ChildItem -LiteralPath $Folder.FullName -Recurse -Force)
    $other = @($content | Where-Object { $_.PSIsContainer -or $_.Name -notlike '*.resources.dll' })
    return $content.Count -gt 0 -and $other.Count -eq 0
}

$package = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath.TrimEnd('\')
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory).TrimEnd('\')
if ($outputRoot.Equals($package, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($package + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside the package folder.'
}

$readmeSource = Join-Path $PSScriptRoot 'package\README.txt'
if (-not (Test-Path -LiteralPath $readmeSource -PathType Leaf)) {
    throw "The end-user readme is missing: $readmeSource"
}
foreach ($required in @(
    'Soltex.exe', 'BUILD.json', 'SHA256.json', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt',
    'TaskbarHelper\x64\SoltexTaskbar.dll', 'TaskbarHelper\arm64\SoltexTaskbar.dll',
    'Soltex.Audio.Host\Soltex.Audio.Host.Service.exe',
    'soltex-rnnoise.dll', 'Soltex.Audio.Host\soltex-rnnoise.dll', 'licenses\rnnoise\COPYING'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) {
        throw "Not a complete integrated package (missing $required). Build one with eng/build-audio.ps1 -Integrated."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $package 'licenses') -PathType Container)) {
    throw 'Not a complete integrated package (missing licenses\).'
}

# Package identity and version.
$build = Get-Content -Raw -LiteralPath (Join-Path $package 'BUILD.json') | ConvertFrom-Json
if ((Get-BuildValue $build 'ApplicationMode') -ne 'Integrated' -or (Get-BuildValue $build 'SelfContained') -ne $true) {
    throw 'The release zip needs a self-contained integrated package: eng/build-audio.ps1 -Integrated, without -FrameworkDependent.'
}
if ((Get-BuildValue $build 'Runtime') -ne $runtime) {
    throw "The package runtime is '$(Get-BuildValue $build 'Runtime')'; expected '$runtime'."
}
if ((Get-BuildValue $build 'SourceDirty') -eq $true) {
    Write-Warning 'BUILD.json says the package was built from a working tree with uncommitted changes.'
}
if ((Get-BuildValue $build 'TestsSkipped') -eq $true) {
    Write-Warning 'BUILD.json says the package was built with -SkipTests.'
}
$version = Get-BuildValue $build 'Version'
if ([string]::IsNullOrWhiteSpace($version)) {
    $props = [xml](Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'Directory.Build.props'))
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode) { throw 'Neither BUILD.json nor Directory.Build.props names a version.' }
    $version = $versionNode.InnerText
}
$version = $version.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version must look like 1.2.3 or 1.2.3-suffix; found '$version'."
}
$exeVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $package 'Soltex.exe')).ProductVersion
$exeVersion = if ($exeVersion) { ($exeVersion -split '\+')[0].Trim() } else { '' }
if ($exeVersion -ne $version) {
    throw "Soltex.exe reports version '$exeVersion', but the release version is '$version'."
}

$releaseName = "Soltex-$version-$runtime"
$zipPath = Join-Path $outputRoot "$releaseName.zip"
$sumsPath = Join-Path $outputRoot 'SHA256SUMS.txt'
foreach ($existing in @($zipPath, $sumsPath)) {
    if (Test-Path -LiteralPath $existing) {
        throw "Refusing to overwrite an existing file: $existing"
    }
}

# Choose the shipped files. Anything unexpected stops the release instead of shipping silently.
$sourceFiles = New-Object System.Collections.Generic.List[System.IO.FileInfo]
foreach ($item in @(Get-ChildItem -LiteralPath $package -Force)) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "The package contains a link: $($item.Name)"
    }
    if ($item.PSIsContainer) {
        if ($excludedFolders -contains $item.Name) { continue }
        if ($shippedFolders -notcontains $item.Name -and -not (Test-LanguageFolder $item)) {
            throw "Unexpected folder in the package: $($item.Name). List it as shipped or excluded in eng/make-release.ps1."
        }
        foreach ($child in @(Get-ChildItem -LiteralPath $item.FullName -Recurse -Force)) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "The package contains a link: $($child.FullName.Substring($package.Length + 1))"
            }
            if (-not $child.PSIsContainer -and $excludedExtensions -notcontains $child.Extension) {
                $sourceFiles.Add($child)
            }
        }
    }
    elseif ($excludedRootFiles -notcontains $item.Name -and $excludedExtensions -notcontains $item.Extension) {
        $sourceFiles.Add($item)
    }
}

# Every shipped file must still match the hash recorded when the package was built.
$recorded = @{}
$recordedEntries = Get-Content -Raw -LiteralPath (Join-Path $package 'SHA256.json') | ConvertFrom-Json
foreach ($entry in $recordedEntries) { $recorded[[string]$entry.Path] = [string]$entry.SHA256 }

$entries = New-Object System.Collections.Generic.List[object]
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($package.Length + 1)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if (-not $recorded.ContainsKey($relative)) {
        throw "The package's SHA256.json does not list $relative."
    }
    if ($recorded[$relative] -ne $hash) {
        throw "$relative changed after the package was built (SHA-256 mismatch)."
    }
    $entries.Add([pscustomobject]@{ Path = $relative; Bytes = $file.Length; SHA256 = $hash; Source = $file.FullName; Content = $null; Time = $file.LastWriteTime })
}
$readmeBytes = [IO.File]::ReadAllBytes($readmeSource)
$readmeHash = (Get-FileHash -LiteralPath $readmeSource -Algorithm SHA256).Hash
$entries.Add([pscustomobject]@{ Path = 'README.txt'; Bytes = $readmeBytes.Length; SHA256 = $readmeHash; Source = $null; Content = $readmeBytes; Time = (Get-Item -LiteralPath $readmeSource).LastWriteTime })

# SHA256.json for the shipped files, in the same format as the package's own.
$manifest = @($entries | Sort-Object Path | ForEach-Object { [pscustomobject]@{ Path = $_.Path; Bytes = $_.Bytes; SHA256 = $_.SHA256 } })
$manifestBytes = $utf8.GetBytes((ConvertTo-Json -InputObject $manifest -Depth 3))
$entries.Add([pscustomobject]@{ Path = 'SHA256.json'; Bytes = $manifestBytes.Length; SHA256 = $null; Source = $null; Content = $manifestBytes; Time = Get-Date })

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$partialPath = $zipPath + '.partial'
if (Test-Path -LiteralPath $partialPath) { Remove-Item -LiteralPath $partialPath -Force }
Write-Host "Writing $($entries.Count) files to $releaseName.zip ..."
$completed = $false
$stream = [IO.File]::Open($partialPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $zip = New-Object System.IO.Compression.ZipArchive -ArgumentList $stream, ([IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in @($entries | Sort-Object Path)) {
            $name = $releaseName + '/' + $entry.Path.Replace('\', '/')
            $zipEntry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $zipEntry.LastWriteTime = [DateTimeOffset]$entry.Time
            $target = $zipEntry.Open()
            try {
                if ($null -ne $entry.Content) {
                    $target.Write($entry.Content, 0, $entry.Content.Length)
                }
                else {
                    $source = [IO.File]::OpenRead($entry.Source)
                    try { $source.CopyTo($target) } finally { $source.Dispose() }
                }
            }
            finally { $target.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $completed = $true
}
finally {
    $stream.Dispose()
    if (-not $completed -and (Test-Path -LiteralPath $partialPath)) { Remove-Item -LiteralPath $partialPath -Force }
}
Move-Item -LiteralPath $partialPath -Destination $zipPath

$zipItem = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
# LF line ending so `sha256sum -c` also works.
[IO.File]::WriteAllText($sumsPath, "$zipHash  $($zipItem.Name)`n", $utf8)

$unpacked = ($entries | Measure-Object -Property Bytes -Sum).Sum
Write-Host ''
Write-Host "Release zip : $zipPath"
Write-Host "  size      : $($zipItem.Length) bytes ($([math]::Round($zipItem.Length / 1MB, 1)) MB; $([math]::Round($unpacked / 1MB, 1)) MB unpacked, $($entries.Count) files)"
Write-Host "  sha256    : $zipHash"
Write-Host "Checksums   : $sumsPath"
