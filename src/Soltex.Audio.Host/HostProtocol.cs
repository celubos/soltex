using System.Buffers.Binary;
using System.Text.Json;

namespace Soltex.Audio.Host;

internal sealed record HostRequest(int Version, Guid Id, string Token, string Operation, AudioHostProfile? Profile)
{
    public string? CaptureEndpointId { get; init; }
}
internal sealed record HostReply(int Version, Guid Id, HostSnapshot? Snapshot, string? Error);

internal static class HostProtocol
{
    internal const int MaximumMessageBytes = 524288;
    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, HostProfileStore.Json);
        if (body.Length > MaximumMessageBytes) throw new IOException("Audio host message exceeds its limit.");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is < 1 or > MaximumMessageBytes) throw new IOException("Invalid audio host message size.");
        byte[] body = new byte[size]; await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, HostProfileStore.Json) ?? throw new JsonException("Empty audio host message.");
    }
}
