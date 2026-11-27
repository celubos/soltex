using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

/// <summary>
/// Measurements of the neural noise removal stage (Xiph RNNoise 0.2 through soltex-rnnoise.dll). The suite requires the
/// DLL next to the engine: build native/Soltex.Rnnoise/build.ps1 first (the integrated build does).
/// </summary>
internal static class NeuralNoiseChecks
{
    private const int Rate = 48000;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void LibraryAndSettingsContract()
    {
        Check(PcmDspProcessor.NeuralNoiseRemovalAvailable,
            $"RNNoise is unavailable ({RnnoiseLibrary.SharedFailure}); build native/Soltex.Rnnoise/build.ps1 before this suite.");
        var library = RnnoiseLibrary.Shared!;
        // Measured by the load-time probe, not assumed: RNNoise 0.2 outputs each 10 ms frame two frames later.
        Check(library.ModelDelayFrames == 960, $"RNNoise model delay measured {library.ModelDelayFrames} frames; 960 expected for 0.2");
        Check(new DspSettings().NeuralNoiseRemoval == new NeuralNoiseRemovalSettings(false, 100), "Neural noise removal is not off with no cap by default");
        foreach (double bad in new[] { -0.01, 100.01, double.NaN, double.PositiveInfinity })
            Throws<ArgumentOutOfRangeException>(() => _ = new PcmDspProcessor(Rate, 1, Unity(Rate) with { NeuralNoiseRemoval = new(true, bad) }), $"cap {bad}");
        Throws<ArgumentNullException>(() => _ = new PcmDspProcessor(Rate, 1, Unity(Rate) with { NeuralNoiseRemoval = null! }), "null settings");
        using (new PcmDspProcessor(Rate, 1, Unity(Rate) with { NeuralNoiseRemoval = new(true, 0) })) { }
        using (new PcmDspProcessor(Rate, 1, Unity(Rate) with { NeuralNoiseRemoval = new(true, 100) })) { }
        // Settings saved before the property existed load with the stage off; saved settings round-trip.
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
        foreach (var serializer in new[] { new JsonSerializerOptions(), options })
        {
            var old = JsonSerializer.SerializeToNode(new DspSettings { GainDb = 3 }, serializer)!.AsObject();
            Check(old.Remove(nameof(DspSettings.NeuralNoiseRemoval)), "Serialized settings lack the neural property");
            var loaded = old.Deserialize<DspSettings>(serializer)!;
            Check(loaded.GainDb == 3 && loaded.NeuralNoiseRemoval == new NeuralNoiseRemovalSettings(), "Old settings did not load with neural noise removal off");
            var saved = JsonSerializer.Deserialize<DspSettings>(JsonSerializer.Serialize(new DspSettings { NeuralNoiseRemoval = new(true, 12) }, serializer), serializer)!;
            Check(saved.NeuralNoiseRemoval == new NeuralNoiseRemovalSettings(true, 12), "Neural settings did not round-trip through JSON");
        }
        string[] levels = ["SSE2", "SSE4.1", "AVX2"];
        Console.WriteLine($"  RNNoise 0.2 loaded: model delay {library.ModelDelayFrames} frames (measured), state {library.StateBytes} bytes per channel, {levels[Math.Clamp(library.CpuLevel, 0, 2)]} kernels on this CPU.");
    }

    internal static void RemovesStationaryNoise()
    {
        var lines = new List<string>();
        foreach (int rate in new[] { 44100, 48000, 96000 })
            foreach (bool pink in new[] { false, true })
                foreach (double levelDb in new[] { -30.0, -50 })
                {
                    var input = Noise(rate * 6, Math.Pow(10, levelDb / 20), pink, 31); var output = (float[])input.Clone();
                    using var p = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(true) });
                    p.Process(output);
                    double reduction = Db(Rms(output, 2 * rate, output.Length) / Rms(input, 2 * rate, input.Length));
                    Check(reduction < -40, $"{(pink ? "Pink" : "White")} noise at {levelDb} dBFS, {rate} Hz: only {reduction:F1} dB removed (limit -40 dB)");
                    lines.Add($"{rate / 1000.0:0.#} kHz {(pink ? "pink" : "white")} {levelDb} dBFS {reduction:F1} dB");
                }
        Console.WriteLine("  Stationary noise after 2 s: " + string.Join("; ", lines) + ".");
    }

    internal static void CapLimitsReduction()
    {
        foreach (int rate in new[] { 44100, 48000 })
        {
            var results = new List<string>();
            foreach (double cap in new[] { 3.0, 6, 12, 24, 40 })
            {
                var input = Noise(rate * 5, 0.0316, false, 32); var output = (float[])input.Clone();
                using var p = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(true, cap) });
                p.Process(output);
                double reduction = -Db(Rms(output, 2 * rate, output.Length) / Rms(input, 2 * rate, input.Length));
                Check(Math.Abs(reduction - cap) <= 0.5, $"{rate} Hz cap {cap} dB: noise reduced by {reduction:F2} dB (tolerance 0.5 dB)");
                results.Add($"{cap:0} -> {reduction:F2}");
            }
            Console.WriteLine($"  Cap vs measured reduction of -30 dBFS white noise at {rate} Hz (dB): {string.Join(", ", results)}.");
        }
        // No reduction at all is the dry signal itself, exactly, delayed by the stage.
        var source = Noise(Rate * 2, 0.1, false, 33); var dry = (float[])source.Clone();
        using var exact = new PcmDspProcessor(Rate, 1, Unity(Rate) with { NeuralNoiseRemoval = new(true, 0) });
        exact.Process(dry);
        int delay = exact.LatencyFrames;
        for (int i = 0; i < dry.Length; i++) Check(dry[i] == (i < delay ? 0 : source[i - delay]), "A 0 dB cap is not an exact delay of the input");
    }

    internal static void DelayMatchesMeasurement()
    {
        var lines = new List<string>();
        foreach (int rate in new[] { 8000, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 192000 })
        {
            // Below RNNoise's silence threshold the model resynthesizes its input unchanged, so the wet path can be
            // timed with white noise: the cross-correlation peak is its delay.
            var input = Noise(rate * 2, 3e-7, false, 34); var wet = (float[])input.Clone();
            using var p = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(true) });
            p.Process(wet);
            var (lag, correlation) = PeakLag(input, wet, rate / 2, rate / 10);
            // Above 48 kHz the wet path carries only what RNNoise can (below 24 kHz), so less of white noise correlates.
            double minimum = rate <= Rate ? 0.95 : 0.4;
            Check(lag == p.LatencyFrames && p.NeuralNoiseRemovalLatencyFrames == p.LatencyFrames && correlation > minimum,
                $"{rate} Hz: wet path peaks at {lag} frames (correlation {correlation:F3}); the processor reports {p.LatencyFrames}");
            var dry = (float[])input.Clone();
            using (var q = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(true, 0) })) q.Process(dry);
            for (int i = p.LatencyFrames; i < dry.Length; i++) Check(dry[i] == input[i - p.LatencyFrames], $"{rate} Hz: dry path is not delayed by the reported {p.LatencyFrames} frames");
            lines.Add($"{rate / 1000.0:0.###} kHz {p.LatencyFrames} ({1000.0 * p.LatencyFrames / rate:F2} ms)");
        }
        Console.WriteLine("  Delay reported = measured (wet and dry): " + string.Join(", ", lines) + ".");
    }

    internal static void PartitionInvariance()
    {
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            var input = Noise(rate * 3, 0.02, true, 35);
            for (int i = 0; i < input.Length; i++) input[i] += (float)(0.1 * Math.Sin(2 * Math.PI * 180 * (i / 2) / rate));
            var settings = Unity(rate) with { NeuralNoiseRemoval = new(true, 18) };
            var reference = (float[])input.Clone();
            using (var whole = new PcmDspProcessor(rate, 2, settings)) whole.Process(reference);
            foreach (int block in new[] { 1, 7, 127, 480, 1000, 4096 })
            {
                var split = (float[])input.Clone();
                using var p = new PcmDspProcessor(rate, 2, settings);
                for (int frame = 0; frame < split.Length / 2; frame += block) p.Process(split.AsSpan(frame * 2, Math.Min(block, split.Length / 2 - frame) * 2));
                Check(split.AsSpan().SequenceEqual(reference), $"{rate} Hz: {block}-frame blocks changed the neural output");
            }
        }
    }

    internal static void StateKeptAndSharedInputProcessedOnce()
    {
        int rate = 44100;
        var mono = Noise(rate * 3, 0.03, true, 36);
        var stereo = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { stereo[2 * i] = mono[i]; stereo[2 * i + 1] = mono[i]; }
        var settings = Unity(rate) with { NeuralNoiseRemoval = new(true, 30) };
        // Updates that leave the stage on keep everything it has buffered: same output as never updating.
        var steady = Run(rate, 2, settings, stereo, null);
        var updated = Run(rate, 2, settings, stereo, (p, block) => { if (block % 25 == 10) p.UpdateSettings(settings with { Compressor = new(false, -20 - block % 7) }); });
        Check(updated.AsSpan().SequenceEqual(steady), "A settings update that keeps the stage on changed or restarted it");
        // One selected input feeds every channel: processed once, copied, identical to a mono processor.
        var selected = Run(rate, 2, settings with { InputChannel = 0 }, stereo, null);
        var single = Run(rate, 1, settings, mono, null);
        for (int i = 0; i < single.Length; i++) Check(selected[2 * i] == single[i] && selected[2 * i + 1] == single[i], "Shared-input output differs from one mono pass");
        // Selecting and releasing the input while running continues each channel's state exactly (the inputs are equal here).
        var switching = Run(rate, 2, settings, stereo, (p, block) => { if (block % 20 == 7) p.UpdateSettings(settings with { InputChannel = block % 40 == 7 ? 0 : null }); });
        Check(switching.AsSpan().SequenceEqual(steady), "Switching shared and separate input lost or changed channel state");
        // A cap change glides without restarting: the first block after it still matches the unchanged run closely.
        var capped = Run(rate, 2, settings, stereo, (p, block) => { if (block == 100) p.UpdateSettings(settings with { NeuralNoiseRemoval = new(true, 6) }); });
        int first = 100 * 441 * 2;
        Check(capped.AsSpan(0, first).SequenceEqual(steady.AsSpan(0, first)) && Math.Abs(capped[first] - steady[first]) < 0.01, "A cap change restarted the stage");
        // Time for two channels sharing one input vs two separate channels.
        double Cost(int? input)
        {
            using var p = new PcmDspProcessor(rate, 2, settings with { InputChannel = input });
            var block = new float[882]; var clock = Stopwatch.StartNew();
            for (int i = 0; i + 882 <= stereo.Length; i += 882) { stereo.AsSpan(i, 882).CopyTo(block); p.Process(block); }
            return clock.Elapsed.TotalMilliseconds;
        }
        Cost(0); Cost(null);
        Console.WriteLine($"  Stereo with one selected input: {Cost(0):F0} ms for 3 s of audio vs {Cost(null):F0} ms for two separate channels.");
    }

    internal static void ChangesAreClickFree()
    {
        // 100 Hz passes RNNoise, so on/off is judged on a tone that sounds on both sides; 1 kHz is removed by it, so the dry
        // mix-in of a cap change is all that is heard and a step there would be a click.
        var off = Unity(Rate);
        var on = off with { NeuralNoiseRemoval = new(true) };
        var suppressed = off with { NoiseSuppression = new(true, -60, 12) };
        var scenarios = new (string Name, DspSettings Start, DspSettings Change, double Frequency)[]
        {
            ("neural on", off, on, 100),
            ("neural off", on, off, 100),
            ("neural on with spectral suppression running", suppressed, suppressed with { NeuralNoiseRemoval = new(true) }, 100),
            ("neural off with spectral suppression running", suppressed with { NeuralNoiseRemoval = new(true) }, suppressed, 100),
            ("neural and limiter on together", off, on with { Limiter = new(true) }, 100),
            ("cap 100 -> 0 dB", on, on with { NeuralNoiseRemoval = new(true, 0) }, 1000),
            ("cap 0 -> 100 dB", on with { NeuralNoiseRemoval = new(true, 0) }, on, 1000),
            ("cap 100 -> 12 dB", on, on with { NeuralNoiseRemoval = new(true, 12) }, 1000),
        };
        double worstCurvature = double.MinValue, worstStep = double.MinValue; string worstName = "";
        foreach (var (name, start, change, frequency) in scenarios)
        {
            const int blocks = 220, changeBlock = 100; const double amplitude = 0.25;
            using var p = new PcmDspProcessor(Rate, 1, start);
            var y = new float[blocks * 480];
            for (int i = 0; i < y.Length; i++) y[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
            for (int b = 0; b < blocks; b++) { if (b == changeBlock) p.UpdateSettings(change); p.Process(y.AsSpan(b * 480, 480)); }
            // The clean tone's own curvature and steepest step are the reference: a click or a stepped gain exceeds both.
            double toneCurvature = amplitude * Math.Pow(2 * Math.PI * frequency / Rate, 2), toneStep = amplitude * 2 * Math.Sin(Math.PI * frequency / Rate);
            double transition = MaxSecondDifference(y, changeBlock * 480 - 2, (changeBlock + 80) * 480);
            double step = MaxFirstDifference(y, changeBlock * 480 - 1, (changeBlock + 80) * 480);
            double curvatureExcess = Db(transition / toneCurvature), stepExcess = Db(step / toneStep);
            if (curvatureExcess > worstCurvature) { worstCurvature = curvatureExcess; worstName = name; }
            worstStep = Math.Max(worstStep, stepExcess);
            Check(curvatureExcess <= 6, $"{name}: second-difference peak {curvatureExcess:F1} dB above the clean {frequency} Hz tone's (limit 6 dB)");
            Check(stepExcess <= 1, $"{name}: largest sample-to-sample step {stepExcess:F2} dB above the tone's own steepest step (limit 1 dB)");
        }
        Console.WriteLine($"  {scenarios.Length} neural changes, 800 ms after each: worst curvature {worstCurvature:F1} dB re the clean tone's ({worstName}), largest step {worstStep:F2} dB re the tone's own; limits 6 dB and 1 dB.");
    }

    internal static void UnavailablePassesThroughUnchanged()
    {
        var input = Noise(Rate * 2, 0.05, true, 37);
        var settings = Unity(Rate) with { NeuralNoiseRemoval = new(true, 20), NoiseSuppression = new(true, -60, 10) };
        var expected = (float[])input.Clone();
        using (var plain = new PcmDspProcessor(Rate, 1, settings with { NeuralNoiseRemoval = new() })) plain.Process(expected);
        var missing = (float[])input.Clone();
        using (var p = new PcmDspProcessor(Rate, 1, settings, library: null))
        {
            Check(p.NeuralNoiseRemovalLatencyFrames == 0, "An unavailable stage reports delay");
            for (int b = 0; b < missing.Length / 480; b++)
            {
                // Toggling it changes nothing either: no dip, no delay.
                if (b % 30 == 5) p.UpdateSettings(settings with { NeuralNoiseRemoval = new(b % 60 == 5, 20) });
                p.Process(missing.AsSpan(b * 480, 480));
            }
        }
        Check(missing.AsSpan().SequenceEqual(expected), "Without the library, enabling the stage changed the audio");
        string empty = Directory.CreateTempSubdirectory("soltex-rnnoise-").FullName;
        try
        {
            Check(RnnoiseLibrary.Load(empty, out string? absent) is null && absent is not null, "Loading from a folder without the DLL did not fail cleanly");
            File.WriteAllText(Path.Combine(empty, RnnoiseLibrary.FileName), "not a library");
            Check(RnnoiseLibrary.Load(empty, out string? broken) is null && broken is not null, "A damaged DLL was not rejected cleanly");
        }
        finally { Directory.Delete(empty, recursive: true); }
        // Digital silence stays exactly silent through the model and both converters; a disposed processor is silent.
        foreach (int rate in new[] { 44100, 48000 })
        {
            var silence = new float[rate * 2 * 2];
            using var quiet = new PcmDspProcessor(rate, 2, Unity(rate) with { NeuralNoiseRemoval = new(true) });
            quiet.Process(silence);
            Check(silence.All(x => x == 0), $"{rate} Hz: digital silence did not stay silent through neural noise removal");
        }
        var disposed = new PcmDspProcessor(Rate, 2, Unity(Rate) with { NeuralNoiseRemoval = new(true) });
        var block = Noise(960, 0.1, false, 38); disposed.Process(block);
        disposed.Dispose(); disposed.Dispose();
        block = Noise(960, 0.1, false, 39); disposed.Process(block);
        Check(block.All(x => x == 0), "A disposed processor did not output silence");
    }

    internal static void AllocationFree()
    {
        foreach (int rate in new[] { 44100, 48000 })
        {
            int frames = rate / 100;
            var settings = Unity(rate) with { NeuralNoiseRemoval = new(true, 100), NoiseSuppression = new(true, -60, 12) };
            using var p = new PcmDspProcessor(rate, 2, settings);
            var block = Noise(frames * 2, 0.05, true, 40);
            void Blocks(int count) { for (int i = 0; i < count; i++) p.Process(block); }
            Blocks(200);
            // Each change is published outside the measured window and applied inside it, by the audio thread.
            foreach (var change in new[] { settings with { NeuralNoiseRemoval = new(true, 12) }, settings with { NeuralNoiseRemoval = new() }, settings,
                settings with { InputChannel = 1 }, settings })
            {
                p.UpdateSettings(change);
                long before = GC.GetAllocatedBytesForCurrentThread();
                Blocks(150);
                Check(GC.GetAllocatedBytesForCurrentThread() == before, $"{rate} Hz: neural processing allocated after a change to {change.NeuralNoiseRemoval} input {change.InputChannel}");
            }
        }
    }

    internal static void Cost()
    {
        var input = Noise(Rate * 20, 0.03, true, 41);
        double Milliseconds(int rate, bool on)
        {
            using var p = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(on) });
            int frames = rate / 100; var block = new float[frames];
            var clock = Stopwatch.StartNew();
            for (int i = 0; i + frames <= rate * 20; i += frames) { input.AsSpan(i % (input.Length - frames), frames).CopyTo(block); p.Process(block); }
            return clock.Elapsed.TotalMilliseconds / 20;
        }
        Milliseconds(Rate, true);
        double on48 = Milliseconds(Rate, true), off48 = Milliseconds(Rate, false), on44 = Milliseconds(44100, true), off44 = Milliseconds(44100, false);
        double share48 = (on48 - off48) / 10, share44 = (on44 - off44) / 10;
        // A loose bound that only catches a broken build (for example the dispatch stuck on a slow path).
        Check(share48 < 15, $"Neural noise removal costs {share48:F2} % of a core per channel at 48 kHz");
        Console.WriteLine($"  Cost per channel: {share48:F2} % of one core at 48 kHz, {share44:F2} % at 44.1 kHz (converters included); the chain without it {off48 / 10:F2} %.");
    }

    internal static void SpeechKeptNoiseRemoved()
    {
        var lines = new List<string>();
        foreach (var (format, rate) in new[] { (38, 48000), (34, 44100) })
        {
            float[]? speech = SynthesizeSpeech(format, rate, out string? reason);
            if (speech is null) { Console.WriteLine($"  Skipped: {reason}"); return; }
            foreach (bool pink in new[] { false, true })
                foreach (double snr in new[] { 0.0, 10 })
                {
                    int lead = rate, length = lead + speech.Length + rate / 2, frame = rate / 50;
                    var clean = new float[length]; Array.Copy(speech, 0, clean, lead, speech.Length);
                    // 20 ms frames: speech where the clean frame is within 20 dB of the loudest, pauses where it is 70 dB down.
                    var energies = Enumerable.Range(0, length / frame).Select(f => Energy(clean, f * frame, frame)).ToArray();
                    double loudest = energies.Max();
                    var active = Enumerable.Range(0, energies.Length).Where(f => energies[f] > loudest * 1e-2).ToArray();
                    var pauses = Enumerable.Range(0, energies.Length).Where(f => energies[f] < loudest * 1e-7 && f * frame >= rate / 2).ToArray();
                    double speechRms = Math.Sqrt(active.Sum(f => energies[f]) / (active.Length * (double)frame));
                    double scale = Math.Pow(10, -26 / 20.0) / speechRms; for (int i = 0; i < length; i++) clean[i] = (float)(clean[i] * scale);
                    var noise = Noise(length, Math.Pow(10, (-26 - snr) / 20), pink, 42);
                    var mixture = new float[length]; for (int i = 0; i < length; i++) mixture[i] = clean[i] + noise[i];
                    var output = (float[])mixture.Clone();
                    using var p = new PcmDspProcessor(rate, 1, Unity(rate) with { NeuralNoiseRemoval = new(true) });
                    p.Process(output);
                    int delay = p.LatencyFrames;
                    double Sum(float[] x, int[] frames, int shift) => frames.Where(f => f * frame + shift + frame <= length).Sum(f => Energy(x, f * frame + shift, frame));
                    double kept = 10 * Math.Log10(Sum(output, active, delay) / Sum(clean, active, 0));
                    double removed = 10 * Math.Log10(Sum(output, pauses, delay) / Sum(mixture, pauses, 0));
                    // How much of the clean waveform itself is in the output (projection onto the clean speech).
                    double dot = 0, norm = 0;
                    foreach (int f in active) for (int i = f * frame; i < f * frame + frame && i + delay < length; i++) { dot += output[i + delay] * (double)clean[i]; norm += clean[i] * (double)clean[i]; }
                    double component = 20 * Math.Log10(dot / norm);
                    string label = $"{rate / 1000.0:0.#} kHz {(pink ? "pink" : "white")} SNR {snr:0} dB";
                    Check(Math.Abs(kept) <= 3 && component >= -3, $"{label}: speech frames {kept:F2} dB, speech component {component:F2} dB (limit ±3 dB)");
                    Check(removed <= -40, $"{label}: noise between words only {removed:F1} dB lower (limit -40 dB)");
                    lines.Add($"{label}: speech {kept:+0.00;-0.00} dB (component {component:F2}), pauses {removed:F1} dB");
                }
        }
        Console.WriteLine("  Synthesized speech plus noise: " + string.Join("; ", lines) + ".");
    }

    /// <summary>Speaks a fixed sentence with the first installed SAPI voice into memory (nothing is written to disk).</summary>
    private static float[]? SynthesizeSpeech(int format, int rate, out string? reason)
    {
        reason = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice"); var streamType = Type.GetTypeFromProgID("SAPI.SpMemoryStream");
            if (voiceType is null || streamType is null) { reason = "SAPI speech synthesis is not installed."; return null; }
            dynamic voice = Activator.CreateInstance(voiceType)!;
            if ((int)voice.GetVoices().Count == 0) { reason = "no SAPI voice is installed."; return null; }
            dynamic stream = Activator.CreateInstance(streamType)!;
            dynamic audio = stream.Format; audio.Type = format; stream.Format = audio;
            dynamic wave = stream.Format.GetWaveFormatEx();
            if ((int)wave.SamplesPerSec != rate || (int)wave.Channels != 1 || (int)wave.BitsPerSample != 16) { reason = "SAPI did not accept the requested format."; return null; }
            voice.AudioOutputStream = stream;
            voice.Speak("The quick brown fox jumps over the lazy dog. Noise removal should keep every word of this sentence clear and easy to follow.", 0);
            byte[] data = (byte[])stream.GetData();
            var samples = new float[data.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, 2 * i) / 32768f;
            if (samples.Length < rate) { reason = "SAPI produced no speech."; return null; }
            return samples;
        }
        catch (Exception error) // any failure here is the machine's speech setup, not the stage under test
        {
            reason = $"SAPI speech synthesis failed ({error.GetType().Name}).";
            return null;
        }
    }

    private static float[] Run(int rate, int channels, DspSettings settings, float[] input, Action<PcmDspProcessor, int>? control)
    {
        var output = (float[])input.Clone(); int frames = rate / 100 * channels;
        using var p = new PcmDspProcessor(rate, channels, settings);
        for (int b = 0; b * frames < output.Length; b++)
        {
            control?.Invoke(p, b);
            p.Process(output.AsSpan(b * frames, Math.Min(frames, output.Length - b * frames)));
        }
        return output;
    }

    private static DspSettings Unity(int rate) => new()
    {
        HeadroomDb = 0,
        Bands = DspSettings.FlatBands().Select(b => b with { FrequencyHz = Math.Min(b.FrequencyHz, rate * 0.45) }).ToArray()
    };

    /// <summary>Gaussian white noise, or pink noise (Paul Kellett's filter), at the given RMS.</summary>
    private static float[] Noise(int frames, double rms, bool pink, int seed)
    {
        var random = new Random(seed); var x = new double[frames];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < frames; i++)
        {
            double u1 = 1 - random.NextDouble(), u2 = random.NextDouble(), w = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            if (!pink) { x[i] = w; continue; }
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            x[i] = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362; b6 = w * 0.115926;
        }
        double actual = Math.Sqrt(x.Sum(v => v * v) / frames);
        return x.Select(v => (float)(v * rms / actual)).ToArray();
    }

    private static (int Lag, double Correlation) PeakLag(float[] input, float[] output, int from, int maximumLag)
    {
        int best = -1; double bestCorrelation = double.MinValue;
        for (int lag = 0; lag <= maximumLag; lag++)
        {
            double cross = 0, a = 0, b = 0;
            for (int i = from; i < output.Length; i++) { cross += output[i] * (double)input[i - lag]; a += input[i - lag] * (double)input[i - lag]; b += output[i] * (double)output[i]; }
            double correlation = cross / Math.Sqrt(Math.Max(1e-300, a * b));
            if (correlation > bestCorrelation) { bestCorrelation = correlation; best = lag; }
        }
        return (best, bestCorrelation);
    }

    private static double MaxSecondDifference(float[] y, int from, int to)
    {
        double maximum = 0;
        for (int i = Math.Max(2, from); i < to; i++) maximum = Math.Max(maximum, Math.Abs(y[i] - 2.0 * y[i - 1] + y[i - 2]));
        return maximum;
    }

    private static double MaxFirstDifference(float[] y, int from, int to)
    {
        double maximum = 0;
        for (int i = Math.Max(1, from); i < to; i++) maximum = Math.Max(maximum, Math.Abs(y[i] - (double)y[i - 1]));
        return maximum;
    }

    private static double Energy(float[] x, int start, int length) { double e = 0; for (int i = start; i < start + length; i++) e += x[i] * (double)x[i]; return e; }
    private static double Rms(float[] x, int from, int to) { double e = 0; for (int i = from; i < to; i++) e += x[i] * (double)x[i]; return Math.Sqrt(e / (to - from)); }
    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(1e-30, ratio));
    private static void Throws<T>(Action action, string what) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} for {what}");
    }
}
