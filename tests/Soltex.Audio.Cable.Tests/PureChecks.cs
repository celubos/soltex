using Soltex.Audio.Engine;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Cable.Tests;

internal static class PureChecks
{
    internal static void Run()
    {
        SignalMeasurement.SelfTest();
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            count++; Console.WriteLine("PASS " + name);
        }
        string[] valid = ["--allow-cable-transport", "--cable-render", "r", "--cable-capture", "c", "--physical-output", "p", "--receipt", "D:\\test.json"];
        Check(Options.Parse(valid).PhysicalOutput == "p", "explicit endpoint contract");
        bool invalid = false;
        try { Options.Parse([]); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "live mode requires opt-in and every endpoint");
        invalid = false; string[] duplicated = (string[])valid.Clone(); duplicated[6] = "r";
        try { Options.Parse(duplicated); } catch (ArgumentException) { invalid = true; }
        Check(invalid, "feedback/duplicate endpoint IDs rejected");

        const int rate = 48000;
        float[] tone = Enumerable.Range(0, rate).Select(index => (float)(.002 * Math.Sin(2 * Math.PI * 1619 * index / rate))).ToArray();
        DspSettings setting = new() { HeadroomDb = 0, EqBypass = true };
        PcmDspProcessor processor = new(rate, 1, setting);
        float[] unity = (float[])tone.Clone(); processor.Process(unity);
        processor.UpdateSettings(setting with { GainDb = -6.020599913279624 });
        float[] half = (float[])tone.Clone(); processor.Process(half);
        double a = SignalMeasurement.Amplitude(unity.AsSpan(rate / 2), rate, 1619);
        double b = SignalMeasurement.Amplitude(half.AsSpan(rate / 2), rate, 1619);
        Check(Math.Abs(SignalMeasurement.Decibels(b / a) + 6.020599913279624) < .001, "actual DSP half gain matches acceptance math after smoothing");
        processor.UpdateSettings(setting with { Mute = true });
        float[] mute = (float[])tone.Clone(); processor.Process(mute);
        Check(SignalMeasurement.Amplitude(mute.AsSpan(rate / 2), rate, 1619) < a * .000001, "actual DSP mute settles below measurement threshold");

        const int correlationRate = 12000, lag = 83;
        Random random = new(679);
        float[] reference = Enumerable.Range(0, correlationRate * 3).Select(_ => (float)((random.NextDouble() - .5) * .04)).ToArray();
        float[] returned = new float[reference.Length];
        for (int index = lag; index < returned.Length; index++) returned[index] = reference[index - lag] * .5f;
        AlignmentResult result = SignalAlignment.Analyze(reference, returned, correlationRate, .5);
        Check(result.Windows.All(window => window.Correlation > .999 && Math.Abs(window.Gain - .5) < .000001), "RAM correlation identifies source and expected gain");
        Check(result.Windows.All(window => Math.Abs(window.LagMilliseconds - 1000.0 * lag / correlationRate) < .001), "fine alignment finds non-decimation-aligned lag");
        Check(result.LagSpreadMilliseconds < .001, "short-window clock shift remains observable");
        bool inconclusive = false;
        try { SignalAlignment.Analyze(new float[reference.Length], new float[reference.Length], correlationRate, .5); }
        catch (InconclusiveException) { inconclusive = true; }
        Check(inconclusive, "silent microphone cannot pass correlation");
        inconclusive = false;
        float[] unrelated = Enumerable.Range(0, reference.Length).Select(_ => (float)((random.NextDouble() - .5) * .02)).ToArray();
        try { SignalAlignment.Analyze(reference, unrelated, correlationRate, .5); }
        catch (InconclusiveException) { inconclusive = true; }
        Check(inconclusive, "unrelated return cannot pass correlation");
        inconclusive = false;
        try { SignalAlignment.Analyze(reference, returned, correlationRate, .25); }
        catch (InconclusiveException) { inconclusive = true; }
        Check(inconclusive, "wrong fitted microphone gain cannot pass");
        inconclusive = false;
        for (int index = correlationRate; index < returned.Length; index++) returned[index] = reference[index - lag - 120] * .5f;
        try { SignalAlignment.Analyze(reference, returned, correlationRate, .5); }
        catch (InconclusiveException) { inconclusive = true; }
        Check(inconclusive, "10ms clock shift exceeds the qualified 5ms short-window bound");
        foreach (float[] buffer in new[] { reference, returned, unrelated, tone, unity, half, mute }) Array.Clear(buffer);
        Console.WriteLine($"{count}/{count} focused pure checks plus 15 linked signal assertions passed; no device opened, no audio state changed.");
    }
}
