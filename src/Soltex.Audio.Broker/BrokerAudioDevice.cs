using System.IO.Pipes;
using System.Security.Principal;
using Soltex.Audio.Transport;

namespace Soltex.Audio.Broker;

/// <summary>Ordinary-user client for the fixed, authenticated SYSTEM audio broker service. Never elevates, installs or retries a write.</summary>
public sealed class BrokerAudioDevice : IOwnedAudioDevice
{
    private readonly object sync = new();
    private readonly string pipeName;
    private readonly IBrokerIdentity identity;
    private NamedPipeClientStream? pipe;
    private BrokerPeer? server;
    private ulong sequence;
    private bool disposed;
    public BrokerAudioDevice() : this(BrokerProtocol.PipeName, new WindowsBrokerIdentity()) { }
    internal BrokerAudioDevice(string pipeName, IBrokerIdentity identity) { this.pipeName = pipeName; this.identity = identity; }
    public byte[] Exchange(uint controlCode, byte[] request, int responseCapacity)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this); BrokerProtocol.Validate(controlCode, request, responseCapacity);
            try { return ExchangeAsync(controlCode, request, responseCapacity).GetAwaiter().GetResult(); }
            catch { disposed = true; pipe?.Dispose(); pipe = null; throw; }
        }
    }
    private async Task<byte[]> ExchangeAsync(uint code, byte[] request, int capacity)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (pipe is null)
        {
            identity.BeforeConnect();
            pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        }
        var current = identity.Server(pipe);
        if (server is not null && current != server) throw new UnauthorizedAccessException("Broker service identity changed.");
        server = current;
        ulong id = checked(++sequence);
        await BrokerProtocol.WriteAsync(pipe, BrokerProtocol.Request(id, code, request, capacity), deadline.Token).ConfigureAwait(false);
        return BrokerProtocol.ParseResponse(await BrokerProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false), id, capacity);
    }
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; pipe?.Dispose(); pipe = null; } }
}
