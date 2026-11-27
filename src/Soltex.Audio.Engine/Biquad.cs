namespace Soltex.Audio.Engine;

/// <summary>Normalized biquad coefficients (a0 = 1) from the RBJ/W3C Audio EQ Cookbook equations.</summary>
internal readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    internal static readonly BiquadCoefficients Identity = new(1, 0, 0, 0, 0);

    private static BiquadCoefficients Normalized(double b0, double b1, double b2, double a0, double a1, double a2) =>
        new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);

    internal double Magnitude(double radians)
    {
        var z = System.Numerics.Complex.FromPolarCoordinates(1, -radians);
        return ((B0 + B1 * z + B2 * z * z) / (1 + A1 * z + A2 * z * z)).Magnitude;
    }

    internal static BiquadCoefficients Design(EqBand band, int sampleRate, bool bypass) =>
        bypass || band.Bypass ? Identity : Shape(band, sampleRate);

    /// <summary>The band's filter shape, ignoring its bypass flag.</summary>
    internal static BiquadCoefficients Shape(EqBand band, int sampleRate)
    {
        double w = 2 * Math.PI * band.FrequencyHz / sampleRate;
        double c = Math.Cos(w), alpha = Math.Sin(w) / (2 * band.Q);
        double a = Math.Pow(10, band.GainDb / 40), beta = 2 * Math.Sqrt(a) * alpha;
        // 1 - cos(w) without cancellation for very low frequencies at high sample rates.
        double oneMinusCos = 2 * Math.Sin(w / 2) * Math.Sin(w / 2);
        return band.Type switch
        {
            EqFilterType.Peak => Normalized(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a),
            EqFilterType.LowPass => Normalized(oneMinusCos / 2, oneMinusCos, oneMinusCos / 2, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.HighPass => Normalized((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.Notch => Normalized(1, -2 * c, 1, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.LowShelf => Normalized(a * (a + 1 - (a - 1) * c + beta), 2 * a * (a - 1 - (a + 1) * c),
                a * (a + 1 - (a - 1) * c - beta), a + 1 + (a - 1) * c + beta,
                -2 * (a - 1 + (a + 1) * c), a + 1 + (a - 1) * c - beta),
            EqFilterType.HighShelf => Normalized(a * (a + 1 + (a - 1) * c + beta), -2 * a * (a - 1 + (a + 1) * c),
                a * (a + 1 + (a - 1) * c - beta), a + 1 - (a - 1) * c + beta,
                2 * (a - 1 - (a + 1) * c), a + 1 - (a - 1) * c - beta),
            _ => throw new ArgumentOutOfRangeException(nameof(band))
        };
    }
}

/// <summary>
/// Double-precision direct form I biquad. Its state is the actual past input and output, so a coefficient change
/// keeps the running signal history; new coefficients are reached by per-sample linear interpolation, which
/// stays inside the (convex) stability region and replaces the click a filter restarted from zero would make.
/// </summary>
internal sealed class Biquad
{
    private const double Flush = 1e-30;
    private double b0, b1, b2, a1, a2;
    private double db0, db1, db2, da1, da2;
    private int rampRemaining;
    private BiquadCoefficients target;
    private double x1, x2, y1, y2;

    internal Biquad(BiquadCoefficients coefficients) => Assign(target = coefficients);

    internal BiquadCoefficients Target => target;
    internal bool Ramping => rampRemaining > 0;
    internal double OutputHistory => y1;

    /// <summary>Moves toward new coefficients over the given number of samples, keeping the filter history.</summary>
    internal void SetTarget(BiquadCoefficients coefficients, int rampSamples)
    {
        if (coefficients == target) return;
        target = coefficients;
        if (rampSamples <= 1) { Assign(coefficients); rampRemaining = 0; return; }
        db0 = (coefficients.B0 - b0) / rampSamples; db1 = (coefficients.B1 - b1) / rampSamples; db2 = (coefficients.B2 - b2) / rampSamples;
        da1 = (coefficients.A1 - a1) / rampSamples; da2 = (coefficients.A2 - a2) / rampSamples;
        rampRemaining = rampSamples;
    }

    internal double Process(double input)
    {
        if (rampRemaining > 0)
        {
            if (--rampRemaining == 0) Assign(target);
            else { b0 += db0; b1 += db1; b2 += db2; a1 += da1; a2 += da2; }
        }
        else if (transparent)
        {
            // A settled flat band (numerator equals denominator) is exactly the identity; keep its history consistent.
            x2 = x1; x1 = input; y2 = y1; y1 = input;
            return input;
        }
        double output = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        if (!double.IsFinite(output)) { x1 = x2 = y1 = y2 = 0; return 0; }
        if (Math.Abs(output) < Flush) output = 0;
        x2 = x1; x1 = input; y2 = y1; y1 = output;
        return output;
    }

    private void Assign(BiquadCoefficients c)
    {
        b0 = c.B0; b1 = c.B1; b2 = c.B2; a1 = c.A1; a2 = c.A2;
        transparent = c.B0 == 1 && c.B1 == c.A1 && c.B2 == c.A2;
    }
    private bool transparent;
}

/// <summary>Exact small-signal EQ response from the same coefficients as the running processor; excludes nonlinear dynamics.</summary>
public static class EqualizerResponse
{
    public static double[] Decibels(DspSettings settings, int sampleRate, IReadOnlyList<double> frequencies)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(frequencies);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        var copy = settings.ValidatedCopy(sampleRate);
        var filters = copy.Bands.Select(b => BiquadCoefficients.Design(b, sampleRate, copy.EqBypass)).ToArray();
        var response = new double[frequencies.Count];
        for (int i = 0; i < response.Length; i++)
        {
            DspSettings.Range(frequencies[i], 1, sampleRate * 0.499, nameof(frequencies));
            double magnitude = 1;
            foreach (var filter in filters) magnitude *= filter.Magnitude(2 * Math.PI * frequencies[i] / sampleRate);
            response[i] = 20 * Math.Log10(Math.Max(1e-12, magnitude));
        }
        return response;
    }
}

/// <summary>Band coefficients (designed as if not bypassed) plus which bands are in the signal path.</summary>
internal sealed class EqualizerDesign(BiquadCoefficients[] coefficients, bool[] active)
{
    internal BiquadCoefficients[] Coefficients { get; } = coefficients;
    internal bool[] Active { get; } = active;
}

/// <summary>
/// Ten bands per channel. Every band filter keeps running with its own coefficients, also while bypassed, so its
/// history is always valid: a gain/frequency/Q/type change glides the coefficients (20 ms) and a bypass toggle is a
/// smooth wet/dry crossfade (20 ms, smoothstep) instead of switching to a filter whose state is wrong. Settled bands
/// are exact: fully active output is the filter output, fully bypassed output is the input.
/// </summary>
internal sealed class EqualizerBank
{
    private sealed class Stage(BiquadCoefficients coefficients, bool active)
    {
        public readonly Biquad Filter = new(coefficients);
        public double Wet = active ? 1 : 0;
        public double WetStep;
    }
    private readonly Stage[][] stages;
    internal EqualizerBank(int channels, EqualizerDesign design)
    {
        stages = new Stage[channels][];
        for (int channel = 0; channel < channels; channel++)
        {
            stages[channel] = new Stage[design.Coefficients.Length];
            for (int band = 0; band < design.Coefficients.Length; band++) stages[channel][band] = new Stage(design.Coefficients[band], design.Active[band]);
        }
    }

    internal static EqualizerDesign Design(DspSettings settings, int sampleRate)
    {
        var coefficients = new BiquadCoefficients[settings.Bands.Count];
        var active = new bool[coefficients.Length];
        for (int band = 0; band < coefficients.Length; band++)
        {
            coefficients[band] = BiquadCoefficients.Shape(settings.Bands[band], sampleRate);
            active[band] = !settings.EqBypass && !settings.Bands[band].Bypass;
        }
        return new(coefficients, active);
    }

    /// <summary>Audio thread; allocation free.</summary>
    internal void Retarget(EqualizerDesign design, int rampSamples)
    {
        foreach (var channel in stages)
            for (int band = 0; band < channel.Length; band++)
            {
                var stage = channel[band];
                stage.Filter.SetTarget(design.Coefficients[band], rampSamples);
                double target = design.Active[band] ? 1 : 0;
                stage.WetStep = stage.Wet == target ? 0 : (target - stage.Wet) / rampSamples;
                if (stage.WetStep != 0 && Math.Abs(stage.WetStep) < 1.0 / rampSamples) stage.WetStep = Math.Sign(stage.WetStep) * (1.0 / rampSamples);
            }
    }

    internal Biquad Filter(int channel, int band) => stages[channel][band].Filter;

    internal double Process(int channel, double input)
    {
        foreach (var stage in stages[channel])
        {
            double filtered = stage.Filter.Process(input);
            if (stage.WetStep != 0)
            {
                stage.Wet += stage.WetStep;
                if (stage.Wet >= 1) { stage.Wet = 1; stage.WetStep = 0; }
                else if (stage.Wet <= 0) { stage.Wet = 0; stage.WetStep = 0; }
            }
            if (stage.Wet == 1) input = filtered;
            else if (stage.Wet != 0)
            {
                double w = stage.Wet * stage.Wet * (3 - 2 * stage.Wet);
                input = filtered * w + input * (1 - w);
            }
        }
        return input;
    }
}
