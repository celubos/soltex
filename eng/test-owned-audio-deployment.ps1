#Requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'owned-audio-deployment.ps1')
$testRoot = Join-Path ([IO.Path]::GetFullPath($ArtifactDirectory)) ('deployment-fixture-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $testRoot 'Broker') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../native/Soltex.Audio.Driver/SoltexAudio.inf') -Destination (Join-Path $testRoot 'SoltexAudio.inf')
foreach ($relative in @('SoltexAudio.sys', 'SoltexAudio.cat', 'Broker/Soltex.Audio.Broker.Service.exe')) {
    [IO.File]::WriteAllText((Join-Path $testRoot $relative), 'INERT TEST FIXTURE, NOT A DRIVER OR SIGNED RELEASE')
}
$checks = 0
function Assert-Check([bool]$Condition, [string]$Text) {
    if (-not $Condition) { throw $Text }; $script:checks++; Write-Output ('PASS ' + $Text)
}
$facts = Get-OwnedPackageFacts $testRoot
Assert-Check ($facts.Errors.Count -eq 0 -and $facts.Files.Count -eq 4) 'Exact INF identity and bounded payload inventory are read without loading a driver.'
Assert-Check (@(Test-OwnedReleasePins $facts $null).Count -eq 1) 'Unapproved unsigned prototype is rejected even when filenames and hardware identity match.'
$fixturePins = [ordered]@{
    ReleaseId = 'TEST-ONLY-NOT-A-RELEASE'; Files = $facts.Files
    KernelCatalogSignerThumbprint = ('A' * 40); BrokerSignerThumbprint = ('B' * 40); BrokerSignerSubject = 'CN=INERT FIXTURE'
}
Assert-Check (@(Test-OwnedReleasePins $facts $fixturePins).Count -eq 0) 'Pure pin comparison accepts an exact fixture inventory; this does not assert signature trust.'
$plan = New-OwnedAudioDeploymentPlan $facts $false @('Fixture has no valid signatures.')
Assert-Check (-not $plan.PackageVerified -and -not $plan.ApplyImplemented -and -not $plan.DeploymentReady -and $plan.MutationsExecuted.Count -eq 0) 'A plan never claims installation, readiness or mutations.'
Assert-Check ($plan.Identity.HardwareId -ceq 'ROOT\SoltexAudio' -and $plan.Identity.BrokerService -ceq 'SoltexAudioBroker' -and $plan.Identity.BrokerArguments[0] -ceq '--service') 'Deployment identity is fixed to owned hardware and broker only.'
Assert-Check (($plan.Installation -join ' ') -match 'DIF_REGISTERDEVICE' -and ($plan.Installation -join ' ') -match 'PnPUtil add/install alone does not create') 'Plan requires explicit ROOT device registration, not an ineffective pnputil-only install.'
Assert-Check (($plan.Uninstallation -join ' ') -match 'receipt-owned device instance' -and ($plan.Uninstallation -join ' ') -match 'no other instance uses') 'Uninstall plan requires exact instance ownership and protects shared driver-store packages.'
foreach ($unsafe in @('../SoltexAudio.inf', 'C:\foreign.inf', 'Broker/../../foreign.inf', 'SoltexAudio.sys:stream')) {
    $rejected = $false; try { $null = Resolve-OwnedPayloadPath $testRoot $unsafe } catch { $rejected = $true }
    Assert-Check $rejected ('Unsafe payload path rejected: ' + $unsafe)
}
[IO.File]::AppendAllText((Join-Path $testRoot 'SoltexAudio.sys'), 'modified')
$changed = Get-OwnedPackageFacts $testRoot
Assert-Check ((@(Test-OwnedReleasePins $changed $fixturePins) -join ' ') -match 'hash mismatch') 'Modified driver bytes fail their pinned SHA256.'
[IO.File]::WriteAllText((Join-Path $testRoot 'unapproved.dll'), 'extra')
Assert-Check ((@(Test-OwnedReleasePins (Get-OwnedPackageFacts $testRoot) $fixturePins) -join ' ') -match 'Unapproved extra payload') 'Unapproved dependency injection is rejected.'
$infPath = Join-Path $testRoot 'SoltexAudio.inf'
[IO.File]::WriteAllText($infPath, [IO.File]::ReadAllText($infPath).Replace('ROOT\SoltexAudio', 'ROOT\VendorDevice'))
Assert-Check (((Get-OwnedPackageFacts $testRoot).Errors -join ' ') -match 'HardwareId') 'Foreign hardware identity is rejected before signature verification.'
$allowlist = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-audio-release-allowlist.json') -Raw | ConvertFrom-Json
Assert-Check ($allowlist.ApprovedReleases.Count -eq 0) 'No unsigned prototype or invented production publisher is allowlisted.'
$command = Get-Command (Join-Path $PSScriptRoot 'owned-audio-deployment.ps1')
Assert-Check (-not $command.Parameters.ContainsKey('Apply') -and -not $command.Parameters.ContainsKey('Install')) 'No unimplemented mutation switch is presented as an installer.'
$start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'owned-audio-deployment.ps1'), '-Mode', 'Verify', '-PackagePath', $testRoot)) { $start.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($start)
try {
    $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
    if (-not $child.WaitForExit(15000)) { $child.Kill(); throw 'Read-only verifier fixture timed out.' }
    $result = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
    Assert-Check ($child.ExitCode -eq 2 -and -not $result.PackageVerified -and $result.MutationsExecuted.Count -eq 0) 'Actual Verify command exits 2 and reports no mutations for an unapproved fixture.'
    Assert-Check ([string]::IsNullOrEmpty($stderr.GetAwaiter().GetResult())) 'Expected package rejection is a structured result, not an unhandled script error.'
} finally { $child.Dispose() }
Write-Output "$checks/$checks deployment-plan checks passed. Inert files only; no native device, service, UAC or signature success fabricated. Fixture retained at $testRoot"
