using System.Runtime.InteropServices;

namespace Soltex.Audio.Engine;

/// <summary>
/// Binding to soltex-rnnoise.dll (Xiph RNNoise 0.2, built from native/Soltex.Rnnoise). The DLL is loaded by absolute path
/// from the directory of the engine assembly, never through PATH or the default DLL search. Entry points are unmanaged
/// function pointers, so a call marshals and allocates nothing. Loading, the export checks and the delay probe run once,
/// on the first control-thread call that needs the library.
/// </summary>
internal sealed unsafe class RnnoiseLibrary
{
    internal const string FileName = "soltex-rnnoise.dll";
    /// <summary>RNNoise works on 10 ms frames of 48 kHz mono audio.</summary>
    internal const int FrameSize = 480;
    internal const int ModelRate = 48000;
    /// <summary>RNNoise expects 16-bit sample values in float: full scale is ±32768.</summary>
    internal const float SampleScale = 32768f;

    private static readonly Lazy<(RnnoiseLibrary? Library, string? Failure)> SharedLibrary =
        new(() => (Load(DefaultDirectory(), out string? failure), failure), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly delegate* unmanaged<nint, nint> create;
    private readonly delegate* unmanaged<nint, void> destroy;
    private readonly delegate* unmanaged<nint, float*, float*, float> process;

    /// <summary>Bytes of one DenoiseState (copied when channels that shared one input start to differ).</summary>
    internal int StateBytes { get; }
    /// <summary>Measured delay from a frame's input to the same audio in the model's output stream (960 for RNNoise 0.2).</summary>
    internal int ModelDelayFrames { get; }
    /// <summary>Kernel set the library's CPU dispatch chose: 0 SSE2, 1 SSE4.1, 2 AVX2.</summary>
    internal int CpuLevel { get; }

    /// <summary>The library next to the engine assembly, or null when it is missing or failed its checks.</summary>
    internal static RnnoiseLibrary? Shared => SharedLibrary.Value.Library;
    /// <summary>Why <see cref="Shared"/> is null (diagnostics only).</summary>
    internal static string? SharedFailure => SharedLibrary.Value.Failure;

    private RnnoiseLibrary(nint module, int stateBytes)
    {
        create = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(module, "rnnoise_create");
        destroy = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(module, "rnnoise_destroy");
        process = (delegate* unmanaged<nint, float*, float*, float>)NativeLibrary.GetExport(module, "rnnoise_process_frame");
        CpuLevel = ((delegate* unmanaged<int>)NativeLibrary.GetExport(module, "rnn_select_arch"))();
        StateBytes = stateBytes;
        ModelDelayFrames = MeasureDelay();
    }

    private static string DefaultDirectory()
    {
        // Single-file hosts have no assembly location; the engine's files then sit in the application directory.
        string location = typeof(RnnoiseLibrary).Assembly.Location;
        return string.IsNullOrEmpty(location) ? AppContext.BaseDirectory : Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
    }

    /// <summary>Loads the library from <paramref name="directory"/>; null with a reason when it is absent, not x64 or fails a check.</summary>
    internal static RnnoiseLibrary? Load(string directory, out string? failure)
    {
        nint module = 0;
        RnnoiseLibrary? library = null;
        try
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) { failure = "The RNNoise library is built for x64 processes only."; return null; }
            string path = Path.Combine(Path.GetFullPath(directory), FileName);
            if (!File.Exists(path)) { failure = $"{FileName} is not next to the audio engine."; return null; }
            if (!NativeLibrary.TryLoad(path, out module)) { failure = $"{FileName} could not be loaded."; return null; }
            foreach (string export in (string[])["rnnoise_get_size", "rnnoise_get_frame_size", "rnnoise_create", "rnnoise_destroy", "rnnoise_init", "rnnoise_process_frame", "rnn_select_arch"])
            {
                if (!NativeLibrary.TryGetExport(module, export, out _)) { failure = $"{FileName} does not export {export}."; return null; }
            }
            int frame = ((delegate* unmanaged<int>)NativeLibrary.GetExport(module, "rnnoise_get_frame_size"))();
            int size = ((delegate* unmanaged<int>)NativeLibrary.GetExport(module, "rnnoise_get_size"))();
            if (frame != FrameSize || size <= 0) { failure = $"{FileName} reports {frame}-sample frames; {FrameSize} are required."; return null; }
            var candidate = new RnnoiseLibrary(module, size);
            if (candidate.ModelDelayFrames < 0) { failure = $"{FileName} failed its delay probe."; return null; }
            failure = null;
            library = candidate;
            return library;
        }
        catch (Exception error) when (error is BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            failure = $"{FileName} could not be loaded: {error.Message}";
            return null;
        }
        finally
        {
            // A library that failed a check is unloaded; one that passed stays loaded for the life of the process.
            if (library is null && module != 0) NativeLibrary.Free(module);
        }
    }

    /// <summary>Allocates and initializes one denoiser state with the built-in model; 0 when out of memory.</summary>
    internal nint Create() => create(0);
    internal void Destroy(nint state) => destroy(state);
    /// <summary>Denoises one 480-sample frame (values scaled to ±32768). Returns the voice probability.</summary>
    internal float ProcessFrame(nint state, float* output, float* input) => process(state, output, input);

    /// <summary>
    /// Measures the model's delay instead of assuming it. Very quiet input (peaks at 5e-7 of full scale) takes RNNoise's
    /// "no audio" path, where the previous frame's spectrum is resynthesized without gains: the output is the input,
    /// high-passed below about 30 Hz and delayed by exactly the model's delay. The lag of the cross-correlation peak is
    /// that delay. Returns -1 when the library does not behave like that.
    /// </summary>
    private int MeasureDelay()
    {
        const int frames = 12, maximumLag = 4 * FrameSize;
        nint state = Create();
        if (state == 0) return -1;
        try
        {
            var input = new float[frames * FrameSize]; var output = new float[frames * FrameSize];
            uint seed = 0x2545F491;
            for (int i = 0; i < input.Length; i++)
            {
                seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5;
                input[i] = (seed / (float)uint.MaxValue - 0.5f) * 0.03f;
            }
            fixed (float* source = input, destination = output)
                for (int f = 0; f < frames; f++) ProcessFrame(state, destination + f * FrameSize, source + f * FrameSize);
            int best = -1; double bestCorrelation = 0;
            for (int lag = 0; lag <= maximumLag; lag++)
            {
                double cross = 0, inputEnergy = 0, outputEnergy = 0;
                for (int i = maximumLag; i < output.Length; i++)
                {
                    cross += output[i] * (double)input[i - lag]; inputEnergy += input[i - lag] * (double)input[i - lag]; outputEnergy += output[i] * (double)output[i];
                }
                double correlation = cross / Math.Sqrt(Math.Max(1e-30, inputEnergy * outputEnergy));
                if (correlation > bestCorrelation) { bestCorrelation = correlation; best = lag; }
            }
            return bestCorrelation > 0.99 ? best : -1;
        }
        finally { Destroy(state); }
    }
}
