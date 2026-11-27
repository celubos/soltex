using NAudio.Wave;

namespace Soltex.Audio.Engine;

public sealed record StreamMixSendSettings(double GainDb = 0, bool Mute = false)
{
    internal double Target()
    { DspSettings.Range(GainDb, -80, 12, nameof(GainDb)); return Mute ? 0 : Math.Pow(10, GainDb / 20); }
}

public sealed record StreamMixQueueStatus(int BufferedFrames, long DroppedFrames, long SilentFrames);

/// <summary>One producer processes each input exactly once, then publishes identical PCM to two independently bounded consumers.</summary>
public sealed class StreamMixPcmSource
{
    private readonly BoundedFloatBus personalBus, streamBus;
    private readonly SendProvider personal, stream;
    private long processedFrames;
    public PcmDspProcessor Processor { get; }
    public ISampleProvider Personal => personal;
    public ISampleProvider Stream => stream;
    public long ProcessedFrames => Interlocked.Read(ref processedFrames);
    public StreamMixQueueStatus PersonalQueue => Snapshot(personalBus);
    public StreamMixQueueStatus StreamQueue => Snapshot(streamBus);
    internal BoundedFloatBus PersonalBus => personalBus;
    internal BoundedFloatBus StreamBus => streamBus;

    public StreamMixPcmSource(int sampleRate, int channels, int capacityFrames, DspSettings dsp,
        StreamMixSendSettings personalSettings, StreamMixSendSettings streamSettings)
    {
        ArgumentNullException.ThrowIfNull(dsp); ArgumentNullException.ThrowIfNull(personalSettings); ArgumentNullException.ThrowIfNull(streamSettings);
        Processor = new(sampleRate, channels, dsp);
        personalBus = new(sampleRate, channels, capacityFrames); streamBus = new(sampleRate, channels, capacityFrames);
        personal = new(personalBus, personalSettings); stream = new(streamBus, streamSettings);
    }

    /// <summary>Single capture producer only. Mutates the supplied span. Render consumers never execute the shared input DSP.</summary>
    public void ProcessAndPublish(Span<float> samples)
    {
        Processor.Process(samples); personalBus.Write(samples); streamBus.Write(samples);
        Interlocked.Add(ref processedFrames, samples.Length / Processor.Channels);
    }

    public void Update(DspSettings dsp, StreamMixSendSettings personalSettings, StreamMixSendSettings streamSettings)
    {
        ArgumentNullException.ThrowIfNull(personalSettings); ArgumentNullException.ThrowIfNull(streamSettings);
        // Validate every control before publishing any part of an update.
        double personalTarget = personalSettings.Target(), streamTarget = streamSettings.Target();
        Processor.UpdateSettings(dsp); personal.SetTarget(personalTarget); stream.SetTarget(streamTarget);
    }

    private static StreamMixQueueStatus Snapshot(BoundedFloatBus bus) => new(bus.BufferedFrames, bus.DroppedFrames, bus.SilentFrames);

    private sealed class SendProvider : ISampleProvider
    {
        private readonly ISampleProvider source;
        private readonly double coefficient;
        private double gain, target;
        public WaveFormat WaveFormat => source.WaveFormat;
        internal SendProvider(ISampleProvider source, StreamMixSendSettings settings)
        { this.source = source; target = gain = settings.Target(); coefficient = Math.Exp(-1 / (.02 * WaveFormat.SampleRate)); }
        internal void SetTarget(double value) => Volatile.Write(ref target, value);
        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count); double destination = Volatile.Read(ref target);
            for (int frame = offset; frame < offset + read; frame += WaveFormat.Channels)
            {
                gain = destination + (gain - destination) * coefficient;
                if (Math.Abs(gain - destination) < 1e-15) gain = destination; // a muted send never decays into subnormals
                for (int c = 0; c < WaveFormat.Channels; c++) buffer[frame + c] *= (float)gain;
            }
            return read;
        }
    }
}

/// <summary>Fixed set of format-matched sources summed with bounded scratch space and a final clipping guard.</summary>
public sealed class StreamMixSumProvider : ISampleProvider
{
    private readonly ISampleProvider[] sources;
    private readonly float[] scratch;
    private readonly float ceiling;
    private long limitedSamples;
    private float peak;
    public WaveFormat WaveFormat { get; }
    public long LimitedSamples => Interlocked.Read(ref limitedSamples);
    public float Peak => Volatile.Read(ref peak);

    public StreamMixSumProvider(IReadOnlyList<ISampleProvider> sources, double ceiling = .98)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > 5) throw new ArgumentException("One to five sources are required.", nameof(sources));
        DspSettings.Range(ceiling, .01, 1, nameof(ceiling));
        this.sources = sources.ToArray();
        if (this.sources.Any(s => s is null)) throw new ArgumentException("Null source.", nameof(sources));
        WaveFormat = this.sources[0].WaveFormat;
        if (this.sources.Any(s => s.WaveFormat.Channels != WaveFormat.Channels || s.WaveFormat.SampleRate != WaveFormat.SampleRate))
            throw new ArgumentException("Source formats must match before summing.", nameof(sources));
        scratch = new float[2048 * WaveFormat.Channels]; this.ceiling = (float)ceiling;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer); _ = buffer.AsSpan(offset, count);
        if (count % WaveFormat.Channels != 0) throw new ArgumentException("Whole frames required.", nameof(count));
        float measured = 0; long clipped = 0;
        for (int done = 0; done < count; done += scratch.Length)
        {
            int block = Math.Min(scratch.Length, count - done); var destination = buffer.AsSpan(offset + done, block); destination.Clear();
            foreach (var source in sources)
            {
                int read = source.Read(scratch, 0, block);
                for (int i = 0; i < read; i++) destination[i] += float.IsFinite(scratch[i]) ? scratch[i] : 0;
            }
            for (int i = 0; i < destination.Length; i++)
            {
                float value = destination[i]; if (Math.Abs(value) > ceiling) clipped++;
                destination[i] = float.IsFinite(value) ? Math.Clamp(value, -ceiling, ceiling) : 0;
                measured = Math.Max(measured, Math.Abs(destination[i]));
            }
        }
        Interlocked.Add(ref limitedSamples, clipped); Volatile.Write(ref peak, measured); return count;
    }
}
