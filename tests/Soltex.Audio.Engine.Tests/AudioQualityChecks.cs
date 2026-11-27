using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using NAudio.Wave;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

/// <summary>Offline measurements of the DSP chain, resampler and drift loop on synthetic signals. No devices.</summary>
internal static class AudioQualityChecks
{
    private const int Rate = 48000;
    private static readonly DspSettings Unity = new() { HeadroomDb = 0 };
    private static readonly int[] Rates = [44100, 48000, 88200, 96000, 192000];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    // ---------- EQ ----------

    /// <summary>Every filter type, at five sample rates, against the analog prototype mapped through the bilinear transform.</summary>
    internal static void EqualizerMatchesAnalyticCurves()
    {
        int checkedPoints = 0; double worstCoefficient = 0, worstMeasured = 0;
        foreach (int rate in Rates)
        {
            foreach (var band in Bands(rate))
            {
                var coefficients = BiquadCoefficients.Shape(band, rate);
                Check(Math.Abs(coefficients.A2) < 1 && Math.Abs(coefficients.A1) < 1 + coefficients.A2, $"Unstable {band} at {rate}");
                var settings = Single(band, rate);
                double[] frequencies = Probe(band.FrequencyHz, rate);
                double[] graph = EqualizerResponse.Decibels(settings, rate, frequencies);
                for (int i = 0; i < frequencies.Length; i++)
                {
                    double analytic = Analytic(band, frequencies[i], rate);
                    if (analytic < -60) continue;
                    worstCoefficient = Math.Max(worstCoefficient, Math.Abs(graph[i] - analytic));
                    Check(Math.Abs(graph[i] - analytic) < 1e-6, $"{band} at {rate} Hz, {frequencies[i]:F1} Hz: graph {graph[i]:F6} vs analytic {analytic:F6} dB");
                    checkedPoints++;
                }
                // Measured PCM response at three of the probe frequencies (steady state, lock-in amplitude).
                foreach (double frequency in new[] { frequencies[0], frequencies[3], frequencies[^2] })
                {
                    double analytic = Analytic(band, frequency, rate);
                    if (analytic < -40) continue;
                    var tone = Tone(frequency, 0.05, rate * 2, rate);
                    new PcmDspProcessor(rate, 1, settings).Process(tone);
                    double measured = Db(Amplitude(tone, frequency, rate, rate, rate) / 0.05);
                    worstMeasured = Math.Max(worstMeasured, Math.Abs(measured - analytic));
                    Check(Math.Abs(measured - analytic) < 0.1, $"{band} at {rate}: measured {measured:F4} vs analytic {analytic:F4} dB at {frequency:F1} Hz");
                }
            }
        }
        // All ten bands together: the product of the analytic curves, graph and PCM.
        var preset = Unity with { Bands = Preset() };
        foreach (double frequency in new[] { 40.0, 90, 200, 1000, 3000, 9000, 15000 })
        {
            double analytic = preset.Bands.Sum(b => Analytic(b, frequency, Rate));
            double graph = EqualizerResponse.Decibels(preset, Rate, [frequency])[0];
            var tone = Tone(frequency, 0.05, Rate * 2, Rate); new PcmDspProcessor(Rate, 1, preset).Process(tone);
            double measured = Db(Amplitude(tone, frequency, Rate, Rate, Rate) / 0.05);
            Check(Math.Abs(graph - analytic) < 1e-6 && Math.Abs(measured - analytic) < 0.1, $"Ten-band sum at {frequency} Hz: analytic {analytic:F4}, graph {graph:F4}, measured {measured:F4}");
        }
        Console.WriteLine($"  {checkedPoints} graph points within {worstCoefficient:E1} dB of the analytic curves; PCM within {worstMeasured:F4} dB (limit 0.1 dB).");
    }

    /// <summary>Extreme Q, gain and near-Nyquist bands, and random parameter jumps while running, stay finite and bounded.</summary>
    internal static void EqualizerStableAtExtremes()
    {
        var random = new Random(4242);
        foreach (int rate in Rates)
        {
            var extremes = new EqBand[] { new(10, 24, 20), new(rate * 0.49, 24, 20), new(rate * 0.49, -24, 0.1, EqFilterType.HighShelf),
                new(10, 24, 0.1, EqFilterType.LowShelf), new(rate * 0.49, 0, 20, EqFilterType.LowPass), new(10, 0, 20, EqFilterType.HighPass),
                new(rate * 0.49, 0, 20, EqFilterType.Notch), new(rate * 0.25, -24, 20), new(rate * 0.1, 24, 20, EqFilterType.HighShelf), new(100, 24, 20, EqFilterType.LowShelf) };
            foreach (var band in extremes) { var c = BiquadCoefficients.Shape(band, rate); Check(Math.Abs(c.A2) < 1 && Math.Abs(c.A1) < 1 + c.A2, $"Unstable extreme {band}"); }
            var p = new PcmDspProcessor(rate, 1, Unity with { Bands = extremes, HeadroomDb = -30, GainDb = -40 });
            var noise = Noise(rate / 2, 0.05, 3);
            for (int jump = 0; jump < 40; jump++)
            {
                p.Process(noise.AsSpan((jump % 10) * (noise.Length / 10), noise.Length / 10));
                var bands = Enumerable.Range(0, 10).Select(_ => new EqBand(10 * Math.Pow(rate * 0.049, random.NextDouble()), random.NextDouble() * 48 - 24,
                    0.1 * Math.Pow(200, random.NextDouble()), (EqFilterType)random.Next(6), random.Next(5) == 0)).ToArray();
                p.UpdateSettings(Unity with { Bands = bands, HeadroomDb = -30, GainDb = -40 });
            }
            var tail = Noise(rate, 0.05, 9); p.Process(tail);
            // The output is always sanitized and clamped, so judge divergence by the counters: no non-finite or runaway
            // value was ever replaced, and nothing reached the output ceiling. -70 dB of gain leaves room for the loudest
            // legitimate random design (stacked +24 dB resonant shelves); an unstable filter grows without bound and still trips it.
            Check(p.SanitizedSamples == 0 && p.LimitedSamples == 0,
                $"EQ at {rate} Hz diverged under extreme jumps: {p.SanitizedSamples} sanitized, {p.LimitedSamples} at the ceiling");
        }
    }

    // ---------- THD+N and noise ----------

    internal static void ChainDistortionAndNoiseFloor()
    {
        foreach (var (name, settings) in new (string, DspSettings)[] { ("neutral", new DspSettings()), ("preset EQ", Unity with { Bands = Preset() }),
            ("preset EQ, open gate/expander, limiter armed", Unity with { Bands = Preset(), Gate = new(true, -70), NoiseAttenuation = new(true, -80), Limiter = new(true) }) })
        {
            foreach (double frequency in new[] { 100.0, 1000, 10000 })
            {
                var tone = Tone(frequency, 0.5, Rate * 2, Rate); new PcmDspProcessor(Rate, 1, settings).Process(tone);
                double thd = ThdN(tone, frequency, Rate, Rate, Rate);
                Check(thd < -120, $"THD+N {thd:F1} dB for {name} at {frequency} Hz");
            }
        }
        var silence = new float[Rate * 2];
        new PcmDspProcessor(Rate, 2, Unity with { Bands = Preset(), Compressor = new(true, -40, 4, 5, 100, 24), NoiseSuppression = new(true), Limiter = new(true), Stereo = new(true, Width: 2, Crossfeed: 1) }).Process(silence);
        Check(silence.All(x => x == 0), "Digital silence in did not stay digital silence out");
        var tone2 = Tone(1000, 0.5, Rate * 2, Rate); new PcmDspProcessor(Rate, 1).Process(tone2);
        Console.WriteLine($"  Neutral chain 1 kHz THD+N {ThdN(tone2, 1000, Rate, Rate, Rate):F1} dB (limit -120 dB); silence stays exactly zero.");
    }

    // ---------- Smoothness ----------

    internal static void ParameterChangesAreClickFree()
    {
        var preset = Unity with { Bands = Preset() };
        var comp = Unity with { Compressor = new(false, -30, 4, 10, 120, 6) };
        var compOn = Unity with { Compressor = new(true, -30, 4, 10, 120, 0) };
        var scenarios = new (string Name, DspSettings Start, Action<PcmDspProcessor, int> Apply, int Channels)[]
        {
            ("EQ band 0 -> +9 dB", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Bands = WithBand(DspSettings.FlatBands(), 2, new(125, 9, 1.4)) }); }, 1),
            ("preset EQ band edit", preset, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Bands = WithBand(Preset(), 7, new(3000, 6, 1)) }); }, 1),
            ("EQ gain drag, update every 10 ms", Unity, (p, b) => { if (b is >= 100 and < 140) p.UpdateSettings(Unity with { Bands = WithBand(DspSettings.FlatBands(), 2, new(125, 12.0 * (b - 99) / 40, 1.4)) }); }, 1),
            ("EQ frequency drag 60-400 Hz", Unity, (p, b) => { if (b is >= 100 and < 140) p.UpdateSettings(Unity with { Bands = WithBand(DspSettings.FlatBands(), 2, new(60 * Math.Pow(400 / 60.0, (b - 99) / 40.0), 9, 2)) }); }, 1),
            ("EQ global bypass", preset, (p, b) => { if (b == 100) p.UpdateSettings(preset with { EqBypass = true }); }, 1),
            ("EQ high-pass band bypass", preset, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Bands = WithBand(Preset(), 0, Preset()[0] with { Bypass = true }) }); }, 1),
            ("EQ type peak -> high-pass", Unity with { Bands = WithBand(DspSettings.FlatBands(), 2, new(125, 9, 1.4)) }, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Bands = WithBand(DspSettings.FlatBands(), 2, new(125, 0, 0.7071, EqFilterType.HighPass)) }); }, 1),
            ("compressor enable with +6 dB makeup", comp, (p, b) => { if (b == 100) p.UpdateSettings(comp with { Compressor = comp.Compressor with { Enabled = true } }); }, 1),
            ("compressor makeup +6 dB", compOn, (p, b) => { if (b == 100) p.UpdateSettings(compOn with { Compressor = compOn.Compressor with { MakeupDb = 6 } }); }, 1),
            ("compressor threshold -30 -> -50", compOn, (p, b) => { if (b == 100) p.UpdateSettings(compOn with { Compressor = compOn.Compressor with { ThresholdDb = -50 } }); }, 1),
            ("noise suppression on", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { NoiseSuppression = new(true, -60, 12) }); }, 1),
            ("noise suppression off", Unity with { NoiseSuppression = new(true, -60, 12) }, (p, b) => { if (b == 100) p.UpdateSettings(Unity); }, 1),
            ("safety limiter on", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Limiter = new(true) }); }, 1),
            ("safety limiter off", Unity with { Limiter = new(true) }, (p, b) => { if (b == 100) p.UpdateSettings(Unity); }, 1),
            ("mute", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Mute = true }); }, 1),
            ("gain +6 dB", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { GainDb = 6 }); }, 1),
            ("gate enable below threshold", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Gate = new(true, -6) }); }, 1),
            ("stereo width 1 -> 2", Unity, (p, b) => { if (b == 100) p.UpdateSettings(Unity with { Stereo = new(true, Width: 2) }); }, 2),
        };
        double worst = double.MinValue; string worstName = "";
        foreach (var (name, start, apply, channels) in scenarios)
        {
            var (steady, transition) = ClickScenario(start, apply, channels);
            double excess = Db(transition / steady);
            if (excess > worst) { worst = excess; worstName = name; }
            Check(excess <= 12 && Db(transition / 0.25) <= -60, $"{name}: second-difference peak {Db(transition / 0.25):F1} dB re tone ({excess:F1} dB above steady state)");
        }
        Console.WriteLine($"  {scenarios.Length} control changes; worst curvature excess {worst:F1} dB ({worstName}); limit 12 dB and -60 dB re tone.");
    }

    internal static void StartAndStopAreFaded()
    {
        var source = new ToneProvider(Rate, 100, 0.5, 2, startPhase: Math.PI / 2); // starts at the crest: a hard start would be a full-scale step
        var head = new RenderHeadSampleProvider(source, realtimeThread: false);
        var output = new float[480 * 2 * 40];
        for (int block = 0; block < 20; block++) head.Read(output, block * 960, 960);
        Check(Math.Abs(output[0]) < 0.01 && MaxSecondDifference(output, 2, 400, 2) / 0.5 < Math.Pow(10, -50 / 20.0), "Render start is not faded in");
        head.BeginFadeOut();
        for (int block = 20; block < 40; block++) head.Read(output, block * 960, 960);
        Check(head.Silenced && output.Skip(21 * 960).All(x => x == 0), "Fade-out did not reach silence");
        Check(MaxSecondDifference(output, 2, 19 * 480, 40 * 480) / 0.5 < Math.Pow(10, -50 / 20.0), "Render stop is not faded out");
        // The drift-compensating converter primes until one read plus one packet (and its fade reserve) is queued, then
        // fades in. Packets arrive 10 ms apart on a virtual clock.
        var clock = new VirtualClock();
        var bus = new BoundedFloatBus(Rate, 1, Rate / 5, clock);
        var resampler = new AdaptiveResamplingSampleProvider(bus, Rate, bus);
        var crest = new float[480]; Array.Fill(crest, 0.5f);
        var read = new float[480];
        clock.Ticks = 1; bus.Write(crest);
        clock.Ticks = 5_000_000; resampler.Read(read, 0, 480);
        Check(read.All(x => x == 0) && resampler.InsertedSilenceFrames == 480, "Converter started before its preroll");
        clock.Ticks = 10_000_000; bus.Write(crest); clock.Ticks = 20_000_000; bus.Write(crest);
        clock.Ticks = 25_000_000; resampler.Read(read, 0, 480);
        Check(Math.Abs(read[0]) < 0.01 && Math.Abs(read[^1] - 0.5) < 1e-6, "Converter start is not a fade-in to the signal");
    }

    // ---------- Dynamics ----------

    internal static void CompressorStaticCurve()
    {
        foreach (var (threshold, ratio, makeup) in new[] { (-20.0, 4.0, 3.0), (-30.0, 10.0, 0.0), (-10.0, 2.0, 6.0) })
        {
            for (double input = -60; input <= 0; input += 5)
            {
                var dc = new float[Rate * 2]; Array.Fill(dc, (float)Math.Pow(10, input / 20));
                new PcmDspProcessor(Rate, 1, Unity with { Compressor = new(true, threshold, ratio, 10, 100, makeup), OutputCeiling = 1 }).Process(dc);
                double expected = (input <= threshold ? input : threshold + (input - threshold) / ratio) + makeup;
                if (expected > 0) continue; // the output ceiling is not part of the compressor curve
                Check(Math.Abs(Db(dc[^1]) - expected) < 0.01, $"Compressor {threshold}/{ratio}:1 at {input} dB: {Db(dc[^1]):F3} vs {expected:F3} dB");
            }
        }
    }

    internal static void GateTimesMatchSettings()
    {
        // Threshold -30 dB, 6 dB hysteresis, attack 1 ms, hold 100 ms, release 20 ms, closed -80 dB.
        var settings = Unity with { Gate = new(true, -30, 6, 1, 100, 20, -80) };
        var p = new PcmDspProcessor(Rate, 1, settings);
        p.Process(Constant(0.1f, Rate / 2));
        var quiet = Constant(0.001f, Rate / 2); p.Process(quiet);
        // The 30 ms peak detector falls from -20 dB below -30 dB after 30 ms * ln(10^0.5), then the hold runs, then release.
        double detector = 30 * Math.Log(Math.Sqrt(10));
        double closeAt = FirstBelow(quiet, 0.001 * 0.5) * 1000.0 / Rate;
        double expected = detector + 100 + 20 * Math.Log(2);
        Check(Math.Abs(closeAt - expected) < 2, $"Gate reached -6 dB after {closeAt:F1} ms, expected {expected:F1} ms (detector + hold + release)");
        var loud = Constant(0.1f, Rate / 10); p.Process(loud);
        double openAt = FirstAbove(loud, 0.1 * 0.5) * 1000.0 / Rate;
        Check(openAt < 2, $"Gate took {openAt:F2} ms to open with a 1 ms attack");
        Console.WriteLine($"  Gate: -6 dB {closeAt:F1} ms after the drop (expected {expected:F1}); reopens to -6 dB in {openAt:F2} ms.");
    }

    // ---------- Limiter ----------

    internal static void SafetyLimiterIsTransparentAndBounded()
    {
        // Below the ceiling: an exact delay.
        var source = Noise(Rate, 0.1, 21); var limited = (float[])source.Clone();
        var p = new PcmDspProcessor(Rate, 1, Unity with { Limiter = new(true) }); p.Process(limited);
        int delay = p.LimiterLatencyFrames;
        Check(delay > 0 && delay < Rate / 400, $"Unexpected limiter latency {delay}");
        for (int i = delay; i < source.Length; i++) Check(limited[i] == source[i - delay], "Limiter changed a signal below the ceiling");
        // Overload: never above the ceiling, also between samples, and far cleaner than the hard ceiling clip.
        foreach (bool on in new[] { false, true })
        {
            var tone = Tone(100, 0.89 * 2, Rate * 2, Rate);
            var q = new PcmDspProcessor(Rate, 1, Unity with { OutputCeiling = 0.89, Limiter = new(on) }); q.Process(tone);
            double thd = ThdN(tone, 100, Rate, Rate, Rate);
            if (on) Check(thd < -35 && TruePeak(tone) <= 0.89 * 1.005, $"Limited overload THD+N {thd:F1} dB, true peak {TruePeak(tone):F4}");
            else Check(thd > -20, "Hard ceiling unexpectedly clean");
        }
        // The limiter itself must hold the ceiling: measured on its own output, before the processor's final clamp (which
        // would hide an overshoot). Low-frequency overs give long rising-gain stretches after each crest (50-100 Hz).
        double worstOvershoot = 0, worstTruePeak = 0;
        foreach (bool truePeak in new[] { false, true })
            foreach (double frequency in new[] { 50.0, 63, 80, 100 })
                foreach (double drive in new[] { 1.2, 2, 4 })
                {
                    var limiter = new SafetyLimiter(Rate, 1, Unity with { OutputCeiling = 0.89, Limiter = new(true, 80, truePeak) });
                    var x = Tone(frequency, 0.89 * drive, Rate, Rate); var frame = new double[1]; var y = new float[x.Length];
                    double peak = 0;
                    for (int i = 0; i < x.Length; i++) { frame[0] = x[i]; limiter.ProcessFrame(frame); y[i] = (float)frame[0]; peak = Math.Max(peak, Math.Abs(frame[0])); }
                    worstOvershoot = Math.Max(worstOvershoot, peak / 0.89);
                    Check(peak <= 0.89 * (1 + 1e-9), $"Limiter output {peak:F5} above the 0.89 ceiling before the final clamp ({frequency} Hz, {drive}x, true peak {truePeak})");
                    if (truePeak)
                    {
                        double tp = TruePeak(y); worstTruePeak = Math.Max(worstTruePeak, tp / 0.89);
                        Check(tp <= 0.89 * 1.005, $"Limiter true peak {tp:F5} above the ceiling ({frequency} Hz, {drive}x)");
                    }
                }
        Console.WriteLine($"  Limiter alone, 50-100 Hz overs up to 4x: sample peak {worstOvershoot:F6} x ceiling, true peak {worstTruePeak:F4} x ceiling.");
        var burst = Tone(5000, 0.3, Rate, Rate); for (int i = 24000; i < 24480; i++) burst[i] *= 4;
        var r = new PcmDspProcessor(Rate, 1, Unity with { OutputCeiling = 0.89, Limiter = new(true) }); r.Process(burst);
        Check(TruePeak(burst) <= 0.89 * 1.005, $"Inter-sample peak {TruePeak(burst):F4} above the 0.89 ceiling");
        Check(r.LimitedSamples > 0, "Limited samples were not counted");
        Check(new PcmDspProcessor(Rate, 1).LimiterLatencyFrames == 0, "The limiter is not off by default");
    }

    // ---------- Noise suppression ----------

    internal static void SmoothResidualRemovesMusicalNoise()
    {
        double inputKurtosis = 0;
        var results = new Dictionary<bool, (double Residual, double Kurtosis, double Tone)>();
        foreach (bool smooth in new[] { false, true })
        {
            var noise = Noise(Rate * 4, Math.Pow(10, -50 / 20.0), 11); var dry = (float[])noise.Clone();
            var p = new PcmDspProcessor(Rate, 1, Unity with { NoiseSuppression = new(true, -50, 18, smooth) }); p.Process(noise);
            inputKurtosis = SpectralKurtosis(dry, Rate);
            var mixture = Noise(Rate * 4, Math.Pow(10, -50 / 20.0), 12);
            for (int i = 0; i < mixture.Length; i++) mixture[i] += (float)(0.03 * Math.Sin(2 * Math.PI * 440 * i / Rate));
            new PcmDspProcessor(Rate, 1, Unity with { NoiseSuppression = new(true, -50, 18, smooth) }).Process(mixture);
            var aligned = mixture.Skip(p.NoiseSuppressionLatencyFrames).ToArray();
            results[smooth] = (Db(Rms(noise, Rate) / Rms(dry, Rate)), SpectralKurtosis(noise, Rate), Db(Amplitude(aligned, 440, Rate, Rate, Rate * 2) / 0.03));
        }
        Check(results[false].Kurtosis > 1.4 * inputKurtosis, "Default suppressor no longer shows the documented musical-noise signature");
        Check(results[true].Kurtosis < 1.2 * inputKurtosis, $"SmoothResidual still leaves musical noise: kurtosis {results[true].Kurtosis:F2} vs input {inputKurtosis:F2}");
        Check(results[true].Residual < results[false].Residual && Math.Abs(results[true].Tone) < 0.05, "SmoothResidual lost noise reduction or the tone");
        // Zero reduction is still an exact delay with the opt-in rule.
        var source = Noise(Rate, 0.05, 5); var samples = (float[])source.Clone();
        var exact = new PcmDspProcessor(Rate, 1, Unity with { NoiseSuppression = new(true, -40, 0, true) }); exact.Process(samples);
        int delay = exact.NoiseSuppressionLatencyFrames;
        for (int i = delay; i < samples.Length; i++) Check(Math.Abs(samples[i] - source[i - delay]) < 1e-6, "SmoothResidual zero reduction is not an exact delay");
        Console.WriteLine($"  Spectral kurtosis: input {inputKurtosis:F2}, default {results[false].Kurtosis:F2} (residual {results[false].Residual:F1} dB), SmoothResidual {results[true].Kurtosis:F2} (residual {results[true].Residual:F1} dB).");
    }

    // ---------- Numerics ----------

    internal static void NoSubnormalsAfterLongSilence()
    {
        var settings = Unity with { Bands = Preset(), Compressor = new(true, -30, 4, 10, 5000, 0), Gate = new(true, -60), NoiseAttenuation = new(true),
            Stereo = new(true, Crossfeed: 0.5), Limiter = new(true), GainSmoothingMs = 2000 };
        var p = new PcmDspProcessor(Rate, 2, settings);
        var loud = new float[Rate * 2]; for (int i = 0; i < loud.Length; i++) loud[i] = (float)(0.5 * Math.Sin(i * 0.01));
        p.Process(loud);
        p.UpdateSettings(settings with { Mute = true });
        var block = new float[960]; var timer = Stopwatch.StartNew();
        for (int i = 0; i < Rate * 240 / 480; i++) { Array.Clear(block); p.Process(block); } // four minutes of digital silence (longer than the 5 s release needs to reach zero)
        double silentRate = timer.Elapsed.TotalMilliseconds;
        var subnormal = Subnormals(p, new HashSet<object>(ReferenceEqualityComparer.Instance));
        Check(subnormal.Count == 0, "Subnormal state after silence: " + string.Join(", ", subnormal.Take(6)));
        var noisy = Noise(960, 0.1, 8); timer.Restart();
        for (int i = 0; i < Rate * 60 / 480; i++) p.Process(noisy);
        Console.WriteLine($"  Four minutes of silence processed in {silentRate:F0} ms ({silentRate / 4:F0} ms per minute) vs {timer.Elapsed.TotalMilliseconds:F0} ms for one minute of noise; no subnormal state.");
    }

    // ---------- Resampler ----------

    internal static void ResamplerQuality()
    {
        foreach (var (from, to) in new[] { (44100, 48000), (48000, 44100), (96000, 48000), (88200, 48000), (48000, 96000), (192000, 48000) })
        {
            double worstRipple = 0, worstThd = double.MinValue;
            foreach (double frequency in new[] { 20.0, 100, 1000, 5000, 10000, 15000, 19000 })
            {
                var y = Resample(from, to, frequency);
                double gain = Db(Amplitude(y, frequency, to, to, to) / 0.5);
                worstRipple = Math.Max(worstRipple, Math.Abs(gain));
                if (frequency <= 15000) worstThd = Math.Max(worstThd, ThdN(y, frequency, to, to, to));
            }
            Check(worstRipple < 0.01, $"{from}->{to} passband deviation {worstRipple:F4} dB (limit 0.01 dB to 19 kHz)");
            Check(worstThd < -95, $"{from}->{to} THD+N {worstThd:F1} dB (limit -95 dB to 15 kHz)");
            // Tones that would alias into the audible band must be rejected.
            foreach (double tone in new[] { to * 0.55, to * 0.625, to * 0.75 })
            {
                if (tone >= from / 2.0 || from <= to) continue;
                double alias = Math.Abs(to * Math.Round(tone / to) - tone);
                if (alias > 20000) continue;
                double rejection = Db(Amplitude(Resample(from, to, tone), alias, to, to, to) / 0.5);
                Check(rejection < -85, $"{from}->{to}: {tone} Hz aliases to {alias} Hz at {rejection:F1} dB (limit -85 dB)");
            }
            Console.WriteLine($"  {from}->{to}: passband within {worstRipple:F4} dB to 19 kHz, THD+N {worstThd:F1} dB.");
        }
        // Equal rates and no drift: a bit-exact delay.
        var input = Noise(Rate, 0.3, 77); var provider = new ArraySource(input, Rate, 1);
        var exact = new AdaptiveResamplingSampleProvider(provider, Rate);
        var output = new float[Rate]; int got = 0, last = 1;
        while (got < output.Length && last > 0) { last = exact.Read(output, got, Math.Min(500, output.Length - got)); got += last; }
        int delay = exact.KernelDelayFrames;
        Check(got > Rate - 2 * delay, "1:1 conversion stopped early");
        for (int i = delay; i < got; i++) Check(output[i] == input[i - delay], "1:1 conversion is not a bit-exact delay");
    }

    internal static void ResamplerAllocationFree()
    {
        var bus = new BoundedFloatBus(44100, 2, 8820); var resampler = new AdaptiveResamplingSampleProvider(bus, 48000, bus);
        var head = new RenderHeadSampleProvider(new OutputCeilingSampleProvider(resampler), realtimeThread: false);
        var input = new float[882]; var output = new float[960];
        for (int i = 0; i < 200; i++) { bus.Write(input); head.Read(output, 0, output.Length); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++) { bus.Write(input); head.Read(output, 0, output.Length); }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Resampler/render head allocated in the audio callback");
        var limiter = new PcmDspProcessor(Rate, 2, Unity with { Limiter = new(true), NoiseSuppression = new(true, -50, 12, true) });
        var frames = Noise(1024, 0.9, 4);
        for (int i = 0; i < 50; i++) limiter.Process(frames);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++) limiter.Process(frames);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Limiter or SmoothResidual allocated in the audio callback");
    }

    // ---------- Clock drift ----------

    internal static void DriftCompensationHoldsLatencyWithoutGlitches()
    {
        foreach (double ppm in new[] { 100.0, -100, 1000, -1000 })
        {
            var result = SimulateClocks(ppm, seconds: 150, renderStallAt: null, captureStallAt: null);
            Check(result.BusDropped == 0 && result.Underruns == 0 && result.Discarded == 0, $"{ppm} ppm: dropped {result.BusDropped}, underruns {result.Underruns}, discarded {result.Discarded}");
            Check(Math.Abs(result.MeanCorrectionPpm - ppm) < Math.Max(5, Math.Abs(ppm) * 0.05), $"{ppm} ppm: loop settled at {result.MeanCorrectionPpm:F1} ppm");
            Check(result.CorrectionSpreadPpm < 60, $"{ppm} ppm: ratio wandered by {result.CorrectionSpreadPpm:F1} ppm");
            Check(result.MaxFillMs < 60 && result.MinFillMs > 10, $"{ppm} ppm: latency moved between {result.MinFillMs:F1} and {result.MaxFillMs:F1} ms");
            Check(result.Clicks == 0, $"{ppm} ppm: {result.Clicks} discontinuities after start");
            Console.WriteLine($"  {ppm,6} ppm: ratio {result.MeanCorrectionPpm:F1} ppm (sd {result.CorrectionSdPpm:F1}), bus fill {result.MinFillMs:F0}-{result.MaxFillMs:F0} ms, no drops, no underruns, no clicks.");
        }
        // A 150 ms render stall: backlog skipped with a dip, never an overflow drop or a hard edge.
        var stall = SimulateClocks(50, seconds: 30, renderStallAt: 15, captureStallAt: null);
        Check(stall.Resyncs == 1 && stall.Discarded > 0 && stall.BusDropped == 0 && stall.Clicks == 0, $"Render stall: resyncs {stall.Resyncs}, discarded {stall.Discarded}, dropped {stall.BusDropped}, clicks {stall.Clicks}");
        // A 60 ms capture stall: concealed underrun (fade, silence, fade), then back to normal. The stall starts at every
        // millisecond across a 10 ms packet, so the input runs out at every point of a render read, including on its
        // boundary (where the old code had nothing left to fade) and a few frames into it (a fade of only those frames).
        for (int offset = 0; offset < 10; offset++)
        {
            var dry = SimulateClocks(50, seconds: 30, renderStallAt: null, captureStallAt: 15 + offset * 0.001);
            Check(dry.Underruns == 1 && dry.Clicks == 0 && dry.BusDropped == 0, $"Capture stall at +{offset} ms: underruns {dry.Underruns}, clicks {dry.Clicks}");
        }
        // The previous FIFO for comparison: latency creeps at +100 ppm and drops out at -100 ppm.
        var (creepMs, _) = SimulateLegacyFifo(100, 600);
        var (_, dropouts) = SimulateLegacyFifo(-100, 600);
        Check(creepMs > 40 && dropouts > 0, $"Legacy FIFO comparison no longer reproduces drift ({creepMs:F0} ms, {dropouts} insertions)");
        Console.WriteLine($"  Without the loop (old FIFO, 10 min): +100 ppm latency grows {creepMs:F0} ms; -100 ppm gives {dropouts} silence insertions.");
    }

    private sealed record ClockResult(long BusDropped, long Underruns, long Discarded, long Resyncs, double MeanCorrectionPpm, double CorrectionSdPpm,
        double CorrectionSpreadPpm, double MinFillMs, double MaxFillMs, int Clicks);

    /// <summary>Capture packets of 480 frames on a clock offset by <paramref name="ppm"/>, render reads of 480 frames every 10 ms,
    /// packet jitter 0-2 ms plus occasional 6 ms lateness, all on a virtual clock.</summary>
    private static ClockResult SimulateClocks(double ppm, int seconds, double? renderStallAt, double? captureStallAt)
    {
        var clock = new VirtualClock();
        var bus = new BoundedFloatBus(Rate, 1, Rate / 5, clock);
        var resampler = new AdaptiveResamplingSampleProvider(bus, Rate, bus);
        var random = new Random(17);
        double capturePeriod = 0.010 / (1 + ppm * 1e-6), renderPeriod = 0.010, captureTime = 0, renderTime = 0.0047;
        long captured = 0; var packet = new float[480]; var read = new float[480];
        double previous1 = 0, previous2 = 0; int clicks = 0; var corrections = new List<double>(); double minFill = double.MaxValue, maxFill = 0;
        bool renderStalled = false, captureStalled = false;
        while (renderTime < seconds)
        {
            double captureEvent = captureTime + (random.NextDouble() < 0.003 ? 0.006 : random.NextDouble() * 0.002);
            if (captureEvent <= renderTime)
            {
                clock.Ticks = (long)(captureEvent * 1e9);
                if (captureStallAt is double c && captureEvent >= c && captureEvent < c + 0.06) { captureStalled = true; captureTime += capturePeriod; captured += 480; continue; }
                for (int i = 0; i < 480; i++) packet[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 100 * (captured + i) / Rate));
                captured += 480; bus.Write(packet); captureTime += capturePeriod;
                continue;
            }
            clock.Ticks = (long)(renderTime * 1e9);
            if (renderStallAt is double r && renderTime >= r && renderTime < r + 0.15) { renderStalled = true; renderTime += renderPeriod; continue; }
            int queued = bus.BufferedFrames; // fill as the render callback sees it, before taking its block
            resampler.Read(read, 0, 480);
            for (int i = 0; i < 480; i++)
            {
                double second = read[i] - 2 * previous1 + previous2; previous2 = previous1; previous1 = read[i];
                // A gap in the sine (stall) is allowed only as a smooth fade; a step of more than -50 dB re the tone counts.
                if (renderTime > 1 && Math.Abs(second) > 0.25 * Math.Pow(10, -50 / 20.0)) clicks++;
            }
            if (renderTime > 100 || (seconds < 100 && renderTime > 5))
            {
                corrections.Add(resampler.CorrectionPpm);
                if (!renderStalled && !captureStalled) { minFill = Math.Min(minFill, queued / 48.0); maxFill = Math.Max(maxFill, queued / 48.0); }
            }
            renderTime += renderPeriod;
        }
        double mean = corrections.Average();
        return new(bus.DroppedFrames, resampler.Underruns, resampler.DiscardedFrames, resampler.Resyncs, mean,
            Math.Sqrt(corrections.Average(x => (x - mean) * (x - mean))), corrections.Max() - corrections.Min(), minFill, maxFill, clicks);
    }

    private static (double CreepMs, long Insertions) SimulateLegacyFifo(double ppm, int seconds)
    {
        var bus = new BoundedFloatBus(Rate, 1, Rate / 5); var random = new Random(17);
        double capturePeriod = 0.010 / (1 + ppm * 1e-6), captureTime = 0, renderTime = 0.0047; var packet = new float[480]; var read = new float[480];
        double firstFill = -1; long insertions = 0;
        while (renderTime < seconds)
        {
            double captureEvent = captureTime + (random.NextDouble() < 0.003 ? 0.006 : random.NextDouble() * 0.002);
            if (captureEvent <= renderTime) { bus.Write(packet); captureTime += capturePeriod; continue; }
            long silent = bus.SilentFrames; bus.Read(read, 0, 480); if (renderTime > 1 && bus.SilentFrames != silent) insertions++;
            if (firstFill < 0 && renderTime > 10) firstFill = bus.BufferedFrames / 48.0;
            renderTime += 0.010;
        }
        return (bus.BufferedFrames / 48.0 - firstFill, insertions);
    }

    internal static void RealtimeRegistrationIsSafe()
    {
        // Real MMCSS: never throws, cached per thread, released on request.
        bool first = false, second = false, registeredFlag = false, releasedFlag = true, otherBefore = true; Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                first = AudioThreadScheduling.RegisterCurrentThread(); second = AudioThreadScheduling.RegisterCurrentThread(); registeredFlag = AudioThreadScheduling.CurrentThreadRegistered;
                AudioThreadScheduling.ReleaseCurrentThread(); AudioThreadScheduling.ReleaseCurrentThread(); releasedFlag = AudioThreadScheduling.CurrentThreadRegistered;
            }
            catch (Exception error) { failure = error; }
        });
        thread.Start(); thread.Join();
        var other = new Thread(() => otherBefore = AudioThreadScheduling.CurrentThreadRegistered);
        other.Start(); other.Join();
        Check(failure is null && first == second && registeredFlag == first && !releasedFlag, "MMCSS registration threw, was not cached per thread or was not released");
        Check(!otherBefore, "Registration leaked to another thread");

        // Fake scheduler: each stream start registers its capture and render threads, each stop must release them on the
        // thread that registered (NAudio raises RecordingStopped/PlaybackStopped on the stream thread).
        var fake = new FakeScheduler();
        var previous = AudioThreadScheduling.Scheduler;
        AudioThreadScheduling.Scheduler = fake;
        try
        {
            OnThread(() =>
            {
                for (int i = 0; i < 100; i++) AudioThreadScheduling.RegisterCurrentThread();
                Check(fake.Granted == 1, "Repeated callbacks registered a thread more than once");
                AudioThreadScheduling.ReleaseCurrentThread(); AudioThreadScheduling.ReleaseCurrentThread();
                Check(fake.Outstanding == 0 && fake.Reverted == 1, "Release did not revert exactly the granted handle");
                AudioThreadScheduling.RegisterCurrentThread();
                Check(fake.Granted == 2, "A thread could not register again after a release");
                AudioThreadScheduling.ReleaseCurrentThread();
            });
            const int cycles = 200;
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                var capture = new FakeStream(); var render = new FakeStream();
                AudioThreadScheduling.ReleaseOnStop((IWaveIn)capture); AudioThreadScheduling.ReleaseOnStop((IWavePlayer)render);
                var head = new RenderHeadSampleProvider(new SilenceProvider(), realtimeThread: true);
                var captureThread = new Thread(() =>
                {
                    for (int callback = 0; callback < 5; callback++) AudioThreadScheduling.RegisterCurrentThread(); // DataAvailable callbacks
                    capture.RaiseRecordingStopped();
                });
                var renderThread = new Thread(() =>
                {
                    var buffer = new float[960];
                    for (int callback = 0; callback < 5; callback++) head.Read(buffer, 0, buffer.Length); // the render head registers
                    render.RaisePlaybackStopped();
                });
                captureThread.Start(); renderThread.Start(); captureThread.Join(); renderThread.Join();
                Check(fake.Outstanding == 0, $"MMCSS registrations accumulate: {fake.Outstanding} still held after stop {cycle + 1}");
            }
            Check(fake.Granted == 2 + 2 * cycles && fake.Reverted == fake.Granted && fake.MaximumOutstanding <= 2,
                $"Start/stop cycles: {fake.Granted} granted, {fake.Reverted} released, at most {fake.MaximumOutstanding} held at once");
            // A refusal is counted once per thread and leaves nothing to release.
            fake.Refuse = true;
            long failuresBefore = AudioThreadScheduling.Failures;
            OnThread(() =>
            {
                Check(!AudioThreadScheduling.RegisterCurrentThread() && !AudioThreadScheduling.RegisterCurrentThread(), "A refused registration reported success");
                AudioThreadScheduling.ReleaseCurrentThread();
            });
            Check(AudioThreadScheduling.Failures == failuresBefore + 1 && fake.Outstanding == 0, "Refused registration was retried or released");
        }
        finally { AudioThreadScheduling.Scheduler = previous; }
        Console.WriteLine($"  MMCSS \"{AudioThreadScheduling.TaskName}\" on a worker thread: {(first ? "granted" : "refused (service unavailable)")}; " +
            $"200 fake start/stop cycles held at most {fake.MaximumOutstanding} registrations and released all {fake.Granted}.");
    }

    /// <summary>
    /// The router's pacer: 10 ms waits that really last about 10 ms (not the default timer's 15.6 ms), without raising the
    /// system timer resolution or spinning, and a stop that wakes it at once. Timer only; no audio device.
    /// </summary>
    internal static void RouterPacingIsHighResolutionAndIdle()
    {
        using var stop = new ManualResetEventSlim();
        double meanMs = 0, cpuMs = 0; bool highResolution = false;
        OnThread(() =>
        {
            using var pacer = new AudioThreadPacer(stop);
            highResolution = pacer.IsHighResolution;
            pacer.Wait(10); // first wait warms up the timer path
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
            const int waits = 50;
            for (int i = 0; i < waits; i++) Check(!pacer.Wait(10), "Pacer reported a stop that was never requested");
            meanMs = clock.Elapsed.TotalMilliseconds / waits;
            process.Refresh(); cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        });
        Check(highResolution, "The high-resolution waitable timer is unavailable on this Windows build");
        Check(meanMs >= 9.5 && meanMs < 12.5, $"10 ms pacer waits averaged {meanMs:F2} ms");
        Check(cpuMs < 0.2 * meanMs * 50, $"Pacing used {cpuMs:F0} ms of CPU in {meanMs * 50:F0} ms: busy waiting");
        double stopMs = 0; bool stopped = false;
        OnThread(() =>
        {
            using var pacer = new AudioThreadPacer(stop);
            var waker = new Thread(() => { Thread.Sleep(20); stop.Set(); }); waker.Start();
            var clock = Stopwatch.StartNew(); stopped = pacer.Wait(1000); stopMs = clock.Elapsed.TotalMilliseconds;
            waker.Join();
        });
        Check(stopped && stopMs < 200, $"Stop woke a 1 s pacer wait after {stopMs:F0} ms (reported stop: {stopped})");
        Console.WriteLine($"  10 ms router waits average {meanMs:F2} ms (default timer would give 15.6 ms), {cpuMs:F0} ms CPU over {meanMs * 50:F0} ms; stop wakes in {stopMs:F0} ms.");
    }

    /// <summary>Runs on a fresh thread (a new MMCSS identity) and rethrows its failure here.</summary>
    private static void OnThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException(failure.Message, failure);
    }

    private sealed class FakeScheduler : AudioThreadScheduling.IScheduler
    {
        private readonly object sync = new();
        private readonly HashSet<nint> held = [];
        private long next;
        public bool Refuse { get; set; }
        public int Granted { get; private set; }
        public int Reverted { get; private set; }
        public int MaximumOutstanding { get; private set; }
        public int Outstanding { get { lock (sync) return held.Count; } }
        public nint Register(string taskName)
        {
            if (taskName != AudioThreadScheduling.TaskName) throw new InvalidOperationException("Unexpected MMCSS task");
            if (Refuse) return 0;
            lock (sync) { nint handle = (nint)Interlocked.Increment(ref next); held.Add(handle); Granted++; MaximumOutstanding = Math.Max(MaximumOutstanding, held.Count); return handle; }
        }
        public void Revert(nint handle)
        {
            lock (sync) { if (!held.Remove(handle)) throw new InvalidOperationException("Reverted an unknown or already reverted handle"); Reverted++; }
        }
    }

    private sealed class FakeStream : IWaveIn, IWavePlayer
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public WaveFormat OutputWaveFormat => WaveFormat;
        public PlaybackState PlaybackState => PlaybackState.Stopped;
        public float Volume { get; set; } = 1;
        public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void RaiseRecordingStopped() => RecordingStopped?.Invoke(this, new());
        public void RaisePlaybackStopped() => PlaybackStopped?.Invoke(this, new());
        public void StartRecording() { }
        public void StopRecording() { }
        public void Init(IWaveProvider waveProvider) { }
        public void Play() { }
        public void Stop() { }
        public void Pause() { }
        public void Dispose() { }
    }

    private sealed class SilenceProvider : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
    }

    // ---------- helpers ----------

    private static (double Steady, double Transition) ClickScenario(DspSettings start, Action<PcmDspProcessor, int> apply, int channels)
    {
        const int blocks = 200, changeBlock = 100; const double amplitude = 0.25;
        var p = new PcmDspProcessor(Rate, channels, start);
        var y = new float[blocks * 480 * channels];
        for (int i = 0; i < blocks * 480; i++) { float v = (float)(amplitude * Math.Sin(2 * Math.PI * 100 * i / Rate)); for (int c = 0; c < channels; c++) y[i * channels + c] = v; }
        for (int b = 0; b < blocks; b++) { apply(p, b); p.Process(y.AsSpan(b * 480 * channels, 480 * channels)); }
        return (MaxSecondDifference(y, channels, (changeBlock - 40) * 480, (changeBlock - 1) * 480),
            MaxSecondDifference(y, channels, changeBlock * 480 - 2, (changeBlock + 60) * 480));
    }

    private static double MaxSecondDifference(float[] y, int channels, int fromFrame, int toFrame)
    {
        double maximum = 0;
        for (int c = 0; c < channels; c++)
            for (int i = Math.Max(2, fromFrame); i < toFrame; i++)
                maximum = Math.Max(maximum, Math.Abs(y[i * channels + c] - 2.0 * y[(i - 1) * channels + c] + y[(i - 2) * channels + c]));
        return maximum;
    }

    private static EqBand[] WithBand(EqBand[] bands, int index, EqBand band) { bands[index] = band; return bands; }

    private static EqBand[] Preset()
    {
        var b = DspSettings.FlatBands();
        b[0] = new(80, 0, 0.7071, EqFilterType.HighPass);
        b[3] = new(200, 3, 0.7071, EqFilterType.LowShelf);
        b[7] = new(3000, 4, 1);
        b[9] = new(10000, 2, 0.7071, EqFilterType.HighShelf);
        return b;
    }

    private static DspSettings Single(EqBand band, int rate)
    {
        var bands = DspSettings.FlatBands().Select(b => b with { FrequencyHz = Math.Min(b.FrequencyHz, rate * 0.45) }).ToArray();
        bands[5] = band;
        return Unity with { Bands = bands };
    }

    private static IEnumerable<EqBand> Bands(int rate)
    {
        foreach (var type in Enum.GetValues<EqFilterType>())
            foreach (var (frequency, gain, q) in new[] { (31.0, 12.0, 4.0), (1000.0, -9.0, 0.5), (rate * 0.4, 6.0, 1.0), (125.0, 24.0, 20.0), (8000.0, -24.0, 0.1) })
                yield return new EqBand(frequency, type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf ? gain : 0, q, type);
    }

    private static readonly double[] ProbeRatios = [0.1, 0.5, 0.9, 1.0, 1.1, 2.0, 10.0];
    private static double[] Probe(double centre, int rate)
    {
        double nyquist = rate * 0.499;
        return ProbeRatios.Select(r => Math.Clamp(centre * r, 15, nyquist * 0.98)).Append(Math.Min(20000, nyquist * 0.98)).ToArray();
    }

    /// <summary>RBJ analog prototypes evaluated at the bilinear-transform image of the digital frequency.</summary>
    private static double Analytic(EqBand band, double frequency, int rate)
    {
        double normalized = Math.Tan(Math.PI * frequency / rate) / Math.Tan(Math.PI * band.FrequencyHz / rate);
        var s = new Complex(0, normalized);
        double a = Math.Pow(10, band.GainDb / 40), q = band.Q, root = Math.Sqrt(a);
        Complex h = band.Type switch
        {
            EqFilterType.LowPass => 1 / (s * s + s / q + 1),
            EqFilterType.HighPass => s * s / (s * s + s / q + 1),
            EqFilterType.Notch => (s * s + 1) / (s * s + s / q + 1),
            EqFilterType.Peak => (s * s + s * (a / q) + 1) / (s * s + s / (a * q) + 1),
            EqFilterType.LowShelf => a * (s * s + root / q * s + a) / (a * s * s + root / q * s + 1),
            EqFilterType.HighShelf => a * (a * s * s + root / q * s + 1) / (s * s + root / q * s + a),
            _ => throw new ArgumentOutOfRangeException(nameof(band))
        };
        return 20 * Math.Log10(Math.Max(1e-30, h.Magnitude));
    }

    private static float[] Resample(int from, int to, double frequency)
    {
        var resampler = new AdaptiveResamplingSampleProvider(new ToneProvider(from, frequency, 0.5, 1), to);
        var y = new float[to * 2]; int got = 0; while (got < y.Length) got += resampler.Read(y, got, Math.Min(4096, y.Length - got));
        return y;
    }

    private static List<string> Subnormals(object root, HashSet<object> seen)
    {
        var found = new List<string>();
        if (!seen.Add(root)) return found;
        if (root is Array elements)
        {
            foreach (var item in elements) if (item is not null && (item is Array || item.GetType().Namespace?.StartsWith("Soltex", StringComparison.Ordinal) == true)) found.AddRange(Subnormals(item, seen));
            return found;
        }
        for (var type = root.GetType(); type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                object? value = field.GetValue(root);
                switch (value)
                {
                    case double d when double.IsSubnormal(d): found.Add($"{type.Name}.{field.Name}"); break;
                    case double[] array when array.Any(double.IsSubnormal): found.Add($"{type.Name}.{field.Name}[]"); break;
                    case float[] array when array.Any(float.IsSubnormal): found.Add($"{type.Name}.{field.Name}[]"); break;
                    case System.Collections.IEnumerable items and not string when field.FieldType.Namespace?.StartsWith("Soltex", StringComparison.Ordinal) == true || value is Array:
                        foreach (var item in items) if (item is not null && item.GetType().Namespace?.StartsWith("Soltex", StringComparison.Ordinal) == true) found.AddRange(Subnormals(item, seen));
                        break;
                    case not null when value.GetType().Namespace?.StartsWith("Soltex", StringComparison.Ordinal) == true && !value.GetType().IsEnum && !value.GetType().IsPrimitive:
                        found.AddRange(Subnormals(value, seen)); break;
                }
            }
        }
        return found;
    }

    private static int FirstBelow(float[] x, double level) { for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i]) < level) return i; return x.Length; }
    private static int FirstAbove(float[] x, double level) { for (int i = 0; i < x.Length; i++) if (Math.Abs(x[i]) > level) return i; return x.Length; }
    private static float[] Constant(float value, int count) { var values = new float[count]; Array.Fill(values, value); return values; }
    private static float[] Tone(double frequency, double amplitude, int frames, int rate)
    { var x = new float[frames]; for (int i = 0; i < frames; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate)); return x; }
    private static float[] Noise(int frames, double rms, int seed)
    {
        var random = new Random(seed); var x = new float[frames];
        for (int i = 0; i < frames; i++) { double u1 = 1 - random.NextDouble(), u2 = random.NextDouble(); x[i] = (float)(rms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }
        return x;
    }
    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(1e-30, ratio));
    private static double Rms(float[] x, int from) { double e = 0; for (int i = from; i < x.Length; i++) e += x[i] * (double)x[i]; return Math.Sqrt(e / (x.Length - from)); }

    /// <summary>Lock-in amplitude with a 4-term Blackman-Harris window (leakage below -92 dB).</summary>
    private static double Amplitude(float[] y, double frequency, int rate, int start, int length)
    {
        double s = 0, c = 0, weights = 0;
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / (length - 1);
            double w = 0.35875 - 0.48829 * Math.Cos(2 * Math.PI * t) + 0.14128 * Math.Cos(4 * Math.PI * t) - 0.01168 * Math.Cos(6 * Math.PI * t);
            double phase = 2 * Math.PI * frequency * (start + i) / rate;
            s += w * y[start + i] * Math.Sin(phase); c += w * y[start + i] * Math.Cos(phase); weights += w;
        }
        return 2 * Math.Sqrt(s * s + c * c) / weights;
    }

    /// <summary>Least-squares sine + DC fit at the known frequency; everything else is distortion plus noise.</summary>
    private static double ThdN(float[] y, double frequency, int rate, int start, int length)
    {
        double ss = 0, sc = 0, cc = 0, ys = 0, yc = 0, sum = 0;
        for (int i = 0; i < length; i++)
        {
            double phase = 2 * Math.PI * frequency * (start + i) / rate, s = Math.Sin(phase), c = Math.Cos(phase);
            ss += s * s; sc += s * c; cc += c * c; ys += y[start + i] * s; yc += y[start + i] * c; sum += y[start + i];
        }
        double det = ss * cc - sc * sc, a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det, dc = sum / length, residual = 0, signal = 0;
        for (int i = 0; i < length; i++)
        {
            double phase = 2 * Math.PI * frequency * (start + i) / rate, fit = a * Math.Sin(phase) + b * Math.Cos(phase);
            double error = y[start + i] - fit - dc; residual += error * error; signal += fit * fit;
        }
        return 10 * Math.Log10(Math.Max(1e-300, residual / signal));
    }

    /// <summary>Peak of a 4x windowed-sinc reconstruction (true-peak estimate).</summary>
    private static double TruePeak(float[] x)
    {
        double peak = 0;
        for (int i = 16; i < x.Length - 16; i++)
            for (int p = 0; p < 4; p++)
            {
                double fraction = p / 4.0, value = 0;
                for (int k = -15; k <= 16; k++)
                {
                    double t = k - fraction, sinc = Math.Abs(t) < 1e-12 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                    value += x[i + k] * sinc * (0.5 + 0.5 * Math.Cos(Math.PI * t / 17));
                }
                peak = Math.Max(peak, Math.Abs(value));
            }
        return peak;
    }

    /// <summary>Kurtosis of short-time power in 1.9–19 kHz bins; Gaussian noise gives about 9, musical noise much more.</summary>
    private static double SpectralKurtosis(float[] x, int from)
    {
        const int size = 512; var values = new List<double>();
        var window = Enumerable.Range(0, size).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / size)).ToArray();
        for (int start = from; start + size <= x.Length; start += size)
            for (int k = 20; k < 200; k += 2)
            {
                double re = 0, im = 0;
                for (int n = 0; n < size; n++) { double phase = 2 * Math.PI * k * n / size; re += window[n] * x[start + n] * Math.Cos(phase); im -= window[n] * x[start + n] * Math.Sin(phase); }
                values.Add(re * re + im * im);
            }
        double mean = values.Average(), m2 = values.Average(v => (v - mean) * (v - mean)), m4 = values.Average(v => Math.Pow(v - mean, 4));
        return m4 / (m2 * m2);
    }

    private sealed class ToneProvider(int rate, double frequency, double amplitude, int channels, double startPhase = 0) : ISampleProvider
    {
        private long n;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += channels)
            {
                float value = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * n++ / rate + startPhase));
                for (int c = 0; c < channels; c++) buffer[offset + i + c] = value;
            }
            return count;
        }
    }

    private sealed class ArraySource(float[] samples, int rate, int channels) : ISampleProvider
    {
        private int position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, samples.Length - position);
            Array.Copy(samples, position, buffer, offset, available); position += available;
            return available;
        }
    }

    private sealed class VirtualClock : TimeProvider
    {
        public long Ticks = 1;
        public override long GetTimestamp() => Math.Max(1, Ticks);
        public override long TimestampFrequency => 1_000_000_000;
    }
}
