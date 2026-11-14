using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

internal static class ExtendedDspChecks
{
    private const int Rate = 48000;
    private static readonly DspSettings Unity = new() { HeadroomDb = 0 };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Rms(ReadOnlySpan<float> data) { double energy = 0; foreach (float sample in data) energy += sample * sample; return Math.Sqrt(energy / data.Length); }
    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(1e-20, ratio));
    private static float[] Noise(int frames, double rms)
    {
        var random = new Random(7261); var samples = new float[frames];
        for (int i = 0; i < frames; i++) samples[i] = (float)((random.NextDouble() * 2 - 1) * rms * Math.Sqrt(3));
        return samples;
    }

    internal static void SpectralQuality()
    {
        var settings = Unity with { NoiseSuppression = new(true, -40, 24) };
        var noise = Noise(Rate * 3, .01); var dryNoise = (float[])noise.Clone();
        var processor = new PcmDspProcessor(Rate, 1, settings); processor.Process(noise);
        double reduction = Db(Rms(noise.AsSpan(Rate)) / Rms(dryNoise.AsSpan(Rate)));
        Check(reduction < -3, $"Stationary floor was not reduced: {reduction:F2}dB");
        var mixture = Noise(Rate * 3, .01);
        for (int i = 0; i < mixture.Length; i++) mixture[i] += (float)(.1 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
        new PcmDspProcessor(Rate, 1, settings).Process(mixture);
        int delay = processor.NoiseSuppressionLatencyFrames; double sine = 0, cosine = 0;
        for (int i = Rate; i < mixture.Length; i++) { double phase = 2 * Math.PI * 1000 * (i - delay) / Rate; sine += mixture[i] * Math.Sin(phase); cosine += mixture[i] * Math.Cos(phase); }
        double amplitude = 2 * Math.Sqrt(sine * sine + cosine * cosine) / (mixture.Length - Rate);
        Check(Math.Abs(Db(amplitude / .1)) < .5, $"Strong tone lost: amplitude {amplitude}");
        double residual = 0;
        for (int i = Rate; i < mixture.Length; i++) { double error = mixture[i] - .1 * Math.Sin(2 * Math.PI * 1000 * (i - delay) / Rate); residual += error * error; }
        double snrImprovement = Db(.01 / Math.Sqrt(residual / (mixture.Length - Rate)));
        Check(snrImprovement > 2.5, $"Mixed signal SNR improvement only {snrImprovement:F2}dB");
        Console.WriteLine($"  Measured stationary noise {reduction:F2} dB; strong tone {Db(amplitude / .1):F3} dB; SNR improvement {snrImprovement:F2} dB; latency {delay} frames.");
    }

    internal static void SpectralReconstructionAndUpdates()
    {
        foreach (int rate in new[] { 8000, 16000, 44100, 48000, 96000, 192000 })
        {
            var settings = Unity with { Bands = DspSettings.FlatBands().Select(b => b with { FrequencyHz = Math.Min(b.FrequencyHz, rate * .45) }).ToArray(), NoiseSuppression = new(true, -40, 0) };
            var source = Noise(rate, .05); var samples = (float[])source.Clone();
            var p = new PcmDspProcessor(rate, 1, settings); p.Process(samples);
            int delay = p.NoiseSuppressionLatencyFrames;
            Check(samples.AsSpan(0, delay).ToArray().All(x => Math.Abs(x) < 1e-7), "Unexpected pre-delay output");
            for (int i = delay; i < samples.Length; i++) Check(Math.Abs(samples[i] - source[i - delay]) < 1e-6, "Overlap/add reconstruction or latency failed");
            var split = (float[])source.Clone(); var other = new PcmDspProcessor(rate, 1, settings);
            for (int i = 0; i < split.Length; i += 137) other.Process(split.AsSpan(i, Math.Min(137, split.Length - i)));
            Check(samples.SequenceEqual(split), "Spectral output depends on callback partition");
            other.UpdateSettings(settings with { GainDb = 0, NoiseSuppression = new(true, -45, 0) });
            var tail = new float[delay]; other.Process(tail);
            for (int i = 0; i < tail.Length; i++) Check(Math.Abs(tail[i] - source[source.Length - delay + i]) < 1e-6, "Control update dropped buffered audio");
        }
    }

    internal static void StereoControls()
    {
        float[] Input() { var samples = new float[Rate * 2]; for (int i = 0; i < Rate; i++) samples[i * 2] = (float)(.1 * Math.Sin(2 * Math.PI * 300 * i / Rate)); return samples; }
        foreach (var stereo in new[] { new StereoSettings(true, Width: 0), new StereoSettings(true, Mono: true) })
        {
            var data = Input(); new PcmDspProcessor(Rate, 2, Unity with { Stereo = stereo }).Process(data);
            for (int i = Rate; i < data.Length; i += 2) Check(Math.Abs(data[i] - data[i + 1]) < 1e-7, "Mono/width0 failed");
        }
        var swapped = Input(); new PcmDspProcessor(Rate, 2, Unity with { Stereo = new(true, SwapChannels: true) }).Process(swapped);
        Check(swapped.Where((_, i) => i > Rate && i % 2 == 0).All(x => Math.Abs(x) < 1e-7), "Swap retained original channel");
        Check(Rms(swapped.AsSpan(Rate)) > .045, "Swap lost signal");
        var panned = Input(); new PcmDspProcessor(Rate, 2, Unity with { Stereo = new(true, Pan: 1) }).Process(panned);
        Check(Rms(panned.AsSpan(Rate)) < 1e-7, "Right pan did not suppress left");
        double Crossfeed(double frequency)
        {
            var data = new float[Rate * 2]; for (int i = 0; i < Rate; i++) data[i * 2] = (float)(.1 * Math.Sin(2 * Math.PI * frequency * i / Rate));
            new PcmDspProcessor(Rate, 2, Unity with { Stereo = new(true, Crossfeed: 1) }).Process(data);
            return Rms(data.Where((_, i) => i > Rate && i % 2 == 1).ToArray());
        }
        double low = Crossfeed(100), high = Crossfeed(8000);
        Check(low > .03 && Db(high / low) < -18, "Headphone crossfeed lacks low-pass separation");
        Console.WriteLine($"  Crossfeed 8kHz vs100Hz: {Db(high / low):F2}dB.");
        var mono = Noise(Rate, .05); var expected = (float[])mono.Clone();
        new PcmDspProcessor(Rate, 1, Unity with { Stereo = new(true, Width: 2, Pan: 1, Crossfeed: 1, Mono: true) }).Process(mono);
        Check(mono.SequenceEqual(expected), "Stereo processing changed mono input");
    }

    internal static void InputAndValidation()
    {
        foreach (int selected in new[] { 0, 1 })
        {
            float[] samples = [.2f, -.4f, .1f, -.3f];
            new PcmDspProcessor(Rate, 2, Unity with { InputChannel = selected }).Process(samples);
            Check(samples[0] == samples[1] && samples[2] == samples[3], "Selected input was not duplicated");
            Check(Math.Abs(samples[0] - (selected == 0 ? .2f : -.4f)) < 1e-7, "Incorrect physical input selected");
        }
        var invalid = new[] { Unity with { InputChannel = 2 }, Unity with { InputChannel = -1 }, Unity with { NoiseSuppression = new(true, double.NaN) }, Unity with { NoiseSuppression = new(true, ReductionDb: 37) }, Unity with { Stereo = new(true, Width: 3) }, Unity with { Stereo = new(true, Pan: double.PositiveInfinity) } };
        foreach (var settings in invalid)
        {
            bool rejected = false; try { _ = new PcmDspProcessor(Rate, 2, settings); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Malformed extended setting accepted");
        }
    }

    internal static void RealtimeSafety()
    {
        var settings = Unity with { NoiseSuppression = new(true, -45, 24), Stereo = new(true, Width: 1.2, Crossfeed: .4) };
        var processor = new PcmDspProcessor(Rate, 2, settings); var samples = Noise(512 * 2, .01);
        for (int i = 0; i < 100; i++) processor.Process(samples);
        processor.UpdateSettings(settings with { GainDb = -3 });
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) processor.Process(samples);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Extended DSP allocated callback memory");
        samples[0] = float.NaN; samples[1] = float.PositiveInfinity;
        processor.Process(samples);
        Check(samples.All(float.IsFinite), "Extended DSP propagated nonfinite input");
        Check(processor.SanitizedSamples == 2, "Invalid inputs were not accounted for");
    }
}
