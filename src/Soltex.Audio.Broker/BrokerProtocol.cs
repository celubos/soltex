using System.Buffers.Binary;
using Soltex.Audio.Transport;

namespace Soltex.Audio.Broker;

internal static class BrokerProtocol
{
    internal const int MaximumMessage = 19272;
    internal const string PipeName = "Soltex.Audio.Broker.v1";
    internal const string ServiceName = "SoltexAudioBroker";
    internal static void Validate(uint code, byte[] request, int capacity)
    {
        if (request.Length < 32 || request.Length > 19232 || capacity < 56 || capacity > 19256)
            throw new InvalidDataException("Invalid audio command bounds.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(request), size = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4));
        uint bus = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)), frames = BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12));
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(16)), generation = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24));
        if (version != 1 || size != 32 || bus > 5 || frames > 4800) throw new InvalidDataException("Unsupported audio command.");
        bool valid = code switch
        {
            OwnedAudioTransport.QueryControl => frames == 0 && sequence == 0 && generation == 0 && request.Length == 32 && capacity == 56,
            OwnedAudioTransport.ReadControl => bus < 4 && frames > 0 && sequence == 0 && generation == 0 && request.Length == 32 && capacity == 56 + frames * 4,
            OwnedAudioTransport.WriteControl => bus is 4 or 5 && frames > 0 && request.Length == 32 + frames * 4 && capacity == 56,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Audio command is outside the broker's narrow protocol.");
    }

    internal static byte[] Request(ulong sequence, uint code, byte[] request, int capacity)
    {
        Validate(code, request, capacity);
        byte[] data = new byte[24 + request.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), code);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), sequence); BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), capacity);
        request.CopyTo(data, 24); return data;
    }
    internal static (ulong Sequence, uint Code, byte[] Request, int Capacity) ParseRequest(byte[] data)
    {
        if (data.Length < 56 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)) != 0)
            throw new InvalidDataException("Invalid broker request envelope.");
        uint code = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)); ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8));
        int capacity = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16)); byte[] request = data.AsSpan(24).ToArray();
        Validate(code, request, capacity); return (sequence, code, request, capacity);
    }
    internal static byte[] Response(ulong sequence, uint status, byte[]? payload = null)
    {
        byte[] data = new byte[16 + (payload?.Length ?? 0)]; BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), status); BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), sequence);
        payload?.CopyTo(data, 16); return data;
    }
    internal static byte[] ParseResponse(byte[] data, ulong sequence, int capacity)
    {
        if (data.Length < 16 || data.Length > 16 + capacity || BinaryPrimitives.ReadUInt32LittleEndian(data) != 1 || BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)) != sequence)
            throw new InvalidDataException("Mismatched broker response.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 0) throw new IOException("Privileged audio broker refused or could not confirm this operation; the connection is closed.");
        if (data.Length < 72) throw new InvalidDataException("Truncated audio response.");
        return data.AsSpan(16).ToArray();
    }
    internal static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 16 or > MaximumMessage) throw new InvalidDataException("Broker frame exceeds its fixed limit.");
        byte[] data = new byte[size]; await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false); return data;
    }
    internal static async Task WriteAsync(Stream stream, byte[] message, CancellationToken cancellationToken)
    {
        if (message.Length is < 16 or > MaximumMessage) throw new InvalidDataException("Invalid broker frame length.");
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, message.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false); await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
