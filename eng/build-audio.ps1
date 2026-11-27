[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ArtifactRoot,
    [string] $Dotnet = 'dotnet',
    [switch] $Integrated,
    [switch] $SkipTests,
    [switch] $FrameworkDependent,
    # Download the pinned LLVM-MinGW toolchain for the native parts (taskbar helper, RNNoise) when it is missing.
    [switch] $Acquire,
    # Where the native build keeps that toolchain (passed to its -CacheRoot).
    [string] $ToolchainCacheRoot
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path -Parent $PSScriptRoot
# Artifacts go to a local fixed drive, outside the checkout, never through a network path or a link.
if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot)) { throw 'ArtifactRoot must be an absolute path.' }
$artifactPath = [IO.Path]::GetFullPath($ArtifactRoot)
$artifactDrive = [IO.Path]::GetPathRoot($artifactPath)
if ($artifactDrive.StartsWith('\\') -or [IO.DriveInfo]::new($artifactDrive).DriveType -ne [IO.DriveType]::Fixed) {
    throw 'ArtifactRoot must be on a local fixed drive, not a network share (UNC path) or a removable drive.'
}
if ($artifactPath.TrimEnd('\').Equals($repoRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $artifactPath.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an artifact directory outside the source checkout.'
}
for ($directory = [IO.DirectoryInfo]::new($artifactPath); $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Artifact path traverses a link: $($directory.FullName)" }
}
$dotnetPath = (Get-Command $Dotnet -CommandType Application -ErrorAction Stop).Source
$nativeArguments = @{}
if ($Acquire) { $nativeArguments.Acquire = $true }
if ($ToolchainCacheRoot) { $nativeArguments.CacheRoot = $ToolchainCacheRoot }
$applicationProject = if ($Integrated) { 'Soltex.App' } else { 'Soltex.Audio.App' }
$applicationExecutable = if ($Integrated) { 'Soltex.exe' } else { 'Soltex.Audio.Desktop.exe' }
$applicationMode = if ($Integrated) { 'Integrated' } else { 'StandaloneAudio' }
$guide = if ($Integrated) { 'docs/audio/INTEGRATED-RUN.md' } else { 'docs/audio/RUN.md' }
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $guide) -PathType Leaf)) { throw "Required package guide is missing: $guide" }
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$workPath = Join-Path $artifactPath ('build-' + $runId)
$logs = Join-Path $workPath 'logs'
New-Item -ItemType Directory -Path $logs | Out-Null
$prior = @{}
foreach ($name in @('TEMP','TMP','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $prior[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:TEMP = Join-Path $workPath 'temp'; $env:TMP = $env:TEMP
    $env:DOTNET_CLI_HOME = Join-Path $workPath 'dotnet-home'
    $env:NUGET_PACKAGES = Join-Path $workPath 'nuget-packages'
    foreach ($dir in @($env:TEMP, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Push-Location $repoRoot
    try {
        $sourceCommit = & git -c "safe.directory=$repoRoot" rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source revision.' }
        $sourceChanges = @(& git -c "safe.directory=$repoRoot" status --porcelain)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source working-tree status.' }
        $sdkVersion = & $dotnetPath --version
        if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the .NET SDK.' }
        $verificationReceipt = $null
        $verification = [ordered]@{ Status = 'Skipped'; Mode = $applicationMode; SuiteCount = 0; Receipt = $null; SHA256 = $null }
        if (-not $SkipTests -and $Integrated) {
            $verificationRoot = Join-Path $workPath 'verification'
            $verifyArguments = @{ ExternalArtifactRoot = $verificationRoot; DotnetPath = $dotnetPath }
            if ($Acquire) { $verifyArguments.Acquire = $true }
            if ($ToolchainCacheRoot) { $verifyArguments.ToolchainCacheRoot = $ToolchainCacheRoot }
            & (Join-Path $PSScriptRoot 'verify-integrated.ps1') @verifyArguments *>&1 |
                Tee-Object -FilePath (Join-Path $logs 'verification.log') | Out-Host
            $receipts = @(Get-ChildItem -LiteralPath $verificationRoot -Directory | ForEach-Object {
                $candidate = Join-Path $_.FullName 'results.json'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $candidate }
            })
            if ($receipts.Count -ne 1) { throw 'Expected exactly one integrated verification receipt.' }
            $verificationReceipt = $receipts[0]
            $report = Get-Content -LiteralPath $verificationReceipt -Raw | ConvertFrom-Json
            if (-not $report.matrixCompleted -or -not $report.passed) { throw 'Integrated verification receipt does not report a completed passing matrix.' }
            $verification.Status = 'Passed'
            $verification.SuiteCount = @($report.checks | Where-Object name -like '*.Tests').Count
        }
        elseif (-not $SkipTests) {
            $checks = [System.Collections.Generic.List[object]]::new()
            $verificationReceipt = Join-Path $workPath 'standalone-verification.json'
            $finished = $false
            try {
                foreach ($test in @('Soltex.Audio.Tests','Soltex.Audio.Control.Tests','Soltex.Audio.Sonar.Tests','Soltex.Audio.Engine.Tests','Soltex.Audio.Transport.Tests','Soltex.Audio.Routing.Tests','Soltex.Audio.App.Tests')) {
                    $testArguments = if ($test -eq 'Soltex.Audio.Routing.Tests') { @('--', '--self-test') } else { @() }
                    $log = Join-Path $logs ($test + '.log')
                    & $dotnetPath run --project "tests/$test/$test.csproj" --configuration Release --artifacts-path (Join-Path $workPath 'test-build') @testArguments 2>&1 |
                        Tee-Object -FilePath $log | Out-Host
                    $code = $LASTEXITCODE
                    # Relative to the work folder; receipts never record absolute local paths.
                    $checks.Add([ordered]@{ name = $test; exitCode = $code; log = 'logs/' + $test + '.log' })
                    if ($code -ne 0) { throw "$test failed ($code)." }
                }
                $finished = $true
            }
            finally {
                [ordered]@{ schemaVersion = 1; mode = $applicationMode; matrixCompleted = $finished; passed = $finished; checks = $checks.ToArray() } |
                    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $verificationReceipt -Encoding utf8
            }
            $verification.Status = 'Passed'
            $verification.SuiteCount = $checks.Count
        }
        $package = Join-Path $artifactPath ('package-' + $runId)
        if (Test-Path -LiteralPath $package) { throw "Package already exists: $package" }
        $contained = if ($FrameworkDependent) { 'false' } else { 'true' }
        # The integrated app ships the native taskbar helper (TaskbarHelper\x64, TaskbarHelper\arm64).
        if ($Integrated) {
            & (Join-Path $repoRoot 'native\Soltex.TaskbarStyle\build.ps1') @nativeArguments *>&1 | Tee-Object -FilePath (Join-Path $logs 'native-taskbar.log') | Out-Host
        }
        # Every component that carries the audio engine also carries RNNoise (soltex-rnnoise.dll), which the engine project
        # copies next to Soltex.Audio.Engine.Core.dll when it has been built.
        & (Join-Path $repoRoot 'native\Soltex.Rnnoise\build.ps1') @nativeArguments *>&1 | Tee-Object -FilePath (Join-Path $logs 'native-rnnoise.log') | Out-Host
        $rnnoise = Join-Path $repoRoot 'artifacts\native\rnnoise\x64\soltex-rnnoise.dll'
        if (-not (Test-Path -LiteralPath $rnnoise -PathType Leaf)) { throw 'The RNNoise build produced no soltex-rnnoise.dll.' }
        foreach ($product in @($applicationProject,'Soltex.Audio.Tool','Soltex.Audio.Engine.Cli','Soltex.Audio.Host.Cli','Soltex.Audio.Broker.Cli')) {
            $folder = if ($product -eq 'Soltex.Audio.Engine.Cli') { 'Soltex.Audio.Engine' } elseif ($product -eq 'Soltex.Audio.Host.Cli') { 'Soltex.Audio.Host' } elseif ($product -eq 'Soltex.Audio.Broker.Cli') { 'Soltex.Audio.Broker' } else { $product }
            $destination = if ($product -eq $applicationProject) { $package } else { Join-Path $package $folder }
            & $dotnetPath publish "src/$product/$product.csproj" --configuration Release --runtime win-x64 --self-contained $contained --artifacts-path (Join-Path $workPath 'publish-build') --output $destination -p:DebugType=none 2>&1 |
                Tee-Object -FilePath (Join-Path $logs ("publish-$product.log")) | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "$product publish failed ($LASTEXITCODE)." }
        }
        $publishedExecutable = Join-Path $package $applicationExecutable
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) { throw "Published application executable is missing: $applicationExecutable" }
        # Version of the built executable (Directory.Build.props <Version>) without the +commit suffix.
        $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable).ProductVersion
        if ([string]::IsNullOrWhiteSpace($productVersion)) { throw "Published application has no product version: $applicationExecutable" }
        $packageVersion = ($productVersion -split '\+')[0].Trim()
        Copy-Item -LiteralPath (Join-Path $repoRoot $guide) -Destination (Join-Path $package 'README.md')
        if ($Integrated) { Copy-Item -LiteralPath (Join-Path $repoRoot 'eng/install-audio-shortcuts.ps1') -Destination (Join-Path $package 'Install-Shortcuts.ps1') }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Destination (Join-Path $package 'LICENSE.txt')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/audio/PORTABLE-RECOVERY.md') -Destination (Join-Path $package 'RECOVERY.md')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Soltex.Audio.Engine/README.md') -Destination (Join-Path $package 'Soltex.Audio.Engine/README.md')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Soltex.Audio.Tool/README.md') -Destination (Join-Path $package 'Soltex.Audio.Tool/README.md')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/audio/INDEPENDENT-HOST.md') -Destination (Join-Path $package 'Soltex.Audio.Host/README.md')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/audio/INDEPENDENT-HOST.md') -Destination (Join-Path $package 'INDEPENDENT-HOST.md')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Soltex.Audio.Broker/README.md') -Destination (Join-Path $package 'Soltex.Audio.Broker/README.md')
        $deploymentFolder = Join-Path $package 'OwnedAudioDeployment'
        New-Item -ItemType Directory -Path $deploymentFolder | Out-Null
        foreach ($deploymentFile in @('OWNED-AUDIO-DEPLOYMENT.md','owned-audio-deployment.ps1','owned-audio-release-allowlist.json')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot $deploymentFile) -Destination $deploymentFolder
        }
        if (-not $FrameworkDependent) {
            $assets = Get-Content -Raw -LiteralPath (Join-Path $workPath "publish-build/obj/$applicationProject/project.assets.json") | ConvertFrom-Json
            $downloads = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies })
            foreach ($runtimePackage in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
                $dependency = @($downloads | Where-Object name -eq $runtimePackage)[0]
                if ($null -eq $dependency -or $dependency.version -notmatch '^\[([^,]+),\s*\1\]$') { throw "Cannot resolve notices for $runtimePackage." }
                $runtimeVersion = $Matches[1]
                $runtimeFolder = Join-Path $env:NUGET_PACKAGES ($runtimePackage.ToLowerInvariant() + '/' + $runtimeVersion)
                $notices = @(Get-ChildItem -LiteralPath $runtimeFolder -File | Where-Object Name -in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT'))
                if ($notices.Count -eq 0) { throw "Runtime license is missing: $runtimePackage." }
                $licenseFolder = Join-Path $package ('licenses/' + $runtimePackage + '/' + $runtimeVersion)
                New-Item -ItemType Directory -Path $licenseFolder -Force | Out-Null
                foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $licenseFolder }
            }
        }
        if ($Integrated) {
            if (-not (Test-Path -LiteralPath (Join-Path $package 'TaskbarHelper\x64\SoltexTaskbar.dll') -PathType Leaf)) { throw 'The taskbar helper is missing from the package.' }
            $helperLicenses = Join-Path $package 'licenses/llvm-mingw-runtime'
            New-Item -ItemType Directory -Path $helperLicenses -Force | Out-Null
            Copy-Item -Path (Join-Path $repoRoot 'artifacts/native/taskbar/licenses/*') -Destination $helperLicenses
        }
        # soltex-rnnoise.dll must sit, as built, next to every copy of the engine; the engine loads it only from its own folder.
        $rnnoiseHash = (Get-FileHash -LiteralPath $rnnoise -Algorithm SHA256).Hash
        $engines = @(Get-ChildItem -LiteralPath $package -Recurse -File -Filter 'Soltex.Audio.Engine.Core.dll')
        if ($engines.Count -eq 0) { throw 'The package contains no audio engine.' }
        foreach ($engine in $engines) {
            $beside = Join-Path $engine.DirectoryName 'soltex-rnnoise.dll'
            $where = if ($engine.DirectoryName -eq $package) { 'the package root' } else { $engine.DirectoryName.Substring($package.Length + 1) }
            if (-not (Test-Path -LiteralPath $beside -PathType Leaf)) { throw "soltex-rnnoise.dll is missing next to the audio engine in $where." }
            if ((Get-FileHash -LiteralPath $beside -Algorithm SHA256).Hash -ne $rnnoiseHash) { throw "soltex-rnnoise.dll in $where is not the one just built." }
        }
        # RNNoise's BSD licence and authors, and the notices of the runtime pieces linked into it (licenses/rnnoise,
        # licenses/llvm-mingw-runtime); THIRD-PARTY-NOTICES.txt beside the engine carries the same BSD text.
        foreach ($folder in @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'artifacts/native/rnnoise/licenses') -Directory)) {
            $target = Join-Path $package ('licenses/' + $folder.Name)
            New-Item -ItemType Directory -Path $target -Force | Out-Null
            Copy-Item -Path (Join-Path $folder.FullName '*') -Destination $target -Force
        }
        if (-not (Test-Path -LiteralPath (Join-Path $package 'licenses/rnnoise/COPYING') -PathType Leaf)) { throw 'The RNNoise licence is missing from the package.' }
        if (-not (Select-String -LiteralPath (Join-Path $package 'THIRD-PARTY-NOTICES.txt') -SimpleMatch 'RNNoise' -Quiet)) { throw 'THIRD-PARTY-NOTICES.txt does not carry the RNNoise notice.' }
        if ($null -ne $verificationReceipt) {
            Copy-Item -LiteralPath $verificationReceipt -Destination (Join-Path $package 'VERIFICATION.json')
            $verification.Receipt = 'VERIFICATION.json'
            $verification.SHA256 = (Get-FileHash -LiteralPath $verificationReceipt -Algorithm SHA256).Hash
        }
        # BUILD.json and SHA256.json hold only package-relative paths, never local machine paths.
        [pscustomobject]@{
            Version = $packageVersion
            BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            SourceCommit = $sourceCommit.Trim()
            SourceDirty = $sourceChanges.Count -ne 0
            DotnetSdk = $sdkVersion.Trim()
            Runtime = 'win-x64'
            SelfContained = -not $FrameworkDependent
            TestsSkipped = [bool]$SkipTests
            ApplicationMode = $applicationMode
            ApplicationProject = $applicationProject
            ApplicationExecutable = $applicationExecutable
            Verification = $verification
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'BUILD.json') -Encoding utf8
        Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName.Substring($package.Length + 1); Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $package 'SHA256.json') -Encoding utf8
        Write-Output "Package: $package"
        if ($Integrated) {
            Write-Output 'Integrated Soltex: Windows sessions, explicit app routing and the independent channel host work without GG. Channels require separately supplied endpoints and explicit start. Sonar comparison remains optional. This package installs no driver and is not application-signed; live hardware qualification is separate.'
        }
        else {
            Write-Output 'This package installs no driver and is not application-signed. The mixer requires separately installed Sonar; the independent PCM host requires explicit existing capture/render endpoints.'
        }
    } finally { Pop-Location }
} finally {
    foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable($name, $prior[$name], 'Process') }
}
