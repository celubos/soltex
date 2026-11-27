[CmdletBinding()]
param(
    [switch]$RunEicar,
    # Download the pinned LLVM-MinGW toolchain for the native parts (taskbar helper, RNNoise) when it is missing.
    [switch]$Acquire
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'verify-identity.ps1')

# The App suite drives the native taskbar helper, and the app build copies it only when it exists.
$nativeArguments = @{}
if ($Acquire) { $nativeArguments.Acquire = $true }
& (Join-Path $repoRoot 'native\Soltex.TaskbarStyle\build.ps1') @nativeArguments
# The engine build copies RNNoise next to Soltex.Audio.Engine.Core.dll only when it exists.
& (Join-Path $repoRoot 'native\Soltex.Rnnoise\build.ps1') @nativeArguments

$dotnet = Join-Path $repoRoot '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet build (Join-Path $repoRoot 'Soltex.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($RunEicar) {
    $env:SOLTEX_RUN_EICAR = '1'
}

try {
    & $dotnet run --project (Join-Path $repoRoot 'tests\Soltex.Security.Tests\Soltex.Security.Tests.csproj') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnet run --project (Join-Path $repoRoot 'tests\Soltex.Monitoring.Tests\Soltex.Monitoring.Tests.csproj') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $dotnet run --project (Join-Path $repoRoot 'tests\Soltex.App.Tests\Soltex.App.Tests.csproj') --configuration Release --no-build
    exit $LASTEXITCODE
}
finally {
    Remove-Item Env:SOLTEX_RUN_EICAR -ErrorAction SilentlyContinue
}
