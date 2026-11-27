using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

/// <summary>
/// Device-free simulations of the drift-compensating converter on the real device periods: steady latency, a source that
/// pauses (with and without the router's empty packets), router-sized reads, and the exact moment a stalled source runs dry.
/// Everything runs on a virtual clock.
/// </summary>
internal static class ConverterLatencyChecks
{
    private const int Rate = 48000;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class VirtualClock : TimeProvider
    {
        public long Ticks = 1;
        public double Seconds { set => Ticks = Math.Max(1, (long)(value * 1e9)); }
        public override long GetTimestamp() => Math.Max(1, Ticks);
        public override long TimestampFrequency => 1_000_000_000;
    }

    /// <summary>What feeds the converter's bus: 10 ms shared-mode capture packets, or the router delivering what an owned
    /// render endpoint produced since its previous pump.</summary>
    private enum Source { Capture, RouterRender }
    /// <summary>What reads the converter: a 10 ms WASAPI render callback, or the router refilling the owned capture
    /// endpoint's driver queue to 960 frames on each pump.</summary>
    private enum Sink { Render, RouterRefill }
    /// <summary>Router wake-ups: the high-resolution timer (10 ms, up to 1 ms late), or the plain wait it falls back to
    /// (the default 15.6 ms timer tick, +-0.5 ms).</summary>
    private enum Pacing { HighResolution, DefaultTimer }

    /// <param name="JitterMs">Uniform capture lateness 0..JitterMs per packet.</param>
    /// <param name="SpikeMs">Occasional extra lateness, with probability SpikeChance per packet.</param>
    private sealed record Profile(string Name, Source Source, Sink Sink, Pacing Pacing, double Ppm, double JitterMs, double SpikeMs, double SpikeChance, int Seed = 17);

    /// <param name="ConverterMs">Mean time in the converter's bus plus its kernel delay.</param>
    /// <param name="DriverMs">Mean time in the owned endpoint's driver queue that the router fills or drains (0 without the router).</param>
    private sealed record PathResult(double TargetMs, double ConverterMs, double DriverMs, long Underruns, int Clicks, double MinQueuedMs,
        long DriverUnderruns, double EarlyTargetMs)
    {
        public double TotalMs => ConverterMs + DriverMs;
    }

    // ---------- steady latency ----------

    /// <summary>
    /// The converter holds one read plus one packet, a 2 ms fade reserve and a small jitter allowance. Measured on 10 ms
    /// shared-mode capture feeding a 10 ms render (the bridge, the mixer), the owned transport router refilling the virtual
    /// microphone (mic to cable), and the router draining an owned render endpoint to speakers, with the router on its
    /// high-resolution 10 ms timer and on the plain-wait fallback, with realistic jitter and the drift test's harsher
    /// jitter. Before these changes the converter held 30.0 ms (bridge) / 35.6 ms (mic to cable on the 15.6 ms timer)
    /// and added 20.7 / 23.5 ms (35.7 ms with the driver queue). Mic to cable: the router refills the virtual microphone
    /// to two 10 ms driver packets on every pump, so a faster cadence moves time from the converter into the driver queue
    /// and the total stays put; owned endpoint to speakers gains the whole difference.
    /// </summary>
    internal static void SteadyLatencyFollowsDevicePeriods()
    {
        (Profile Profile, double MaxTargetMs, double MaxTotalMs)[] cases =
        [
            (new("bridge, quiet PC (0-1 ms jitter)", Source.Capture, Sink.Render, Pacing.HighResolution, 37, 1, 0, 0), 26.5, 17.0),
            (new("mic to cable, router on the high-resolution timer, quiet PC", Source.Capture, Sink.RouterRefill, Pacing.HighResolution, 37, 1, 0, 0), 27.0, 32.0),
            (new("mic to cable, router on the plain-wait fallback, quiet PC", Source.Capture, Sink.RouterRefill, Pacing.DefaultTimer, 37, 1, 0, 0), 32.0, 32.0),
            (new("owned endpoint to speakers, router on the high-resolution timer", Source.RouterRender, Sink.Render, Pacing.HighResolution, 37, 0, 0, 0), 27.0, 23.0),
            (new("owned endpoint to speakers, router on the plain-wait fallback", Source.RouterRender, Sink.Render, Pacing.DefaultTimer, 37, 0, 0, 0), 32.5, 28.0),
            // The drift test's jitter (late packets up to 8 ms) genuinely needs about the old margin; it must stay glitch free.
            (new("bridge, drift-test jitter (0-2 ms, 0.3 % at +6 ms)", Source.Capture, Sink.Render, Pacing.HighResolution, 100, 2, 6, 0.003), 31.5, 22.0),
            (new("mic to cable, high-resolution timer, drift-test jitter", Source.Capture, Sink.RouterRefill, Pacing.HighResolution, 100, 2, 6, 0.003), 32.0, 37.5),
        ];
        foreach (var (profile, maxTarget, maxTotal) in cases)
        {
            var r = Simulate(profile, 600, 300);
            Console.WriteLine($"  {profile.Name}: holds {r.TargetMs:F1} ms (first 15 s {r.EarlyTargetMs:F1}), converter adds {r.ConverterMs:F1} ms" +
                (r.DriverMs > 0 ? $" + driver queue {r.DriverMs:F1} ms = {r.TotalMs:F1} ms" : "") + $"; least queued after a read {r.MinQueuedMs:F1} ms");
            Check(r.Underruns == 0 && r.Clicks == 0 && r.DriverUnderruns == 0, $"{profile.Name}: {r.Underruns} underruns, {r.Clicks} clicks, {r.DriverUnderruns} driver underruns");
            Check(r.TargetMs <= maxTarget && r.TotalMs <= maxTotal, $"{profile.Name}: holds {r.TargetMs:F1} ms (limit {maxTarget}), adds {r.TotalMs:F1} ms (limit {maxTotal})");
            Check(r.EarlyTargetMs <= 36, $"{profile.Name}: started at {r.EarlyTargetMs:F1} ms");
        }
    }

    private static double Pump(Random random, Pacing pacing) =>
        pacing == Pacing.HighResolution ? 0.010 + random.NextDouble() * 0.001 : 0.015625 + (random.NextDouble() - 0.5) * 0.001;

    /// <summary>
    /// Event simulation of one converter between its producer and consumer on a virtual clock. The router refill keeps the
    /// owned capture endpoint's driver queue at 960 frames after each pump (the driver drains it continuously); the router
    /// drain takes everything an owned render endpoint produced since the previous pump. Latency is the time-weighted mean
    /// bus fill plus the kernel delay, and the time-weighted mean driver queue.
    /// </summary>
    private static PathResult Simulate(Profile profile, double seconds, double measureFrom)
    {
        var clock = new VirtualClock();
        var bus = new BoundedFloatBus(Rate, 1, Rate / 5, clock);
        var resampler = new AdaptiveResamplingSampleProvider(bus, Rate, bus);
        var random = new Random(profile.Seed);
        double sourceRate = Rate * (1 + profile.Ppm * 1e-6), capturePeriod = 0.010 / (1 + profile.Ppm * 1e-6), captureTime = 0;
        bool routerSource = profile.Source == Source.RouterRender, routerSink = profile.Sink == Sink.RouterRefill;
        double producerEvent = routerSource ? Pump(random, profile.Pacing) : Lateness(random, profile);
        double consumerTime = 0.0047, driver = 0, driverTime = routerSource ? 0 : 0.0047; long driverUnderruns = 0;
        long produced = 0; var packet = new float[4800]; var read = new float[960];
        double previous1 = 0, previous2 = 0; int clicks = 0;
        double fillIntegral = 0, driverIntegral = 0, measuredTime = 0, lastEvent = 0, minQueued = double.MaxValue;
        double targetSum = 0, earlyTargetSum = 0; int targetCount = 0, earlyCount = 0;
        double DriverLevel(double t) => routerSource ? driver + sourceRate * (t - driverTime) : routerSink ? Math.Max(0, driver - Rate * (t - driverTime)) : 0;
        while (consumerTime < seconds)
        {
            double now = Math.Min(producerEvent, consumerTime);
            if (now >= measureFrom)
            {
                double from = Math.Max(lastEvent, measureFrom);
                fillIntegral += bus.BufferedFrames * (now - from);
                driverIntegral += (DriverLevel(from) + DriverLevel(now)) / 2 * (now - from);
                measuredTime += now - from;
            }
            lastEvent = now;
            clock.Seconds = now;
            if (producerEvent <= consumerTime)
            {
                int frames = 480;
                if (routerSource)
                {
                    driver += sourceRate * (now - driverTime); driverTime = now;
                    frames = Math.Min(packet.Length, (int)driver); driver -= frames;
                    producerEvent = now + Pump(random, profile.Pacing);
                }
                else { captureTime += capturePeriod; producerEvent = captureTime + Lateness(random, profile); }
                for (int i = 0; i < frames; i++) packet[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 100 * (produced + i) / Rate));
                produced += frames; bus.Write(packet.AsSpan(0, frames));
                continue;
            }
            int wanted = 480;
            if (routerSink)
            {
                driver -= Rate * (consumerTime - driverTime); driverTime = consumerTime;
                if (driver < 0) { driverUnderruns++; driver = 0; }
                wanted = Math.Clamp(960 - (int)Math.Round(driver), 0, 960);
                driver += wanted;
            }
            int queued = bus.BufferedFrames;
            if (wanted > 0)
            {
                resampler.Read(read, 0, wanted);
                for (int i = 0; i < wanted; i++)
                {
                    double second = read[i] - 2 * previous1 + previous2; previous2 = previous1; previous1 = read[i];
                    if (consumerTime > 1 && Math.Abs(second) > 0.25 * Math.Pow(10, -50 / 20.0)) clicks++;
                }
            }
            if (consumerTime > 5) minQueued = Math.Min(minQueued, (queued - wanted) * 1000.0 / Rate);
            if (consumerTime >= measureFrom) { targetSum += resampler.TargetFillFrames; targetCount++; }
            else if (consumerTime > 5 && consumerTime < 15) { earlyTargetSum += resampler.TargetFillFrames; earlyCount++; }
            consumerTime += routerSink ? Pump(random, profile.Pacing) : 0.010;
        }
        double meanFill = fillIntegral / measuredTime * 1000 / Rate;
        return new(targetSum / targetCount * 1000 / Rate, meanFill + resampler.KernelDelayFrames * 1000.0 / Rate, driverIntegral / measuredTime * 1000 / Rate,
            resampler.Underruns, clicks, minQueued, driverUnderruns, earlyTargetSum / earlyCount * 1000 / Rate);
    }

    private static double Lateness(Random random, Profile profile) =>
        random.NextDouble() * profile.JitterMs / 1000 + (random.NextDouble() < profile.SpikeChance ? profile.SpikeMs / 1000 : 0);

    // ---------- a source that pauses ----------

    /// <summary>
    /// An owned source that pauses must not make the converter learn a larger margin: before this change every pause
    /// added 5-7.5 ms (30 ms grew to 80 ms after 20 pauses, and so did the silence before audio came back). Covers a
    /// producer that goes silent, the router's empty packets from an idle endpoint, and router-sized reads.
    /// </summary>
    internal static void PausedSourceKeepsItsLatency()
    {
        // An empty packet delivers nothing: it must not reset the packet size or the write clock.
        var clock = new VirtualClock();
        var bus = new BoundedFloatBus(Rate, 2, Rate, clock);
        clock.Seconds = 1; bus.Write(new float[960]);
        clock.Seconds = 2; bus.Write(ReadOnlySpan<float>.Empty);
        clock.Seconds = 3;
        Check(bus.LastWriteFrames == 480 && bus.WriteCount == 1 && Math.Abs(bus.SecondsSinceLastWrite - 2) < 1e-9 && bus.BufferedFrames == 480,
            "An empty write changed the bus's packet size, write clock or contents");
        // Writes microseconds apart (one delivery cut into blocks) are one packet; the producer's lateness is reported.
        clock.Seconds = 3.0102; bus.Write(new float[1920]); clock.Seconds = 3.01021; bus.Write(new float[960]);
        Check(bus.LastWriteFrames == 1440 && Math.Abs(bus.TakeLatenessPeak() - (2.0102 - 0.010)) < 1e-6 && bus.TakeLatenessPeak() == 0,
            "Split deliveries were not one packet, or lateness was not measured from the previous packet's end");

        foreach (var (empty, router) in new[] { (false, false), (true, false), (true, true) })
        {
            const int cycles = 20;
            var r = SimulatePauses(empty, router, cycles);
            string name = $"{(empty ? "empty packets" : "no packets")}, {(router ? "router reads" : "10 ms render")}";
            double growth = r.TargetBeforePauseMs.Skip(1).Max() - r.TargetBeforePauseMs[1];
            int slowest = r.RestartSilenceFrames.Take(cycles - 1).Max();
            Console.WriteLine($"  Pauses ({name}): held {r.TargetBeforePauseMs[0]:F1} ms before the first pause and {r.TargetBeforePauseMs[^1]:F1} ms " +
                $"before the 20th; audio returns within {slowest / 48.0:F1} ms; {r.SourcePauses} pauses, {r.Clicks} clicks.");
            Check(growth < 0.5, $"Pauses ({name}): held fill grew {growth:F2} ms over {cycles} pauses");
            Check(slowest <= 35 * 48, $"Pauses ({name}): {slowest / 48.0:F1} ms of silence after the source came back");
            Check(r.SourcePauses == cycles && r.Underruns == cycles && r.Clicks == 0, $"Pauses ({name}): {r.SourcePauses} pauses, {r.Underruns} underruns, {r.Clicks} clicks");
        }
    }

    private sealed record PauseResult(double[] TargetBeforePauseMs, int[] RestartSilenceFrames, long Underruns, long SourcePauses, int Clicks);

    /// <summary>A source plays 3 s and pauses 0.5 s, repeatedly; while paused the producer sends nothing or empty packets.</summary>
    private static PauseResult SimulatePauses(bool emptyPackets, bool routerReads, int cycles)
    {
        var clock = new VirtualClock();
        var bus = new BoundedFloatBus(Rate, 1, Rate / 5, clock);
        var resampler = new AdaptiveResamplingSampleProvider(bus, Rate, bus);
        var random = new Random(5);
        var packet = new float[480]; var read = new float[960];
        double captureTime = 0, consumerTime = 0.0047, kernel = 0, lastKernel = 0.0047;
        long captured = 0; double previous1 = 0, previous2 = 0; int clicks = 0;
        var targets = new double[cycles]; var restart = new int[cycles];
        const double cycleLength = 3.5, playing = 3.0;
        int cycle = 0; long resumeMark = 0; bool waiting = false;
        while (consumerTime < cycles * cycleLength)
        {
            double captureEvent = captureTime + random.NextDouble() * 0.001;
            if (captureEvent <= consumerTime)
            {
                clock.Seconds = captureEvent;
                if (captureTime % cycleLength < playing)
                {
                    for (int i = 0; i < 480; i++) packet[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 100 * (captured + i) / Rate));
                    captured += 480; bus.Write(packet);
                }
                else if (emptyPackets) bus.Write(ReadOnlySpan<float>.Empty);
                captureTime += 0.010;
                continue;
            }
            clock.Seconds = consumerTime;
            int index = (int)(consumerTime / cycleLength);
            double inCycle = consumerTime - index * cycleLength;
            // The source came back: count the silence until audio flows again.
            if (index != cycle) { cycle = index; resumeMark = resampler.InsertedSilenceFrames; waiting = true; }
            if (inCycle < playing) targets[cycle] = resampler.TargetFillFrames * 1000 / Rate; // last value before the pause
            int frames = 480;
            if (routerReads)
            {
                kernel -= Rate * (consumerTime - lastKernel); lastKernel = consumerTime; if (kernel < 0) kernel = 0;
                frames = Math.Clamp(960 - (int)Math.Round(kernel), 0, 960); kernel += frames;
            }
            long silentBefore = resampler.InsertedSilenceFrames;
            if (frames > 0) resampler.Read(read, 0, frames);
            for (int i = 0; i < frames; i++)
            {
                double second = read[i] - 2 * previous1 + previous2; previous2 = previous1; previous1 = read[i];
                if (consumerTime > 1 && Math.Abs(second) > 0.25 * Math.Pow(10, -50 / 20.0)) clicks++;
            }
            if (waiting && frames > 0 && resampler.InsertedSilenceFrames == silentBefore)
            {
                restart[cycle - 1] = (int)(silentBefore - resumeMark); waiting = false;
            }
            consumerTime += routerReads ? 0.015625 + (random.NextDouble() - 0.5) * 0.001 : 0.010;
        }
        return new(targets, restart, resampler.Underruns, resampler.SourcePauses, clicks);
    }

    // ---------- the read that runs dry ----------

    /// <summary>
    /// A steady DC source stops after a final partial packet of 0..479 frames, so its last input runs out at every position
    /// inside a render read, including exactly on a read boundary (the old code then had nothing left to fade and cut to
    /// silence: 16 of 160 positions gave a full-scale step) and a few frames into one (a fade of only those frames). With
    /// the fade reserve every case fades over the full 2 ms.
    /// </summary>
    internal static void RunningDryAlwaysFades()
    {
        int cases = 0, shortest = int.MaxValue, boundary = 0, short_ = 0; double worst = 0;
        int fadeFrames = 0;
        for (int tail = 0; tail < 480; tail += 2)
        {
            var clock = new VirtualClock();
            var bus = new BoundedFloatBus(Rate, 1, Rate / 5, clock);
            var resampler = new AdaptiveResamplingSampleProvider(bus, Rate, bus);
            fadeFrames = resampler.FadeFrames;
            var packet = new float[480]; Array.Fill(packet, 0.5f);
            var output = new List<float>(); var read = new float[480];
            double captureTime = 0, renderTime = 0.0047;
            const double stopAt = 3.0;
            bool finalWritten = false; int dryQueue = -1;
            while (renderTime < stopAt + 0.3)
            {
                if (captureTime <= renderTime)
                {
                    clock.Seconds = captureTime;
                    if (captureTime < stopAt) bus.Write(packet);
                    else if (!finalWritten) { finalWritten = true; if (tail > 0) bus.Write(packet.AsSpan(0, tail)); }
                    captureTime += 0.010;
                    continue;
                }
                clock.Seconds = renderTime;
                int queued = bus.BufferedFrames; long underruns = resampler.Underruns;
                resampler.Read(read, 0, 480);
                if (dryQueue < 0 && resampler.Underruns != underruns) dryQueue = queued;
                if (renderTime > stopAt - 0.1) output.AddRange(read);
                renderTime += 0.010;
            }
            // What the old code would have had left for the read that ran dry: it produced every read it could cover.
            int oldDone = dryQueue >= 480 ? dryQueue - 480 : dryQueue;
            if (oldDone <= 2) boundary++; else if (oldDone < fadeFrames) short_++;
            var y = output.ToArray();
            double maximum = 0;
            for (int i = 2; i < y.Length; i++) maximum = Math.Max(maximum, Math.Abs(y[i] - 2.0 * y[i - 1] + y[i - 2]));
            int last = Array.FindLastIndex(y, v => Math.Abs(v - 0.5) < 1e-5), zero = Array.FindIndex(y, Math.Max(0, last), v => v == 0);
            Check(last >= 0 && zero > last && y.Skip(zero).All(v => v == 0), $"Stop after {tail} frames: no clean fade to digital silence");
            shortest = Math.Min(shortest, zero - last - 1);
            worst = Math.Max(worst, maximum);
            cases++;
        }
        double smooth = 0.5 * Math.Pow(Math.PI / fadeFrames, 2) / 2; // largest curvature of a full-length raised-cosine fade
        Console.WriteLine($"  {cases} stop positions ({boundary} where the old code had nothing left to fade, {short_} with a shorter fade): " +
            $"every fade {shortest}+ frames, worst curvature {worst:E2} (full {fadeFrames}-frame fade {smooth:E2}, hard edge 0.5).");
        Check(boundary > 0 && short_ > 0, "The sweep no longer reaches the read-boundary cases");
        Check(shortest >= fadeFrames - 1 && worst <= smooth * 1.05, $"Shortest fade {shortest} frames, worst curvature {worst:E2}");
    }
}
