param(
    # Expanded WDK/SDK packages from acquire-kit.ps1 (same default as its -Destination).
    [string]$KitRoot = (Join-Path $PSScriptRoot '..\..\artifacts\native\driver-kit'),
    # MSVC toolset folder (...\VC\Tools\MSVC\<version>). Found with vswhere when omitted.
    [string]$MsvcRoot,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\native\driver')
)
$ErrorActionPreference = 'Stop'
$lock = Get-Content -LiteralPath "$PSScriptRoot\toolchain.lock.json" -Raw | ConvertFrom-Json
$KitRoot = [IO.Path]::GetFullPath($KitRoot)
$Output = [IO.Path]::GetFullPath($Output)
$ancestor = $Output
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Use a direct output path, not a junction or symbolic link: $ancestor" }
    }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if (-not (Test-Path -LiteralPath (Join-Path $KitRoot 'wdk') -PathType Container)) {
    throw "The WDK packages are missing from $KitRoot. Run acquire-kit.ps1 first."
}

# The compiler version is pinned in toolchain.lock.json ("MSVC 14.51.36231").
if ($lock.compiler -notmatch '^MSVC (\d+\.\d+\.\d+)$') { throw "Unexpected compiler pin in toolchain.lock.json: $($lock.compiler)" }
$msvcVersion = $Matches[1]
if (-not $MsvcRoot) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'vswhere.exe was not found. Install Visual Studio (or its Build Tools) with the C++ x64 build tools, or pass -MsvcRoot.'
    }
    $installations = @(& $vswhere -all -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    foreach ($installation in $installations) {
        $candidate = Join-Path $installation "VC\Tools\MSVC\$msvcVersion"
        if (Test-Path -LiteralPath (Join-Path $candidate 'bin\Hostx64\x64\cl.exe') -PathType Leaf) { $MsvcRoot = $candidate; break }
    }
    if (-not $MsvcRoot) {
        throw "MSVC $msvcVersion (pinned in toolchain.lock.json) was not found in any Visual Studio installation. Install that toolset or pass -MsvcRoot."
    }
}
$MsvcRoot = [IO.Path]::GetFullPath($MsvcRoot)
$cl = Join-Path $MsvcRoot 'bin\Hostx64\x64\cl.exe'
$link = Join-Path $MsvcRoot 'bin\Hostx64\x64\link.exe'
if (-not (Test-Path -LiteralPath $cl -PathType Leaf)) { throw "cl.exe was not found under $MsvcRoot." }

New-Item -ItemType Directory -Force $Output | Out-Null
$wdk = Join-Path $KitRoot 'wdk\c'
$sdk = Join-Path $KitRoot 'microsoft.windows.sdk.cpp\c'
$include = @("$wdk\Include\10.0.28000.0\km", "$wdk\Include\10.0.28000.0\shared", "$wdk\Include\wdf\kmdf\1.31", "$wdk\Include\10.0.28000.0\km\acx\km\1.1", "$sdk\Include\10.0.28000.0\shared", "$sdk\Include\10.0.28000.0\ucrt", "$sdk\Include\10.0.28000.0\um", "$MsvcRoot\include")
$compileArgs = @('/nologo','/c','/kernel','/std:c++17','/W4','/WX','/wd4324','/O2','/GS','/Zl','/GR-','/EHs-c-','/D_AMD64_','/DAMD64','/D_WIN64','/D_WIN32_WINNT=0x0A00','/DNTDDI_VERSION=0x0A000008','/DUNICODE','/D_UNICODE','/DACX_VERSION_MAJOR=1','/DACX_VERSION_MINOR=1','/DKMDF_VERSION_MAJOR=1','/DKMDF_VERSION_MINOR=31','/DPOOL_NX_OPTIN=1','/DDEPRECATE_DDK_FUNCTIONS=1')
foreach ($path in $include) { $compileArgs += "/I$path" }
$compileArgs += @("/Fo$Output\driver.obj", "$PSScriptRoot\driver.cpp")
& $cl @compileArgs
if ($LASTEXITCODE) { throw "Driver compile failed: $LASTEXITCODE" }
$lib = "$wdk\Lib\10.0.28000.0\km\x64"
& $link /nologo /driver /subsystem:native,10.00 /entry:FxDriverEntry /nodefaultlib /machine:x64 /integritycheck /dynamicbase /nxcompat /opt:ref /opt:icf "/out:$Output\SoltexAudio.sys" "$Output\driver.obj" "$lib\ntoskrnl.lib" "$lib\hal.lib" "$lib\BufferOverflowFastFailK.lib" "$lib\wmilib.lib" "$lib\wdmsec.lib" "$wdk\Lib\wdf\kmdf\x64\1.31\WdfDriverEntry.lib" "$wdk\Lib\wdf\kmdf\x64\1.31\WdfLdr.lib" "$lib\acx\km\1.1\AcxStub.lib"
if ($LASTEXITCODE) { throw "Driver link failed: $LASTEXITCODE" }
Copy-Item -LiteralPath "$PSScriptRoot\SoltexAudio.inf" -Destination "$Output\SoltexAudio.inf"
& "$wdk\tools\10.0.28000.0\x64\infverif.exe" /w "$Output\SoltexAudio.inf"
if ($LASTEXITCODE) { throw "INF verification failed: $LASTEXITCODE" }
$sdkLib = Join-Path $KitRoot 'microsoft.windows.sdk.cpp.x64\c'
& $cl /nologo /std:c++17 /EHsc /W4 /WX /O2 "/I$MsvcRoot\include" "/I$sdk\Include\10.0.28000.0\ucrt" "/I$sdk\Include\10.0.28000.0\shared" "/I$sdk\Include\10.0.28000.0\um" "$PSScriptRoot\tests\transport_tests.cpp" "/Fo$Output\transport_tests.obj" "/Fe$Output\transport_tests.exe" /link "/libpath:$MsvcRoot\lib\x64" "/libpath:$sdkLib\ucrt\x64" "/libpath:$sdkLib\um\x64"
if ($LASTEXITCODE) { throw "Native transport test compile failed: $LASTEXITCODE" }
& "$Output\transport_tests.exe"
if ($LASTEXITCODE) { throw "Native transport tests failed: $LASTEXITCODE" }
# The receipt records versions and file hashes only, no local paths.
$receipt = [ordered]@{ formatVersion=1; endpointCount=6; installed=$false; signed=$false; runtimeVerified=$false; compiler="MSVC $(Split-Path -Leaf $MsvcRoot)"; kitVersion=$lock.version; acx='1.1'; kmdf='1.31'; infVerif='passed /w'; portableCore='passed'; artifacts=@() }
foreach ($file in @('SoltexAudio.sys','SoltexAudio.inf')) { $receipt.artifacts += @{file=$file;sha256=(Get-FileHash -LiteralPath "$Output\$file" -Algorithm SHA256).Hash} }
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$Output\build-receipt.json"
Write-Output "Compiled unsigned prototype: $Output\SoltexAudio.sys (not installed, not runtime validated)."
