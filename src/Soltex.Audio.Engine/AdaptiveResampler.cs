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
/// ±0.5 % (a PI loop on a 0.5 s average of the fill), so independent device clocks no longer cause periodic dropouts,
/// dropped frames or slowly growing latency. It waits for a short preroll before consuming, conceals a genuine underrun
/// with a 3 ms fade instead of a hard edge (and learns a larger margin), and skips excess backlog after a stall with a
/// dip instead of overflow drops. Without <c>observed</c> it is a fixed-ratio converter. Allocation free after construction;
/// one consumer thread calls Read.
/// </summary>
public sealed class AdaptiveResamplingSampleProvider : ISampleProvider
{
    private const int ChunkFrames = 256;
    internal const double MaximumCorrection = 0.005;
    // Loop natural frequency ~0.055 rad/s, damping ~0.9: a 1 ms fill error moves the ratio by 100 ppm, so a late
    // packet changes the pitch by a few ppm at most (inaudible; wow becomes audible near 1000 ppm).
    private const double ProportionalGain = 0.1, IntegralGain = 0.003, FillFilterSeconds = 1.0;
    private readonly ISampleProvider source;
    private readonly BoundedFloatBus? observed;
    private readonly int channels, width, inputRate;
    private readonly float[] table;
    private readonly double[] kernel;
    private readonly float[] history;
    private readonly double nominalStep;
    private readonly int fadeFrames, startFrames, minimumTargetFrames, maximumTargetFrames, marginStepFrames, maximumMarginFrames, excessFrames,
        initialMarginFrames, nearMissFrames;
    private double calmSeconds, sinceNearMiss = double.MaxValue, runSeconds;
    private int filled;
    private double position, step;
    private bool running, primed;
    private double filteredFill, integrator, readAverage, writeAverage, marginFrames;
    private int fadeInRemaining;
    private long insertedSilence, discarded, underruns, resyncs;
    private double correction, target;

    public WaveFormat WaveFormat { get; }
    /// <summary>Input frames of delay inside the kernel (half its width).</summary>
    public int KernelDelayFrames => width / 2;
    /// <summary>Silent output frames inserted while priming or concealing an underrun.</summary>
    public long InsertedSilenceFrames => Interlocked.Read(ref insertedSilence);
    /// <summary>Backlog frames skipped after a consumer stall.</summary>
    public long DiscardedFrames => Interlocked.Read(ref discarded);
    public long Underruns => Interlocked.Read(ref underruns);
    public long Resyncs => Interlocked.Read(ref resyncs);
    /// <summary>Current clock-drift correction in parts per million (positive consumes input faster).</summary>
    public double CorrectionPpm => Volatile.Read(ref correction) * 1e6;
    /// <summary>Bus fill the drift loop is holding, in input frames.</summary>
    public double TargetFillFrames => Volatile.Read(ref target);
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
        history = new float[(width + (int)Math.Ceiling(ChunkFrames * maximumStep) + 8) * channels];
        fadeFrames = Math.Max(1, outputSampleRate * 3 / 1000);
        int capacity = observed?.CapacityFrames ?? 0;
        startFrames = Math.Min(inputRate / 50, Math.Max(1, capacity / 2));            // 20 ms preroll
        minimumTargetFrames = Math.Min(inputRate * 15 / 1000, Math.Max(1, capacity / 2));
        maximumTargetFrames = Math.Max(minimumTargetFrames, capacity * 6 / 10);
        marginStepFrames = inputRate / 200;                                            // 5 ms per underrun
        initialMarginFrames = inputRate / 100;                                         // 10 ms jitter allowance
        nearMissFrames = inputRate / 500;                                              // < 2 ms headroom counts as a near miss
        maximumMarginFrames = inputRate * 60 / 1000;
        excessFrames = inputRate * 60 / 1000;
        marginFrames = initialMarginFrames;
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
        int fill = observed.BufferedFrames;
        if (!running)
        {
            // First start waits 20 ms; after an underrun wait for the whole (now larger) target so the margin is real.
            int threshold = primed ? (int)Math.Clamp(Volatile.Read(ref target), startFrames, maximumTargetFrames) : startFrames;
            if (fill < threshold) { output.Clear(); Interlocked.Add(ref insertedSilence, frames); return count; }
            running = true; ResetHistory(); fadeInRemaining = fadeFrames;
            if (!primed) { primed = true; filteredFill = fill; }
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
            ResetHistory(); fadeInRemaining = fadeFrames;
        }
        int available = observed.BufferedFrames;
        int remaining = frames - done;
        LearnMargin(available - ((long)Math.Floor(position + (remaining - 1) * step) + 1 - filled), (double)frames / WaveFormat.SampleRate);
        if (remaining > 1)
        {
            // If speeding up (or a late packet) would need more input than is queued, consume more slowly for this
            // read only, within the same ±0.5 % range, instead of running dry.
            double fitting = (filled + available - 1 - position) / (remaining - 1);
            if (fitting < step && fitting >= nominalStep * (1 - MaximumCorrection)) step = fitting;
        }
        int produced2 = Produce(output[(done * channels)..], remaining, available);
        ApplyFadeIn(output.Slice(done * channels, produced2 * channels), produced2);
        done += produced2;
        if (done < frames)
        {
            // Genuine underrun: fade the last real samples, insert silence, re-prime, and keep a larger safety margin.
            FadeOut(output[..(done * channels)], done);
            output[(done * channels)..].Clear();
            Interlocked.Add(ref insertedSilence, frames - done); Interlocked.Increment(ref underruns);
            marginFrames = Math.Min(maximumMarginFrames, marginFrames + marginStepFrames);
            calmSeconds = 0;
            running = false;
        }
        return count;
    }

    /// <summary>
    /// Grows the jitter margin on a near miss (less than 2 ms of queued input beyond this read), at most once a second,
    /// and gives back 1 ms after every two calm minutes, never below the initial 10 ms.
    /// </summary>
    private void LearnMargin(long headroom, double seconds)
    {
        sinceNearMiss += seconds; calmSeconds += seconds;
        if (headroom < nearMissFrames)
        {
            calmSeconds = 0;
            if (sinceNearMiss >= 1) { sinceNearMiss = 0; marginFrames = Math.Min(maximumMarginFrames, marginFrames + marginStepFrames / 2.0); }
        }
        else if (calmSeconds >= 120)
        {
            calmSeconds = 0;
            marginFrames = Math.Max(initialMarginFrames, marginFrames - inputRate / 1000.0);
        }
    }

    private void UpdateDrift(int fill, int frames)
    {
        double seconds = (double)frames / WaveFormat.SampleRate;
        double consumed = frames * step;
        readAverage = readAverage == 0 ? consumed : readAverage + (consumed - readAverage) * 0.05;
        double written = observed!.LastWriteFrames;
        writeAverage = writeAverage == 0 ? written : writeAverage + (written - writeAverage) * 0.05;
        double desired = Math.Clamp(Math.Max(minimumTargetFrames, readAverage + writeAverage) + marginFrames, minimumTargetFrames, maximumTargetFrames);
        // Slew the set point (10 s) so a margin change is absorbed gently instead of kicking the ratio.
        double smoothedTarget = target == 0 ? desired : target + (desired - target) * Math.Min(1, seconds / 10);
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

    private void ResetHistory()
    {
        Array.Clear(history, 0, (width - 1) * channels);
        filled = width - 1; position = width - 1;
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
