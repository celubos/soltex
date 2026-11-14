param([string]$Destination = (Join-Path $PSScriptRoot '..\..\artifacts\native\driver-kit'))
# Downloads the WDK/SDK NuGet packages pinned in toolchain.lock.json, checks their size and SHA-256,
# and expands them for build.ps1 (whose -KitRoot has the same default).
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Destination = [IO.Path]::GetFullPath($Destination)
$ancestor = $Destination
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Use a direct path, not a junction or symbolic link: $ancestor" }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Force $Destination | Out-Null
$manifest = Get-Content -LiteralPath "$PSScriptRoot\toolchain.lock.json" -Raw | ConvertFrom-Json
foreach ($package in $manifest.packages) {
    $archive = Join-Path $Destination $package.file
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $package.url -OutFile $archive }
    $bytes = (Get-Item -LiteralPath $archive).Length
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($bytes -ne $package.bytes -or $hash -ne $package.sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "Package size or hash mismatch (removed, run again to download it again): $($package.file)"
    }
    $name = $package.file -replace '\.10\.0\.28000\.2526\.nupkg$', ''
    if ($name -eq 'microsoft.windows.wdk.x64') { $name = 'wdk' }
    $expanded = Join-Path $Destination $name
    if (-not (Test-Path -LiteralPath $expanded)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $expanded) }
    Write-Output "Verified pinned build package: $($package.file)"
}
