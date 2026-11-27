using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Soltex.Audio.Engine;

/// <summary>
/// Xiph RNNoise 0.2 on every channel of a processor. RNNoise runs on 480-sample frames at 48 kHz; at other device rates
/// each channel is converted to 48 kHz and back with fixed-ratio Kaiser-windowed sinc filters (the kernels of
/// <see cref="AdaptiveResamplingSampleProvider"/>), whose positions are exact integer fractions so they never drift.
/// The whole path is a whole number of device frames long, <see cref="LatencyFrames"/>: the minimum, found by walking
/// one period of the frame and filter timing, at which every output sample is ready when it is due whatever the block
/// size. The dry signal is delayed by the same count and mixed back at 10^(-MaximumReductionDb/20) so a cap limits how
/// far anything is pulled down.
/// <para>
/// Threading: created on a control thread (native states, buffers and kernels are allocated there); <see cref="Reset"/>,
/// <see cref="Configure"/> and <see cref="Process"/> run on the audio thread and allocate nothing, take no locks and do
/// no I/O. The native states belong to a <see cref="SafeHandle"/>: the audio thread holds a reference for each block
/// (<see cref="TryEnter"/>), so <see cref="Dispose"/> on another thread frees them only once no block is running.
/// </para>
/// </summary>
internal sealed unsafe class NeuralNoiseRemover : IDisposable
{
    private const int Frame = RnnoiseLibrary.FrameSize;
    private const float Scale = RnnoiseLibrary.SampleScale, InverseScale = 1 / RnnoiseLibrary.SampleScale;
    // Converter kernels for every phase a fixed ratio visits, when there are few enough of them (all common rates).
    private const int MaximumPhaseTable = 1 << 17;
    private static readonly ConcurrentDictionary<(int Rate, int ModelDelay), (int Delay, int Lag)> Plans = new();
    private static readonly ConcurrentDictionary<(int Width, long CutoffBits, long Phases), double[]> PhaseTables = new();
    private readonly RnnoiseLibrary library;
    private readonly StateHandle states;
    private readonly int channels;
    // Device rate : 48 kHz = deviceUnits : modelUnits (reduced). A 48 kHz sample is deviceUnits/modelUnits device samples.
    private readonly long deviceUnits, modelUnits;
    private readonly bool direct;
    private readonly int widthIn, widthOut, halfIn, halfOut;
    private readonly float[]? tableIn, tableOut;
    private readonly double[]? phasesIn, phasesOut;
    private readonly double[] kernelIn, kernelOut;
    private readonly double rowScaleIn, rowScaleOut;
    private readonly int ringIn, ringOut, ringDry;
    // Per channel: device-rate input history (mirrored ring), the frame being filled and the model's output frame (both
    // scaled to ±32768), the model's output stream at 48 kHz (mirrored ring) and the dry delay line.
    private readonly float[][] historyIn, frameIn, frameOut, historyOut, dry;
    private readonly long firstOutputIndex, firstOutputPhase;
    private readonly double mixCoefficient;
    // Shared timing (all channels move in lockstep).
    private long inputIndex, inputPhase, written, outputIndex, outputPhase;
    private int fill;
    private double mix, mixTarget;
    private bool shared;

    /// <summary>Device frames from an input sample to the same sample in the output (wet and dry alike).</summary>
    internal int LatencyFrames { get; }

    private NeuralNoiseRemover(RnnoiseLibrary library, StateHandle states, int sampleRate, int channels)
    {
        this.library = library; this.states = states; this.channels = channels;
        int divisor = (int)BigInteger.GreatestCommonDivisor(sampleRate, RnnoiseLibrary.ModelRate);
        deviceUnits = sampleRate / divisor; modelUnits = RnnoiseLibrary.ModelRate / divisor;
        direct = sampleRate == RnnoiseLibrary.ModelRate;
        if (direct) { widthIn = widthOut = 1; }
        else
        {
            // Into the model: device samples per 48 kHz sample. Out of it: 48 kHz samples per device sample.
            // A 48 kHz sample falls at one of modelUnits phases between device samples; an output sample at one of deviceUnits.
            double cutoffIn, cutoffOut;
            (widthIn, cutoffIn, tableIn) = Kernel((double)sampleRate / RnnoiseLibrary.ModelRate);
            (widthOut, cutoffOut, tableOut) = Kernel((double)RnnoiseLibrary.ModelRate / sampleRate);
            halfIn = widthIn / 2; halfOut = widthOut / 2;
            phasesIn = PhaseTable(tableIn, widthIn, cutoffIn, modelUnits);
            phasesOut = PhaseTable(tableOut, widthOut, cutoffOut, deviceUnits);
        }
        kernelIn = new double[widthIn]; kernelOut = new double[widthOut];
        rowScaleIn = (double)SincKernels.Phases / modelUnits; rowScaleOut = (double)SincKernels.Phases / deviceUnits;
        int modelDelay = library.ModelDelayFrames;
        var (delay, lag) = Plans.GetOrAdd((sampleRate, modelDelay), key => Plan(key.Rate, deviceUnits, modelUnits, halfIn, halfOut, key.ModelDelay));
        LatencyFrames = delay;
        ringIn = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, widthIn));
        ringOut = (int)BitOperations.RoundUpToPowerOf2((uint)(widthOut + lag + 1));
        ringDry = (int)BitOperations.RoundUpToPowerOf2((uint)(delay + 1));
        historyIn = Buffers(2 * ringIn); frameIn = Buffers(Frame); frameOut = Buffers(Frame); historyOut = Buffers(2 * ringOut); dry = Buffers(ringDry);
        // The first output sample reads the model stream at ((0 - delay + halfIn) * modelUnits + (modelDelay + halfOut) * deviceUnits) / deviceUnits.
        long start = (-delay + (long)halfIn) * modelUnits + (modelDelay + (long)halfOut) * deviceUnits;
        firstOutputIndex = FloorDivide(start, deviceUnits); firstOutputPhase = start - firstOutputIndex * deviceUnits;
        mixCoefficient = Math.Exp(-1 / (0.015 * sampleRate)); // the processor's 15 ms parameter glide
        outputIndex = firstOutputIndex; outputPhase = firstOutputPhase;
    }

    /// <summary>Control thread: one native state per channel plus every buffer. Null when native memory runs out.</summary>
    internal static NeuralNoiseRemover? TryCreate(RnnoiseLibrary library, int sampleRate, int channels)
    {
        var states = StateHandle.Allocate(library, channels);
        if (states is null) return null;
        try { return new NeuralNoiseRemover(library, states, sampleRate, channels); }
        catch { states.Dispose(); throw; }
    }

    /// <summary>Dry weight for a cap: 100 dB means no cap (the model's own output).</summary>
    internal static double DryWeight(NeuralNoiseRemovalSettings settings) =>
        settings.MaximumReductionDb >= 100 ? 0 : Math.Pow(10, -settings.MaximumReductionDb / 20);

    /// <summary>Audio thread (or before the first block): back to a fresh state, as if just created. Allocation free.</summary>
    internal void Reset(NeuralNoiseRemovalSettings settings, bool sharedInput)
    {
        // A copy of a freshly created state (taken on the control thread) is cheaper than rnnoise_init and allocates nothing.
        fixed (byte* pristine = states.Pristine)
            foreach (nint state in states.States) Buffer.MemoryCopy(pristine, (void*)state, library.StateBytes, library.StateBytes);
        for (int c = 0; c < channels; c++)
        {
            Array.Clear(historyIn[c]); Array.Clear(frameIn[c]); Array.Clear(frameOut[c]); Array.Clear(historyOut[c]); Array.Clear(dry[c]);
        }
        inputIndex = 0; inputPhase = 0; written = 0; fill = 0;
        outputIndex = firstOutputIndex; outputPhase = firstOutputPhase;
        mixTarget = mix = DryWeight(settings);
        shared = sharedInput && channels > 1;
    }

    /// <summary>
    /// Audio thread: a settings change that keeps the stage running. The cap glides (15 ms); everything buffered stays.
    /// When channels stop sharing one input, the first channel's complete state (native and buffered) is copied to the
    /// others: they carried the same samples, so that is exactly the state they would have had.
    /// </summary>
    internal void Configure(NeuralNoiseRemovalSettings settings, bool sharedInput)
    {
        mixTarget = DryWeight(settings);
        bool next = sharedInput && channels > 1;
        if (shared && !next)
        {
            // DenoiseState is a plain struct whose only pointers refer to the library's static model weights.
            var native = states.States;
            for (int c = 1; c < channels; c++)
            {
                Buffer.MemoryCopy((void*)native[0], (void*)native[c], library.StateBytes, library.StateBytes);
                historyIn[0].CopyTo(historyIn[c], 0); frameIn[0].CopyTo(frameIn[c], 0); historyOut[0].CopyTo(historyOut[c], 0); dry[0].CopyTo(dry[c], 0);
            }
        }
        shared = next;
    }

    /// <summary>Audio thread: holds the native states for one block. False once disposed.</summary>
    internal bool TryEnter()
    {
        if (states.IsClosed) return false;
        bool added = false;
        try { states.DangerousAddRef(ref added); }
        catch (ObjectDisposedException) { return false; }
        return added;
    }

    internal void Exit() => states.DangerousRelease();

    /// <summary>Audio thread: one device frame (one sample per channel), replaced by the delayed, denoised frame.</summary>
    internal void Process(Span<double> samples)
    {
        int active = shared ? 1 : channels;
        long n = inputIndex;
        int inputSlot = (int)(n & (ringIn - 1)), drySlot = (int)(n & (ringDry - 1));
        for (int c = 0; c < active; c++)
        {
            float value = (float)samples[c];
            historyIn[c][inputSlot] = value; historyIn[c][inputSlot + ringIn] = value; dry[c][drySlot] = value;
        }
        // Every 48 kHz sample whose position falls on this device sample: n <= position < n + 1.
        while (inputPhase < modelUnits)
        {
            if (direct) for (int c = 0; c < active; c++) frameIn[c][fill] = (float)samples[c] * Scale;
            else
            {
                var kernel = KernelAt(phasesIn, tableIn!, widthIn, inputPhase, rowScaleIn, kernelIn);
                int newest = inputSlot + ringIn;
                for (int c = 0; c < active; c++)
                {
                    var history = historyIn[c]; double sum = 0;
                    for (int k = 0; k < kernel.Length; k++) sum += kernel[k] * history[newest - k];
                    frameIn[c][fill] = (float)(sum * Scale);
                }
            }
            if (++fill == Frame) RunModel(active);
            inputPhase += deviceUnits;
        }
        inputPhase -= modelUnits;
        inputIndex = n + 1;

        mix = Smooth(mix, mixTarget, mixCoefficient);
        var kernelAt = direct ? default : KernelAt(phasesOut, tableOut!, widthOut, outputPhase, rowScaleOut, kernelOut);
        int dryRead = (int)((n - LatencyFrames) & (ringDry - 1));
        for (int c = 0; c < active; c++)
        {
            double wet = 0;
            if (outputIndex >= 0)
            {
                var history = historyOut[c];
                int newest = (int)(outputIndex & (ringOut - 1)) + ringOut;
                if (direct) wet = history[newest];
                else for (int k = 0; k < kernelAt.Length; k++) wet += kernelAt[k] * history[newest - k];
            }
            // Exactly the wet signal at a weight of 0 and exactly the dry one at 1.
            samples[c] = (1 - mix) * wet + mix * dry[c][dryRead];
        }
        for (int c = active; c < channels; c++) samples[c] = samples[0];
        outputPhase += modelUnits;
        while (outputPhase >= deviceUnits) { outputPhase -= deviceUnits; outputIndex++; }
    }

    private void RunModel(int active)
    {
        var native = states.States;
        for (int c = 0; c < active; c++)
        {
            fixed (float* input = frameIn[c], output = frameOut[c]) library.ProcessFrame(native[c], output, input);
            var source = frameOut[c]; var history = historyOut[c];
            for (int p = 0; p < Frame; p++)
            {
                float value = source[p] * InverseScale;
                if (!float.IsFinite(value)) value = 0;
                int slot = (int)((written + p) & (ringOut - 1));
                history[slot] = value; history[slot + ringOut] = value;
            }
        }
        written += Frame; fill = 0;
    }

    public void Dispose() => states.Dispose();

    private float[][] Buffers(int length)
    {
        var buffers = new float[channels][];
        for (int c = 0; c < channels; c++) buffers[c] = new float[length];
        return buffers;
    }

    /// <summary>The same kernel rule as the adaptive converter: cutoff at the input Nyquist when raising the rate,
    /// −6 dB at 96 % of the output Nyquist when lowering it, 64 taps scaled with the ratio.</summary>
    private static (int Width, double Cutoff, float[] Table) Kernel(double step)
    {
        double cutoff = step <= 1 ? 0.5 : 0.48 / step;
        int width = (int)Math.Ceiling(SincKernels.BaseTaps * Math.Max(1, step) / 2) * 2;
        return (width, cutoff, SincKernels.Get(width, cutoff));
    }

    /// <summary>The interpolated kernel of each of <paramref name="phases"/> evenly spaced phases (the same values the
    /// per-sample interpolation gives, computed once and shared); null when there are too many.</summary>
    private static double[]? PhaseTable(float[] table, int width, double cutoff, long phases)
    {
        if (phases * width > MaximumPhaseTable) return null;
        return PhaseTables.GetOrAdd((width, BitConverter.DoubleToInt64Bits(cutoff), phases), _ =>
        {
            var rows = new double[phases * width]; var row = new double[width];
            for (long p = 0; p < phases; p++)
            {
                Interpolate(table, width, p * ((double)SincKernels.Phases / phases), row);
                row.CopyTo(rows, p * width);
            }
            return rows;
        });
    }

    private static ReadOnlySpan<double> KernelAt(double[]? rows, float[] table, int width, long phase, double rowScale, double[] scratch)
    {
        if (rows is not null) return rows.AsSpan((int)phase * width, width);
        Interpolate(table, width, phase * rowScale, scratch);
        return scratch;
    }

    private static void Interpolate(float[] table, int width, double phase, double[] kernel)
    {
        int row = (int)phase; double alpha = phase - row;
        int row0 = row * width, row1 = row0 + width;
        if (alpha == 0) for (int k = 0; k < width; k++) kernel[k] = table[row0 + k];
        else for (int k = 0; k < width; k++) kernel[k] = table[row0 + k] + alpha * (table[row1 + k] - table[row0 + k]);
    }

    /// <summary>
    /// Smallest whole delay D (device frames) at which the output never needs model output that does not exist yet, and
    /// the largest distance the output then trails the newest model sample. A device sample n is followed by every
    /// 48 kHz sample m with floor(m·deviceUnits/modelUnits) = n; frame f completes with sample 480f+479; the output at n
    /// reads the model stream at index floor(((n − D + halfIn)·modelUnits + (modelDelay + halfOut)·deviceUnits) / deviceUnits),
    /// which makes the end-to-end delay exactly D. The timing repeats every rate/gcd(rate, 100) device frames.
    /// </summary>
    private static (int Delay, int Lag) Plan(int rate, long deviceUnits, long modelUnits, int halfIn, int halfOut, int modelDelay)
    {
        long horizon = 2L * rate / (long)BigInteger.GreatestCommonDivisor(rate, 100) + 8 * Frame;
        long delay = long.MinValue;
        Walk((n, available) => delay = Math.Max(delay, FloorDivide((n + halfIn) * modelUnits + (modelDelay + halfOut - available) * deviceUnits, modelUnits) + 1));
        long lag = 0;
        Walk((n, available) =>
        {
            long index = FloorDivide((n - delay + halfIn) * modelUnits + (modelDelay + (long)halfOut) * deviceUnits, deviceUnits);
            if (index >= 0) lag = Math.Max(lag, available - 1 - index);
        });
        return ((int)delay, (int)lag);

        void Walk(Action<long, long> visit)
        {
            long frames = 0, completes = FloorDivide((Frame - 1) * deviceUnits, modelUnits);
            for (long n = 0; n < horizon; n++)
            {
                while (completes <= n) { frames++; completes = FloorDivide((Frame * frames + Frame - 1) * deviceUnits, modelUnits); }
                visit(n, Frame * frames);
            }
        }
    }

    private static long FloorDivide(long numerator, long denominator) =>
        numerator >= 0 ? numerator / denominator : -((-numerator + denominator - 1) / denominator);

    private static double Smooth(double value, double target, double coefficient)
    {
        double next = target + (value - target) * coefficient;
        return Math.Abs(next - target) < 1e-15 ? target : next;
    }

    /// <summary>Owns one native denoiser state per channel; frees them when released (also by finalization).</summary>
    private sealed class StateHandle : SafeHandle
    {
        private RnnoiseLibrary? owner;
        private long pressure;
        internal nint[] States { get; private set; } = [];
        /// <summary>The bytes of a freshly created state, for resets on the audio thread.</summary>
        internal byte[] Pristine { get; private set; } = [];

        public StateHandle() : base(0, ownsHandle: true) { }

        public override bool IsInvalid => handle == 0;

        internal static StateHandle? Allocate(RnnoiseLibrary library, int count)
        {
            var created = new nint[count];
            for (int i = 0; i < count; i++)
            {
                created[i] = library.Create();
                if (created[i] == 0)
                {
                    for (int j = 0; j < i; j++) library.Destroy(created[j]);
                    return null;
                }
            }
            var pristine = new byte[library.StateBytes];
            Marshal.Copy(created[0], pristine, 0, pristine.Length);
            var result = new StateHandle { owner = library, States = created, Pristine = pristine, pressure = (long)library.StateBytes * count };
            result.SetHandle(created[0]);
            GC.AddMemoryPressure(result.pressure);
            return result;
        }

        protected override bool ReleaseHandle()
        {
            foreach (nint state in States) owner!.Destroy(state);
            GC.RemoveMemoryPressure(pressure);
            return true;
        }
    }
}
