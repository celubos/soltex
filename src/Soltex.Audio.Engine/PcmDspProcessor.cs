namespace Soltex.Audio.Engine;

/// <summary>
/// Independent interleaved-float processing. One processing thread per instance; settings may arrive concurrently.
/// Dispose (after the processing thread has stopped calling <see cref="Process"/>) frees the native RNNoise states
/// at once; a disposed processor outputs silence.
/// </summary>
public sealed class PcmDspProcessor : IDisposable
{
    private sealed class Prepared(DspSettings settings, EqualizerDesign equalizer, SpectralNoiseSuppressor[]? suppressors, SafetyLimiter? limiter,
        NeuralNoiseRemover? neural)
    {
        public DspSettings Settings { get; } = settings;
        public EqualizerDesign Equalizer { get; } = equalizer;
        public SpectralNoiseSuppressor[]? Suppressors { get; set; } = suppressors;
        public SafetyLimiter? Limiter { get; set; } = limiter;
        /// <summary>The processor's one neural stage while enabled (shared by every snapshot), else null.</summary>
        public NeuralNoiseRemover? Neural { get; } = neural;
    }

    /// <summary>Per-block constants derived from the active settings.</summary>
    private struct BlockParameters
    {
        public double GainCoefficient, Target, DetectorAttack, DetectorRelease, CompressorAttack, CompressorRelease,
            GateAttack, GateRelease, ExpanderAttack, ExpanderRelease, GateThreshold, CloseThreshold, ClosedGain,
            CompressorMix, CompressorSlope;
        public int HoldFrames;
    }

    /// <summary>Smoothed values closer than this to their target snap to it, so decays never reach subnormal numbers.</summary>
    private const double Snap = 1e-15;
    private Prepared current;
    private Prepared? pending;
    // A change that adds or removes delay (neural or spectral noise removal, limiter) waits here while the output dips smoothly to zero.
    private Prepared? staged;
    private readonly EqualizerBank equalizer;
    private readonly int eqRampFrames, transitionFrames;
    private double transitionPhase = 1;
    // After switching to a freshly created (empty) delay stage, stay silent until its first real output arrives.
    private int transitionHold, transitionFlush;
    private double gain;
    private double detector;
    private double compressorDetector;
    private double gateGain = 1;
    private double expanderGain = 1;
    private double compressorMix, compressorThreshold, compressorSlope, compressorMakeup;
    private readonly double parameterCoefficient;
    private bool gateOpen = true;
    private int holdRemaining;
    private long sanitizedSamples, limitedSamples;
    private readonly StereoImageProcessor stereo;
    private readonly double[] frame, neuralFrame;
    // The RNNoise library this processor uses (the shared one next to the engine unless a test supplies another), and the
    // one neural stage, created on a control thread the first time settings enable it and kept until Dispose.
    private readonly RnnoiseLibrary? injectedLibrary;
    private readonly bool sharedLibrary;
    private NeuralNoiseRemover? neuralStage;
    private int disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public long SanitizedSamples => Interlocked.Read(ref sanitizedSamples);
    /// <summary>Samples that reached the output ceiling (clamped, or limited when the safety limiter is on).</summary>
    public long LimitedSamples => Interlocked.Read(ref limitedSamples);
    /// <summary>
    /// True when soltex-rnnoise.dll (Xiph RNNoise 0.2) loaded from the engine assembly's directory and passed its checks in
    /// this process, so <see cref="DspSettings.NeuralNoiseRemoval"/> works at every sample rate and channel count this
    /// processor accepts. Static: it describes the installation, not one stream, and can be read before any stream
    /// exists. The first read loads the library (a few milliseconds). When false, enabling the stage changes nothing.
    /// </summary>
    public static bool NeuralNoiseRemovalAvailable => RnnoiseLibrary.Shared is not null;
    /// <summary>Delay of the neural noise removal stage in frames at this rate; zero while it is off or unavailable.</summary>
    public int NeuralNoiseRemovalLatencyFrames => current.Neural?.LatencyFrames ?? 0;
    /// <summary>Algorithm delay only; device, buffering and sample conversion delays are additional.</summary>
    public int NoiseSuppressionLatencyFrames => current.Suppressors?[0].LatencyFrames ?? 0;
    /// <summary>Lookahead delay of the opt-in safety limiter; zero while it is off.</summary>
    public int LimiterLatencyFrames => current.Limiter?.LatencyFrames ?? 0;
    /// <summary>Total algorithm delay of this processor.</summary>
    public int LatencyFrames => NeuralNoiseRemovalLatencyFrames + NoiseSuppressionLatencyFrames + LimiterLatencyFrames;

    public PcmDspProcessor(int sampleRate, int channels, DspSettings? settings = null) : this(sampleRate, channels, settings, null, sharedLibrary: true) { }

    /// <summary>Uses <paramref name="library"/> (null: unavailable) instead of the library next to the engine.</summary>
    internal PcmDspProcessor(int sampleRate, int channels, DspSettings? settings, RnnoiseLibrary? library) : this(sampleRate, channels, settings, library, sharedLibrary: false) { }

    private PcmDspProcessor(int sampleRate, int channels, DspSettings? settings, RnnoiseLibrary? library, bool sharedLibrary)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        SampleRate = sampleRate; Channels = channels;
        injectedLibrary = library; this.sharedLibrary = sharedLibrary;
        stereo = new(sampleRate);
        frame = new double[channels]; neuralFrame = new double[channels];
        eqRampFrames = Math.Max(1, sampleRate / 50);       // 20 ms coefficient glide
        transitionFrames = Math.Max(1, sampleRate / 100);  // 10 ms raised-cosine dip each way
        parameterCoefficient = Coefficient(15);
        settings ??= new DspSettings { Bands = DspSettings.FlatBands().Select(b =>
            b with { FrequencyHz = Math.Min(b.FrequencyHz, sampleRate * 0.45) }).ToArray() };
        current = Prepare(settings);
        current.Neural?.Reset(current.Settings.NeuralNoiseRemoval, current.Settings.InputChannel is not null);
        equalizer = new EqualizerBank(channels, current.Equalizer);
        gain = TargetGain(current.Settings);
        var compressor = current.Settings.Compressor;
        compressorMix = compressor.Enabled ? 1 : 0; compressorThreshold = compressor.ThresholdDb;
        compressorSlope = 1 - 1 / compressor.Ratio; compressorMakeup = compressor.MakeupDb;
    }

    /// <summary>Validates and snapshots settings on the control thread; newest complete update wins at the next block.</summary>
    public void UpdateSettings(DspSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref pending, Prepare(settings));
    }

    public void Process(Span<float> interleaved)
    {
        if (interleaved.Length % Channels != 0)
            throw new ArgumentException("Input must contain whole interleaved frames.", nameof(interleaved));
        if (Volatile.Read(ref disposed) != 0) { interleaved.Clear(); return; }
        var next = Interlocked.Exchange(ref pending, null);
        // Hold the neural stage's native states for the whole block (it is the same object in every snapshot that has it).
        var neural = current.Neural ?? next?.Neural ?? staged?.Neural;
        if (neural is not null && !neural.TryEnter()) { interleaved.Clear(); return; }
        try
        {
            if (next is not null)
            {
                if (ChangesDelay(next)) staged = next;
                else { staged = null; Apply(next); }
            }
            ProcessFrames(interleaved);
        }
        finally { neural?.Exit(); }
    }

    private void ProcessFrames(Span<float> interleaved)
    {
        var p = Parameters(current.Settings);
        for (int offset = 0; offset < interleaved.Length; offset += Channels)
        {
            if (staged is not null)
            {
                if (transitionPhase > 0)
                {
                    transitionPhase -= 1.0 / transitionFrames;
                    // The limiter sits after the dip: let its delay line fill with the silence before removing or replacing it.
                    if (transitionPhase <= 0) { transitionPhase = 0; transitionFlush = LimiterLatencyFrames; }
                }
                else if (transitionFlush > 0) transitionFlush--;
                if (transitionPhase <= 0 && transitionFlush == 0)
                {
                    int before = LatencyFrames;
                    bool restarted = Apply(staged); staged = null; p = Parameters(current.Settings);
                    // A new delay stage starts empty: its output is silence for its delay, then the signal at full level. A
                    // restarted one (the suppressor when the neural stage before it toggles) is empty too, so everything
                    // before the dip counts in full.
                    transitionHold = Math.Max(0, LatencyFrames - before);
                    if (restarted) transitionHold = Math.Max(transitionHold, NeuralNoiseRemovalLatencyFrames + NoiseSuppressionLatencyFrames);
                }
            }
            else if (transitionHold > 0) transitionHold--;
            else if (transitionPhase < 1) transitionPhase = Math.Min(1, transitionPhase + 1.0 / transitionFrames);
            var s = current.Settings;
            if (s.InputChannel is int selected)
            {
                float sample = interleaved[offset + selected];
                for (int channel = 0; channel < Channels; channel++) interleaved[offset + channel] = sample;
            }
            for (int channel = 0; channel < Channels; channel++)
            {
                double input = interleaved[offset + channel];
                if (!double.IsFinite(input)) { input = 0; Interlocked.Increment(ref sanitizedSamples); }
                // Extremely large finite upstream data must not poison recursive filter history.
                if (Math.Abs(input) > 8) { input = Math.Clamp(input, -8, 8); Interlocked.Increment(ref sanitizedSamples); }
                neuralFrame[channel] = input;
            }
            current.Neural?.Process(neuralFrame);
            double peak = 0;
            for (int channel = 0; channel < Channels; channel++)
            {
                double input = neuralFrame[channel];
                if (current.Suppressors is not null) input = current.Suppressors[channel].Process(input);
                double filtered = equalizer.Process(channel, input);
                interleaved[offset + channel] = (float)filtered;
                peak = Math.Max(peak, Math.Abs(filtered));
            }
            detector = Smooth(detector, peak, peak > detector ? p.DetectorAttack : p.DetectorRelease);
            compressorDetector = Smooth(compressorDetector, peak, peak > compressorDetector ? p.CompressorAttack : p.CompressorRelease);
            if (detector >= p.GateThreshold) { gateOpen = true; holdRemaining = p.HoldFrames; }
            else if (holdRemaining > 0) holdRemaining--;
            else if (detector < p.CloseThreshold) gateOpen = false;
            double gateTarget = !s.Gate.Enabled || gateOpen ? 1 : p.ClosedGain;
            gateGain = Smooth(gateGain, gateTarget, gateTarget > gateGain ? p.GateAttack : p.GateRelease);
            double expanderDb = Math.Min(0, (Db(detector) - s.NoiseAttenuation.ThresholdDb) * (s.NoiseAttenuation.Ratio - 1));
            double expanderTarget = s.NoiseAttenuation.Enabled ? Linear(Math.Max(-s.NoiseAttenuation.MaximumAttenuationDb, expanderDb)) : 1;
            expanderGain = Smooth(expanderGain, expanderTarget, expanderTarget > expanderGain ? p.ExpanderAttack : p.ExpanderRelease);
            // Compressor controls glide (15 ms) so enabling it or moving threshold/ratio/makeup cannot step the gain.
            compressorMix = Smooth(compressorMix, p.CompressorMix, parameterCoefficient);
            compressorThreshold = Smooth(compressorThreshold, s.Compressor.ThresholdDb, parameterCoefficient);
            compressorSlope = Smooth(compressorSlope, p.CompressorSlope, parameterCoefficient);
            compressorMakeup = Smooth(compressorMakeup, s.Compressor.MakeupDb, parameterCoefficient);
            double reduction = Math.Max(0, Db(compressorDetector) - compressorThreshold) * compressorSlope;
            double compressorGain = compressorMix == 0 ? 1 : Linear(compressorMix * (compressorMakeup - reduction));
            gain = Smooth(gain, p.Target, p.GainCoefficient);
            double combined = gain * gateGain * expanderGain * compressorGain;
            if (transitionPhase < 1) combined *= 0.5 - 0.5 * Math.Cos(Math.PI * transitionPhase);
            if (Channels == 2)
            {
                double left = interleaved[offset], right = interleaved[offset + 1];
                stereo.Process(ref left, ref right, s.Stereo);
                interleaved[offset] = (float)left; interleaved[offset + 1] = (float)right;
            }
            double ceiling = s.OutputCeiling;
            for (int channel = 0; channel < Channels; channel++)
            {
                double output = interleaved[offset + channel] * combined;
                if (!double.IsFinite(output)) { output = 0; Interlocked.Increment(ref sanitizedSamples); }
                if (Math.Abs(output) > ceiling) Interlocked.Increment(ref limitedSamples);
                frame[channel] = output;
            }
            current.Limiter?.ProcessFrame(frame);
            for (int channel = 0; channel < Channels; channel++)
                interleaved[offset + channel] = (float)Math.Clamp(frame[channel], -ceiling, ceiling);
        }
    }

    /// <summary>
    /// Control thread, once the processing thread no longer calls <see cref="Process"/>: frees the native RNNoise states.
    /// A block still running holds them until it ends; later blocks output silence.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Interlocked.Exchange(ref neuralStage, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool ChangesDelay(Prepared next) =>
        next.Suppressors is null != current.Suppressors is null || next.Limiter is null != current.Limiter is null || next.Neural is null != current.Neural is null;

    /// <summary>
    /// Audio thread. Keeps EQ history (coefficients glide), suppressor buffers, the neural stage and limiter state where both
    /// sides use them. Returns true when it emptied a suppressor because the neural stage before it was switched on or off
    /// (its buffers would otherwise splice two differently delayed signals).
    /// </summary>
    private bool Apply(Prepared next)
    {
        equalizer.Retarget(next.Equalizer, eqRampFrames);
        bool neuralToggles = next.Neural is null != current.Neural is null, restarted = false;
        if (next.Suppressors is not null && current.Suppressors is not null)
        {
            next.Suppressors = current.Suppressors;
            foreach (var suppressor in next.Suppressors)
            {
                suppressor.Configure(next.Settings.NoiseSuppression);
                if (neuralToggles) { suppressor.Reset(); restarted = true; }
            }
        }
        if (next.Neural is not null)
        {
            bool sharedInput = next.Settings.InputChannel is not null;
            // Switched on: start from a fresh state rather than audio from before it was last switched off.
            if (current.Neural is null) next.Neural.Reset(next.Settings.NeuralNoiseRemoval, sharedInput);
            else next.Neural.Configure(next.Settings.NeuralNoiseRemoval, sharedInput);
        }
        if (next.Limiter is not null && current.Limiter is not null)
        {
            next.Limiter = current.Limiter;
            next.Limiter.Configure(next.Settings);
        }
        current = next;
        return restarted;
    }

    private BlockParameters Parameters(DspSettings s) => new()
    {
        GainCoefficient = Coefficient(s.GainSmoothingMs),
        Target = TargetGain(s),
        DetectorAttack = Coefficient(1), DetectorRelease = Coefficient(30),
        CompressorAttack = Coefficient(s.Compressor.AttackMs), CompressorRelease = Coefficient(s.Compressor.ReleaseMs),
        GateAttack = Coefficient(s.Gate.AttackMs), GateRelease = Coefficient(s.Gate.ReleaseMs),
        ExpanderAttack = Coefficient(s.NoiseAttenuation.AttackMs), ExpanderRelease = Coefficient(s.NoiseAttenuation.ReleaseMs),
        GateThreshold = Linear(s.Gate.ThresholdDb), CloseThreshold = Linear(s.Gate.ThresholdDb - s.Gate.HysteresisDb),
        ClosedGain = Linear(s.Gate.ClosedGainDb),
        CompressorMix = s.Compressor.Enabled ? 1 : 0,
        CompressorSlope = 1 - 1 / s.Compressor.Ratio,
        HoldFrames = (int)(s.Gate.HoldMs * SampleRate / 1000)
    };

    private Prepared Prepare(DspSettings settings)
    {
        var copy = settings.ValidatedCopy(SampleRate);
        if (copy.InputChannel >= Channels) throw new ArgumentOutOfRangeException(nameof(settings), "Selected input channel is unavailable in this capture format.");
        SpectralNoiseSuppressor[]? suppressors = null;
        if (copy.NoiseSuppression.Enabled)
        {
            suppressors = new SpectralNoiseSuppressor[Channels];
            for (int i = 0; i < Channels; i++) suppressors[i] = new(SampleRate, copy.NoiseSuppression);
        }
        var limiter = copy.Limiter.Enabled ? new SafetyLimiter(SampleRate, Channels, copy) : null;
        var neural = copy.NeuralNoiseRemoval.Enabled ? NeuralStage() : null;
        return new(copy, EqualizerBank.Design(copy, SampleRate), suppressors, limiter, neural);
    }

    /// <summary>
    /// Control thread: the processor's neural stage, created (native states, buffers, converter kernels) the first time it
    /// is needed. Null when the library is unavailable or out of memory: the stage then passes audio through unchanged.
    /// </summary>
    private NeuralNoiseRemover? NeuralStage()
    {
        var stage = Volatile.Read(ref neuralStage);
        if (stage is not null || Volatile.Read(ref disposed) != 0) return stage;
        var library = sharedLibrary ? RnnoiseLibrary.Shared : injectedLibrary;
        if (library is null) return null;
        var created = NeuralNoiseRemover.TryCreate(library, SampleRate, Channels);
        if (created is null) return null;
        var raced = Interlocked.CompareExchange(ref neuralStage, created, null);
        if (raced is not null) { created.Dispose(); return raced; }
        if (Volatile.Read(ref disposed) != 0) { Interlocked.Exchange(ref neuralStage, null)?.Dispose(); return null; }
        return created;
    }

    private double Coefficient(double milliseconds) => Math.Exp(-1 / (milliseconds * 0.001 * SampleRate));
    private static double TargetGain(DspSettings s) => s.Mute ? 0 : Linear(s.GainDb + s.HeadroomDb);
    private static double Linear(double db) => Math.Pow(10, db / 20);
    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-12, value));
    private static double Smooth(double value, double target, double coefficient)
    {
        double next = target + (value - target) * coefficient;
        return Math.Abs(next - target) < Snap ? target : next;
    }
}
