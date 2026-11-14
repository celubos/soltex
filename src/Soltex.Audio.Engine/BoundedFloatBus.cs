using NAudio.Wave;

namespace Soltex.Audio.Engine;

/// <summary>Bounded frame-aligned FIFO. Drops oldest frames on overflow, inserts silence on underrun.</summary>
public sealed class BoundedFloatBus : ISampleProvider
{
    private readonly object sync = new();
    private readonly float[] ring;
    private int readIndex, count;
    private long droppedFrames, silentFrames;
    public WaveFormat WaveFormat { get; }
    public int CapacityFrames => ring.Length / WaveFormat.Channels;
    public int BufferedFrames { get { lock (sync) return count / WaveFormat.Channels; } }
    public long DroppedFrames { get { lock (sync) return droppedFrames; } }
    public long SilentFrames { get { lock (sync) return silentFrames; } }

    public BoundedFloatBus(int sampleRate, int channels, int capacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacityFrames, sampleRate * 10);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        ring = new float[checked(capacityFrames * channels)];
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        int channels = WaveFormat.Channels;
        if (samples.Length % channels != 0) throw new ArgumentException("Only complete frames may be written.", nameof(samples));
        lock (sync)
        {
            if (samples.Length > ring.Length)
            {
                droppedFrames += (samples.Length - ring.Length) / channels;
                samples = samples[^ring.Length..];
            }
            int discard = Math.Max(0, count + samples.Length - ring.Length);
            readIndex = (readIndex + discard) % ring.Length;
            count -= discard;
            droppedFrames += discard / channels;
            int writeIndex = (readIndex + count) % ring.Length;
            int first = Math.Min(samples.Length, ring.Length - writeIndex);
            samples[..first].CopyTo(ring.AsSpan(writeIndex));
            samples[first..].CopyTo(ring);
            count += samples.Length;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var destination = buffer.AsSpan(offset, count);
        if (count % WaveFormat.Channels != 0) throw new ArgumentException("Only complete frames may be read.", nameof(count));
        lock (sync)
        {
            int available = Math.Min(this.count, count);
            int first = Math.Min(available, ring.Length - readIndex);
            ring.AsSpan(readIndex, first).CopyTo(destination);
            ring.AsSpan(0, available - first).CopyTo(destination[first..]);
            destination[available..].Clear();
            readIndex = (readIndex + available) % ring.Length;
            this.count -= available;
            silentFrames += (count - available) / WaveFormat.Channels;
        }
        return count;
    }
}

public sealed class DspSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    public PcmDspProcessor Processor { get; }
    public WaveFormat WaveFormat => source.WaveFormat;
    public DspSampleProvider(ISampleProvider source, PcmDspProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(processor);
        if (source.WaveFormat.SampleRate != processor.SampleRate || source.WaveFormat.Channels != processor.Channels)
            throw new ArgumentException("DSP and source formats must agree.", nameof(processor));
        this.source = source; Processor = processor;
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        Processor.Process(buffer.AsSpan(offset, read));
        return read;
    }
}

/// <summary>Final sample guard after format conversion/resampling, which can otherwise overshoot a DSP ceiling.</summary>
public sealed class OutputCeilingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private float ceiling;
    private float peak;
    /// <summary>Actual peak of the last rendered block after the final guard.</summary>
    public float Peak => Volatile.Read(ref peak);
    public WaveFormat WaveFormat => source.WaveFormat;
    public OutputCeilingSampleProvider(ISampleProvider source, double ceiling = 0.98)
    {
        ArgumentNullException.ThrowIfNull(source);
        DspSettings.Range(ceiling, 0.01, 1, nameof(ceiling));
        this.source = source; this.ceiling = (float)ceiling;
    }
    public void SetCeiling(double value)
    {
        DspSettings.Range(value, 0.01, 1, nameof(value));
        Volatile.Write(ref ceiling, (float)value);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        var samples = buffer.AsSpan(offset, read);
        float limit = Volatile.Read(ref ceiling), measured = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -limit, limit) : 0;
            measured = Math.Max(measured, Math.Abs(samples[i]));
        }
        Volatile.Write(ref peak, measured);
        return read;
    }
}
