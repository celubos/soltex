using System.IO.Pipes;
using Soltex.Audio.Transport;

namespace Soltex.Audio.Broker;

internal sealed record BrokerPeer(uint ProcessId, long CreationTime, uint SessionId, string Sid);
internal interface IBrokerIdentity
{
    void BeforeConnect();
    BrokerPeer Client(NamedPipeServerStream pipe);
    BrokerPeer Server(NamedPipeClientStream pipe);
}

/// <summary>Fixed-protocol service host. A single authorized interactive session owns the adapter until disconnect/timeout.</summary>
public sealed class AudioBrokerServer
{
    private readonly Func<NamedPipeServerStream> createPipe;
    private readonly Func<IOwnedAudioDevice> openDevice;
    private readonly IBrokerIdentity identity;
    private readonly TimeSpan operationTimeout;
    private int leased;
    private Exception? failure;
    public string? Failure => Volatile.Read(ref failure)?.Message;
    public AudioBrokerServer() : this(WindowsBrokerIdentity.CreateServerPipe, OwnedAudioTransport.OpenNativeDevice, new WindowsBrokerIdentity(), TimeSpan.FromSeconds(3)) { }
    internal AudioBrokerServer(Func<NamedPipeServerStream> createPipe, Func<IOwnedAudioDevice> openDevice, IBrokerIdentity identity, TimeSpan operationTimeout)
    { this.createPipe = createPipe; this.openDevice = openDevice; this.identity = identity; this.operationTimeout = operationTimeout; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = createPipe();
            try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A client that connected and hung up before the accept completed ("The pipe is being closed").
                // Drop it and keep serving: one hang-up must not stop the broker service for every user.
                continue;
            }
            await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref failure) is Exception cleanup) throw new IOException("Audio adapter cleanup failed; broker restart is required.", cleanup);
        }
    }

    internal async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        IOwnedAudioDevice? device = null; bool ownsLease = false, deferred = false; Task<byte[]>? pending = null;
        BrokerPeer? owner = null; ulong last = 0;
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var command = BrokerProtocol.ParseRequest(await BrokerProtocol.ReadAsync(pipe, deadline.Token).ConfigureAwait(false));
                if (command.Sequence != checked(last + 1)) throw new InvalidDataException("Broker sequence is not the next command.");
                var current = identity.Client(pipe);
                if (owner is not null && current != owner) throw new UnauthorizedAccessException("Audio lease identity changed.");
                owner = current;
                if (!ownsLease)
                {
                    if (Interlocked.CompareExchange(ref leased, 1, 0) != 0) throw new IOException("Audio adapter lease is busy.");
                    ownsLease = true; device = openDevice();
                }
                var ownedDevice = device!;
                var expectedOwner = owner;
                pending = Task.Run(() =>
                {
                    // Revalidate on the actual forwarding worker, after any thread-pool scheduling delay.
                    if (identity.Client(pipe) != expectedOwner) throw new UnauthorizedAccessException("Audio client identity changed before forwarding.");
                    return ownedDevice.Exchange(command.Code, command.Request, command.Capacity);
                }, CancellationToken.None);
                byte[] response = await pending.WaitAsync(operationTimeout, cancellationToken).ConfigureAwait(false);
                pending = null;
                if (response.Length < 56 || response.Length > command.Capacity) throw new InvalidDataException("Driver response violates broker bounds.");
                last = command.Sequence;
                await BrokerProtocol.WriteAsync(pipe, BrokerProtocol.Response(last, 0, response), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception or ArgumentException or OverflowException)
        {
            // Close the channel. Its caller must reconcile; no automatic retry of an unknown write result.
        }
        finally
        {
            if (pending is { IsCompleted: false } && device is not null)
            {
                deferred = true; var retained = device;
                _ = ReleaseAfterCompletionAsync(pending, retained);
            }
            if (!deferred && ownsLease)
            {
                bool closed = true;
                try { device?.Dispose(); }
                catch (Exception error) { closed = false; Interlocked.CompareExchange(ref failure, error, null); }
                if (closed) Interlocked.Exchange(ref leased, 0);
            }
        }
    }

    private async Task ReleaseAfterCompletionAsync(Task<byte[]> pending, IOwnedAudioDevice device)
    {
        try { await pending.ConfigureAwait(false); } catch (Exception) { /* Failure cannot release storage before completion. */ }
        try { device.Dispose(); }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); return; }
        Interlocked.Exchange(ref leased, 0);
    }
}
