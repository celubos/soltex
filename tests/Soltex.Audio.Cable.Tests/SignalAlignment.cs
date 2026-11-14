namespace Soltex.Audio.Cable.Tests;

internal sealed record AlignmentWindow(double Correlation, double Gain, double ReferenceRms, double ReturnRms, double LagMilliseconds);
internal sealed record AlignmentResult(IReadOnlyList<AlignmentWindow> Windows, double LagSpreadMilliseconds);

internal static class SignalAlignment
{
    internal const double MinimumRms = .00001;

    // Source and return use independent capture clocks. Fit three short windows with a bounded
    // ±500ms lag search; do not call this lag acoustic/round-trip latency. Fine alignment retains
    // original samples after an anti-aliased coarse search, so an arbitrary sample offset is allowed.
    internal static AlignmentResult Analyze(float[] reference, float[] returned, int rate, double expectedGain)
    {
        if (reference.Length < rate * 2.5 || returned.Length < rate * 2.5)
            throw new InconclusiveException("At least 2.5 seconds of complete input and return samples are required.");
        if (reference.Any(sample => !float.IsFinite(sample)) || returned.Any(sample => !float.IsFinite(sample)))
            throw new IOException("Nonfinite alignment input.");
        int step = Math.Max(1, rate / 6000), bound = rate / 2, width = rate / 3;
        float[] coarseA = Average(reference, step), coarseB = Average(returned, step);
        List<AlignmentWindow> windows = [];
        try
        {
            foreach (double fraction in new[] { .65, 1.05, 1.45 })
            {
                int start = (int)(fraction * rate);
                double referenceRms = Rms(reference.AsSpan(start, width));
                if (referenceRms < MinimumRms)
                    throw new InconclusiveException("Microphone signal is too quiet for a credible path proof; silence is not a pass.");
                int bestCoarse = 0; double best = double.NegativeInfinity;
                for (int lag = -bound / step; lag <= bound / step; lag++)
                {
                    double score = Fit(coarseA, coarseB, start / step, width / step, lag, 1).Correlation;
                    if (score > best) { best = score; bestCoarse = lag; }
                }
                int candidate = bestCoarse * step, bestLag = candidate; best = double.NegativeInfinity;
                for (int lag = candidate - step; lag <= candidate + step; lag++)
                {
                    double score = Fit(reference, returned, start, width, lag, step).Correlation;
                    if (score > best) { best = score; bestLag = lag; }
                }
                var fit = Fit(reference, returned, start, width, bestLag, 1);
                if (fit.Correlation < .85 || fit.Gain <= 0 || Math.Abs(20 * Math.Log10(fit.Gain / expectedGain)) > 1)
                    throw new InconclusiveException($"Return does not establish the expected microphone path: correlation={fit.Correlation:F4}, gain={fit.Gain:F6}, expected={expectedGain:F6}.");
                if (Math.Abs(bestLag) >= bound - step * 2)
                    throw new InconclusiveException("Best alignment reached the search boundary; latency exceeds the qualified window.");
                windows.Add(new(fit.Correlation, fit.Gain, referenceRms, fit.ReturnRms, 1000.0 * bestLag / rate));
            }
            double spread = windows.Max(item => item.LagMilliseconds) - windows.Min(item => item.LagMilliseconds);
            if (spread > 5) throw new InconclusiveException("Clock alignment changed by more than 5ms across the short observation.");
            return new(windows.AsReadOnly(), spread);
        }
        finally { Array.Clear(coarseA); Array.Clear(coarseB); }
    }

    internal static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) throw new ArgumentException("Samples are required.", nameof(samples));
        double sum = 0;
        foreach (float value in samples) { if (!float.IsFinite(value)) throw new IOException("Nonfinite sample."); sum += (double)value * value; }
        return Math.Sqrt(sum / samples.Length);
    }

    private static float[] Average(float[] source, int step)
    {
        float[] result = new float[source.Length / step];
        for (int index = 0; index < result.Length; index++)
        {
            double sum = 0;
            for (int offset = 0; offset < step; offset++) sum += source[index * step + offset];
            result[index] = (float)(sum / step);
        }
        return result;
    }

    private static (double Correlation, double Gain, double ReturnRms) Fit(float[] a, float[] b, int start, int width, int lag, int step)
    {
        if (start + lag < 0 || start + width + lag > b.Length) return (double.NegativeInfinity, 0, 0);
        double aa = 0, bb = 0, ab = 0, sa = 0, sb = 0; int count = 0;
        for (int index = start; index < start + width; index += step)
        {
            double x = a[index], y = b[index + lag];
            aa += x * x; bb += y * y; ab += x * y; sa += x; sb += y; count++;
        }
        double centeredA = aa - sa * sa / count, centeredB = bb - sb * sb / count;
        double centeredAB = ab - sa * sb / count;
        return (centeredA > 1e-20 && centeredB > 1e-20 ? centeredAB / Math.Sqrt(centeredA * centeredB) : 0,
            centeredA > 1e-20 ? centeredAB / centeredA : 0, Math.Sqrt(bb / count));
    }
}
