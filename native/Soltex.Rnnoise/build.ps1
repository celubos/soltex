param(
    # LLVM-MinGW folder (the one that holds bin\clang.exe). When empty: $env:SOLTEX_LLVM_MINGW, else
    # <CacheRoot>\<folder named in native/Soltex.TaskbarStyle/toolchain.lock.json>.
    [string]$Toolchain,
    # Where -Acquire puts the pinned toolchain. Default: <repo>\.tools.
    [string]$CacheRoot,
    # Download the pinned LLVM-MinGW release into CacheRoot when it is not there yet. The archive's size and SHA-256
    # must match the lock file.
    [switch]$Acquire,
    [string]$Output = (Join-Path $PSScriptRoot '..\..\artifacts\native\rnnoise')
)
# Builds soltex-rnnoise.dll (x64) from the unmodified RNNoise 0.2 sources in native/third_party/rnnoise with the same
# pinned LLVM-MinGW release as the taskbar helper. The x86 run-time CPU dispatch is on: the library runs on any x64 CPU
# (SSE2) and switches to its SSE4.1 or AVX2 kernels where the CPU has them. Runs in Windows PowerShell 5.1 and
# PowerShell 7.
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

$repoRoot = Resolve-FullPath (Join-Path $PSScriptRoot '..\..')
# One toolchain pin for every native part of Soltex.
$lock = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'native\Soltex.TaskbarStyle\toolchain.lock.json') | ConvertFrom-Json
$package = @($lock.packages)[0]
if (-not $package -or -not $package.url -or -not $package.sha256 -or -not $package.bytes) { throw 'toolchain.lock.json has no complete package entry.' }
$folder = if ($package.folder) { [string]$package.folder } else { [IO.Path]::GetFileNameWithoutExtension([string]$package.file) }

if (-not $CacheRoot) { $CacheRoot = Join-Path $repoRoot '.tools' }
$CacheRoot = Resolve-FullPath $CacheRoot
$from = '-Toolchain'
if (-not $Toolchain) {
    if ($env:SOLTEX_LLVM_MINGW) { $Toolchain = $env:SOLTEX_LLVM_MINGW; $from = 'SOLTEX_LLVM_MINGW' }
    else { $Toolchain = Join-Path $CacheRoot $folder; $from = 'the cache' }
}
$Toolchain = Resolve-FullPath $Toolchain
$Output = Resolve-FullPath $Output

function Install-Toolchain {
    # The same steps as native/Soltex.TaskbarStyle/build.ps1: download the pinned archive, check it against the lock file,
    # and extract it next to the final folder first, so an interrupted run never leaves a half-extracted toolchain under
    # the name the build looks for.
    if (Test-Path -LiteralPath $Toolchain) {
        throw "$Toolchain exists but has no bin\clang.exe. Delete that folder and run again."
    }
    New-Item -ItemType Directory -Force -Path $CacheRoot | Out-Null
    $archive = Join-Path $CacheRoot ([string]$package.file + '.download')
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Write-Output "Downloading $($lock.release) from $($package.url)"
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }
    $progress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
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
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { $tar = 'tar' }
        Invoke-Native $tar @('-xf', $archive, '-C', $staging)
        $extracted = Join-Path $staging $folder
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'bin\clang.exe') -PathType Leaf)) {
            throw "$($package.file) does not contain $folder\bin\clang.exe."
        }
        Move-Item -LiteralPath $extracted -Destination $Toolchain
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    }
    Write-Output "Installed $($lock.release): $Toolchain"
}

$clang = Join-Path $Toolchain 'bin\clang.exe'
if (-not (Test-Path -LiteralPath $clang -PathType Leaf)) {
    if (-not $Acquire) {
        throw ("LLVM-MinGW $($lock.release) was not found ($clang is missing; toolchain from $from). Pass -Toolchain <llvm-mingw folder>, " +
            "set SOLTEX_LLVM_MINGW to that folder, or add -Acquire to download the pinned release into $CacheRoot.")
    }
    if ($from -ne 'the cache') {
        throw "-Acquire downloads into the cache ($CacheRoot), but the toolchain was set by $from to $Toolchain, which has no bin\clang.exe. Fix that path or leave it unset."
    }
    Install-Toolchain
}
$version = @(Invoke-Native $clang @('--version'))[0]
if ($version -notlike "*$($lock.clang)*") { throw "Expected clang $($lock.clang) from $($lock.release), found: $version" }

# The vendored sources must be exactly the files recorded in source.lock.json: same bytes, nothing added or removed.
$sourceLock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'source.lock.json') | ConvertFrom-Json
$sourceRoot = Resolve-FullPath (Join-Path $repoRoot ([string]$sourceLock.folder))
$expected = @{}
foreach ($file in @($sourceLock.files)) { $expected[[string]$file.path] = $file }
$present = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse -Force | ForEach-Object { $_.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/') })
foreach ($relative in $present) {
    if (-not $expected.ContainsKey($relative)) { throw "native/third_party/rnnoise holds a file that source.lock.json does not list: $relative" }
}
foreach ($relative in $expected.Keys) {
    $path = Join-Path $sourceRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Vendored RNNoise file is missing: $relative" }
    $entry = $expected[$relative]
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($item.Length -ne [int64]$entry.bytes -or $hash -ne [string]$entry.sha256) {
        throw "Vendored RNNoise file $relative is $($item.Length) bytes with SHA-256 $hash; source.lock.json records $($entry.bytes) bytes, $($entry.sha256). The upstream files must stay unmodified (check that Git did not convert line endings)."
    }
}

# Makefile.am RNNOISE_SOURCES plus the x86 run-time CPU dispatch (RNN_ENABLE_X86_RTCD, as configure --enable-x86-rtcd).
$baseSources = @('src/denoise.c', 'src/rnn.c', 'src/pitch.c', 'src/kiss_fft.c', 'src/celt_lpc.c', 'src/nnet.c', 'src/nnet_default.c',
    'src/parse_lpcnet_weights.c', 'src/rnnoise_data.c', 'src/rnnoise_tables.c', 'src/x86/x86_dnn_map.c', 'src/x86/x86cpu.c')
$kernels = @(
    @{ Source = 'src/x86/nnet_sse4_1.c'; Flags = @('-msse4.1') },
    @{ Source = 'src/x86/nnet_avx2.c'; Flags = @('-mavx', '-mfma', '-mavx2') }
)
# The defines configure would write to config.h for this build; no config.h is used. Upstream's own warning set.
$common = @('-O2', '-Wall', '-Wextra', '-Wno-sign-compare', '-Wno-parentheses', '-U__STRICT_ANSI__',
    '-DRNN_ENABLE_X86_RTCD', '-DCPU_INFO_BY_ASM', '-DDISABLE_DEBUG_FLOAT',
    '-I', (Join-Path $sourceRoot 'include'), '-I', (Join-Path $sourceRoot 'src'),
    # No local build path may end up in the binary.
    "-ffile-prefix-map=$sourceRoot=rnnoise")
$cc = Join-Path $Toolchain 'bin\x86_64-w64-mingw32-clang.exe'

$dir = Join-Path $Output 'x64'
$objects = Join-Path $Output 'obj'
foreach ($path in @($dir, $objects)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
New-Item -ItemType Directory -Force -Path $dir, $objects | Out-Null
$compiled = @()
$units = @($baseSources | ForEach-Object { @{ Source = $_; Flags = @() } }) + $kernels
foreach ($unit in $units) {
    $object = Join-Path $objects (([string]$unit.Source).Replace('/', '_').Replace('.c', '.o'))
    Invoke-Native $cc ($common + $unit.Flags + @('-c', (Join-Path $sourceRoot $unit.Source), '-o', $object))
    $compiled += $object
}
$dll = Join-Path $dir 'soltex-rnnoise.dll'
Invoke-Native $cc (@('-shared', '-static', '-o', $dll) + $compiled + @((Join-Path $PSScriptRoot 'soltex-rnnoise.def'),
    '-Wl,--dynamicbase', '-Wl,--nxcompat', '-Wl,--high-entropy-va', '-Wl,--no-insert-timestamp', '-s'))
Remove-Item -LiteralPath $objects -Recurse -Force

# The DLL may import only Windows itself (the C runtime through the UCRT API sets) and must export the whole RNNoise API.
$dump = @(Invoke-Native (Join-Path $Toolchain 'bin\llvm-objdump.exe') @('-p', $dll))
$imports = @($dump | Select-String -CaseSensitive -Pattern '^\s+DLL Name: (.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() })
if ($imports.Count -eq 0) { throw "$dll lists no imported DLLs; the import check cannot run." }
foreach ($import in $imports) {
    if ($import -ne 'KERNEL32.dll' -and $import -notlike 'api-ms-win-crt-*') { throw "$dll imports an unexpected DLL: $import" }
}
$definitions = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'soltex-rnnoise.def') | ForEach-Object { $_.Trim() } |
    Where-Object { $_ -cmatch '^[A-Za-z_][A-Za-z0-9_]*$' -and $_ -cne 'EXPORTS' })
$exported = @($dump | Select-String -Pattern '^\s+\d+\s+0x[0-9a-fA-F]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*$' | ForEach-Object { $_.Matches[0].Groups[1].Value })
foreach ($name in $definitions) {
    if ($exported -notcontains $name) { throw "$dll does not export $name." }
}
if ($exported.Count -ne $definitions.Count) { throw "$dll exports $($exported.Count) names; soltex-rnnoise.def lists $($definitions.Count)." }

# RNNoise's BSD licence and author list, and the notices for the runtime pieces linked in statically (LLVM compiler-rt,
# mingw-w64 CRT startup), as the taskbar helper ships them.
$licenses = Join-Path $Output 'licenses'
if (Test-Path -LiteralPath $licenses) { Remove-Item -LiteralPath $licenses -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $licenses 'rnnoise'), (Join-Path $licenses 'llvm-mingw-runtime') | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceRoot 'COPYING') -Destination (Join-Path $licenses 'rnnoise\COPYING')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'AUTHORS') -Destination (Join-Path $licenses 'rnnoise\AUTHORS')
Copy-Item -LiteralPath (Join-Path $Toolchain 'LICENSE.TXT') -Destination (Join-Path $licenses 'llvm-mingw-runtime\LLVM-LICENSE.TXT')
Copy-Item -LiteralPath (Join-Path $Toolchain 'x86_64-w64-mingw32\share\mingw32\COPYING.MinGW-w64-runtime.txt') -Destination (Join-Path $licenses 'llvm-mingw-runtime')

$receipt = [ordered]@{
    formatVersion = 1; toolchain = $lock.release; clang = $lock.clang
    source = [ordered]@{ name = $sourceLock.name; version = $sourceLock.version; archive = $sourceLock.release.file; sha256 = $sourceLock.release.sha256 }
    artifacts = @([ordered]@{ file = 'x64/soltex-rnnoise.dll'; bytes = (Get-Item -LiteralPath $dll).Length; sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash; imports = @($imports); exports = @($exported) })
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'build-receipt.json') -Encoding utf8
Write-Output "Built RNNoise $($sourceLock.version): $dll"
