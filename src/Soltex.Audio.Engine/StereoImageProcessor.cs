namespace Soltex.Audio.Engine;

internal sealed class StereoImageProcessor
{
    private const double Snap = 1e-15;
    private readonly double[] leftDelay, rightDelay;
    private readonly double lowPassCoefficient, smoothing;
    private double lowLeft, lowRight, width = 1, pan, crossfeed, swap, mono;
    private int position;

    internal StereoImageProcessor(int sampleRate)
    {
        int delay = Math.Max(1, (int)Math.Round(sampleRate * .0003));
        leftDelay = new double[delay]; rightDelay = new double[delay];
        lowPassCoefficient = Math.Exp(-2 * Math.PI * 700 / sampleRate);
        smoothing = Math.Exp(-1 / (.01 * sampleRate));
    }

    internal void Process(ref double left, ref double right, StereoSettings settings)
    {
        width = Smooth(width, settings.Enabled ? settings.Width : 1, smoothing);
        pan = Smooth(pan, settings.Enabled ? settings.Pan : 0, smoothing);
        crossfeed = Smooth(crossfeed, settings.Enabled ? settings.Crossfeed : 0, smoothing);
        swap = Smooth(swap, settings.Enabled && settings.SwapChannels ? 1 : 0, smoothing);
        mono = Smooth(mono, settings.Enabled && settings.Mono ? 1 : 0, smoothing);
        double mid = (left + right) * .5, side = (left - right) * .5 * width * (1 - mono) * (1 - 2 * swap);
        left = mid + side; right = mid - side;
        // The crossfeed low-pass runs even while off; snapping keeps digital silence out of subnormal numbers.
        lowLeft = Smooth(lowLeft, left, lowPassCoefficient);
        lowRight = Smooth(lowRight, right, lowPassCoefficient);
        double delayedLeft = leftDelay[position], delayedRight = rightDelay[position];
        leftDelay[position] = lowLeft; rightDelay[position] = lowRight;
        position = (position + 1) % leftDelay.Length;
        // Normalize the mix so correlated low frequencies cannot gain amplitude.
        left = (left + crossfeed * delayedRight) / (1 + crossfeed);
        right = (right + crossfeed * delayedLeft) / (1 + crossfeed);
        left *= Math.Cos(Math.Max(0, pan) * Math.PI / 2);
        right *= Math.Cos(Math.Max(0, -pan) * Math.PI / 2);
    }

    private static double Smooth(double value, double target, double coefficient)
    {
        double next = target + (value - target) * coefficient;
        return Math.Abs(next - target) < Snap ? target : next;
    }
}
