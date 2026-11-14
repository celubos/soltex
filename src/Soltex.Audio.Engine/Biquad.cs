namespace Soltex.Audio.Engine;

// Double-precision transposed direct form II; RBJ/W3C Audio EQ Cookbook equations.
internal sealed class Biquad
{
    private readonly double b0, b1, b2, a1, a2;
    private double z1, z2;

    private Biquad(double numerator0, double numerator1, double numerator2,
        double denominator0, double denominator1, double denominator2)
    {
        b0 = numerator0 / denominator0; b1 = numerator1 / denominator0; b2 = numerator2 / denominator0;
        a1 = denominator1 / denominator0; a2 = denominator2 / denominator0;
    }

    internal double Process(double input)
    {
        double output = b0 * input + z1;
        z1 = b1 * input - a1 * output + z2;
        z2 = b2 * input - a2 * output;
        if (!double.IsFinite(output) || !double.IsFinite(z1) || !double.IsFinite(z2))
        { z1 = z2 = 0; return 0; }
        if (Math.Abs(z1) < 1e-30) z1 = 0;
        if (Math.Abs(z2) < 1e-30) z2 = 0;
        return output;
    }

    internal double Magnitude(double radians)
    {
        var z = System.Numerics.Complex.FromPolarCoordinates(1, -radians);
        return ((b0 + b1 * z + b2 * z * z) / (1 + a1 * z + a2 * z * z)).Magnitude;
    }

    internal static Biquad Create(EqBand band, int sampleRate, bool bypass)
    {
        if (bypass || band.Bypass) return new(1, 0, 0, 1, 0, 0);
        double w = 2 * Math.PI * band.FrequencyHz / sampleRate;
        double c = Math.Cos(w), alpha = Math.Sin(w) / (2 * band.Q);
        double a = Math.Pow(10, band.GainDb / 40), beta = 2 * Math.Sqrt(a) * alpha;
        return band.Type switch
        {
            EqFilterType.Peak => new(1 + alpha * a, -2 * c, 1 - alpha * a, 1 + alpha / a, -2 * c, 1 - alpha / a),
            EqFilterType.LowPass => new((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.HighPass => new((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.Notch => new(1, -2 * c, 1, 1 + alpha, -2 * c, 1 - alpha),
            EqFilterType.LowShelf => new(a * (a + 1 - (a - 1) * c + beta), 2 * a * (a - 1 - (a + 1) * c),
                a * (a + 1 - (a - 1) * c - beta), a + 1 + (a - 1) * c + beta,
                -2 * (a - 1 + (a + 1) * c), a + 1 + (a - 1) * c - beta),
            EqFilterType.HighShelf => new(a * (a + 1 + (a - 1) * c + beta), -2 * a * (a - 1 + (a + 1) * c),
                a * (a + 1 + (a - 1) * c - beta), a + 1 - (a - 1) * c + beta,
                2 * (a - 1 - (a + 1) * c), a + 1 - (a - 1) * c - beta),
            _ => throw new ArgumentOutOfRangeException(nameof(band))
        };
    }
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
        var filters = copy.Bands.Select(b => Biquad.Create(b, sampleRate, copy.EqBypass)).ToArray();
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

internal sealed class EqualizerBank
{
    private readonly Biquad[][] filters;
    internal EqualizerBank(int sampleRate, int channels, DspSettings settings)
    {
        filters = new Biquad[channels][];
        for (int channel = 0; channel < channels; channel++)
            filters[channel] = settings.Bands.Select(b => Biquad.Create(b, sampleRate, settings.EqBypass)).ToArray();
    }

    internal double Process(int channel, double input)
    {
        foreach (var filter in filters[channel]) input = filter.Process(input);
        return input;
    }
}
