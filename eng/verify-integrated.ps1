[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExternalArtifactRoot,
    [Parameter(Mandatory = $true)][string]$DotnetPath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$IncludeNativeReadOnly,
    # Download the pinned LLVM-MinGW toolchain for the native parts (taskbar helper, RNNoise) when it is missing.
    [switch]$Acquire,
    # Where the native build keeps that toolchain (passed to its -CacheRoot).
    [string]$ToolchainCacheRoot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repoRoot = Split-Path -Parent $PSScriptRoot
# Artifacts go to a local fixed drive, outside the checkout, never through a network path or a link.
if (-not [IO.Path]::IsPathFullyQualified($ExternalArtifactRoot)) { throw 'ExternalArtifactRoot must be absolute.' }
$artifactRoot = [IO.Path]::GetFullPath($ExternalArtifactRoot)
$artifactDrive = [IO.Path]::GetPathRoot($artifactRoot)
if ($artifactDrive.StartsWith('\\') -or [IO.DriveInfo]::new($artifactDrive).DriveType -ne [IO.DriveType]::Fixed) {
    throw 'ExternalArtifactRoot must be on a local fixed drive, not a network share (UNC path) or a removable drive.'
}
if ($artifactRoot.TrimEnd('\').Equals($repoRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -or
    $artifactRoot.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ExternalArtifactRoot must be outside the source checkout.'
}
for ($directory = [IO.DirectoryInfo]::new($artifactRoot); $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Artifact path traverses a link: $($directory.FullName)"
    }
}
if (-not [IO.Path]::IsPathFullyQualified($DotnetPath) -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'DotnetPath must name the installed dotnet executable by absolute path.'
}
$dotnetSdk = @(& $DotnetPath --version)
if ($LASTEXITCODE -ne 0 -or $dotnetSdk.Count -eq 0) { throw 'Cannot identify the .NET SDK.' }
$dotnetSdk = ([string]$dotnetSdk[0]).Trim()
$runRoot = Join-Path $artifactRoot ('verify-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$logs = Join-Path $runRoot 'logs'
$buildRoot = Join-Path $runRoot 'artifacts'
$tempRoot = Join-Path $runRoot 'temp'
New-Item -ItemType Directory -Path $logs, $tempRoot -Force | Out-Null
$savedEnvironment = @{}
$results = [System.Collections.Generic.List[object]]::new()
$exclusions = @(
    'Audio.Hardware: microphone capture, replay and bridge require separate explicit hardware acceptance.',
    'Audio.Transport live, switches and ChatMix modes: audible signal and reversible backend mutations require a separate journaled run.',
    'Audio.Routing default/read-only: opens a silent playback stream; only --self-test runs here. Routing mutation/recovery modes are excluded.',
    'OwnedTransport, OwnedEndpoints and Broker use fake devices/local pipes; kernel installation and driver runtime are excluded.',
    'Audio.Host and Independent.UI use fake graph backends and local protocol/render fixtures; Cable runs only pure checks. None establishes driver, live multibus, hotplug or physical microphone acceptance.',
    'EICAR interoperability is disabled. Security scans, signature updates, quarantine of user files and external remote-client launches are excluded.',
    'Full application launch/render-smoke, packaged launch, live UI interaction and performance are separate acceptance; control tests do not prove those.',
    'RemoteAssist has no separate test project; its argument and discovery contracts are covered by Security.Tests.'
)
if (-not $IncludeNativeReadOnly) {
    $exclusions += 'Optional Control --live-read-only and Sonar --live probes were not requested. Default Audio/Monitoring/Security/App suites still include bounded native read-only observations.'
}

function Set-ScopedEnvironment([string]$Name, [string]$Value) {
    if (-not $savedEnvironment.ContainsKey($Name)) {
        $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

# results.json names logs relative to its own folder and never records absolute local paths.
function Get-RelativeLog([string]$Name) { return 'logs/' + $Name + '.log' }
function Get-SafeMessage([string]$Message) {
    return $Message.Replace($runRoot, '<run>').Replace($repoRoot, '<repository>')
}

function Invoke-NativeCheck([string]$Name, [string[]]$Arguments) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $logs ($Name + '.log')
    & $DotnetPath @Arguments 2>&1 | Tee-Object -FilePath $log | Out-Host
    $code = $LASTEXITCODE
    $results.Add([ordered]@{ name = $Name; exitCode = $code; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog $Name) })
    return $code -eq 0
}

function Invoke-Test([string]$Project, [string[]]$TestArguments = @(), [string]$Suffix = '', [string]$AssemblyName = $Project, [string]$RuntimeIdentifier = '') {
    $pivot = $Configuration.ToLowerInvariant()
    if ($RuntimeIdentifier) { $pivot += '_' + $RuntimeIdentifier }
    $binary = Join-Path $buildRoot ("bin\$Project\$pivot\$AssemblyName.dll")
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Missing built test assembly: $binary" }
    [void](Invoke-NativeCheck ($Project + $Suffix) (@($binary) + $TestArguments))
}

$completed = $false
$failure = $null
try {
    Set-ScopedEnvironment 'TEMP' $tempRoot
    Set-ScopedEnvironment 'TMP' $tempRoot
    Set-ScopedEnvironment 'DOTNET_ROOT' (Split-Path -Parent $DotnetPath)
    Set-ScopedEnvironment 'SOLTEX_TEST_REPOSITORY_ROOT' $repoRoot
    Set-ScopedEnvironment 'SOLTEX_TEST_CHILD_MODE' $null
    # Read both current and legacy opt-in names from the canonical identity definitions.
    $identitySource = Get-Content -LiteralPath (Join-Path $repoRoot 'src\Soltex.Security\ProductIdentity.cs') -Raw
    $eicarNames = [regex]::Matches($identitySource, 'const string (?:Legacy)?RunEicarEnvironmentVariable\s*=\s*"([^"]+)"')
    if ($eicarNames.Count -ne 2) { throw 'Expected both EICAR opt-in environment definitions.' }
    foreach ($match in $eicarNames) { Set-ScopedEnvironment $match.Groups[1].Value '0' }
    # Scope Git trust to this invocation, preserving any caller-provided config entries.
    $configIndex = 0
    if ($env:GIT_CONFIG_COUNT) { $configIndex = [int]$env:GIT_CONFIG_COUNT }
    Set-ScopedEnvironment "GIT_CONFIG_KEY_$configIndex" 'safe.directory'
    Set-ScopedEnvironment "GIT_CONFIG_VALUE_$configIndex" $repoRoot.Replace('\', '/')
    Set-ScopedEnvironment 'GIT_CONFIG_COUNT' ([string]($configIndex + 1))
    foreach ($policy in @('verify-identity', 'verify-design-tokens', 'test-owned-audio-deployment')) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $log = Join-Path $logs ($policy + '.log')
        try {
            $policyArguments = if ($policy -eq 'test-owned-audio-deployment') { @{ ArtifactDirectory = $tempRoot } } else { @{} }
            & (Join-Path $PSScriptRoot ($policy + '.ps1')) @policyArguments *>&1 | Tee-Object -FilePath $log | Out-Host
            $results.Add([ordered]@{ name = $policy; exitCode = 0; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog $policy) })
        }
        catch {
            $results.Add([ordered]@{ name = $policy; exitCode = 1; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog $policy); error = (Get-SafeMessage $_.Exception.Message) })
            throw
        }
    }
    # The taskbar helper is native (LLVM-MinGW, pinned in native/Soltex.TaskbarStyle/toolchain.lock.json); the app build
    # packages it and App.Tests drives it in a XAML test host. -Acquire downloads and verifies the pinned toolchain.
    $nativeArguments = @{}
    if ($Acquire) { $nativeArguments.Acquire = $true }
    if ($ToolchainCacheRoot) { $nativeArguments.CacheRoot = $ToolchainCacheRoot }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $logs 'native-taskbar.log'
    try {
        & (Join-Path $repoRoot 'native\Soltex.TaskbarStyle\build.ps1') @nativeArguments *>&1 | Tee-Object -FilePath $log | Out-Host
        $results.Add([ordered]@{ name = 'native-taskbar'; exitCode = 0; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog 'native-taskbar') })
    }
    catch {
        $results.Add([ordered]@{ name = 'native-taskbar'; exitCode = 1; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog 'native-taskbar'); error = (Get-SafeMessage $_.Exception.Message) })
        throw
    }
    # RNNoise (soltex-rnnoise.dll) from the vendored, hash-checked sources; the engine build copies it next to every copy of
    # the engine, and Audio.Engine.Tests measure neural noise removal through it.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $logs 'native-rnnoise.log'
    try {
        & (Join-Path $repoRoot 'native\Soltex.Rnnoise\build.ps1') @nativeArguments *>&1 | Tee-Object -FilePath $log | Out-Host
        $results.Add([ordered]@{ name = 'native-rnnoise'; exitCode = 0; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog 'native-rnnoise') })
    }
    catch {
        $results.Add([ordered]@{ name = 'native-rnnoise'; exitCode = 1; durationSeconds = $timer.Elapsed.TotalSeconds; log = (Get-RelativeLog 'native-rnnoise'); error = (Get-SafeMessage $_.Exception.Message) })
        throw
    }
    if (-not (Invoke-NativeCheck 'build' @('build', (Join-Path $repoRoot 'Soltex.sln'), '--configuration', $Configuration, '--artifacts-path', $buildRoot))) {
        throw 'Integrated build failed; no test results are claimed.'
    }
    foreach ($project in @(
        'Soltex.Security.Tests', 'Soltex.Security.SupplyChain.Tests', 'Soltex.Security.Hardening.Tests',
        'Soltex.Update.Tests', 'Soltex.DeviceFabric.Tests', 'Soltex.Monitoring.Tests', 'Soltex.Customize.Tests', 'Soltex.App.Tests',
        'Soltex.Audio.Tests', 'Soltex.Audio.Control.Tests', 'Soltex.Audio.Sonar.Tests', 'Soltex.Audio.Engine.Tests',
        'Soltex.Audio.Sessions.Tests', 'Soltex.Audio.Host.Tests', 'Soltex.Audio.Cable.Tests',
        'Soltex.Audio.OwnedTransport.Tests', 'Soltex.Audio.OwnedEndpoints.Tests', 'Soltex.Audio.Broker.Tests'
    )) { Invoke-Test $project }
    Invoke-Test 'Soltex.Audio.App.Tests' @('--work-dir', $tempRoot, '--render-dir', (Join-Path $runRoot 'ui-renders'))
    Invoke-Test 'Soltex.Audio.Independent.UI.Tests' @('--render-dir', (Join-Path $runRoot 'independent-ui-renders'))
    Invoke-Test 'Soltex.Audio.Transport.Tests' @('--self-test')
    Invoke-Test -Project 'Soltex.Audio.Routing.Tests' -TestArguments @('--self-test') -AssemblyName 'Soltex.OwnedAudioRouteProbe' -RuntimeIdentifier 'win-x64'
    if ($IncludeNativeReadOnly) {
        Invoke-Test 'Soltex.Audio.Control.Tests' @('--live-read-only') '-native-read-only'
        Invoke-Test 'Soltex.Audio.Sonar.Tests' @('--live', (Join-Path $runRoot 'sonar-read-only.json')) '-native-read-only'
    }
    $completed = $true
    if (@($results | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) { throw 'One or more integrated checks failed; see results.json and individual logs.' }
}
catch {
    $failure = Get-SafeMessage $_.Exception.Message
    throw
}
finally {
    try {
        [ordered]@{
            schemaVersion = 1
            recordedAt = [DateTimeOffset]::UtcNow.ToString('o')
            configuration = $Configuration
            dotnetSdk = $dotnetSdk
            matrixCompleted = $completed
            passed = $completed -and @($results | Where-Object { $_.exitCode -ne 0 }).Count -eq 0
            error = $failure
            checks = @($results.ToArray())
            excluded = $exclusions
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'results.json') -Encoding UTF8
    }
    finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    Write-Host "Integrated verification results: $runRoot"
    foreach ($exclusion in $exclusions) { Write-Host "Excluded: $exclusion" }
}
