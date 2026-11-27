namespace Soltex.Audio.Engine;

/// <summary>
/// Opt-in, channel-linked lookahead peak limiter. Below the ceiling it is a pure delay (bit-exact gain of one).
/// Gain is the sliding minimum of the required gain over the lookahead window, released exponentially and then
/// box-averaged over the same window, which makes the attack a smooth ramp that completes exactly on the peak.
/// The optional true-peak detector also examines three 4x-interpolated points between samples, so a later
/// resampler or DAC does not rebuild an inter-sample peak above the ceiling. All storage is allocated here.
/// </summary>
internal sealed class SafetyLimiter
{
    private const int InterpolationTaps = 8;
    private static readonly double[][] Interpolation = CreateInterpolation();
    private readonly int channels, lookahead, detectorDelay;
    private readonly double[] signal;      // delay line, (lookahead - 1 + detectorDelay + 1) frames
    private readonly int signalFrames;
    private readonly double[] history;     // last InterpolationTaps samples per channel for true-peak estimation
    private readonly double[] windowValue; private readonly long[] windowIndex; // monotonic deque for the sliding minimum
    private readonly double[] box;
    private int windowHead, windowCount, boxPosition, signalPosition, historyPosition;
    private long index;
    private double boxSum, released = 1, threshold, releaseCoefficient;
    private bool truePeak;
    private readonly int sampleRate;

    internal int LatencyFrames => lookahead - 1 + detectorDelay;

    internal SafetyLimiter(int sampleRate, int channels, DspSettings settings)
    {
        this.sampleRate = sampleRate; this.channels = channels;
        lookahead = Math.Max(8, (int)Math.Round(sampleRate * 0.0015));
        // The detector judges sample m = newest - 3 together with the interval (m - 1, m), so the interval's
        // peak constrains both neighbours: each output sample's window covers its own and the next L - 1 requirements.
        detectorDelay = InterpolationTaps / 2 - 1;
        signalFrames = lookahead + detectorDelay;
        signal = new double[signalFrames * channels];
        history = new double[InterpolationTaps * channels];
        windowValue = new double[lookahead]; windowIndex = new long[lookahead];
        box = new double[lookahead]; Array.Fill(box, 1.0); boxSum = lookahead;
        Configure(settings);
    }

    internal void Configure(DspSettings settings)
    {
        threshold = settings.OutputCeiling; truePeak = settings.Limiter.TruePeak;
        releaseCoefficient = Math.Exp(-1 / (settings.Limiter.ReleaseMs * 0.001 * sampleRate));
    }

    /// <summary>Replaces each value with the delayed, limited value. Returns the applied gain.</summary>
    internal double ProcessFrame(double[] frame)
    {
        // 1) Detector: peak of the frame that is detectorDelay frames old, including inter-sample estimates toward the previous sample.
        double peak = 0;
        for (int channel = 0; channel < channels; channel++)
        {
            int baseIndex = channel * InterpolationTaps;
            history[baseIndex + historyPosition] = frame[channel];
            int oldest = (historyPosition + 1) % InterpolationTaps; // ring start = oldest sample; oldest + 7 = newest
            double centre = history[baseIndex + (oldest + InterpolationTaps / 2) % InterpolationTaps];
            double channelPeak = Math.Abs(centre);
            if (truePeak)
            {
                foreach (double[] taps in Interpolation)
                {
                    double value = 0;
                    for (int k = 0; k < InterpolationTaps; k++) value += taps[k] * history[baseIndex + (oldest + k) % InterpolationTaps];
                    channelPeak = Math.Max(channelPeak, Math.Abs(value));
                }
            }
            peak = Math.Max(peak, channelPeak);
        }
        historyPosition = (historyPosition + 1) % InterpolationTaps;
        double required = peak > threshold ? threshold / peak : 1;

        // 2) Sliding minimum over the lookahead window [index - lookahead + 1, index]. Expired entries leave before the push:
        // a rising gain requirement (the falling side of a low-frequency crest) keeps every entry, and pushing first would
        // need lookahead + 1 slots and overwrite the window minimum.
        while (windowCount > 0 && windowIndex[windowHead] <= index - lookahead) { windowHead = (windowHead + 1) % lookahead; windowCount--; }
        while (windowCount > 0 && windowValue[(windowHead + windowCount - 1) % lookahead] >= required) windowCount--;
        int tail = (windowHead + windowCount) % lookahead;
        windowValue[tail] = required; windowIndex[tail] = index; windowCount++;
        double minimum = windowValue[windowHead];
        index++;

        // 3) Instant attack, exponential release, never above the window minimum.
        released = minimum < released ? minimum : minimum + (released - minimum) * releaseCoefficient;
        if (minimum - released < 1e-15) released = minimum;

        // 4) Box average over the window: a linear attack ramp that reaches the minimum on the peak sample.
        boxSum += released - box[boxPosition];
        box[boxPosition] = released;
        if (++boxPosition == lookahead)
        {
            boxPosition = 0;
            double exact = 0; foreach (double value in box) exact += value; boxSum = exact; // no long-run rounding drift
        }
        double gain = Math.Min(1, boxSum / lookahead);

        // 5) Delay the signal by lookahead - 1 + detectorDelay frames so the detected peak meets its gain.
        int read = (signalPosition + 1) % signalFrames;
        for (int channel = 0; channel < channels; channel++)
        {
            signal[signalPosition * channels + channel] = frame[channel];
            frame[channel] = signal[read * channels + channel] * gain;
        }
        signalPosition = read;
        return gain;
    }

    private static double[][] CreateInterpolation()
    {
        // Kaiser-windowed sinc, 8 taps, fractional positions 1/4, 1/2, 3/4 between taps 3 and 4; unity DC gain.
        double[][] phases = new double[3][];
        for (int p = 0; p < 3; p++)
        {
            double fraction = (p + 1) / 4.0, sum = 0;
            phases[p] = new double[InterpolationTaps];
            for (int k = 0; k < InterpolationTaps; k++)
            {
                double t = k - (InterpolationTaps / 2 - 1) - fraction;
                double sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double x = t / (InterpolationTaps / 2.0);
                double window = Math.Abs(x) >= 1 ? 0 : Kaiser.Window(x, 5);
                phases[p][k] = sinc * window; sum += phases[p][k];
            }
            for (int k = 0; k < InterpolationTaps; k++) phases[p][k] /= sum;
        }
        return phases;
    }
}

internal static class Kaiser
{
    /// <summary>Kaiser window at x in [-1, 1].</summary>
    internal static double Window(double x, double beta) => BesselI0(beta * Math.Sqrt(Math.Max(0, 1 - x * x))) / BesselI0(beta);

    internal static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 64; k++)
        {
            term *= half / k; double add = term * term; sum += add;
            if (add < sum * 1e-17) break;
        }
        return sum;
    }
}
