using NAudio.Wave;

namespace Soltex.Audio.Engine;

/// <summary>
/// One producer copies raw PCM into a preallocated bounded ring. A dedicated consumer owns decoding,
/// DSP and WAV I/O. CompleteInput/Dispose must follow producer shutdown (or run on that producer).
/// Overflow fails explicitly; accepted frames drain before finalization. No callback waits for disk.
/// </summary>
public sealed class QueuedPcmRecording : IDisposable
{
    private readonly byte[] ring, block;
    private readonly float[] samples;
    private readonly WaveFormat format;
    private readonly BoundedPcmWaveWriter writer;
    private readonly AutoResetEvent available;
    private readonly Thread worker;
    private readonly long maximumFrames;
    private readonly Exception overflow = new IOException("Recording queue overflowed; recording stopped without silently dropping audio.");
    private readonly Exception partialFrame = new InvalidDataException("Capture supplied an incomplete PCM frame.");
    private long publishedFrames, consumedFrames;
    private int inputCompleted, completed;
    private Exception? failure;
    private bool disposed;

    public PcmDspProcessor Processor { get; }
    public long FramesWritten => writer.FramesWritten;
    public long AcceptedFrames => Volatile.Read(ref publishedFrames);
    public bool Accepting => Volatile.Read(ref inputCompleted) == 0;
    public bool Complete => Volatile.Read(ref completed) != 0;
    public Exception? Failure => Volatile.Read(ref failure);
    public int CapacityFrames => ring.Length / format.BlockAlign;

    public QueuedPcmRecording(Stream destination, WaveFormat format, DspSettings settings, int seconds, int? capacityFrames = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        DspSettings.Range(seconds, 1, 30, nameof(seconds));
        PcmDecoder.Decode([], [], format);
        Processor = new PcmDspProcessor(format.SampleRate, format.Channels, settings);
        DspSettings.Range(format.Channels, 1, 2, nameof(format));
        int capacity = capacityFrames ?? format.SampleRate * 2;
        DspSettings.Range(capacity, 1, format.SampleRate * 30, nameof(capacityFrames));
        this.format = format;
        maximumFrames = (long)format.SampleRate * seconds;
        ring = new byte[checked(capacity * format.BlockAlign)];
        int blockFrames = Math.Min(capacity, Math.Max(1, format.SampleRate / 50));
        block = new byte[blockFrames * format.BlockAlign];
        samples = new float[blockFrames * format.Channels];
        writer = new BoundedPcmWaveWriter(destination, format.SampleRate, format.Channels, seconds);
        available = new AutoResetEvent(false);
        worker = new Thread(Consume) { IsBackground = true, Name = "Soltex microphone WAV writer" };
        try { worker.Start(); }
        catch { try { writer.Dispose(); } finally { available.Dispose(); } throw; }
    }

    /// <summary>Nonwaiting single-producer publish. The final block is trimmed to the exact duration.</summary>
    public bool TryWrite(ReadOnlySpan<byte> bytes)
    {
        if (!Accepting) return false;
        if (bytes.Length % format.BlockAlign != 0) { CompleteInput(partialFrame); return false; }
        long write = publishedFrames;
        int frames = (int)Math.Min(bytes.Length / format.BlockAlign, maximumFrames - write);
        long read = Volatile.Read(ref consumedFrames);
        if (frames > CapacityFrames - (write - read)) { CompleteInput(overflow); return false; }
        int offset = (int)(write % CapacityFrames) * format.BlockAlign;
        int length = frames * format.BlockAlign;
        int first = Math.Min(length, ring.Length - offset);
        bytes[..first].CopyTo(ring.AsSpan(offset));
        bytes.Slice(first, length - first).CopyTo(ring);
        Volatile.Write(ref publishedFrames, write + frames);
        if (write + frames == maximumFrames) Volatile.Write(ref inputCompleted, 1);
        available.Set();
        return true;
    }

    public void CompleteInput(Exception? error = null)
    {
        if (error is not null) Interlocked.CompareExchange(ref failure, error, null);
        Volatile.Write(ref inputCompleted, 1);
        available.Set();
    }

    private void Consume()
    {
        try
        {
            while (true)
            {
                bool inputEnded = !Accepting; // Read completion before publication, so the final block cannot be missed.
                long read = consumedFrames;
                int frames = (int)Math.Min(Volatile.Read(ref publishedFrames) - read, block.Length / format.BlockAlign);
                if (frames == 0)
                {
                    if (inputEnded) break;
                    available.WaitOne();
                    continue;
                }
                int offset = (int)(read % CapacityFrames) * format.BlockAlign;
                int length = frames * format.BlockAlign;
                int first = Math.Min(length, ring.Length - offset);
                ring.AsSpan(offset, first).CopyTo(block);
                ring.AsSpan(0, length - first).CopyTo(block.AsSpan(first));
                Volatile.Write(ref consumedFrames, read + frames);
                int count = PcmDecoder.Decode(block.AsSpan(0, length), samples, format);
                Processor.Process(samples.AsSpan(0, count));
                writer.Write(samples, count);
            }
        }
        catch (Exception error) { CompleteInput(error); }
        finally
        {
            try { writer.Dispose(); }
            catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            Volatile.Write(ref completed, 1);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        CompleteInput();
        worker.Join(); // Only the control owner joins; the producer has already stopped.
        available.Dispose();
        Processor.Dispose(); // the worker that ran it has finished
        disposed = true;
    }
}

/// <summary>Loads a bounded replay before starting audio; the returned buffer owns no file handle.</summary>
public static class ReplayFileBuffer
{
    public const int MaximumBytes = 50 * 1024 * 1024;
    public static MemoryStream Load(Stream source, int maximumBytes = MaximumBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, MaximumBytes);
        long length = source.Length - source.Position;
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Test recording exceeds the replay memory limit.");
        var bytes = new byte[(int)length];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("Test recording changed while loading.");
        return new MemoryStream(bytes, writable: false);
    }
}
