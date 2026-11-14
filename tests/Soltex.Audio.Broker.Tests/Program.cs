using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using Soltex.Audio.Broker;
using Soltex.Audio.Transport;

namespace Soltex.Audio.Broker.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        (string, Func<Task>)[] tests = [("Protocol rejects non-audio commands, malformed sizes and direction", Protocol),
            ("Windows identity policy rejects inactive, mismatched and unknown identities", IdentityPolicy),
            ("Unavailable service preflight fails before trying a pipe", Unavailable),
            ("Ordinary client forwards bounded audio and releases disconnected lease", RoundTrip),
            ("Unknown client or counterfeit server never reaches driver", AuthReject),
            ("PID birth, SID and session changes revoke the lease", IdentityChanges),
            ("Replay and oversized frames cannot invoke the driver", BadFrames),
            ("A second session cannot share an active adapter lease", ExclusiveLease),
            ("Timed out native calls retain ownership until completion", TimeoutOwnership),
            ("Identity is checked again on the forwarding worker", ForwardIdentity),
            ("Failed device cleanup keeps the exclusive lease closed", CleanupFailure),
            ("A client that hangs up before accept cannot stop the broker", HangUpBeforeAccept)];
        int failures = 0;
        foreach (var (name, test) in tests) { try { await test(); Console.WriteLine("PASS " + name); } catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); } }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} broker checks passed; fake identity/device only, no service/driver opened."); return failures == 0 ? 0 : 1;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static byte[] Request(uint bus = 0, uint frames = 0)
    { var bytes = new byte[32 + (bus >= 4 ? frames * 4 : 0)]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 32); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), bus); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), frames); return bytes; }
    private static Task Protocol()
    {
        var cases = new (uint, byte[], int)[] { (0, Request(), 56), (OwnedAudioTransport.ReadControl, Request(4, 1), 60),
            (OwnedAudioTransport.WriteControl, Request(0, 1), 56), (OwnedAudioTransport.QueryControl, Request(6), 56),
            (OwnedAudioTransport.ReadControl, Request(0, 4801), 19260), (OwnedAudioTransport.QueryControl, Request(), 57) };
        foreach (var (code, request, capacity) in cases) { bool denied = false; try { BrokerProtocol.Validate(code, request, capacity); } catch (InvalidDataException) { denied = true; } Check(denied, "Malformed command passed"); }
        BrokerProtocol.Validate(OwnedAudioTransport.WriteControl, Request(5, 4800), 56);
        BrokerProtocol.Validate(OwnedAudioTransport.ReadControl, Request(3, 4800), 19256);
        return Task.CompletedTask;
    }
    private static Task IdentityPolicy()
    {
        static void Rejected(Action action) { bool denied = false; try { action(); } catch (UnauthorizedAccessException) { denied = true; } Check(denied, "Invalid identity accepted"); }
        BrokerIdentityPolicy.ConsoleSession(1, 1);
        Rejected(() => BrokerIdentityPolicy.ConsoleSession(1, 2));
        Rejected(() => BrokerIdentityPolicy.ConsoleSession(1, uint.MaxValue));
        _ = BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Impersonation);
        Rejected(() => BrokerIdentityPolicy.Client(0, 20, 1, 1, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 0, 1, 1, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 0, 0, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 2, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "other", "sid", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "sid", "other", true, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "sid", "sid", false, true, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "sid", "sid", true, false, TokenImpersonationLevel.Impersonation));
        Rejected(() => BrokerIdentityPolicy.Client(10, 20, 1, 1, "sid", "sid", "sid", true, true, TokenImpersonationLevel.Identification));
        _ = BrokerIdentityPolicy.Server(10, 10, 4, 16, "LocalSystem", 20, 0);
        Rejected(() => BrokerIdentityPolicy.Server(10, 11, 4, 16, "LocalSystem", 20, 0));
        Rejected(() => BrokerIdentityPolicy.Server(10, 10, 1, 16, "LocalSystem", 20, 0));
        Rejected(() => BrokerIdentityPolicy.Server(10, 10, 4, 16, "LocalService", 20, 0));
        Rejected(() => BrokerIdentityPolicy.Server(10, 10, 4, 16, "LocalSystem", 20, 1));
        return Task.CompletedTask;
    }
    private static Task Unavailable()
    {
        using var client = new BrokerAudioDevice("not-created-" + Guid.NewGuid().ToString("N"), new FakeIdentity { Unavailable = true });
        bool rejected = false; var watch = System.Diagnostics.Stopwatch.StartNew();
        try { client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56); } catch (IOException) { rejected = true; }
        Check(rejected && watch.Elapsed < TimeSpan.FromSeconds(1), "Unavailable service still waited for a pipe"); return Task.CompletedTask;
    }
    private static async Task RoundTrip()
    {
        var identity = new FakeIdentity(); var device = new FakeDevice(); await using var session = new Session(identity, () => device);
        using var client = new BrokerAudioDevice(session.Name, identity);
        byte[] response = await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56));
        Check(response.Length == 56 && device.Calls == 1, "Query was not forwarded exactly once");
        client.Dispose(); await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)); Check(device.Disposed, "Disconnect retained driver lease");
    }
    private static async Task HangUpBeforeAccept()
    {
        // Deterministic: the first pipe instance is connected and abandoned before RunAsync calls WaitForConnectionAsync,
        // which Windows reports as ERROR_NO_DATA. The broker must drop that client and serve the next one.
        string name = "Soltex.Broker.Test." + Guid.NewGuid().ToString("N"); int created = 0;
        var identity = new FakeIdentity(); var device = new FakeDevice();
        NamedPipeServerStream Create()
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            if (Interlocked.Increment(ref created) == 1)
            { using var early = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly); early.Connect(2000); }
            return pipe;
        }
        var broker = new AudioBrokerServer(Create, () => device, identity, TimeSpan.FromSeconds(1));
        using var stop = new CancellationTokenSource();
        Task running = broker.RunAsync(stop.Token);
        using (var client = new BrokerAudioDevice(name, identity))
        {
            byte[] response = await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56));
            Check(created >= 2 && response.Length == 56 && device.Calls == 1 && !running.IsCompleted, "An early hang-up stopped the broker or blocked the next client");
        }
        stop.Cancel();
        try { await running.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
    }
    private static async Task AuthReject()
    {
        foreach (bool clientSide in new[] { false, true })
        {
            var identity = new FakeIdentity { DenyClient = !clientSide, DenyServer = clientSide }; var device = new FakeDevice();
            await using var session = new Session(identity, () => device); using var client = new BrokerAudioDevice(session.Name, identity);
            bool rejected = false; try { await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
            Check(rejected && device.Calls == 0 && session.Opens == 0, "Untrusted peer reached native device");
        }
    }
    private static async Task IdentityChanges()
    {
        foreach (var changed in new[] { FakeIdentity.User with { CreationTime = 11 }, FakeIdentity.User with { Sid = "other" }, FakeIdentity.User with { SessionId = 2 }, FakeIdentity.User with { ProcessId = 99 } })
        {
            var identity = new FakeIdentity(); var device = new FakeDevice(); await using var session = new Session(identity, () => device);
            using var client = new BrokerAudioDevice(session.Name, identity); await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56));
            identity.Peer = changed; bool rejected = false;
            try { await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { rejected = true; }
            Check(rejected && device.Calls == 1, "Changed identity forwarded another command");
        }
    }
    private static async Task BadFrames()
    {
        var identity = new FakeIdentity(); var device = new FakeDevice(); await using var session = new Session(identity, () => device);
        using var client = new NamedPipeClientStream(".", session.Name, PipeDirection.InOut, PipeOptions.Asynchronous); await client.ConnectAsync();
        await BrokerProtocol.WriteAsync(client, BrokerProtocol.Request(1, OwnedAudioTransport.QueryControl, Request(), 56), default);
        _ = await BrokerProtocol.ReadAsync(client, default);
        await BrokerProtocol.WriteAsync(client, BrokerProtocol.Request(1, OwnedAudioTransport.QueryControl, Request(), 56), default);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2)); Check(device.Calls == 1, "Replay reached native device");
        await using var oversized = new Session(identity, () => new FakeDevice()); using var other = new NamedPipeClientStream(".", oversized.Name, PipeDirection.InOut, PipeOptions.Asynchronous); await other.ConnectAsync();
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, int.MaxValue); await other.WriteAsync(prefix);
        await oversized.Completion.WaitAsync(TimeSpan.FromSeconds(2)); Check(oversized.Opens == 0, "Oversized frame opened native driver");
    }
    private static async Task ExclusiveLease()
    {
        var identity = new FakeIdentity(); int opened = 0;
        var broker = new AudioBrokerServer(() => throw new InvalidOperationException(), () => { Interlocked.Increment(ref opened); return new FakeDevice(); }, identity, TimeSpan.FromSeconds(1));
        await using var first = new Session(identity, () => new FakeDevice(), broker); await using var second = new Session(identity, () => new FakeDevice(), broker);
        using var a = new BrokerAudioDevice(first.Name, identity); using var b = new BrokerAudioDevice(second.Name, identity);
        await Task.Run(() => a.Exchange(OwnedAudioTransport.QueryControl, Request(), 56));
        identity.Peer = FakeIdentity.User with { SessionId = 2, Sid = "second-session-user" };
        bool denied = false; try { await Task.Run(() => b.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { denied = true; }
        Check(denied && opened == 1, "Second client shared the adapter");
    }
    private static async Task TimeoutOwnership()
    {
        var identity = new FakeIdentity(); using var gate = new ManualResetEventSlim(); var blocked = new FakeDevice { Gate = gate }; int opened = 0;
        var broker = new AudioBrokerServer(() => throw new InvalidOperationException(), () => { Interlocked.Increment(ref opened); return blocked; }, identity, TimeSpan.FromMilliseconds(100));
        await using var first = new Session(identity, () => blocked, broker); using var a = new BrokerAudioDevice(first.Name, identity);
        try { await Task.Run(() => a.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { }
        Check(blocked.Calls == 1 && !blocked.Disposed, "Timed out call freed a still-owned native adapter");
        await using var second = new Session(identity, () => blocked, broker); using var b = new BrokerAudioDevice(second.Name, identity);
        try { await Task.Run(() => b.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { }
        Check(opened == 1, "New lease opened while native operation still running");
        gate.Set(); await blocked.Released.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(blocked.Disposed, "Completed quarantined operation never released adapter");
    }
    private static async Task ForwardIdentity()
    {
        var identity = new FakeIdentity { ForwardPeer = FakeIdentity.User with { CreationTime = 99 } }; var device = new FakeDevice();
        await using var session = new Session(identity, () => device); using var client = new BrokerAudioDevice(session.Name, identity);
        try { await Task.Run(() => client.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { }
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(device.Calls == 0 && device.Disposed, "Worker forwarded after process identity changed");
    }
    private static async Task CleanupFailure()
    {
        var identity = new FakeIdentity(); var device = new FakeDevice { FailDispose = true }; int opened = 0;
        var broker = new AudioBrokerServer(() => throw new InvalidOperationException(), () => { opened++; return device; }, identity, TimeSpan.FromSeconds(1));
        await using var first = new Session(identity, () => device, broker); using var a = new BrokerAudioDevice(first.Name, identity);
        await Task.Run(() => a.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); a.Dispose(); await first.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await using var second = new Session(identity, () => device, broker); using var b = new BrokerAudioDevice(second.Name, identity);
        try { await Task.Run(() => b.Exchange(OwnedAudioTransport.QueryControl, Request(), 56)); } catch (IOException) { }
        Check(broker.Failure is not null && opened == 1, "Cleanup failure permitted another adapter owner");
    }
    private sealed class FakeIdentity : IBrokerIdentity
    {
        internal static readonly BrokerPeer User = new(42, 10, 1, "user"); internal BrokerPeer Peer = User;
        internal bool DenyClient, DenyServer, Unavailable;
        internal BrokerPeer? ForwardPeer; private int calls;
        public void BeforeConnect() { if (Unavailable) throw new IOException("Broker not installed"); }
        public BrokerPeer Client(NamedPipeServerStream pipe) => DenyClient ? throw new UnauthorizedAccessException() : Interlocked.Increment(ref calls) >= 2 && ForwardPeer is not null ? ForwardPeer : Peer;
        public BrokerPeer Server(NamedPipeClientStream pipe) => DenyServer ? throw new UnauthorizedAccessException() : new(20, 4, 0, "SYSTEM");
    }
    private sealed class FakeDevice : IOwnedAudioDevice
    {
        internal int Calls; internal bool Disposed, FailDispose; internal ManualResetEventSlim? Gate;
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Exchange(uint code, byte[] request, int responseCapacity) { Interlocked.Increment(ref Calls); Gate?.Wait(); byte[] response = new byte[56]; request.AsSpan(0, 12).CopyTo(response); BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 56); return response; }
        public void Dispose() { Disposed = true; Released.TrySetResult(); if (FailDispose) throw new IOException("Fake cleanup failure"); }
    }
    private sealed class Session : IAsyncDisposable
    {
        internal string Name { get; } = "Soltex.Broker.Test." + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe; private readonly CancellationTokenSource stop = new();
        internal int Opens; internal Task Completion { get; }
        internal Session(IBrokerIdentity identity, Func<IOwnedAudioDevice> device, AudioBrokerServer? broker = null)
        {
            pipe = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            broker ??= new(() => throw new InvalidOperationException(), () => { Opens++; return device(); }, identity, TimeSpan.FromSeconds(1));
            Completion = Task.Run(async () =>
            {
                try
                {
                    // A rejected client may hang up before the accept completes; production RunAsync drops such clients.
                    try { await pipe.WaitForConnectionAsync(stop.Token); }
                    catch (IOException) when (!stop.IsCancellationRequested) { return; }
                    await broker.ServeAsync(pipe, stop.Token);
                }
                finally { pipe.Dispose(); }
            });
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); pipe.Dispose(); try { await Completion.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { } stop.Dispose(); }
    }
}
