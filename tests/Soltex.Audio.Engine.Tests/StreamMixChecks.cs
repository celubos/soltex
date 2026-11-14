using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

internal static class StreamMixChecks
{
    private const int Rate = 48000;
    private static readonly DspSettings Unity = new() { HeadroomDb = 0 };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static double Rms(float[] samples) => Math.Sqrt(samples.Average(v => (double)v * v));
    private static StreamMixPcmSource Create(int capacity = Rate, StreamMixSendSettings? personal = null, StreamMixSendSettings? stream = null) =>
        new(Rate, 2, capacity, Unity, personal ?? new(), stream ?? new());

    internal static void IndependentSends()
    {
        var source = Create(personal: new(-6), stream: new(0, true));
        var tone = new float[Rate * 2];
        for (int i = 0; i < Rate; i++) tone[2 * i] = tone[2 * i + 1] = (float)(.2 * Math.Sin(2 * Math.PI * 997 * i / Rate));
        source.ProcessAndPublish(tone);
        var personal = new float[tone.Length]; var stream = new float[tone.Length];
        source.Personal.Read(personal, 0, personal.Length); source.Stream.Read(stream, 0, stream.Length);
        Check(Math.Abs(20 * Math.Log10(Rms(personal) / Rms(tone)) + 6) < .001, "Personal gain not measured at -6dB");
        Check(stream.All(v => v == 0), "Muted stream contains personal signal");
        Check(source.ProcessedFrames == Rate, "DSP executed separately for each output");
        source.Update(Unity, new(0, true), new(-12));
        source.ProcessAndPublish(tone);
        source.Stream.Read(stream, 0, stream.Length); source.Personal.Read(personal, 0, personal.Length);
        Check(Rms(personal[Rate..]) < 1e-10, "Personal mute did not settle independently");
        Check(Math.Abs(20 * Math.Log10(Rms(stream[Rate..]) / Rms(tone[Rate..])) + 12) < .001, "Stream gain did not settle at -12dB");
    }

    internal static void MixIsolationAndClipping()
    {
        var game = Create(personal: new(), stream: new(0, true)); var mic = Create(personal: new(0, true), stream: new());
        var inputGame = Enumerable.Repeat(.3f, 2000).ToArray(); var inputMic = Enumerable.Repeat(-.2f, 2000).ToArray();
        game.ProcessAndPublish(inputGame); mic.ProcessAndPublish(inputMic);
        var personal = new StreamMixSumProvider([game.Personal, mic.Personal]); var stream = new StreamMixSumProvider([game.Stream, mic.Stream]);
        var output = new float[2000]; personal.Read(output, 0, output.Length);
        Check(output.All(v => Math.Abs(v - .3f) < 1e-7), "Mic leaked into personal-only game mix");
        stream.Read(output, 0, output.Length); Check(output.All(v => Math.Abs(v + .2f) < 1e-7), "Game leaked into stream-only mic mix");
        var a = Create(); var b = Create(); var loud = Enumerable.Repeat(.8f, 2000).ToArray();
        a.ProcessAndPublish(loud); b.ProcessAndPublish(loud);
        var sum = new StreamMixSumProvider([a.Personal, b.Personal], .9); sum.Read(output, 0, output.Length);
        Check(output.All(v => Math.Abs(v - .9f) < 1e-7) && sum.LimitedSamples == output.Length, "Summed clipping escaped guard or counter");
        Check(Math.Abs(sum.Peak - .9f) < 1e-7, "Aggregate peak differs from output");
    }

    internal static void BoundedIndependentConsumers()
    {
        var source = Create(64); var input = Enumerable.Repeat(.25f, 64).ToArray(); var output = new float[64];
        for (int i = 0; i < 20; i++) { source.ProcessAndPublish(input); source.Personal.Read(output, 0, output.Length); }
        Check(source.PersonalQueue.DroppedFrames == 0 && source.PersonalQueue.SilentFrames == 0, "Stalled stream interfered with personal consumer");
        Check(source.StreamQueue.DroppedFrames == 576 && source.StreamQueue.BufferedFrames == 64, "Stalled stream queue is unbounded or loses accounting");
        source.Stream.Read(output, 0, output.Length); Check(output.All(v => v == .25f), "Stream failed to resume at retained tail");
        source.Personal.Read(output, 0, output.Length); Check(output.All(v => v == 0), "Underrun is not silence");
    }

    internal static void CallbackAllocationsAndValidation()
    {
        var source = Create(4096); var sum = new StreamMixSumProvider([source.Personal]);
        var input = new float[512]; var output = new float[512];
        for (int i = 0; i < 100; i++) { source.ProcessAndPublish(input); sum.Read(output, 0, output.Length); source.Stream.Read(output, 0, output.Length); }
        source.Update(Unity with { GainDb = -3 }, new(-4), new(-5));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { source.ProcessAndPublish(input); sum.Read(output, 0, output.Length); source.Stream.Read(output, 0, output.Length); }
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "Owned stream publish/mix callback allocated");
        bool rejected = false;
        try { source.Update(Unity with { GainDb = -50 }, new(), new(double.NaN)); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid send accepted");
        // Duplicate endpoints must fail before opening any native enumerator/capture.
        rejected = false;
        try { using var native = new WasapiStreamMixer(new([new("game", "fake", Unity, new(), new()), new("chat", "fake", Unity, new(), new())], "personal", "stream")); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Duplicate capture ownership was not rejected");
    }
}
