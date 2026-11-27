# RNNoise library

`soltex-rnnoise.dll` is Xiph's RNNoise 0.2 (a recurrent neural network that separates speech from noise, by Jean-Marc
Valin and contributors, BSD 3-Clause) built as an x64 DLL for the audio engine's neural noise removal
(`NeuralNoiseRemovalSettings`, see `src/Soltex.Audio.Engine/README.md`). It is not Sonar's noise cancellation and not
Soltex code.

## Sources

`native/third_party/rnnoise` holds files from the official `rnnoise-0.2.tar.gz` release, unmodified: the library sources
listed in its `Makefile.am` (`RNNOISE_SOURCES`, including the built-in model in `src/rnnoise_data.c`) with their headers,
the x86 run-time CPU dispatch (`src/x86`: CPU detection, the dispatch table and the SSE4.1 and AVX2 kernels),
`include/rnnoise.h`, `COPYING`, `AUTHORS` and `README`. Training tools, examples and the autotools scripts are not
kept, and nothing from the archive is ever run.

`source.lock.json` records the archive's SHA-256 and size and, for every kept file, its size and SHA-256. `build.ps1`
refuses to build when a file differs, is missing or a file it does not list appears in the folder. The repository's
`.gitattributes` keeps Git from converting their line endings, so a checkout has exactly the released bytes.

The only Soltex files are in this folder: `build.ps1`, `soltex-rnnoise.def` (the exports) and `source.lock.json`. There
is no `config.h`; the build passes the defines `configure --enable-x86-rtcd` would set (`RNN_ENABLE_X86_RTCD`,
`CPU_INFO_BY_ASM`, `DISABLE_DEBUG_FLOAT`).

## Build

```powershell
.\native\Soltex.Rnnoise\build.ps1            # toolchain from -Toolchain, SOLTEX_LLVM_MINGW or .tools
.\native\Soltex.Rnnoise\build.ps1 -Acquire   # first time: download the pinned LLVM-MinGW into .tools
```

It uses the LLVM-MinGW release pinned in `native/Soltex.TaskbarStyle/toolchain.lock.json` (one pin for all native
parts) and finds it the same way the taskbar helper's script does. Each source is compiled with `-O2` for the x64
baseline (SSE2); `nnet_sse4_1.c` is compiled with `-msse4.1` and `nnet_avx2.c` with `-mavx -mfma -mavx2`. At run time
RNNoise picks the best kernel set the CPU has, so the DLL runs on any x64 CPU. The runtime is linked statically
(compiler-rt, mingw-w64 CRT startup) as for the taskbar helper; the C library comes from Windows (UCRT). The build
writes `artifacts/native/rnnoise/x64/soltex-rnnoise.dll`, `licenses/rnnoise/{COPYING,AUTHORS}`,
`licenses/llvm-mingw-runtime/` and `build-receipt.json`, and fails unless the DLL imports only `KERNEL32.dll` and the
`api-ms-win-crt-*` API sets and exports exactly the names in `soltex-rnnoise.def`: the `rnnoise_*` API plus
`rnn_select_arch` (the kernel set chosen: 0 SSE2, 1 SSE4.1, 2 AVX2). Builds are byte-for-byte reproducible (no time
stamp, no local paths).

`src/Soltex.Audio.Engine/Soltex.Audio.Engine.csproj` copies the DLL next to `Soltex.Audio.Engine.Core.dll` when it
exists, so it reaches every application, host, tool and test output that contains the engine. The engine loads it only
by full path from its own folder. `eng/build-audio.ps1`, `eng/verify-integrated.ps1`, `eng/verify.ps1` and
`eng/publish-release.ps1` build it before the .NET projects, and the package scripts check that every folder holding
the engine also holds this exact DLL and that the licence files are in `licenses/`.

## Updating RNNoise

Replace the files in `native/third_party/rnnoise` with the same set from the new release archive (verify the archive's
published checksum first), update `source.lock.json` (archive and per-file hashes), then run the engine suite: the
load-time probe measures the model delay again, and `Soltex.Audio.Engine.Tests` checks it, the reductions and the
documented numbers.
