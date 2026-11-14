using System.Numerics;

namespace Soltex.Audio.Engine;

/// <summary>Streaming 50%-overlap square-root Hann analysis/synthesis with bounded Wiener-like gains.
/// All arrays and FFT roots are allocated on the control thread. Each instance owns one channel.</summary>
internal sealed class SpectralNoiseSuppressor
{
    private readonly double[] input, output, window, gains;
    private readonly Complex[] bins, roots;
    private readonly int[] reversed;
    private double noisePower, minimumGain;
    private readonly double smoothing;
    private int position, untilTransform;
    internal int LatencyFrames => input.Length;

    internal SpectralNoiseSuppressor(int sampleRate, SpectralNoiseSuppressionSettings settings)
    {
        int size = 1; while (size < sampleRate * .02) size <<= 1;
        input = new double[size]; output = new double[size]; window = new double[size]; gains = new double[size];
        bins = new Complex[size]; roots = new Complex[size / 2]; reversed = new int[size];
        int bits = BitOperations.Log2((uint)size);
        for (int i = 0; i < size; i++)
        {
            window[i] = Math.Sqrt(.5 - .5 * Math.Cos(2 * Math.PI * i / size));
            gains[i] = 1;
            int value = i, reverse = 0;
            for (int b = 0; b < bits; b++) { reverse = (reverse << 1) | (value & 1); value >>= 1; }
            reversed[i] = reverse;
        }
        for (int i = 0; i < roots.Length; i++) roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / size);
        Configure(settings);
        smoothing = Math.Exp(-(size / 2.0) / (.04 * sampleRate));
        untilTransform = size / 2;
    }

    internal void Configure(SpectralNoiseSuppressionSettings settings)
    {
        noisePower = Math.Pow(10, settings.NoiseFloorDb / 10) * input.Length * .5;
        minimumGain = Math.Pow(10, -settings.ReductionDb / 20);
    }

    internal double Process(double sample)
    {
        double result = output[position]; output[position] = 0; input[position] = sample;
        position = (position + 1) % input.Length;
        if (--untilTransform == 0) { TransformFrame(); untilTransform = input.Length / 2; }
        return result;
    }

    private void TransformFrame()
    {
        int size = input.Length;
        for (int i = 0; i < size; i++) bins[i] = new(input[(position + i) % size] * window[i], 0);
        Fft(false);
        // One gain for each conjugate pair keeps the inverse transform real.
        for (int i = 0; i <= size / 2; i++)
        {
            double power = bins[i].Real * bins[i].Real + bins[i].Imaginary * bins[i].Imaginary;
            double target = Math.Max(minimumGain, Math.Sqrt(Math.Max(0, 1 - noisePower / Math.Max(power, 1e-30))));
            double gain = target + (gains[i] - target) * smoothing;
            gains[i] = gain; bins[i] *= gain;
            if (i > 0 && i < size / 2) bins[size - i] *= gain;
        }
        Fft(true);
        for (int i = 0; i < size; i++) output[(position + i) % size] += bins[i].Real * window[i] / size;
    }

    private void Fft(bool inverse)
    {
        for (int i = 0; i < bins.Length; i++) if (reversed[i] > i) (bins[i], bins[reversed[i]]) = (bins[reversed[i]], bins[i]);
        for (int length = 2; length <= bins.Length; length <<= 1)
        {
            int half = length / 2, stride = bins.Length / length;
            for (int start = 0; start < bins.Length; start += length)
                for (int j = 0; j < half; j++)
                {
                    Complex root = roots[j * stride]; if (inverse) root = Complex.Conjugate(root);
                    Complex even = bins[start + j], odd = bins[start + j + half] * root;
                    bins[start + j] = even + odd; bins[start + j + half] = even - odd;
                }
        }
    }
}
