namespace Soltex.Audio.Engine;

public enum EqFilterType { Peak, LowShelf, HighShelf, HighPass, LowPass, Notch }

public sealed record EqBand(double FrequencyHz, double GainDb = 0, double Q = 0.7071067811865476,
    EqFilterType Type = EqFilterType.Peak, bool Bypass = false);

public sealed record GateSettings(bool Enabled = false, double ThresholdDb = -45,
    double HysteresisDb = 6, double AttackMs = 5, double HoldMs = 80, double ReleaseMs = 100,
    double ClosedGainDb = -80);

public sealed record CompressorSettings(bool Enabled = false, double ThresholdDb = -18,
    double Ratio = 4, double AttackMs = 10, double ReleaseMs = 120, double MakeupDb = 0);

/// <summary>Attenuates quiet passages below a user-set noise floor. Does not separate noise from speech.</summary>
public sealed record DownwardExpanderSettings(bool Enabled = false, double ThresholdDb = -45,
    double Ratio = 2, double MaximumAttenuationDb = 24, double AttackMs = 5, double ReleaseMs = 100);

/// <summary>Frequency-selective suppression against a manually set stationary RMS noise floor; not learned speech separation.
/// SmoothResidual (opt-in, off by default) uses a decision-directed a-priori SNR estimate with Wiener gains: the remaining
/// noise is steady instead of "musical" (warbling tones), and noise-only passages sit closer to the reduction floor, so it
/// sounds different from the default rule.</summary>
public sealed record SpectralNoiseSuppressionSettings(bool Enabled = false, double NoiseFloorDb = -50, double ReductionDb = 18, bool SmoothResidual = false);

/// <summary>
/// Opt-in transparent output limiter at <see cref="DspSettings.OutputCeiling"/>. Off by default: when on it adds a fixed
/// lookahead delay (about 1.6 ms) and replaces hard clipping above the ceiling with a smooth, channel-linked gain
/// reduction. TruePeak also limits 4x-interpolated inter-sample peaks. Below the ceiling it changes nothing.
/// </summary>
public sealed record SafetyLimiterSettings(bool Enabled = false, double ReleaseMs = 80, bool TruePeak = true);

/// <summary>Two-channel image controls and delayed low-pass headphone crossfeed; not surround/HRTF rendering.</summary>
public sealed record StereoSettings(bool Enabled = false, double Width = 1, double Pan = 0,
    double Crossfeed = 0, bool SwapChannels = false, bool Mono = false);

public sealed record DspSettings
{
    /// <summary>Null keeps all channels; a zero-based hardware input index is duplicated to all channels.</summary>
    public int? InputChannel { get; init; }
    public double GainDb { get; init; }
    public bool Mute { get; init; }
    public double GainSmoothingMs { get; init; } = 20;
    public double HeadroomDb { get; init; } = -6;
    public double OutputCeiling { get; init; } = 0.98;
    public bool EqBypass { get; init; }
    public IReadOnlyList<EqBand> Bands { get; init; } = FlatBands();
    public GateSettings Gate { get; init; } = new();
    public CompressorSettings Compressor { get; init; } = new();
    public DownwardExpanderSettings NoiseAttenuation { get; init; } = new();
    public SpectralNoiseSuppressionSettings NoiseSuppression { get; init; } = new();
    public StereoSettings Stereo { get; init; } = new();
    public SafetyLimiterSettings Limiter { get; init; } = new();

    public static EqBand[] FlatBands() => [new(31), new(62), new(125), new(250), new(500),
        new(1000), new(2000), new(4000), new(8000), new(16000)];

    internal DspSettings ValidatedCopy(int sampleRate)
    {
        if (InputChannel is int input) Range(input, 0, 7, nameof(InputChannel));
        Range(GainDb, -80, 24, nameof(GainDb));
        Range(HeadroomDb, -30, 0, nameof(HeadroomDb));
        Range(GainSmoothingMs, 1, 2000, nameof(GainSmoothingMs));
        Range(OutputCeiling, 0.01, 1, nameof(OutputCeiling));
        ArgumentNullException.ThrowIfNull(Bands);
        if (Bands.Count != 10) throw new ArgumentException("Exactly ten EQ bands are required.");
        var copy = Bands.ToArray();
        foreach (var band in copy)
        {
            ArgumentNullException.ThrowIfNull(band);
            Range(band.FrequencyHz, 10, sampleRate * 0.49, nameof(band.FrequencyHz));
            Range(band.GainDb, -24, 24, nameof(band.GainDb));
            Range(band.Q, 0.1, 20, nameof(band.Q));
            if (!Enum.IsDefined(band.Type)) throw new ArgumentException("Unknown EQ filter type.");
        }
        ArgumentNullException.ThrowIfNull(Gate);
        ArgumentNullException.ThrowIfNull(Compressor);
        ArgumentNullException.ThrowIfNull(NoiseAttenuation);
        ArgumentNullException.ThrowIfNull(NoiseSuppression);
        ArgumentNullException.ThrowIfNull(Stereo);
        ArgumentNullException.ThrowIfNull(Limiter);
        Range(Limiter.ReleaseMs, 5, 2000, nameof(Limiter.ReleaseMs));
        Range(NoiseSuppression.NoiseFloorDb, -90, -20, nameof(NoiseSuppression.NoiseFloorDb));
        Range(NoiseSuppression.ReductionDb, 0, 36, nameof(NoiseSuppression.ReductionDb));
        Range(Stereo.Width, 0, 2, nameof(Stereo.Width));
        Range(Stereo.Pan, -1, 1, nameof(Stereo.Pan));
        Range(Stereo.Crossfeed, 0, 1, nameof(Stereo.Crossfeed));
        Range(Gate.ThresholdDb, -100, 0, nameof(Gate.ThresholdDb));
        Range(Gate.HysteresisDb, 0, 24, nameof(Gate.HysteresisDb));
        Range(Gate.ClosedGainDb, -120, 0, nameof(Gate.ClosedGainDb));
        Range(Gate.HoldMs, 0, 5000, nameof(Gate.HoldMs));
        Times(Gate.AttackMs, Gate.ReleaseMs);
        Range(Compressor.ThresholdDb, -80, 0, nameof(Compressor.ThresholdDb));
        Range(Compressor.Ratio, 1, 20, nameof(Compressor.Ratio));
        Range(Compressor.MakeupDb, 0, 24, nameof(Compressor.MakeupDb));
        Times(Compressor.AttackMs, Compressor.ReleaseMs);
        Range(NoiseAttenuation.ThresholdDb, -100, 0, nameof(NoiseAttenuation.ThresholdDb));
        Range(NoiseAttenuation.Ratio, 1, 10, nameof(NoiseAttenuation.Ratio));
        Range(NoiseAttenuation.MaximumAttenuationDb, 0, 80, nameof(NoiseAttenuation.MaximumAttenuationDb));
        Times(NoiseAttenuation.AttackMs, NoiseAttenuation.ReleaseMs);
        return this with { Bands = Array.AsReadOnly(copy) };
    }

    private static void Times(double attack, double release)
    {
        Range(attack, 0.1, 2000, nameof(attack));
        Range(release, 1, 5000, nameof(release));
    }

    internal static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentOutOfRangeException(name, value, $"Must be finite and in [{min}, {max}].");
    }
}

public static class ChatMix
{
    /// <summary>Balance -1 favors game, +1 favors chat. Center leaves both at unity; only attenuates.</summary>
    public static (double Game, double Chat) Gains(double balance)
    {
        DspSettings.Range(balance, -1, 1, nameof(balance));
        return balance < 0 ? (1, Math.Cos(-balance * Math.PI / 2))
            : (Math.Cos(balance * Math.PI / 2), 1);
    }
}
