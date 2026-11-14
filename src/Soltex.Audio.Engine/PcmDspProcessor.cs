namespace Soltex.Audio.Engine;

/// <summary>Independent interleaved-float processing. One processing thread per instance; settings may arrive concurrently.</summary>
public sealed class PcmDspProcessor
{
    private sealed class Prepared(DspSettings settings, EqualizerBank equalizer, SpectralNoiseSuppressor[]? suppressors)
    {
        public DspSettings Settings { get; } = settings;
        public EqualizerBank Equalizer { get; set; } = equalizer;
        public SpectralNoiseSuppressor[]? Suppressors { get; set; } = suppressors;
    }
    private Prepared current;
    private Prepared? pending;
    private EqualizerBank? previousEq;
    private int fadeRemaining;
    private readonly int fadeFrames;
    private double gain;
    private double detector;
    private double compressorDetector;
    private double gateGain = 1;
    private double expanderGain = 1;
    private bool gateOpen = true;
    private int holdRemaining;
    private long sanitizedSamples, limitedSamples;
    private readonly StereoImageProcessor stereo;

    public int SampleRate { get; }
    public int Channels { get; }
    public long SanitizedSamples => Interlocked.Read(ref sanitizedSamples);
    public long LimitedSamples => Interlocked.Read(ref limitedSamples);
    /// <summary>Algorithm delay only; device, buffering and sample conversion delays are additional.</summary>
    public int NoiseSuppressionLatencyFrames => current.Suppressors?[0].LatencyFrames ?? 0;

    public PcmDspProcessor(int sampleRate, int channels, DspSettings? settings = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        SampleRate = sampleRate; Channels = channels;
        stereo = new(sampleRate);
        settings ??= new DspSettings { Bands = DspSettings.FlatBands().Select(b =>
            b with { FrequencyHz = Math.Min(b.FrequencyHz, sampleRate * 0.45) }).ToArray() };
        current = Prepare(settings);
        gain = TargetGain(current.Settings);
        fadeFrames = Math.Max(1, sampleRate / 100);
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
            bool eqChanged = next.Settings.EqBypass != current.Settings.EqBypass;
            for (int index = 0; !eqChanged && index < next.Settings.Bands.Count; index++)
                eqChanged = next.Settings.Bands[index] != current.Settings.Bands[index];
            if (eqChanged) { previousEq = current.Equalizer; fadeRemaining = fadeFrames; }
            else next.Equalizer = current.Equalizer;
            if (next.Suppressors is not null && current.Suppressors is not null)
            {
                next.Suppressors = current.Suppressors;
                foreach (var suppressor in next.Suppressors) suppressor.Configure(next.Settings.NoiseSuppression);
            }
            current = next;
        }
        var s = current.Settings;
        double gainCoefficient = Coefficient(s.GainSmoothingMs);
        double target = TargetGain(s);
        double detectorAttack = Coefficient(1), detectorRelease = Coefficient(30);
        double compressorAttack = Coefficient(s.Compressor.AttackMs), compressorRelease = Coefficient(s.Compressor.ReleaseMs);
        double gateAttack = Coefficient(s.Gate.AttackMs), gateRelease = Coefficient(s.Gate.ReleaseMs);
        double expanderAttack = Coefficient(s.NoiseAttenuation.AttackMs), expanderRelease = Coefficient(s.NoiseAttenuation.ReleaseMs);
        double gateThreshold = Linear(s.Gate.ThresholdDb), closeThreshold = Linear(s.Gate.ThresholdDb - s.Gate.HysteresisDb);
        double closedGain = Linear(s.Gate.ClosedGainDb);
        int holdFrames = (int)(s.Gate.HoldMs * SampleRate / 1000);
        for (int frame = 0; frame < interleaved.Length; frame += Channels)
        {
            if (s.InputChannel is int selected)
            {
                float sample = interleaved[frame + selected];
                for (int channel = 0; channel < Channels; channel++) interleaved[frame + channel] = sample;
            }
            double peak = 0;
            double fade = fadeRemaining > 0 ? 1 - (double)fadeRemaining / fadeFrames : 1;
            for (int channel = 0; channel < Channels; channel++)
            {
                double input = interleaved[frame + channel];
                if (!double.IsFinite(input)) { input = 0; Interlocked.Increment(ref sanitizedSamples); }
                // Extremely large finite upstream data must not poison recursive filter history.
                if (Math.Abs(input) > 8) { input = Math.Clamp(input, -8, 8); Interlocked.Increment(ref sanitizedSamples); }
                if (current.Suppressors is not null) input = current.Suppressors[channel].Process(input);
                double filtered = current.Equalizer.Process(channel, input);
                if (previousEq is not null) filtered = previousEq.Process(channel, input) * (1 - fade) + filtered * fade;
                interleaved[frame + channel] = (float)filtered;
                peak = Math.Max(peak, Math.Abs(filtered));
            }
            if (fadeRemaining > 0 && --fadeRemaining == 0) previousEq = null;
            detector = Smooth(detector, peak, peak > detector ? detectorAttack : detectorRelease);
            compressorDetector = Smooth(compressorDetector, peak, peak > compressorDetector ? compressorAttack : compressorRelease);
            if (detector >= gateThreshold) { gateOpen = true; holdRemaining = holdFrames; }
            else if (holdRemaining > 0) holdRemaining--;
            else if (detector < closeThreshold) gateOpen = false;
            double gateTarget = !s.Gate.Enabled || gateOpen ? 1 : closedGain;
            gateGain = Smooth(gateGain, gateTarget, gateTarget > gateGain ? gateAttack : gateRelease);
            double expanderDb = Math.Min(0, (Db(detector) - s.NoiseAttenuation.ThresholdDb) * (s.NoiseAttenuation.Ratio - 1));
            double expanderTarget = s.NoiseAttenuation.Enabled ? Linear(Math.Max(-s.NoiseAttenuation.MaximumAttenuationDb, expanderDb)) : 1;
            expanderGain = Smooth(expanderGain, expanderTarget, expanderTarget > expanderGain ? expanderAttack : expanderRelease);
            double reduction = Math.Max(0, Db(compressorDetector) - s.Compressor.ThresholdDb) * (1 - 1 / s.Compressor.Ratio);
            double compressorGain = s.Compressor.Enabled ? Linear(s.Compressor.MakeupDb - reduction) : 1;
            gain = Smooth(gain, target, gainCoefficient);
            double combined = gain * gateGain * expanderGain * compressorGain;
            if (Channels == 2)
            {
                double left = interleaved[frame], right = interleaved[frame + 1];
                stereo.Process(ref left, ref right, s.Stereo);
                interleaved[frame] = (float)left; interleaved[frame + 1] = (float)right;
            }
            for (int channel = 0; channel < Channels; channel++)
            {
                double output = interleaved[frame + channel] * combined;
                if (!double.IsFinite(output)) { output = 0; Interlocked.Increment(ref sanitizedSamples); }
                if (Math.Abs(output) > s.OutputCeiling) Interlocked.Increment(ref limitedSamples);
                interleaved[frame + channel] = (float)Math.Clamp(output, -s.OutputCeiling, s.OutputCeiling);
            }
        }
    }

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
        return new(copy, new EqualizerBank(SampleRate, Channels, copy), suppressors);
    }
    private double Coefficient(double milliseconds) => Math.Exp(-1 / (milliseconds * 0.001 * SampleRate));
    private static double TargetGain(DspSettings s) => s.Mute ? 0 : Linear(s.GainDb + s.HeadroomDb);
    private static double Linear(double db) => Math.Pow(10, db / 20);
    private static double Db(double value) => 20 * Math.Log10(Math.Max(1e-12, value));
    private static double Smooth(double value, double target, double coefficient) => target + (value - target) * coefficient;
}
