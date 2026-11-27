namespace Soltex.Audio.Engine;

/// <summary>Independent interleaved-float processing. One processing thread per instance; settings may arrive concurrently.</summary>
public sealed class PcmDspProcessor
{
    private sealed class Prepared(DspSettings settings, EqualizerDesign equalizer, SpectralNoiseSuppressor[]? suppressors, SafetyLimiter? limiter)
    {
        public DspSettings Settings { get; } = settings;
        public EqualizerDesign Equalizer { get; } = equalizer;
        public SpectralNoiseSuppressor[]? Suppressors { get; set; } = suppressors;
        public SafetyLimiter? Limiter { get; set; } = limiter;
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
    // A change that adds or removes delay (noise suppression, limiter) waits here while the output dips smoothly to zero.
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
    private readonly double[] frame;

    public int SampleRate { get; }
    public int Channels { get; }
    public long SanitizedSamples => Interlocked.Read(ref sanitizedSamples);
    /// <summary>Samples that reached the output ceiling (clamped, or limited when the safety limiter is on).</summary>
    public long LimitedSamples => Interlocked.Read(ref limitedSamples);
    /// <summary>Algorithm delay only; device, buffering and sample conversion delays are additional.</summary>
    public int NoiseSuppressionLatencyFrames => current.Suppressors?[0].LatencyFrames ?? 0;
    /// <summary>Lookahead delay of the opt-in safety limiter; zero while it is off.</summary>
    public int LimiterLatencyFrames => current.Limiter?.LatencyFrames ?? 0;
    /// <summary>Total algorithm delay of this processor.</summary>
    public int LatencyFrames => NoiseSuppressionLatencyFrames + LimiterLatencyFrames;

    public PcmDspProcessor(int sampleRate, int channels, DspSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        SampleRate = sampleRate; Channels = channels;
        stereo = new(sampleRate);
        frame = new double[channels];
        eqRampFrames = Math.Max(1, sampleRate / 50);       // 20 ms coefficient glide
        transitionFrames = Math.Max(1, sampleRate / 100);  // 10 ms raised-cosine dip each way
        parameterCoefficient = Coefficient(15);
        settings ??= new DspSettings { Bands = DspSettings.FlatBands().Select(b =>
            b with { FrequencyHz = Math.Min(b.FrequencyHz, sampleRate * 0.45) }).ToArray() };
        current = Prepare(settings);
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
        var next = Interlocked.Exchange(ref pending, null);
        if (next is not null)
        {
            if (ChangesDelay(next)) staged = next;
            else { staged = null; Apply(next); }
        }
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
                    Apply(staged); staged = null; p = Parameters(current.Settings);
                    // A new suppressor/limiter starts empty: its output is silence for its delay, then the signal at full level.
                    transitionHold = Math.Max(0, LatencyFrames - before);
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
            double peak = 0;
            for (int channel = 0; channel < Channels; channel++)
            {
                double input = interleaved[offset + channel];
                if (!double.IsFinite(input)) { input = 0; Interlocked.Increment(ref sanitizedSamples); }
                // Extremely large finite upstream data must not poison recursive filter history.
                if (Math.Abs(input) > 8) { input = Math.Clamp(input, -8, 8); Interlocked.Increment(ref sanitizedSamples); }
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

    private bool ChangesDelay(Prepared next) =>
        next.Suppressors is null != current.Suppressors is null || next.Limiter is null != current.Limiter is null;

    /// <summary>Audio thread. Keeps EQ history (coefficients glide), suppressor buffers and limiter state where both sides use them.</summary>
    private void Apply(Prepared next)
    {
        equalizer.Retarget(next.Equalizer, eqRampFrames);
        if (next.Suppressors is not null && current.Suppressors is not null)
        {
            next.Suppressors = current.Suppressors;
            foreach (var suppressor in next.Suppressors) suppressor.Configure(next.Settings.NoiseSuppression);
        }
        if (next.Limiter is not null && current.Limiter is not null)
        {
            next.Limiter = current.Limiter;
            next.Limiter.Configure(next.Settings);
        }
        current = next;
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
        return new(copy, EqualizerBank.Design(copy, SampleRate), suppressors, limiter);
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
