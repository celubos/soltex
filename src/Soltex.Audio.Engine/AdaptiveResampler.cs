using System.Collections.Concurrent;
using NAudio.Wave;

namespace Soltex.Audio.Engine;

/// <summary>Kaiser-windowed sinc kernels shared by every resampler with the same shape.</summary>
internal static class SincKernels
{
    internal const int Phases = 256;
    internal const int BaseTaps = 64;
    internal const double Beta = 9.0;
    private static readonly ConcurrentDictionary<(int Width, long CutoffBits), float[]> Cache = new();

    /// <summary>(Phases + 1) rows of width taps. Row r holds K(r / Phases - width / 2 + k); every row sums to one.</summary>
    internal static float[] Get(int width, double cutoff) => Cache.GetOrAdd((width, BitConverter.DoubleToInt64Bits(cutoff)), key => Build(key.Width, cutoff));

    private static float[] Build(int width, double cutoff)
    {
        var table = new float[(Phases + 1) * width];
        var row = new double[width];
        for (int phase = 0; phase <= Phases; phase++)
        {
            double fraction = (double)phase / Phases, sum = 0;
            for (int k = 0; k < width; k++)
            {
                double t = fraction - width / 2.0 + k;
                double x = 2 * cutoff * t, nearest = Math.Round(x);
                // Exact zeros at integer sinc arguments keep an unshifted unity-ratio kernel a pure delay (bit exact).
                double sinc = Math.Abs(x - nearest) < 1e-9 ? (nearest == 0 ? 1 : 0) : Math.Sin(Math.PI * x) / (Math.PI * x);
                double position = t / (width / 2.0);
                row[k] = Math.Abs(position) >= 1 ? 0 : 2 * cutoff * sinc * Kaiser.Window(position, Beta);
                sum += row[k];
            }
            for (int k = 0; k < width; k++) table[phase * width + k] = (float)(row[k] / sum);
        }
        return table;
    }
}

/// <summary>
/// High-quality sample-rate conversion (64-tap Kaiser-windowed sinc, beta 9, 256 interpolated phases) that can also
/// absorb clock drift between a capture device and a render device. When <c>observed</c> is the bounded bus that feeds
/// <c>source</c>, the provider keeps that bus near a small target fill by trimming the conversion ratio by at most
/// ±0.5 % (a PI loop on a 1 s average of the fill), so independent device clocks no longer cause periodic dropouts,
/// dropped frames or slowly growing latency.
/// <para>
/// The target is the least that survives the actual device periods: one read plus one producer packet (the input a read
/// needs when the next packet has not landed yet), a 2 ms fade reserve, and a jitter allowance. The allowance starts at
/// 8 ms, covers the lateness the producer really shows (held for two minutes, then given back), grows on a near miss or
/// an underrun while the producer is live, and every calm 10 s gives back up to 1 ms, as far as the closest call of the
/// last three minutes left room, down to 1 ms. With 10 ms shared-mode capture and render that settles at about 25 ms of
/// held fill within a few minutes, so the converter adds about 16 ms on average (it held 30 ms and added 21 ms before).
/// </para>
/// <para>
/// A source that goes quiet (no non-empty packet for 50 ms or four packet periods) is a pause, not jitter: no margin is
/// learned from it and output resumes as soon as one read plus one packet is queued again. When the queued input after a
/// read would drop below the fade reserve, the underrun is declared on that read and its tail is faded, so audio never
/// stops on a hard edge; a stall's backlog is skipped with a dip instead of overflow drops. Without <c>observed</c> it is
/// a fixed-ratio converter. Allocation free after construction; one consumer thread calls Read.
/// </para>
/// </summary>
public sealed class AdaptiveResamplingSampleProvider : ISampleProvider
{
    private const int ChunkFrames = 256;
    internal const double MaximumCorrection = 0.005;
    // Loop natural frequency ~0.055 rad/s, damping ~0.9: a 1 ms fill error moves the ratio by 100 ppm, so a late
    // packet changes the pitch by a few ppm at most (inaudible; wow becomes audible near 1000 ppm).
    private const double ProportionalGain = 0.1, IntegralGain = 0.003, FillFilterSeconds = 1.0;
    // Jitter allowance learning: calm time per give-back step, and how long observed producer lateness is remembered.
    private const double CalmSeconds = 10, JitterHoldSeconds = 120, MinimumIdleSeconds = 0.05, MaximumIdleSeconds = 0.25;
    private readonly ISampleProvider source;
    private readonly BoundedFloatBus? observed;
    private readonly int channels, width, inputRate;
    private readonly float[] table;
    private readonly double[] kernel;
    private readonly float[] history;
    private readonly double nominalStep;
    private readonly int fadeFrames, reserveFrames, minimumTargetFrames, maximumTargetFrames, excessFrames;
    private readonly double minimumSafety, maximumSafety, nearMissFrames, nearMissStep, underrunStep, decayStep, guardFrames;
    private double calmSeconds, sinceNearMiss = double.MaxValue, runSeconds, closestCall = double.MaxValue, jitterHoldSeconds;
    // Closest call (least headroom) of each of the last 18 calm 10 s windows (three minutes).
    private readonly double[] closestCalls = CreateClosestCalls();
    private int closestIndex;
    private int filled;
    private double position, step;
    private bool running, primed, sourcePaused;
    private double filteredFill, integrator, readAverage, writeAverage, learnedSafety, jitterFrames, pendingGrowth;
    private long pendingWrites;
    private int fadeInRemaining;
    private long insertedSilence, discarded, underruns, resyncs, pauses;
    private double correction, target, margin;

    public WaveFormat WaveFormat { get; }
    /// <summary>Input frames of delay inside the kernel (half its width).</summary>
    public int KernelDelayFrames => width / 2;
    /// <summary>Output frames of the fade used at underruns, restarts and resyncs (2 ms).</summary>
    public int FadeFrames => fadeFrames;
    /// <summary>Silent output frames inserted while priming, concealing an underrun or waiting for a paused source.</summary>
    public long InsertedSilenceFrames => Interlocked.Read(ref insertedSilence);
    /// <summary>Backlog frames skipped after a consumer stall.</summary>
    public long DiscardedFrames => Interlocked.Read(ref discarded);
    /// <summary>Times the input ran out (faded to silence), including a source that paused.</summary>
    public long Underruns => Interlocked.Read(ref underruns);
    /// <summary>Times the producer went quiet long enough to count as a pause (no margin is learned from these).</summary>
    public long SourcePauses => Interlocked.Read(ref pauses);
    public long Resyncs => Interlocked.Read(ref resyncs);
    /// <summary>Current clock-drift correction in parts per million (positive consumes input faster).</summary>
    public double CorrectionPpm => Volatile.Read(ref correction) * 1e6;
    /// <summary>Bus fill the drift loop is holding, in input frames.</summary>
    public double TargetFillFrames => Volatile.Read(ref target);
    /// <summary>Part of the target beyond one read and one packet (fade reserve plus jitter allowance), in input frames.</summary>
    public double MarginFrames => Volatile.Read(ref margin);
    /// <summary>Averaged bus fill in input frames.</summary>
    public double AverageFillFrames => Volatile.Read(ref filteredFill);

    public AdaptiveResamplingSampleProvider(ISampleProvider source, int outputSampleRate, BoundedFloatBus? observed = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        DspSettings.Range(outputSampleRate, 8000, 384000, nameof(outputSampleRate));
        if (source.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat) throw new ArgumentException("Float samples are required.", nameof(source));
        if (observed is not null && observed.WaveFormat.SampleRate != source.WaveFormat.SampleRate)
            throw new ArgumentException("The observed bus must feed the source at the source rate.", nameof(observed));
        this.source = source; this.observed = observed;
        channels = source.WaveFormat.Channels; inputRate = source.WaveFormat.SampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate, channels);
        nominalStep = (double)inputRate / outputSampleRate;
        step = nominalStep;
        // Unity/upsampling: cutoff at the input Nyquist keeps an undrifted 1:1 path a bit-exact delay.
        // Downsampling: -6 dB at 96 % of the output Nyquist, so the stop band starts before it (no audible aliases).
        double cutoff = nominalStep <= 1 ? 0.5 : 0.48 / nominalStep;
        width = (int)Math.Ceiling(SincKernels.BaseTaps * Math.Max(1, nominalStep) / 2) * 2;
        table = SincKernels.Get(width, cutoff);
        kernel = new double[width];
        double maximumStep = nominalStep * (1 + MaximumCorrection);
        // Room for the kernel, one chunk, and the half kernel a restart skips (see ResetHistory).
        history = new float[(width + width / 2 + (int)Math.Ceiling(ChunkFrames * maximumStep) + 8) * channels];
        fadeFrames = Math.Max(1, outputSampleRate / 500);                              // 2 ms
        // Input that a full-length fade can consume even at the fastest corrected ratio.
        reserveFrames = (int)Math.Ceiling(fadeFrames * maximumStep) + 1;
        int capacity = observed?.CapacityFrames ?? 0;
        minimumTargetFrames = Math.Min(inputRate * 4 / 1000, Math.Max(1, capacity / 2));
        maximumTargetFrames = Math.Max(minimumTargetFrames, capacity * 6 / 10);
        minimumSafety = inputRate / 1000.0;                                            // 1 ms floor
        learnedSafety = inputRate * 8 / 1000.0;                                        // 8 ms until the real jitter is known
        maximumSafety = inputRate * 50 / 1000.0;
        nearMissFrames = inputRate / 500.0;                                            // < 2 ms beyond the reserve is a near miss
        nearMissStep = inputRate / 1000.0;                                             // +1 ms per confirmed near miss
        underrunStep = inputRate * 4 / 1000.0;                                         // +4 ms per confirmed underrun
        decayStep = inputRate / 1000.0;                                                // at most -1 ms per calm 10 s
        guardFrames = inputRate / 1000.0;                                              // 1 ms kept above the near-miss band
        excessFrames = inputRate * 60 / 1000;
        ResetHistory();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var output = buffer.AsSpan(offset, count);
        if (count % channels != 0) throw new ArgumentException("Whole frames required.", nameof(count));
        int frames = count / channels;
        if (observed is null)
        {
            int produced = Produce(output, frames, long.MaxValue);
            return produced * channels;
        }
        double seconds = (double)frames / WaveFormat.SampleRate;
        int fill = observed.BufferedFrames;
        ObserveProducer(seconds);
        if (!running)
        {
            // First start and a paused source wait for one read plus one packet (and the reserve): the least that
            // survives the worst phase between the two clocks. After a genuine underrun wait for the whole target.
            int start = StartThreshold(frames);
            bool underrunRestart = primed && !sourcePaused;
            int threshold = underrunRestart ? (int)Math.Clamp(DesiredTarget(), start, maximumTargetFrames) : start;
            // Only what is really queued counts here: a producer that just came back has no steady rhythm to rely on yet.
            if (fill < threshold) { output.Clear(); Interlocked.Add(ref insertedSilence, frames); return count; }
            running = true; sourcePaused = false; ResetHistory(skipDelay: true); fadeInRemaining = fadeFrames;
            primed = true; filteredFill = fill; // the averaged fill restarts from what is really queued
            if (underrunRestart) Volatile.Write(ref target, threshold); // hold the re-primed level at once
        }
        UpdateDrift(fill, frames);
        int done = 0;
        if (fill > target + Math.Max(target, excessFrames))
        {
            // The consumer stalled and a backlog built up: fade out, skip to the target, fade back in.
            done = Produce(output, Math.Min(frames, fadeFrames), fill);
            FadeOut(output[..(done * channels)], done);
            int skip = (int)Math.Max(0, observed.BufferedFrames - target);
            int dropped = observed.Discard(skip);
            Interlocked.Add(ref discarded, dropped); Interlocked.Increment(ref resyncs);
            filteredFill = observed.BufferedFrames;
            ResetHistory(skipDelay: true); fadeInRemaining = fadeFrames;
        }
        int available = observed.BufferedFrames;
        int remaining = frames - done;
        long headroom = available - Need(remaining);
        if (headroom < reserveFrames && remaining > 1)
        {
            // If speeding up (or a late packet) would eat into the fade reserve, consume more slowly for this read only,
            // within the same ±0.5 % range, instead of running dry.
            double fitting = (filled + available - reserveFrames - 1 - position) / (remaining - 1);
            if (fitting < step && fitting >= nominalStep * (1 - MaximumCorrection)) { step = fitting; headroom = available - Need(remaining); }
        }
        // Declare the underrun on the read that would leave less than the reserve, while there is still input to fade.
        bool runsDry = headroom < reserveFrames;
        Learn(headroom, seconds, runsDry);
        int produced2 = Produce(output[(done * channels)..], remaining, available);
        ApplyFadeIn(output.Slice(done * channels, produced2 * channels), produced2);
        done += produced2;
        if (runsDry || done < frames)
        {
            // Underrun: fade this read's tail (the reserve leaves a full fade's worth of input), insert silence, re-prime.
            // Whether the margin grows is decided once it is clear the producer is still live (see ObserveProducer).
            FadeOut(output[..(done * channels)], done);
            output[(done * channels)..].Clear();
            Interlocked.Add(ref insertedSilence, frames - done); Interlocked.Increment(ref underruns);
            Schedule(underrunStep);
            calmSeconds = 0;
            running = false;
        }
        return count;
    }

    private static double[] CreateClosestCalls() { var calls = new double[18]; Array.Fill(calls, double.MaxValue); return calls; }

    /// <summary>Input frames beyond the history that producing <paramref name="frames"/> output frames consumes.</summary>
    private long Need(int frames) => frames <= 0 ? 0 : Math.Max(0, (long)Math.Floor(position + (frames - 1) * step) + 1 - filled);

    /// <summary>Gap without a non-empty packet after which the producer counts as paused rather than late.</summary>
    private double IdleSeconds() => Math.Clamp(4 * writeAverage / inputRate, MinimumIdleSeconds, MaximumIdleSeconds);

    /// <summary>One read plus one packet, the fade reserve and the minimum allowance.</summary>
    private int StartThreshold(int frames)
    {
        double reads = readAverage > 0 ? readAverage : frames * nominalStep;
        double writes = writeAverage > 0 ? writeAverage : observed!.LastWriteFrames;
        return (int)Math.Clamp(Math.Max(minimumTargetFrames, reads + writes) + reserveFrames + minimumSafety, minimumTargetFrames, maximumTargetFrames);
    }

    private double Safety() => Math.Min(maximumSafety, Math.Max(learnedSafety, jitterFrames + minimumSafety / 2));

    private double DesiredTarget() =>
        Math.Clamp(Math.Max(minimumTargetFrames, readAverage + writeAverage) + reserveFrames + Safety(), minimumTargetFrames, maximumTargetFrames);

    /// <summary>
    /// Per read: follows the producer's lateness and settles a pending margin increase. The largest lateness is held
    /// while similar lateness keeps appearing, then given back at 0.5 ms per 10 s once none was seen for two minutes (a
    /// held peak keeps the set point still; a decaying one that the next late packet refreshes would wobble the ratio).
    /// A pending increase is applied once the producer delivers again within the idle gap (it was late: jitter a margin
    /// can absorb) and dropped if it stays quiet (it paused or stalled: a margin would not have helped).
    /// </summary>
    private void ObserveProducer(double seconds)
    {
        var bus = observed!;
        double idle = IdleSeconds(), lateness = bus.TakeLatenessPeak();
        bool resumed = lateness >= idle;
        jitterHoldSeconds += seconds;
        if (!resumed && lateness > 0)
        {
            double sample = lateness * inputRate;
            if (sample >= jitterFrames * 0.75) jitterHoldSeconds = 0;
            jitterFrames = Math.Max(jitterFrames, sample);
        }
        if (jitterHoldSeconds > JitterHoldSeconds) jitterFrames = Math.Max(0, jitterFrames - decayStep / 2 * seconds / CalmSeconds);
        bool quiet = resumed || bus.SecondsSinceLastWrite >= idle;
        if (pendingGrowth > 0)
        {
            if (quiet) pendingGrowth = 0;
            else if (bus.WriteCount != pendingWrites)
            {
                learnedSafety = Math.Min(maximumSafety, learnedSafety + pendingGrowth);
                pendingGrowth = 0; calmSeconds = 0;
            }
        }
        if (!running && quiet && primed && !sourcePaused) { sourcePaused = true; Interlocked.Increment(ref pauses); }
    }

    private void Schedule(double growth)
    {
        pendingGrowth = Math.Max(pendingGrowth, growth);
        pendingWrites = observed!.WriteCount;
    }

    /// <summary>
    /// A near miss (less than 2 ms of queued input beyond this read and the fade reserve) asks for 1 ms more allowance,
    /// once the set point has absorbed the previous step (otherwise the next few late packets, still seeing the old fill,
    /// would stack steps). Every 10 calm seconds give back at most half the room that the closest call of the last three
    /// minutes left above the near-miss band plus 1 ms (and at most 1 ms), down to 1 ms: the allowance settles just above
    /// what the devices need instead of hunting up and down, which would wobble the ratio.
    /// </summary>
    private void Learn(long headroom, double seconds, bool runsDry)
    {
        sinceNearMiss += seconds; calmSeconds += seconds;
        if (runsDry) { calmSeconds = 0; closestCall = double.MaxValue; return; }
        closestCall = Math.Min(closestCall, headroom);
        bool settled = Math.Abs(target - DesiredTarget()) < inputRate / 2000.0;
        // Only a near miss while the loop holds its target shows the margin is short; right after a restart or a drain the
        // fill is still being built back up and a close call says nothing about the jitter.
        bool holding = filteredFill >= target - minimumSafety;
        if (headroom < reserveFrames + nearMissFrames)
        {
            calmSeconds = 0; closestCall = double.MaxValue;
            if (sinceNearMiss >= 1 && settled && holding) { sinceNearMiss = 0; Schedule(nearMissStep); }
        }
        else if (calmSeconds >= CalmSeconds)
        {
            closestCalls[closestIndex] = closestCall; closestIndex = (closestIndex + 1) % closestCalls.Length;
            double worst = double.MaxValue;
            foreach (double call in closestCalls) worst = Math.Min(worst, call);
            double giveBack = Math.Min(decayStep, (worst - (reserveFrames + nearMissFrames + guardFrames)) / 2);
            if (giveBack >= inputRate / 10000.0 && settled) learnedSafety = Math.Max(minimumSafety, learnedSafety - giveBack);
            calmSeconds = 0; closestCall = double.MaxValue;
        }
    }

    private void UpdateDrift(int fill, int frames)
    {
        double seconds = (double)frames / WaveFormat.SampleRate;
        double consumed = frames * step;
        readAverage = readAverage == 0 ? consumed : readAverage + (consumed - readAverage) * 0.05;
        double written = observed!.LastWriteFrames;
        writeAverage = writeAverage == 0 ? written : writeAverage + (written - writeAverage) * 0.05;
        Volatile.Write(ref margin, reserveFrames + Safety());
        double desired = DesiredTarget();
        // Slew the set point (20 s time constant) so a margin change is absorbed gently: a 1 ms step moves the ratio by
        // about 25 ppm at most instead of kicking it.
        double smoothedTarget = target == 0 ? desired : target + (desired - target) * Math.Min(1, seconds / 20);
        Volatile.Write(ref target, smoothedTarget);
        desired = smoothedTarget;
        // Packets arrive whole: right after a write the bus holds a full packet "early". Adding the input that a steady
        // producer would have delivered since the last write removes the sawtooth that appears as the capture and render
        // clocks slide past each other, which would otherwise read as fake drift.
        double continuous = fill + Math.Min(written, inputRate * observed.SecondsSinceLastWrite);
        double fillAverage = filteredFill + (continuous - filteredFill) * Math.Min(1, seconds / FillFilterSeconds);
        Volatile.Write(ref filteredFill, fillAverage);
        double error = (fillAverage - desired) / inputRate;
        // Acquisition: 3x faster (same damping) for the first 30 s, blending to the quiet tracking gains by 90 s.
        runSeconds += seconds;
        double speed = runSeconds < 30 ? 3 : runSeconds < 90 ? 3 - 2 * (runSeconds - 30) / 60 : 1;
        double proportional = ProportionalGain * speed * error;
        double candidate = Math.Clamp(integrator + IntegralGain * speed * speed * error * seconds, -MaximumCorrection, MaximumCorrection);
        // Anti-windup: stop integrating further into saturation.
        if (Math.Abs(proportional + candidate) <= MaximumCorrection || Math.Abs(candidate) < Math.Abs(integrator)) integrator = candidate;
        double applied = Math.Clamp(proportional + integrator, -MaximumCorrection, MaximumCorrection);
        Volatile.Write(ref correction, applied);
        step = nominalStep * (1 + applied);
    }

    /// <summary>Produces up to <paramref name="frames"/> output frames while pulling at most <paramref name="budget"/> input frames.</summary>
    private int Produce(Span<float> output, int frames, long budget)
    {
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(ChunkFrames, frames - done);
            long need = Math.Max(0, (long)Math.Floor(position + (n - 1) * step) + 1 - filled);
            int pull = (int)Math.Min(need, budget);
            if (pull > 0)
            {
                int got = Pull(pull);
                budget -= got;
                if (got < pull) budget = 0;
            }
            int possible = n;
            if ((long)Math.Floor(position + (n - 1) * step) >= filled)
            {
                possible = 0;
                while (possible < n && (long)Math.Floor(position + possible * step) < filled) possible++;
            }
            Generate(output.Slice(done * channels, possible * channels), possible);
            Compact();
            done += possible;
            if (possible < n) break;
        }
        return done;
    }

    private int Pull(int frames)
    {
        int got = 0;
        while (got < frames)
        {
            int read = source.Read(history, (filled + got) * channels, (frames - got) * channels) / channels;
            if (read <= 0) break;
            got += read;
        }
        filled += got;
        return got;
    }

    private void Generate(Span<float> output, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            int index = (int)position;
            double phase = (position - index) * SincKernels.Phases;
            int row = (int)phase;
            double alpha = phase - row;
            int row0 = row * width, row1 = row0 + width;
            if (alpha == 0) for (int k = 0; k < width; k++) kernel[k] = table[row0 + k];
            else for (int k = 0; k < width; k++) kernel[k] = table[row0 + k] + alpha * (table[row1 + k] - table[row0 + k]);
            for (int channel = 0; channel < channels; channel++)
            {
                double sum = 0;
                int sample = index * channels + channel;
                for (int k = 0; k < width; k++, sample -= channels) sum += kernel[k] * history[sample];
                output[frame * channels + channel] = (float)sum;
            }
            position += step;
        }
    }

    private void Compact()
    {
        int shift = (int)Math.Min(filled, (long)Math.Floor(position) - (width - 1));
        if (shift <= 0) return;
        Array.Copy(history, shift * channels, history, 0, (filled - shift) * channels);
        filled -= shift; position -= shift;
    }

    /// <summary>
    /// Empties the kernel history. A restart after silence also skips the half kernel that would only interpolate the
    /// cleared history, so the first output frame is the first new input frame and the fade-in starts on real signal
    /// (otherwise the band-limited step into the new audio lands mid-fade and clicks).
    /// </summary>
    private void ResetHistory(bool skipDelay = false)
    {
        Array.Clear(history, 0, (width - 1) * channels);
        filled = width - 1; position = width - 1 + (skipDelay ? width / 2 : 0);
    }

    private void ApplyFadeIn(Span<float> output, int frames)
    {
        for (int frame = 0; frame < frames && fadeInRemaining > 0; frame++, fadeInRemaining--)
        {
            float gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (fadeFrames - fadeInRemaining) / fadeFrames));
            for (int channel = 0; channel < channels; channel++) output[frame * channels + channel] *= gain;
        }
    }

    private void FadeOut(Span<float> output, int frames)
    {
        int length = Math.Min(frames, fadeFrames);
        for (int i = 0; i < length; i++)
        {
            int frame = frames - length + i;
            float gain = (float)(0.5 + 0.5 * Math.Cos(Math.PI * (i + 1) / length));
            for (int channel = 0; channel < channels; channel++) output[frame * channels + channel] *= gain;
        }
    }
}

/// <summary>
/// Last stage before a device: registers the calling render thread with MMCSS "Pro Audio" once, fades in over 5 ms at
/// start, and on request fades out and then renders silence so stopping a stream never cuts a waveform mid-cycle.
/// </summary>
public sealed class RenderHeadSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly bool realtimeThread;
    private readonly int fadeFrames, channels;
    private int fadeInPosition, fadeOutPosition, fadeOutRequested, silenced;
    public WaveFormat WaveFormat => source.WaveFormat;
    /// <summary>True once a requested fade-out has fully reached the output.</summary>
    public bool Silenced => Volatile.Read(ref silenced) != 0;

    public RenderHeadSampleProvider(ISampleProvider source, bool realtimeThread = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source; this.realtimeThread = realtimeThread;
        channels = source.WaveFormat.Channels;
        fadeFrames = Math.Max(1, source.WaveFormat.SampleRate / 200);
    }

    /// <summary>Control thread: starts a 5 ms fade to silence.</summary>
    public void BeginFadeOut() => Volatile.Write(ref fadeOutRequested, 1);

    /// <summary>
    /// Control thread: fades out and waits (bounded) until the fade was rendered and has had time to leave the device
    /// buffer. Several heads faded together with <see cref="BeginFadeOut"/> first only wait once in total.
    /// </summary>
    public void FadeOutAndWait(int deviceBufferMs, int timeoutMs = 60)
    {
        BeginFadeOut();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!Silenced && clock.ElapsedMilliseconds < timeoutMs) Thread.Sleep(2);
        if (!Silenced) return;
        long since = System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref silencedAt)).Ticks / TimeSpan.TicksPerMillisecond;
        int remaining = (int)Math.Clamp(deviceBufferMs + 5 - since, 0, 600);
        if (remaining > 0) Thread.Sleep(remaining);
    }
    private long silencedAt;

    public int Read(float[] buffer, int offset, int count)
    {
        if (realtimeThread) AudioThreadScheduling.RegisterCurrentThread();
        var output = buffer.AsSpan(offset, count);
        if (Volatile.Read(ref silenced) != 0) { output.Clear(); return count; }
        int read = source.Read(buffer, offset, count);
        var samples = output[..read];
        int frames = read / channels;
        for (int frame = 0; frame < frames && fadeInPosition < fadeFrames; frame++, fadeInPosition++)
        {
            float gain = (float)(0.5 - 0.5 * Math.Cos(Math.PI * (fadeInPosition + 1) / fadeFrames));
            for (int channel = 0; channel < channels; channel++) samples[frame * channels + channel] *= gain;
        }
        if (Volatile.Read(ref fadeOutRequested) != 0)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                float gain = fadeOutPosition >= fadeFrames ? 0 : (float)(0.5 + 0.5 * Math.Cos(Math.PI * (fadeOutPosition + 1) / fadeFrames));
                if (fadeOutPosition < fadeFrames) fadeOutPosition++;
                for (int channel = 0; channel < channels; channel++) samples[frame * channels + channel] *= gain;
            }
            if (fadeOutPosition >= fadeFrames && Volatile.Read(ref silenced) == 0)
            {
                Volatile.Write(ref silencedAt, System.Diagnostics.Stopwatch.GetTimestamp());
                Volatile.Write(ref silenced, 1);
            }
        }
        return read;
    }
}
