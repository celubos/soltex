using System.Buffers.Binary;

namespace Soltex.Audio.Transport;

public enum OwnedAudioBus { Game, Chat, Media, Aux, Microphone, Stream }
public sealed record OwnedAudioStatus(OwnedAudioBus Bus, int Frames, ulong Sequence, ulong Generation,
    ulong DroppedFrames, ulong UnderrunFrames, bool Active)
{
    public int BufferedFrames { get; init; }
}
public sealed record OwnedAudioPacket(float[] Samples, OwnedAudioStatus Status);

/// <summary>Injectable device boundary. Implementations must bound calls and own their native resources.</summary>
public interface IOwnedAudioDevice : IDisposable
{
    byte[] Exchange(uint controlCode, byte[] request, int responseCapacity);
}

/// <summary>One shared, serialized privileged lease for all five buses. Opening never installs or starts a driver.</summary>
public sealed class OwnedAudioTransport : IDisposable
{
    public const string DefaultDevicePath = @"\\.\SoltexAudioTransport";
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int MaximumFrames = 4800;
    public const uint QueryControl = 0x22e000;
    public const uint ReadControl = 0x22e004;
    public const uint WriteControl = 0x22e008;
    private const int RequestSize = 32;
    private const int ResponseSize = 56;
    private readonly object sync = new();
    private readonly IOwnedAudioDevice device;
    private bool disposed;

    public OwnedAudioTransport(string devicePath = DefaultDevicePath) : this(new WindowsOwnedAudioDevice(devicePath)) { }
    /// <summary>Privileged broker entry point: opens only Soltex's fixed native control device.</summary>
    public static IOwnedAudioDevice OpenNativeDevice() => new WindowsOwnedAudioDevice(DefaultDevicePath);
    public OwnedAudioTransport(IOwnedAudioDevice device) => this.device = device ?? throw new ArgumentNullException(nameof(device));

    public OwnedAudioStatus Query(OwnedAudioBus bus)
    {
        lock (sync) { ThrowIfDisposed(); return QueryCore(bus); }
    }

    public OwnedAudioPacket ReadRender(OwnedAudioBus bus, int maxFrames = 480)
    {
        ValidateBus(bus);
        if (bus >= OwnedAudioBus.Microphone) throw new ArgumentException("Microphone is an injection bus.", nameof(bus));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrames, MaximumFrames);
        lock (sync)
        {
            ThrowIfDisposed();
            byte[] response = device.Exchange(ReadControl, Request(bus, maxFrames), ResponseSize + maxFrames * 4);
            var status = Parse(response, bus, maxFrames, hasPcm: true);
            float[] samples = new float[status.Frames * Channels];
            for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(ResponseSize + i * 2)) / 32768f;
            return new(samples, status);
        }
    }

    public OwnedAudioStatus WriteMicrophone(ReadOnlySpan<float> samples) => WriteCapture(OwnedAudioBus.Microphone, samples);

    public OwnedAudioStatus WriteCapture(OwnedAudioBus bus, ReadOnlySpan<float> samples)
    {
        ValidateBus(bus);
        if (bus < OwnedAudioBus.Microphone) throw new ArgumentException("Select a capture injection bus.", nameof(bus));
        if (samples.Length == 0 || samples.Length % Channels != 0 || samples.Length > MaximumFrames * Channels)
            throw new ArgumentException("Supply 1–4800 complete stereo frames.", nameof(samples));
        int frames = samples.Length / Channels;
        lock (sync)
        {
            ThrowIfDisposed();
            // A capture stream can restart independently. Query its generation/producer sequence immediately before injection.
            var before = QueryCore(bus);
            byte[] request = Request(bus, frames, before.Sequence, samples.Length * 2, before.Generation);
            for (int i = 0; i < samples.Length; i++)
            {
                if (!float.IsFinite(samples[i])) throw new ArgumentException("PCM samples must be finite.", nameof(samples));
                int value = (int)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32768f);
                BinaryPrimitives.WriteInt16LittleEndian(request.AsSpan(RequestSize + i * 2), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
            }
            var result = Parse(device.Exchange(WriteControl, request, ResponseSize), bus, frames, hasPcm: false);
            if (result.Frames != frames || result.Generation != before.Generation) throw new IOException("Microphone stream generation changed during injection; packet was not confirmed.");
            return result;
        }
    }

    private OwnedAudioStatus QueryCore(OwnedAudioBus bus) => Parse(device.Exchange(QueryControl, Request(bus, 0), ResponseSize), bus, 0, hasPcm: false);
    private static byte[] Request(OwnedAudioBus bus, int frames, ulong sequence = 0, int payload = 0, ulong generation = 0)
    {
        ValidateBus(bus);
        byte[] result = new byte[RequestSize + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), RequestSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)bus);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)frames);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), generation);
        return result;
    }
    private static OwnedAudioStatus Parse(byte[] data, OwnedAudioBus bus, int maxFrames, bool hasPcm)
    {
        if (data.Length < ResponseSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != ResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != (uint)bus)
            throw new InvalidDataException("Unsupported or mismatched Soltex transport response.");
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        uint active = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48));
        uint buffered = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52));
        if (frames > maxFrames || active > 1 || buffered > 9600 ||
            data.Length != ResponseSize + (hasPcm ? frames * 4 : 0)) throw new InvalidDataException("Invalid Soltex PCM response bounds.");
        return new(bus, (int)frames, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24)), BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40)), active != 0) { BufferedFrames = (int)buffered };
    }
    private static void ValidateBus(OwnedAudioBus bus) { if ((uint)bus > 5) throw new ArgumentOutOfRangeException(nameof(bus)); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; device.Dispose(); } }
}
