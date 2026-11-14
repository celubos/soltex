param(
    # LLVM-MinGW folder (the one that holds bin\clang++.exe). When empty: $env:SOLTEX_LLVM_MINGW, else
    # <CacheRoot>\<folder named in toolchain.lock.json>.
    [string]$Toolchain,
    # Where -Acquire puts the pinned toolchain. Default: <repo>\.tools.
    [string]$CacheRoot,
    # Download the pinned LLVM-MinGW release into CacheRoot when it is not there yet. The archive's size and SHA-256
    # must match toolchain.lock.json.
    [switch]$Acquire,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\native\taskbar')
)
# Builds the taskbar look helper (x64 and ARM64) and its x64 XAML test host with the LLVM-MinGW release pinned in
# toolchain.lock.json. Runs in Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    # Relative paths are relative to the current PowerShell location (not the process directory).
    return [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Invoke-Native([string]$Command, [string[]]$Arguments) {
    # Native tools report failure through their exit code. Windows PowerShell 5.1 turns a tool's stderr into error records
    # when the script's output is redirected, which 'Stop' would make fatal, so this checks the exit code instead.
    if (-not (Get-Command -Name $Command -CommandType Application -ErrorAction SilentlyContinue)) { throw "Tool not found: $Command" }
    $ErrorActionPreference = 'Continue'
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $Command) failed ($LASTEXITCODE)" }
}

$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') | ConvertFrom-Json
$package = @($lock.packages)[0]
if (-not $package -or -not $package.url -or -not $package.sha256 -or -not $package.bytes) { throw 'toolchain.lock.json has no complete package entry.' }
$folder = if ($package.folder) { [string]$package.folder } else { [IO.Path]::GetFileNameWithoutExtension([string]$package.file) }

if (-not $CacheRoot) { $CacheRoot = Join-Path $PSScriptRoot '..\..\.tools' }
$CacheRoot = Resolve-FullPath $CacheRoot
$from = '-Toolchain'
if (-not $Toolchain) {
    if ($env:SOLTEX_LLVM_MINGW) { $Toolchain = $env:SOLTEX_LLVM_MINGW; $from = 'SOLTEX_LLVM_MINGW' }
    else { $Toolchain = Join-Path $CacheRoot $folder; $from = 'the cache' }
}
$Toolchain = Resolve-FullPath $Toolchain
$Output = Resolve-FullPath $Output

function Install-Toolchain {
    # Downloads the pinned archive, checks it against the lock file, and extracts it next to the final folder first, so an
    # interrupted run never leaves a half-extracted toolchain under the name the build looks for.
    if (Test-Path -LiteralPath $Toolchain) {
        throw "$Toolchain exists but has no bin\clang++.exe. Delete that folder and run again."
    }
    New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
    $archive = Join-Path $CacheRoot ([string]$package.file + '.download')
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Write-Output "Downloading $($lock.release) from $($package.url)"
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $progress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue' # the progress display slows Windows PowerShell's download a lot
    try { Invoke-WebRequest -Uri ([string]$package.url) -OutFile $archive -UseBasicParsing }
    finally { $ProgressPreference = $progress }

    $bytes = (Get-Item -LiteralPath $archive).Length
    if ($bytes -ne [int64]$package.bytes) {
        Remove-Item -LiteralPath $archive -Force
        throw "The downloaded $($package.file) is $bytes bytes; toolchain.lock.json expects $($package.bytes). The file was deleted."
    }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($hash -ne [string]$package.sha256) {
        Remove-Item -LiteralPath $archive -Force
        throw "The downloaded $($package.file) has SHA-256 $hash; toolchain.lock.json expects $($package.sha256). The file was deleted."
    }

    $staging = Join-Path $CacheRoot ('.x-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $staging | Out-Null
    try {
        # Windows' own bsdtar reads zip archives; a GNU tar earlier on PATH (for example Git's) does not.
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { $tar = 'tar' }
        Invoke-Native $tar @('-xf', $archive, '-C', $staging)
        $extracted = Join-Path $staging $folder
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'bin\clang++.exe') -PathType Leaf)) {
            throw "$($package.file) does not contain $folder\bin\clang++.exe."
        }
        Move-Item -LiteralPath $extracted -Destination $Toolchain
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    }
    Write-Output "Installed $($lock.release): $Toolchain"
}

$clang = Join-Path $Toolchain 'bin\clang++.exe'
if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) {
    if (-not $Acquire) {
        throw ("LLVM-MinGW $($lock.release) was not found ($clang is missing; toolchain from $from). Pass -Toolchain <llvm-mingw folder>, " +
            "set SOLTEX_LLVM_MINGW to that folder, or add -Acquire to download the pinned release into $CacheRoot.")
    }
    if ($from -ne 'the cache') {
        throw "-Acquire downloads into the cache ($CacheRoot), but the toolchain was set by $from to $Toolchain, which has no bin\clang++.exe. Fix that path or leave it unset."
    }
    Install-Toolchain
}

$version = @(Invoke-Native $clang @('--version'))[0]
if ($version -notlike "*$($lock.clang)*") { throw "Expected clang $($lock.clang) from $($lock.release), found: $version" }

$common = @('-std=c++20', '-O2', '-Wall', '-Wextra', '-Werror', '-fno-exceptions', '-fno-rtti', '-static',
    '-Wl,--dynamicbase', '-Wl,--nxcompat', '-Wl,--high-entropy-va', '-Wl,--no-insert-timestamp', '-s')
$allowedImports = @('KERNEL32.dll', 'ADVAPI32.dll', 'ole32.dll', 'OLEAUT32.dll', 'USER32.dll', 'GDI32.dll', 'api-ms-win-core-winrt-l1-1-0.dll',
    'api-ms-win-core-winrt-string-l1-1-0.dll') + @('api-ms-win-crt-*')

function Invoke-Tool([string]$Tool, [string[]]$Arguments) {
    Invoke-Native (Join-Path $Toolchain "bin\$Tool") $Arguments
}

function Assert-Imports([string]$File) {
    $dump = Invoke-Native (Join-Path $Toolchain 'bin\llvm-objdump.exe') @('-p', $File)
    $imports = @($dump | Select-String -CaseSensitive -Pattern '^\s+DLL Name: (.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() })
    if ($imports.Count -eq 0) { throw "$File lists no imported DLLs; the import check cannot run." }
    foreach ($import in $imports) {
        if (-not ($allowedImports | Where-Object { $import -like $_ })) { throw "$File imports an unexpected DLL: $import" }
    }
    return $imports
}

$receipt = [ordered]@{ formatVersion = 1; toolchain = $lock.release; clang = $lock.clang; artifacts = @() }
foreach ($arch in @(@{ Name = 'x64'; Triple = 'x86_64-w64-mingw32' }, @{ Name = 'arm64'; Triple = 'aarch64-w64-mingw32' })) {
    $dir = Join-Path $Output $arch.Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $dll = Join-Path $dir 'SoltexTaskbar.dll'
    Invoke-Tool "$($arch.Triple)-clang++.exe" ($common + @('-shared', '-o', $dll, (Join-Path $PSScriptRoot 'SoltexTaskbar.cpp'),
        (Join-Path $PSScriptRoot 'SoltexTaskbar.def'), '-lruntimeobject', '-lole32', '-loleaut32', '-ladvapi32', '-luser32'))
    $imports = Assert-Imports $dll
    $receipt.artifacts += [ordered]@{ file = "$($arch.Name)/SoltexTaskbar.dll"; bytes = (Get-Item -LiteralPath $dll).Length; sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash; imports = @($imports) }
}

$tests = Join-Path $Output 'test'
New-Item -ItemType Directory -Force -Path $tests | Out-Null
$res = Join-Path $tests 'TaskbarTestHost.res'
Invoke-Tool 'x86_64-w64-mingw32-windres.exe' @('--include-dir', (Join-Path $PSScriptRoot 'tests'), '-O', 'coff', '-o', $res, (Join-Path $PSScriptRoot 'tests\TaskbarTestHost.rc'))
$hostExe = Join-Path $tests 'TaskbarTestHost.exe'
Invoke-Tool 'x86_64-w64-mingw32-clang++.exe' ($common + @('-municode', '-o', $hostExe, (Join-Path $PSScriptRoot 'tests\TaskbarTestHost.cpp'), $res,
    '-lruntimeobject', '-lole32', '-luser32', '-lgdi32'))
Assert-Imports $hostExe | Out-Null
$receipt.artifacts += [ordered]@{ file = 'test/TaskbarTestHost.exe'; bytes = (Get-Item -LiteralPath $hostExe).Length; sha256 = (Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash }
# The helper statically links small runtime pieces (LLVM compiler-rt, mingw-w64 CRT startup); ship their notices.
$licenses = Join-Path $Output 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $Toolchain 'LICENSE.TXT') -Destination (Join-Path $licenses 'LLVM-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $Toolchain 'x86_64-w64-mingw32\share\mingw32\COPYING.MinGW-w64-runtime.txt') -Destination $licenses
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'build-receipt.json') -Encoding utf8
Write-Output "Built taskbar helper: $Output"
