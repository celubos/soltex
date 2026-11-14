#Requires -Version 7.2
<#
Read-only package verifier and deployment-plan generator. No install, service mutation,
driver removal, audio-default write, elevation or reboot implementation is present.
The empty checked-in release allowlist intentionally refuses the unsigned prototype.
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Plan', 'Verify')][string]$Mode = 'Inspect',
    [string]$PackagePath,
    [string]$ReleaseId,
    [string]$SignToolPath,
    [string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OwnedAudioIdentity {
    [ordered]@{
        HardwareId = 'ROOT\SoltexAudio'; Inf = 'SoltexAudio.inf'; Driver = 'SoltexAudio.sys'; Catalog = 'SoltexAudio.cat'
        Provider = 'Soltex'; Class = 'MEDIA'; ClassGuid = '{4d36e96c-e325-11ce-bfc1-08002be10318}'
        DriverService = 'SoltexAudio'; BrokerService = 'SoltexAudioBroker'; BrokerFile = 'Broker/Soltex.Audio.Broker.Service.exe'
        BrokerAccount = 'LocalSystem'; BrokerArguments = @('--service'); Pipe = 'Soltex.Audio.Broker.v1'
    }
}

function Resolve-OwnedPayloadPath([string]$Root, [string]$RelativePath) {
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or $RelativePath -match '(^|[\\/])\.\.?([\\/]|$)') { throw 'Unsafe relative payload path.' }
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($resolvedRoot, $RelativePath))
    if (-not $candidate.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload escaped its package root.' }
    # Reject junctions/symlinks instead of accepting a misleading package boundary.
    $part = Get-Item -LiteralPath $candidate -Force
    while ($null -ne $part) {
        if (($part.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package paths through reparse points are not accepted.' }
        $part = if ($part -is [IO.FileInfo]) { $part.Directory } else { $part.Parent }
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw 'Payload entry is not a file.' }
    return $candidate
}

function Get-OwnedPackageFacts([string]$Root) {
    $identity = Get-OwnedAudioIdentity
    $files = [ordered]@{}; $errors = [Collections.Generic.List[string]]::new()
    $rootItem = Get-Item -LiteralPath $Root -Force
    if ($rootItem -isnot [IO.DirectoryInfo]) { throw 'PackagePath must be a directory.' }
    # Walk one directory at a time so a junction is rejected before recursion follows it.
    $pending = [Collections.Generic.Queue[IO.DirectoryInfo]]::new(); $pending.Enqueue($rootItem)
    $directoryCount = 0; [long]$totalBytes = 0
    while ($pending.Count -gt 0) {
        $directoryCount++; if ($directoryCount -gt 1000) { throw 'Package exceeds directory-count bound.' }
        $directory = $pending.Dequeue()
        if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package contains a directory reparse point.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package contains a reparse point.' }
            if ($entry -is [IO.DirectoryInfo]) { $pending.Enqueue($entry); continue }
            if ($files.Count -ge 3000 -or $entry.Length -gt 536870912) { throw 'Package exceeds file-count or individual-size bounds.' }
            $totalBytes += $entry.Length; if ($totalBytes -gt 2147483648) { throw 'Package exceeds total-size bound.' }
            $relative = [IO.Path]::GetRelativePath($rootItem.FullName, $entry.FullName).Replace('\', '/')
            $safe = Resolve-OwnedPayloadPath $rootItem.FullName $relative
            $files[$relative] = (Get-FileHash -LiteralPath $safe -Algorithm SHA256).Hash
        }
    }
    foreach ($required in @($identity.Inf, $identity.Driver, $identity.Catalog, $identity.BrokerFile)) {
        if (-not $files.Contains($required)) { $errors.Add('Missing required payload: ' + $required) }
    }
    if ($files.Contains($identity.Inf)) {
        $infPath = Resolve-OwnedPayloadPath $Root $identity.Inf
        if ((Get-Item -LiteralPath $infPath).Length -gt 1048576) { throw 'INF exceeds its size limit.' }
        $inf = [IO.File]::ReadAllText($infPath)
        $requirements = @{
            HardwareId = '(?im)^\s*%Device%\s*=\s*Install\s*,\s*ROOT\\SoltexAudio\s*$'
            Class = '(?im)^\s*Class\s*=\s*MEDIA\s*$'
            ClassGuid = '(?im)^\s*ClassGuid\s*=\s*\{4d36e96c-e325-11ce-bfc1-08002be10318\}\s*$'
            Catalog = '(?im)^\s*CatalogFile\s*=\s*SoltexAudio\.cat\s*$'
            Provider = '(?im)^\s*Provider\s*=\s*"?Soltex"?\s*$'
            Service = '(?im)^\s*AddService\s*=\s*SoltexAudio\s*,\s*0x00000002\s*,\s*Service\s*$'
            Binary = '(?im)^\s*ServiceBinary\s*=\s*%13%\\SoltexAudio\.sys\s*$'
        }
        foreach ($rule in $requirements.GetEnumerator()) { if ($inf -notmatch $rule.Value) { $errors.Add('INF identity mismatch: ' + $rule.Key) } }
        $hardwareIds = [regex]::Matches($inf, '(?i)ROOT\\[A-Za-z0-9_.-]+') | ForEach-Object Value | Sort-Object -Unique
        if (@($hardwareIds).Count -ne 1 -or $hardwareIds -ine $identity.HardwareId) { $errors.Add('INF contains a foreign or ambiguous ROOT hardware identity.') }
    }
    [pscustomobject]@{ Root = $rootItem.FullName; Files = $files; Errors = $errors.ToArray() }
}

function Test-OwnedReleasePins($Facts, $ApprovedRelease) {
    $errors = [Collections.Generic.List[string]]::new()
    foreach ($errorText in $Facts.Errors) { $errors.Add($errorText) }
    if ($null -eq $ApprovedRelease) { $errors.Add('No matching reviewed signed release is in the checked-in allowlist.'); return $errors.ToArray() }
    foreach ($field in @('ReleaseId', 'Files', 'KernelCatalogSignerThumbprint', 'BrokerSignerThumbprint', 'BrokerSignerSubject')) {
        if (-not $ApprovedRelease.Contains($field) -or -not $ApprovedRelease[$field]) { $errors.Add('Approved release lacks ' + $field) }
    }
    if ($errors.Count -gt 0) { return $errors.ToArray() }
    foreach ($thumbprint in @($ApprovedRelease.KernelCatalogSignerThumbprint, $ApprovedRelease.BrokerSignerThumbprint)) {
        if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$') { $errors.Add('Signer certificate thumbprint is invalid.') }
    }
    foreach ($file in $ApprovedRelease.Files.GetEnumerator()) {
        if ($file.Value -notmatch '^[A-Fa-f0-9]{64}$') { $errors.Add('Invalid SHA256 pin: ' + $file.Key); continue }
        if (-not $Facts.Files.Contains($file.Key) -or $Facts.Files[$file.Key] -ine $file.Value) { $errors.Add('Payload hash mismatch or missing file: ' + $file.Key) }
    }
    foreach ($file in $Facts.Files.Keys) { if (-not $ApprovedRelease.Files.Contains($file)) { $errors.Add('Unapproved extra payload: ' + $file) } }
    return $errors.ToArray()
}

function Invoke-OwnedSignatureVerification($Facts, $ApprovedRelease, [string]$ToolPath) {
    if (-not $IsWindows) { throw 'Windows trust verification is required.' }
    if ([string]::IsNullOrWhiteSpace($ToolPath)) { throw 'Provide an installed Microsoft-signed Windows SDK signtool.exe for kernel policy verification.' }
    $tool = Get-Item -LiteralPath $ToolPath
    $toolSignature = Get-AuthenticodeSignature -LiteralPath $tool.FullName
    if ($tool.Name -ine 'signtool.exe' -or $toolSignature.Status -ne 'Valid' -or $toolSignature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
        throw 'SignTool must be the valid Microsoft-signed Windows SDK utility.'
    }
    $identity = Get-OwnedAudioIdentity
    $catalog = Resolve-OwnedPayloadPath $Facts.Root $identity.Catalog
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog
    if ($catalogSignature.Status -ne 'Valid' -or $catalogSignature.SignerCertificate.Thumbprint -ine $ApprovedRelease.KernelCatalogSignerThumbprint -or
        $catalogSignature.SignerCertificate.Subject -notmatch 'CN=Microsoft Windows Hardware Compatibility Publisher(,|$)') { throw 'Catalog is not the approved Microsoft kernel release signature.' }
    $broker = Resolve-OwnedPayloadPath $Facts.Root $identity.BrokerFile
    $brokerSignature = Get-AuthenticodeSignature -LiteralPath $broker
    if ($brokerSignature.Status -ne 'Valid' -or $brokerSignature.SignerCertificate.Thumbprint -ine $ApprovedRelease.BrokerSignerThumbprint -or
        $brokerSignature.SignerCertificate.Subject -cne $ApprovedRelease.BrokerSignerSubject) { throw 'Broker does not have the pinned valid publisher signature.' }
    $results = [Collections.Generic.List[object]]::new()
    foreach ($member in @($identity.Inf, $identity.Driver)) {
        $start = [Diagnostics.ProcessStartInfo]::new($tool.FullName); $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        foreach ($argument in @('verify', '/kp', '/v', '/c', $catalog, (Resolve-OwnedPayloadPath $Facts.Root $member))) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Kernel signature verification timed out.' }
            $result = [pscustomobject]@{ Member = $member; ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
            if ($process.ExitCode -ne 0) { throw ('Kernel policy/catalog membership verification failed for ' + $member + ': ' + $result.Output + $result.Error) }
            $results.Add($result)
        } finally { $process.Dispose() }
    }
    # Rehash after trust checks. A future apply must repeat this after copying to protected storage.
    $after = Get-OwnedPackageFacts $Facts.Root
    $changed = @(Test-OwnedReleasePins $after $ApprovedRelease)
    if ($changed.Count -gt 0) { throw ('Package changed during verification: ' + ($changed -join '; ')) }
    return $results.ToArray()
}

function New-OwnedAudioDeploymentPlan($Facts, [bool]$PackageVerified, [string[]]$Blockers) {
    $identity = Get-OwnedAudioIdentity
    [ordered]@{
        SchemaVersion = 1; Kind = 'Soltex owned audio deployment plan'; GeneratedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Identity = $identity; PackagePath = $Facts.Root; PayloadSha256 = $Facts.Files; PackageVerified = $PackageVerified
        ApplyImplemented = $false; DeploymentReady = $false; MutationsExecuted = @(); Blockers = @($Blockers)
        Installation = @(
            'Explicit administrator action only. Snapshot all six audio defaults and owned device/service inventory; reject unknown existing ownership and multiple exact hardware-ID instances.'
            'Copy only the verified allowlisted payload to a protected administrator/SYSTEM-write-only directory; rehash and revalidate signatures there.'
            'Enumerate exact ROOT\SoltexAudio hardware IDs using SetupAPI. If absent, SetupDiCreateDeviceInfoW with DICD_GENERATE_ID, set SPDRP_HARDWAREID to the exact MULTI_SZ, then DIF_REGISTERDEVICE. Journal the returned instance ID before continuing.'
            'Install the pinned INF using UpdateDriverForPlugAndPlayDevicesW only after uniqueness is checked. Record published OEM INF, catalog/hash and any reboot-required flag. PnPUtil add/install alone does not create this ROOT device.'
            'Create only SoltexAudioBroker through SCM, WIN32_OWN_PROCESS, LocalSystem, demand start, quoted protected Soltex.Audio.Broker.Service.exe path plus --service. Preserve admin/SYSTEM control; ordinary users get only QueryConfig/QueryStatus, not modification or start/stop rights.'
            'Do not start audio, start the broker, select defaults, enable endpoints or reboot automatically. Report any PnP-induced default changes and use compare-before-restore against the captured baseline only.'
        )
        Uninstallation = @(
            'Require a protected installation receipt and exact device instance, original hardware ID, published INF/hash, broker image/hash and service account match. Refuse missing/ambiguous or changed ownership.'
            'Request graceful stop of only the receipt-owned broker through SCM and wait for STOPPED; no broad process termination.'
            'Open only the receipt-owned device instance using SetupAPI and remove with DiUninstallDevice. Record reboot-required status; never reboot automatically.'
            'Remove the exact owned service and driver-store package only after proving no other instance uses it. Preserve unrelated vendor drivers, endpoints, user settings and default-role changes.'
        )
        RemainingApplyGap = 'No root-device/SCM mutation executor or transactional recovery implementation is supplied by this script. This reviewed plan and cryptographic verifier are not an installer.'
        References = @(
            'https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdicreatedeviceinfoa'
            'https://learn.microsoft.com/en-us/previous-versions/windows/drivers/install/dif-registerdevice'
            'https://learn.microsoft.com/en-us/windows/win32/api/newdev/nf-newdev-updatedriverforplugandplaydevicesw'
            'https://learn.microsoft.com/en-us/windows-hardware/drivers/install/how-devices-and-driver-packages-are-uninstalled'
            'https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/signtool'
            'https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights'
        )
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    if ([string]::IsNullOrWhiteSpace($PackagePath)) { throw 'Specify -PackagePath for the read-only inventory/plan. No apply/install mode exists.' }
    $facts = Get-OwnedPackageFacts $PackagePath
    $allowlist = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'owned-audio-release-allowlist.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($allowlist.SchemaVersion -ne 1) { throw 'Unsupported release allowlist schema.' }
    $matches = @($allowlist.ApprovedReleases | Where-Object { $_.ReleaseId -ceq $ReleaseId })
    if ($matches.Count -gt 1) { throw 'Ambiguous approved release identity.' }
    $release = if ($matches.Count -eq 1) { $matches[0] } else { $null }
    $blockers = @(Test-OwnedReleasePins $facts $release); $verified = $false
    if ($blockers.Count -eq 0) {
        try { $null = Invoke-OwnedSignatureVerification $facts $release $SignToolPath; $verified = $true }
        catch { $blockers += $_.Exception.Message }
    }
    $report = New-OwnedAudioDeploymentPlan $facts $verified $blockers
    $json = $report | ConvertTo-Json -Depth 12
    if ($OutputPath) {
        $target = [IO.Path]::GetFullPath($OutputPath)
        if (Test-Path -LiteralPath $target) { throw 'Refusing to overwrite an existing deployment receipt.' }
        $receipt = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $receipt.Write($bytes, 0, $bytes.Length); $receipt.Flush($true) } finally { $receipt.Dispose() }
    }
    $json
    if ($Mode -eq 'Verify' -and -not $verified) { exit 2 }
}
