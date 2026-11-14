using System.Runtime.InteropServices;
using NAudio.Wave;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

internal static class RecordingChecks
{
    private const int Rate = 48000;
    private static readonly DspSettings Unity = new() { HeadroomDb = 0 };
    private static readonly WaveFormat Mono = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    public static void DurationAndSamples()
    {
        using var stream = new ProbeStream();
        using var queue = new QueuedPcmRecording(stream, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2), Unity, 1, Rate);
        var input = new float[Rate * 2 + 12];
        for (int i = 0; i < input.Length; i += 2) { input[i] = 0.25f; input[i + 1] = -0.5f; }
        stream.Armed = true;
        int producer = Environment.CurrentManagedThreadId;
        Check(queue.TryWrite(MemoryMarshal.AsBytes(input.AsSpan())), "Final packet must be accepted up to the duration limit.");
        Check(!queue.Accepting && !queue.TryWrite(new byte[8]), "No samples may append beyond the exact frame limit.");
        queue.Dispose();
        Check(queue.Complete && queue.Failure is null && queue.FramesWritten == Rate, "Exactly one second must drain and finish.");
        Check(stream.Closed && stream.WriteThread != producer, "WAV payload/finalization must be owned by the writer thread.");
        using var reader = Reader(stream);
        Check(reader.Length == Rate * 2 * sizeof(float), "Finalized WAV length must match the bounded accepted frames.");
        for (int frame = 0; frame < Rate; frame++)
            Check(reader.ReadNextSampleFrame().SequenceEqual(new float[] { 0.25f, -0.5f }), "Stereo order or sample values changed while queued.");
    }

    public static void SlowDiskOverflow()
    {
        using var stream = new ProbeStream { BlockWrites = true };
        using var queue = new QueuedPcmRecording(stream, Mono, Unity, 1, 64);
        byte[] block = Bytes(64, 0.25f);
        stream.Armed = true;
        try
        {
            Check(queue.TryWrite(block), "First block rejected.");
            Check(stream.Entered.Wait(TimeSpan.FromSeconds(5)), "Writer did not enter the controlled slow stream.");
            var producer = Task.Run(() =>
            {
                Check(queue.TryWrite(block), "Free queue space must accept the next block while disk is stalled.");
                Check(!queue.TryWrite(block), "Full queue must reject overflow.");
            });
            Check(producer.Wait(TimeSpan.FromSeconds(2)), "Capture publishing waited for disk I/O.");
            Check(queue.Failure is IOException && !queue.Accepting, "Overflow must fail and stop acceptance explicitly.");
        }
        finally { stream.Release.Set(); }
        queue.Dispose();
        Check(queue.Complete && queue.FramesWritten == 128 && queue.AcceptedFrames == 128, "Overflow must drain the accepted prefix without dropping frames.");
        using var reader = Reader(stream);
        Check(reader.Length == 128 * sizeof(float), "Failed recording must retain a valid, finalized accepted prefix.");
    }

    public static void StopDrainsAndWraps()
    {
        using var stream = new ProbeStream();
        using var queue = new QueuedPcmRecording(stream, Mono, Unity, 1, 127);
        for (int packet = 0; packet < 40; packet++)
        {
            Check(queue.TryWrite(Bytes(101, (packet + 1) / 100f)), "Sequential fixture packet rejected.");
            long expected = (packet + 1) * 101;
            Check(SpinWait.SpinUntil(() => queue.FramesWritten == expected, TimeSpan.FromSeconds(5)), "Writer did not drain a packet.");
        }
        queue.CompleteInput(); // Explicit early stop/cancellation: preserve every accepted complete frame.
        queue.Dispose(); queue.Dispose();
        Check(queue.Complete && queue.Failure is null && stream.DisposeCount == 1, "Stop must finalize once and join cleanly.");
        using var reader = Reader(stream);
        Check(reader.Length == 4040 * sizeof(float), "Early stop must not pad with silence or lose the tail.");
        for (int packet = 0; packet < 40; packet++)
            for (int frame = 0; frame < 101; frame++)
                Check(Math.Abs(reader.ReadNextSampleFrame()[0] - (packet + 1) / 100f) < 1e-6, "Ring wrap reordered recorded samples.");
    }

    public static void WorkerFailureAndEmptyStop()
    {
        using (var stream = new ProbeStream { ThrowWrites = true })
        using (var queue = new QueuedPcmRecording(stream, Mono, Unity, 1, 64))
        {
            stream.Armed = true;
            Check(queue.TryWrite(Bytes(64, 0.1f)), "Packet rejected before injected failure.");
            Check(SpinWait.SpinUntil(() => queue.Complete, TimeSpan.FromSeconds(5)), "Failed writer did not terminate.");
            queue.Dispose();
            Check(queue.Failure is IOException && !queue.Accepting && stream.Closed, "Worker I/O failure must be surfaced and its stream closed.");
        }
        using var empty = new ProbeStream();
        using var neverStarted = new QueuedPcmRecording(empty, Mono, Unity, 1);
        neverStarted.Dispose();
        using var reader = Reader(empty);
        Check(neverStarted.Complete && neverStarted.FramesWritten == 0 && reader.Length == 0, "Cancellation before the first callback must finalize an empty WAV.");
    }

    public static void ConcurrentFinalTail()
    {
        for (int run = 0; run < 3; run++)
        {
            using var stream = new ProbeStream();
            using var queue = new QueuedPcmRecording(stream, Mono, Unity, 1, Rate);
            var packet = new float[37];
            int sent = 0;
            while (sent < Rate)
            {
                for (int i = 0; i < packet.Length; i++) packet[i] = ((sent + i) % 300) / 1000f;
                Check(queue.TryWrite(MemoryMarshal.AsBytes(packet.AsSpan())), "Concurrent recording unexpectedly rejected a packet.");
                sent += packet.Length;
                if (sent % 7 == 0) Thread.Yield();
            }
            queue.Dispose();
            Check(queue.Complete && queue.Failure is null && queue.FramesWritten == Rate, "Consumer must observe the final publication before completion.");
            using var reader = Reader(stream);
            for (int frame = 0; frame < Rate; frame++)
                Check(Math.Abs(reader.ReadNextSampleFrame()[0] - (frame % 300) / 1000f) < 1e-6, "Concurrent producer reuse corrupted sample order or the final tail.");
        }
    }

    public static void CallbackAllocations()
    {
        var processor = new PcmDspProcessor(Rate, 1, Unity);
        var samples = new float[128];
        long allocated = 0;
        for (int i = 0; i < 120; i++)
        {
            processor.UpdateSettings(Unity with { GainDb = i % 2 == 0 ? -6 : 0 });
            long before = GC.GetAllocatedBytesForCurrentThread();
            processor.Process(samples);
            if (i >= 20) allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Check(allocated == 0, $"Gain-only processing allocated {allocated} bytes on the audio thread.");
        using var stream = new ProbeStream();
        using var queue = new QueuedPcmRecording(stream, Mono, Unity, 1, Rate);
        byte[] packet = Bytes(128, 0.1f);
        queue.TryWrite(packet); // Warm the publish path before measuring.
        long beforePublish = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) queue.TryWrite(packet);
        long publishedAllocation = GC.GetAllocatedBytesForCurrentThread() - beforePublish;
        queue.Dispose();
        Check(publishedAllocation == 0 && queue.Failure is null, "Steady-state capture publication must be allocation-free.");
    }

    public static void ReplayPreload()
    {
        using var source = new MemoryStream(new byte[] { 4, 3, 2, 1 });
        using var replay = ReplayFileBuffer.Load(source, 4);
        source.Dispose();
        Check(replay.ToArray().SequenceEqual(new byte[] { 4, 3, 2, 1 }), "Preloaded replay must remain available after the file closes.");
        using var tooLarge = new MemoryStream(new byte[5]);
        try { using var rejected = ReplayFileBuffer.Load(tooLarge, 4); throw new InvalidOperationException("Oversized replay accepted."); }
        catch (InvalidDataException) { Check(tooLarge.Position == 0, "Size guard must run before allocating/reading payload."); }
    }

    private static WaveFileReader Reader(ProbeStream stream) => new(new MemoryStream(stream.Bytes));
    private static byte[] Bytes(int frames, float value)
    {
        var samples = new float[frames]; Array.Fill(samples, value);
        return MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class ProbeStream : Stream
    {
        private readonly MemoryStream storage = new();
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Armed { get; set; }
        public bool BlockWrites { get; init; }
        public bool ThrowWrites { get; init; }
        public int WriteThread { get; private set; }
        public bool Closed { get; private set; }
        public int DisposeCount { get; private set; }
        public byte[] Bytes => storage.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => storage.Length;
        public override long Position { get => storage.Position; set => storage.Position = value; }
        public override void Flush() => storage.Flush();
        public override long Seek(long offset, SeekOrigin origin) => storage.Seek(offset, origin);
        public override void SetLength(long value) => storage.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Armed)
            {
                WriteThread = Environment.CurrentManagedThreadId;
                Entered.Set();
                if (BlockWrites && !Release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Controlled write timed out.");
                if (ThrowWrites) throw new IOException("Injected disk failure.");
            }
            storage.Write(buffer);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !Closed)
            {
                Closed = true; DisposeCount++;
                storage.Dispose(); Entered.Dispose(); Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
