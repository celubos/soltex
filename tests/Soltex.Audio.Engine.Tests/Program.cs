using System.Buffers.Binary;
using NAudio.Wave;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

internal static class Program
{
    private const int Rate = 48000;
    private static readonly DspSettings Unity = new() { HeadroomDb = 0 };
    private static int Main()
    {
        (string Name, Action Test)[] tests =
        [
            ("Unity sine amplitude and stereo channel isolation", UnityAndIsolation),
            ("Gain responds in dB and mute ramps without a step", GainAndMute),
            ("Peaking EQ measured center and distant response", PeakResponse),
            ("Response graph coefficients agree with measured signal", ResponseGraph),
            ("Shelf EQ measured low/high asymptotes", ShelfResponse),
            ("High/low pass and notch measured rejection", CutFilters),
            ("Q changes bandwidth and bypass restores unity", BandwidthAndBypass),
            ("All ten filters contribute independently", TenBands),
            ("EQ update crossfades and snapshots control input", SettingsSnapshot),
            ("Compressor ratio, threshold, attack and release", Compression),
            ("Gate hold, closure, attack and hysteresis", Gate),
            ("Downward expansion attenuates floor and passes speech-level signal", Expander),
            ("Sanitation and output ceiling recover after invalid input", Sanitation),
            ("Independent processor state and block partition invariance", PartitionAndInstances),
            ("ChatMix center, endpoints, symmetry and bounded law", ChatMixLaw),
            ("Frame-aligned bus wrap, overflow and zero fill", Bus),
            ("Bus and DSP stream integration", StreamIntegration),
            ("Final render guard catches conversion overshoot", RenderGuard),
            ("PCM16/24/32/float decoding known bit patterns", Decode),
            ("Recording is frame bounded and finalizes a readable WAV", BoundedRecording),
            ("Queued recording exact duration, channel order and writer ownership", RecordingChecks.DurationAndSamples),
            ("Slow disk never blocks capture and queue overflow is explicit", RecordingChecks.SlowDiskOverflow),
            ("Early recording stop drains ordered frames across ring wrap", RecordingChecks.StopDrainsAndWraps),
            ("Recording worker I/O failure and empty cancellation close safely", RecordingChecks.WorkerFailureAndEmptyStop),
            ("Concurrent recording publication preserves order and the final tail", RecordingChecks.ConcurrentFinalTail),
            ("Steady capture and gain updates allocate no callback memory", RecordingChecks.CallbackAllocations),
            ("Replay preload closes file dependency and enforces size guard", RecordingChecks.ReplayPreload),
            ("Validation rejects nonfinite and malformed settings", Validation),
            ("Concurrent controls never publish partial settings", ConcurrentControls),
            ("Spectral suppression reduces stationary noise and improves mixed signal SNR", ExtendedDspChecks.SpectralQuality),
            ("Spectral overlap-add latency, partition invariance and updates across rates", ExtendedDspChecks.SpectralReconstructionAndUpdates),
            ("Stereo width, mono, pan, swap and headphone crossfeed response", ExtendedDspChecks.StereoControls),
            ("Physical input selection and extended settings validation", ExtendedDspChecks.InputAndValidation),
            ("Extended DSP allocation-free callbacks and nonfinite input recovery", ExtendedDspChecks.RealtimeSafety),
            ("Shared-capture fanout has independent personal and stream gain/mute", StreamMixChecks.IndependentSends),
            ("Personal and stream mixes isolate buses and guard summed clipping", StreamMixChecks.MixIsolationAndClipping),
            ("Stalled stream send cannot grow or interrupt personal audio", StreamMixChecks.BoundedIndependentConsumers),
            ("Stream mix callbacks allocate no memory and reject duplicate capture ownership", StreamMixChecks.CallbackAllocationsAndValidation),
            ("Owned transport adapters carry real PCM through one capture and independent outputs", OwnedMixAdapterChecks.PcmAndLifetime),
            ("Owned output initialization failure releases every prior adapter without starting audio", OwnedMixAdapterChecks.PartialConstruction),
            ("EQ graph and PCM match the analytic RBJ curves for every type at 44.1-192 kHz", AudioQualityChecks.EqualizerMatchesAnalyticCurves),
            ("EQ stays stable at extreme Q/gain, near Nyquist and under random live changes", AudioQualityChecks.EqualizerStableAtExtremes),
            ("Full chain THD+N below -120 dB and silence stays exactly silent", AudioQualityChecks.ChainDistortionAndNoiseFloor),
            ("EQ, dynamics, suppression, limiter, gain and stereo changes are click free", AudioQualityChecks.ParameterChangesAreClickFree),
            ("Render start and stop are faded; converter primes then fades in", AudioQualityChecks.StartAndStopAreFaded),
            ("Compressor static curve matches threshold, ratio and makeup", AudioQualityChecks.CompressorStaticCurve),
            ("Gate closes after detector, hold and release times and reopens within attack", AudioQualityChecks.GateTimesMatchSettings),
            ("Opt-in safety limiter is an exact delay below the ceiling and bounds true peaks", AudioQualityChecks.SafetyLimiterIsTransparentAndBounded),
            ("Opt-in smooth residual removes musical noise and keeps an exact zero-reduction delay", AudioQualityChecks.SmoothResidualRemovesMusicalNoise),
            ("Long digital silence leaves no subnormal processing state", AudioQualityChecks.NoSubnormalsAfterLongSilence),
            ("Sinc resampler passband, THD+N, alias rejection and bit-exact 1:1 delay", AudioQualityChecks.ResamplerQuality),
            ("Resampler, render head, limiter and smooth residual allocate nothing per callback", AudioQualityChecks.ResamplerAllocationFree),
            ("Clock drift loop holds latency with no drops, underruns or clicks; stalls are concealed", AudioQualityChecks.DriftCompensationHoldsLatencyWithoutGlitches),
            ("MMCSS registration is per thread, released when a stream stops and bounded across start/stop cycles", AudioQualityChecks.RealtimeRegistrationIsSafe),
            ("Router pacing waits 10 ms on a high-resolution timer without spinning and stops at once", AudioQualityChecks.RouterPacingIsHighResolutionAndIdle),
            ("Converter holds one read plus one packet and a small margin on real device periods", ConverterLatencyChecks.SteadyLatencyFollowsDevicePeriods),
            ("A paused source (silent or sending empty packets) never grows latency or restart delay", ConverterLatencyChecks.PausedSourceKeepsItsLatency),
            ("A source that runs dry fades over the full fade length at every stop position", ConverterLatencyChecks.RunningDryAlwaysFades)
        ];
        int failed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{tests.Length - failed}/{tests.Length} independent audio checks passed; no devices opened.");
        return failed == 0 ? 0 : 1;
    }

    private static void UnityAndIsolation()
    {
        var input = Tone(1000, 0.1, Rate);
        var stereo = new float[Rate * 2];
        for (int i = 0; i < Rate; i++) stereo[i * 2] = input[i];
        var p = new PcmDspProcessor(Rate, 2, Settings(new(1000, 6)));
        p.Process(stereo);
        Check(stereo.Where((_, i) => i % 2 == 1).All(x => x == 0), "EQ history leaked to silent channel");
        new PcmDspProcessor(Rate, 1, Unity).Process(input);
        Near(Rms(input.AsSpan(Rate / 2)), 0.1 / Math.Sqrt(2), 1e-7, "unity amplitude");
    }

    private static void GainAndMute()
    {
        var p = new PcmDspProcessor(Rate, 1, Unity with { GainDb = -6 });
        var samples = Constant(0.5f, Rate);
        p.Process(samples);
        Near(samples[^1], 0.2505936, 1e-6, "-6 dB gain");
        p.UpdateSettings(Unity with { Mute = true });
        samples = Constant(0.5f, Rate);
        p.Process(samples);
        Check(samples[0] > 0.25 && samples[0] < 0.251, "mute must begin smoothly");
        Check(Math.Abs(samples[^1]) < 1e-8, "mute must settle to silence");
        p.UpdateSettings(Unity);
        samples = Constant(0.5f, Rate);
        p.Process(samples);
        Check(samples[0] < 0.001, "unmute must ramp from silence");
        Near(samples[^1], 0.5, 1e-6, "unmute settles");
    }

    private static void PeakResponse()
    {
        var s = Settings(new(1000, 6, 2));
        Near(Response(s, 1000), 6, 0.03, "center boost");
        Check(Math.Abs(Response(s, 100)) < 0.1, "distant low frequency should remain unchanged");
        Near(Response(Settings(new(1000, -12, 2)), 1000), -12, 0.03, "center cut");
    }

    private static void ShelfResponse()
    {
        var low = Settings(new(1000, 6, Type: EqFilterType.LowShelf));
        Near(Response(low, 50), 6, 0.05, "low shelf low asymptote");
        Near(Response(low, 10000), 0, 0.05, "low shelf high asymptote");
        var high = Settings(new(1000, -6, Type: EqFilterType.HighShelf));
        Near(Response(high, 50), 0, 0.05, "high shelf low asymptote");
        Near(Response(high, 10000), -6, 0.05, "high shelf high asymptote");
    }

    private static void ResponseGraph()
    {
        var settings = Settings(new(1000, 6, 2));
        double[] frequencies = [100, 1000, 10000];
        var plot = EqualizerResponse.Decibels(settings, Rate, frequencies);
        for (int i = 0; i < frequencies.Length; i++) Near(plot[i], Response(settings, frequencies[i]), 0.01, "plot versus PCM measurement");
        Near(plot[1], 6, 0.001, "graph center gain");
    }

    private static void CutFilters()
    {
        var high = Settings(new(1000, Type: EqFilterType.HighPass));
        Near(Response(high, 1000), -3.0103, 0.03, "high pass cutoff");
        Check(Response(high, 100) < -39, "high pass stop band");
        var low = Settings(new(1000, Type: EqFilterType.LowPass));
        Near(Response(low, 1000), -3.0103, 0.03, "low pass cutoff");
        Check(Response(low, 10000) < -40, "low pass stop band");
        Check(Response(Settings(new(1000, Q: 2, Type: EqFilterType.Notch)), 1000) < -90, "notch center rejection");
    }

    private static void BandwidthAndBypass()
    {
        double wide = Response(Settings(new(1000, 12, 0.5)), 1500);
        double narrow = Response(Settings(new(1000, 12, 8)), 1500);
        Check(wide > narrow + 6, "Q should narrow boost bandwidth");
        Near(Response(Settings(new(1000, 12, Bypass: true)), 1000), 0, 0.001, "band bypass");
        Near(Response(Settings(new(1000, 12)) with { EqBypass = true }, 1000), 0, 0.001, "global bypass");
    }

    private static void TenBands()
    {
        // Ten coincident +1 dB peak filters must yield +10 dB, not just process the first band.
        var s = Unity with { Bands = Enumerable.Range(0, 10).Select(_ => new EqBand(1000, 1, 3)).ToArray() };
        Near(Response(s, 1000), 10, 0.03, "all ten stages");
    }

    private static void SettingsSnapshot()
    {
        var p = new PcmDspProcessor(Rate, 1, Unity);
        var bands = DspSettings.FlatBands(); bands[5] = new(1000, 6);
        p.UpdateSettings(Unity with { Bands = bands });
        bands[5] = new(1000, -12);
        var sample = Tone(1000, 0.01, Rate);
        p.Process(sample);
        Near(20 * Math.Log10(Rms(sample.AsSpan(Rate / 2)) / (0.01 / Math.Sqrt(2))), 6, 0.03, "copied settings");
        p.UpdateSettings(Unity);
        var transition = Tone(1000, 0.01, 960);
        p.Process(transition);
        Check(transition.All(float.IsFinite) && transition.Max(Math.Abs) < 0.03, "EQ transition must remain bounded");
    }

    private static void Compression()
    {
        var s = Unity with { Compressor = new(true, -20, 4, 10, 100) };
        var p = new PcmDspProcessor(Rate, 1, s);
        var loud = Constant(0.5f, Rate);
        p.Process(loud);
        Check(loud[0] > 0.49, "compressor attack must not be instantaneous");
        Near(loud[^1], 0.14953488, 2e-4, "4:1 static curve");
        var quiet = Constant(0.01f, Rate);
        p.Process(quiet);
        Check(quiet[0] < 0.004, "release should retain reduction briefly");
        Near(quiet[^1], 0.01, 1e-6, "below threshold after release");
        var noCompression = Constant(0.05f, Rate);
        new PcmDspProcessor(Rate, 1, s).Process(noCompression);
        Near(noCompression[^1], 0.05, 1e-6, "below threshold is unchanged");
    }

    private static void Gate()
    {
        var s = Unity with { Gate = new(true, -30, 6, 5, 100, 20, -100) };
        var p = new PcmDspProcessor(Rate, 1, s);
        p.Process(Constant(0.1f, Rate / 2));
        var quiet = Constant(0.001f, Rate);
        p.Process(quiet);
        Check(quiet[Rate / 20] > 0.0009, "hold should preserve onset gap");
        Check(quiet[^1] < 2e-8, "gate closes below floor");
        var between = Constant(0.025f, Rate / 2);
        p.Process(between);
        Check(between[^1] < 1e-6, "hysteresis must not open below opening threshold");
        var loud = Constant(0.1f, Rate / 2);
        p.Process(loud);
        Check(loud[0] < 0.001 && loud[^1] > 0.099, "gate attack and reopen");
        between = Constant(0.025f, Rate);
        p.Process(between);
        Near(between[^1], 0.025, 1e-6, "open gate stays open above close threshold");
    }

    private static void Expander()
    {
        var s = Unity with { NoiseAttenuation = new(true, -40, 2, 24, 5, 50) };
        var quiet = Constant(0.001f, Rate);
        var p = new PcmDspProcessor(Rate, 1, s);
        p.Process(quiet);
        Near(quiet[^1], 0.0001, 1e-7, "20 dB quiet-floor attenuation");
        var speechLevel = Constant(0.1f, Rate);
        p.Process(speechLevel);
        Near(speechLevel[^1], 0.1, 1e-6, "above floor passes");
        quiet = Constant(0.001f, Rate);
        new PcmDspProcessor(Rate, 1, s with { NoiseAttenuation = new(true, -40, 2, 12, 5, 50) }).Process(quiet);
        Near(quiet[^1], 0.0002511886, 1e-7, "maximum attenuation cap");
    }

    private static void Sanitation()
    {
        var p = new PcmDspProcessor(Rate, 1, Unity);
        float[] bad = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 2, -2];
        p.Process(bad);
        Check(bad.All(x => float.IsFinite(x) && Math.Abs(x) <= 0.980001), "finite ceiling");
        Check(p.SanitizedSamples == 5 && p.LimitedSamples == 4, "sanitation and limiter counters");
        var clean = Constant(0.1f, Rate / 10); p.Process(clean);
        Near(clean[^1], 0.1, 1e-7, "recovery after bad data");
        var headroom = Constant(0.5f, 1); new PcmDspProcessor(Rate, 1).Process(headroom);
        Near(headroom[0], 0.2505936, 1e-6, "default 6 dB headroom");
    }

    private static void PartitionAndInstances()
    {
        var a = Tone(733, 0.1, Rate); var b = (float[])a.Clone();
        var s = Settings(new(1000, 6)) with { Compressor = new(true), NoiseAttenuation = new(true) };
        new PcmDspProcessor(Rate, 1, s).Process(a);
        var p = new PcmDspProcessor(Rate, 1, s);
        for (int i = 0; i < b.Length; i += 127) p.Process(b.AsSpan(i, Math.Min(127, b.Length - i)));
        Check(a.SequenceEqual(b), "block boundaries changed DSP result");
        var muted = new PcmDspProcessor(Rate, 1, Unity with { Mute = true });
        var independent = new PcmDspProcessor(Rate, 1, Unity);
        var x = Constant(0.1f, 100); muted.Process(x); Check(x.All(v => v == 0), "muted instance");
        x = Constant(0.1f, 100); independent.Process(x); Near(x[^1], 0.1, 1e-7, "bus isolation");
    }

    private static void ChatMixLaw()
    {
        Near(ChatMix.Gains(0).Game, 1, 0, "center game"); Near(ChatMix.Gains(0).Chat, 1, 0, "center chat");
        Near(ChatMix.Gains(-1).Chat, 0, 1e-12, "game-only end"); Near(ChatMix.Gains(1).Game, 0, 1e-12, "chat-only end");
        for (int i = 0; i <= 100; i++)
        {
            double b = i / 100.0; var positive = ChatMix.Gains(b); var negative = ChatMix.Gains(-b);
            Near(positive.Game, negative.Chat, 1e-12, "symmetric law");
            Check(positive.Game is >= 0 and <= 1 && positive.Chat == 1, "no gain above unity");
        }
    }

    private static void Bus()
    {
        var bus = new BoundedFloatBus(Rate, 2, 3);
        bus.Write([1, 11, 2, 12]);
        var read = new float[2]; bus.Read(read, 0, 2); Check(read.SequenceEqual(new float[] { 1, 11 }), "first frame");
        bus.Write([3, 13, 4, 14, 5, 15]);
        var all = new float[8]; bus.Read(all, 0, all.Length);
        Check(all.SequenceEqual(new float[] { 3, 13, 4, 14, 5, 15, 0, 0 }), "bounded newest frames and silence");
        Check(bus.DroppedFrames == 1 && bus.SilentFrames == 1 && bus.BufferedFrames == 0, "bus counters");
        bus.Write([1, 11, 2, 12, 3, 13, 4, 14]); bus.Read(all, 0, 6);
        Check(all.AsSpan(0, 6).SequenceEqual(new float[] { 2, 12, 3, 13, 4, 14 }) && bus.DroppedFrames == 2, "oversized write keeps tail");
    }

    private static void StreamIntegration()
    {
        var bus = new BoundedFloatBus(Rate, 1, Rate);
        var source = new DspSampleProvider(bus, new PcmDspProcessor(Rate, 1, Unity with { GainDb = -6 }));
        bus.Write(Tone(1000, 0.1, Rate));
        var output = new float[Rate + 2]; output[0] = 42; output[^1] = 42;
        Check(source.Read(output, 1, Rate) == Rate && output[0] == 42 && output[^1] == 42, "provider offsets/count");
        Near(Rms(output.AsSpan(1, Rate)), 0.03543929, 1e-6, "stream gain");
    }

    private static void Decode()
    {
        foreach (int bits in new[] { 16, 24, 32 })
        {
            var bytes = new byte[bits / 8 * 3]; int width = bits / 8;
            bytes[width - 1] = 0x80; bytes[width * 3 - 1] = 0x40;
            var decoded = new float[3]; PcmDecoder.Decode(bytes, decoded, new WaveFormat(Rate, bits, 1));
            Check(decoded.SequenceEqual(new float[] { -1, 0, 0.5f }), $"PCM{bits} signs and scale");
        }
        var floats = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(floats, BitConverter.SingleToInt32Bits(-0.25f));
        BinaryPrimitives.WriteInt32LittleEndian(floats.AsSpan(4), BitConverter.SingleToInt32Bits(0.75f));
        var samples = new float[2]; PcmDecoder.Decode(floats, samples, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        Check(samples.SequenceEqual(new float[] { -0.25f, 0.75f }), "float decoding");
    }

    private static void RenderGuard()
    {
        var bus = new BoundedFloatBus(Rate, 1, 8);
        bus.Write([2, -2, float.NaN, float.PositiveInfinity, 0.25f]);
        var guarded = new OutputCeilingSampleProvider(bus, 0.9);
        var output = new float[5]; guarded.Read(output, 0, 5);
        Check(output.SequenceEqual(new float[] { 0.9f, -0.9f, 0, 0, 0.25f }), "post-conversion finite ceiling");
        Check(Math.Abs(guarded.Peak - .9f) < .00001, "meter measures the final guarded signal");
        guarded.SetCeiling(.2);
        bus.Write([.8f, -.8f, .1f]); guarded.Read(output, 0, 3);
        Check(output.Take(3).SequenceEqual(new float[] { .2f, -.2f, .1f }) && Math.Abs(guarded.Peak - .2f) < .00001,
            "lowering a live output ceiling changes post-resample protection and meter");
        guarded.SetCeiling(.95);
        bus.Write([.8f]); guarded.Read(output, 0, 1);
        Check(Math.Abs(output[0] - .8f) < .00001, "raising a live ceiling does not leave the initial limit latched");
        Throws<ArgumentOutOfRangeException>(() => guarded.SetCeiling(double.NaN));
        guarded.Read(output, 0, 1);
        Check(guarded.Peak == 0, "silent render block clears the displayed output peak");
    }

    private static void Validation()
    {
        Throws<ArgumentOutOfRangeException>(() => _ = new PcmDspProcessor(Rate, 1, Unity with { GainDb = double.NaN }));
        Throws<ArgumentException>(() => _ = new PcmDspProcessor(Rate, 1, Unity with { Bands = [] }));
        Throws<ArgumentOutOfRangeException>(() => _ = new PcmDspProcessor(Rate, 1, Settings(new(24000))));
        Throws<ArgumentOutOfRangeException>(() => ChatMix.Gains(2));
        Throws<ArgumentException>(() => new BoundedFloatBus(Rate, 2, 10).Write([1]));
        Throws<ArgumentException>(() => new PcmDspProcessor(Rate, 2).Process(new float[1]));
        Throws<ArgumentException>(() => PcmDecoder.Decode(new byte[1], new float[1], new WaveFormat(Rate, 16, 1)));
    }

    private static void BoundedRecording()
    {
        var memory = new MemoryStream();
        using (var writer = new BoundedPcmWaveWriter(memory, Rate, 2, 1))
        {
            var samples = new float[Rate * 3];
            for (int i = 0; i < samples.Length; i += 2) { samples[i] = 0.25f; samples[i + 1] = -0.5f; }
            Check(writer.Write(samples, samples.Length) == Rate && writer.Complete, "writer must cap at one second of frames");
            Check(writer.Write(samples, 100) == 0 && writer.FramesWritten == Rate, "completed writer cannot append extra audio");
        }
        using var input = new MemoryStream(memory.ToArray());
        using var reader = new WaveFileReader(input);
        Check(reader.Length == Rate * 2 * sizeof(float), "WAV data length");
        Near(reader.TotalTime.TotalSeconds, 1, 1e-9, "WAV duration");
        var first = reader.ReadNextSampleFrame();
        Check(first.SequenceEqual(new float[] { 0.25f, -0.5f }), "recorded channel order and sample amplitude");
    }

    private static void ConcurrentControls()
    {
        var p = new PcmDspProcessor(Rate, 2, Unity);
        var controls = Task.Run(() => { for (int i = 0; i < 200; i++) p.UpdateSettings(Unity with { GainDb = i % 2 == 0 ? -12 : 0 }); });
        var buffer = new float[512];
        for (int i = 0; i < 300; i++)
        {
            Array.Fill(buffer, 0.1f); p.Process(buffer);
            Check(buffer.All(x => float.IsFinite(x) && x >= 0.025 && x <= 0.100001), "partial or invalid settings");
        }
        controls.GetAwaiter().GetResult();
    }

    private static DspSettings Settings(EqBand band)
    { var bands = DspSettings.FlatBands(); bands[5] = band; return Unity with { Bands = bands }; }
    private static double Response(DspSettings settings, double frequency)
    {
        var tone = Tone(frequency, 0.01, Rate);
        new PcmDspProcessor(Rate, 1, settings).Process(tone);
        return 20 * Math.Log10(Rms(tone.AsSpan(Rate / 2)) / (0.01 / Math.Sqrt(2)));
    }
    private static float[] Tone(double frequency, double amplitude, int frames) => Enumerable.Range(0, frames)
        .Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate))).ToArray();
    private static float[] Constant(float value, int count) { var values = new float[count]; Array.Fill(values, value); return values; }
    private static double Rms(ReadOnlySpan<float> values) { double sum = 0; foreach (float x in values) sum += x * (double)x; return Math.Sqrt(sum / values.Length); }
    private static void Near(double actual, double expected, double tolerance, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance, $"{message}: expected {expected} ± {tolerance}, actual {actual}");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}"); }
}
