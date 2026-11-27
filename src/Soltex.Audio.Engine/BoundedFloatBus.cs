using NAudio.Wave;

namespace Soltex.Audio.Engine;

/// <summary>Bounded frame-aligned FIFO. Drops oldest frames on overflow, inserts silence on underrun.</summary>
public sealed class BoundedFloatBus : ISampleProvider
{
    private readonly object sync = new();
    private readonly float[] ring;
    private readonly TimeProvider clock;
    private int readIndex, count, lastWriteFrames, burstFrames;
    private long droppedFrames, silentFrames, lastWriteTimestamp, writeCount, burstStart, previousWrite;
    private double latenessPeak;
    public WaveFormat WaveFormat { get; }
    /// <summary>Seconds since the most recent non-empty write (0 before the first), on the bus clock.</summary>
    public double SecondsSinceLastWrite
    {
        get
        {
            long last = Volatile.Read(ref lastWriteTimestamp);
            return last == 0 ? 0 : Math.Max(0, (double)(clock.GetTimestamp() - last) / clock.TimestampFrequency);
        }
    }
    public int CapacityFrames => ring.Length / WaveFormat.Channels;
    public int BufferedFrames { get { lock (sync) return count / WaveFormat.Channels; } }
    public long DroppedFrames { get { lock (sync) return droppedFrames; } }
    public long SilentFrames { get { lock (sync) return silentFrames; } }
    /// <summary>
    /// Frames in the most recent packet (non-empty writes less than 1 ms apart count as one), i.e. the producer's packet
    /// size, for consumers that size their margin.
    /// </summary>
    public int LastWriteFrames => Volatile.Read(ref lastWriteFrames);
    /// <summary>Non-empty writes so far; a consumer can tell whether the producer delivered since it last looked.</summary>
    public long WriteCount { get { lock (sync) return writeCount; } }

    /// <summary>
    /// Largest producer lateness, in seconds, since the previous call, then resets it. Writes less than 1 ms apart count as
    /// one packet; a packet is late by the time it arrived after the previous packet's audio would have run out (arrival
    /// gap minus the previous packet's duration). A long value means the producer paused and came back.
    /// </summary>
    public double TakeLatenessPeak()
    {
        lock (sync) { double peak = latenessPeak; latenessPeak = 0; return peak; }
    }

    /// <summary>Removes up to <paramref name="frames"/> of the oldest frames (a consumer skipping backlog). Returns frames removed.</summary>
    public int Discard(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        lock (sync)
        {
            int removed = Math.Min(frames, count / WaveFormat.Channels);
            readIndex = (readIndex + removed * WaveFormat.Channels) % ring.Length;
            count -= removed * WaveFormat.Channels;
            return removed;
        }
    }

    public BoundedFloatBus(int sampleRate, int channels, int capacityFrames) : this(sampleRate, channels, capacityFrames, null) { }

    /// <summary>The clock times writes for drift-compensating consumers; tests may supply a virtual one.</summary>
    public BoundedFloatBus(int sampleRate, int channels, int capacityFrames, TimeProvider? clock)
    {
        this.clock = clock ?? TimeProvider.System;
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
        // An empty packet (an idle producer, e.g. the router polling a silent endpoint) delivers nothing: it must not reset
        // the packet size or the write clock, or a consumer would mistake a paused source for a live one that runs dry.
        if (samples.IsEmpty) return;
        int frames = samples.Length / channels;
        long now = Math.Max(1, clock.GetTimestamp());
        lock (sync)
        {
            writeCount++;
            // A producer that splits one delivery into several writes (a long capture callback cut into blocks) sends them
            // within microseconds: they are one packet for sizing and lateness.
            if (previousWrite != 0 && now >= previousWrite && now - previousWrite < clock.TimestampFrequency / 1000) burstFrames += frames;
            else
            {
                if (burstStart != 0)
                    latenessPeak = Math.Max(latenessPeak, (double)(now - burstStart) / clock.TimestampFrequency - (double)burstFrames / WaveFormat.SampleRate);
                burstStart = now; burstFrames = frames;
            }
            previousWrite = now;
            Volatile.Write(ref lastWriteFrames, burstFrames);
            Volatile.Write(ref lastWriteTimestamp, now);
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
