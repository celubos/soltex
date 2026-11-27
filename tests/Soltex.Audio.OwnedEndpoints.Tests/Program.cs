using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.Wave;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.OwnedEndpoints;
using Soltex.Audio.Transport;

int checks = 0;
void Check(bool condition) { if (!condition) throw new InvalidOperationException("Owned endpoint assertion failed."); checks++; }
var device = new FakeDevice();
using (var router = new OwnedAudioRouter(new OwnedAudioTransport(device), automatic: false))
{
    using var first = router.Subscribe(OwnedAudioBus.Game);
    using var replacement = router.Subscribe(OwnedAudioBus.Game);
    router.PumpOnce(); Check(device.Reads == 1);
    float[] a = new float[2], b = new float[2]; first.Buffer.Read(a, 0, 2); replacement.Buffer.Read(b, 0, 2);
    Check(a.SequenceEqual(b) && a.SequenceEqual(new float[] { .25f, -.25f }));
    using var originalMic = router.Inject(new ConstantSource(.25f));
    router.PumpOnce(); Check(device.LastWrites[4] == 8192);
    using (var newMic = router.Inject(new ConstantSource(.5f)))
    {
        int before = device.Writes;
        router.PumpOnce(); Check(device.Writes == before + 1 && device.LastWrites[4] == 16384);
        using var stream = router.Inject(new ConstantSource(-.5f), OwnedAudioBus.Stream);
        before = device.Writes; router.PumpOnce();
        Check(device.Writes == before + 2 && device.LastWrites[4] == 16384 && device.LastWrites[5] == -16384);
    }
    router.PumpOnce(); Check(device.LastWrites[4] == 8192); // failed/disposed replacement restores old writer
    device.Buffered[4] = 960; int held = device.Writes; router.PumpOnce(); Check(device.Writes == held); // kernel clock pacing
    device.Buffered[4] = 0;
    using var player = new OwnedWavePlayer(router, OwnedAudioBus.Stream);
    Check(player.OutputWaveFormat.SampleRate == 48000 && player.OutputWaveFormat.Channels == 2);
    player.Init(new ConstantSource(.125f).ToWaveProvider()); player.Play(); router.PumpOnce();
    Check(device.LastWrites[5] == 4096 && player.PlaybackState == PlaybackState.Playing);
    player.Pause(); held = device.Writes; router.PumpOnce(); Check(device.Writes == held + 1); // only microphone remains
    using var input = new OwnedWaveIn(router, OwnedAudioBus.Chat);
    using var received = new ManualResetEventSlim();
    int bytes = 0, stopped = 0;
    input.DataAvailable += (_, e) => { bytes = e.BytesRecorded; received.Set(); };
    input.RecordingStopped += (_, _) => Interlocked.Increment(ref stopped);
    input.StartRecording(); router.PumpOnce(); Check(received.Wait(TimeSpan.FromSeconds(2)) && bytes == 8);
    input.StopRecording(); Check(stopped == 1);
}
Check(device.DisposeCount == 1);
var endpointDevice = new FakeDevice(); var windows = new FakeWindows
{
    Endpoints = Enum.GetValues<OwnedAudioBus>().Select(bus => new HostEndpoint("native:" + bus, "Native " + bus, bus >= OwnedAudioBus.Microphone, true)).ToArray()
};
using (var backend = new OwnedEndpointBackend(windows, () => new OwnedAudioTransport(endpointDevice), id => Enum.Parse<OwnedAudioBus>(id[7..])))
{
    var endpoints = backend.Enumerate();
    Check(endpoints.Count == 12 && endpoints.Count(e => e.IsCapture) == 6);
    Check(endpoints.Single(e => e.Id == "soltex-owned:Stream") is { IsCapture: false, IsActive: true });
    Check(endpoints.Single(e => e.Id == "native:Microphone").TransportId == "soltex-owned:Microphone");
    bool rejected = false;
    try { backend.Create("soltex-owned:Game", "native:Game", new()); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected);
    Check(endpointDevice.Reads == 0 && endpointDevice.Writes == 0); // enumeration never starts PCM
}
Check(windows.DisposeCount == 1 && endpointDevice.DisposeCount == 1);
using (var unpaired = new OwnedEndpointBackend(new FakeWindows(), () => new OwnedAudioTransport(new FakeDevice())))
    Check(unpaired.Enumerate().All(e => !e.IsActive));
using (var unavailable = new OwnedEndpointBackend(new FakeWindows(), () => throw new UnauthorizedAccessException("Broker unavailable")))
{
    Check(unavailable.Enumerate().Count == 0 && unavailable.TransportUnavailableReason == "Broker unavailable");
}
int metadataAttempts = 0;
using (var hotplug = new OwnedEndpointBackend(new FakeWindows { Endpoints = [new("vanished", "Vanished endpoint", false, true)] },
    () => throw new UnauthorizedAccessException("Broker unavailable"), _ => { metadataAttempts++; Marshal.ThrowExceptionForHR(unchecked((int)0x88890004)); return null; }))
{
    Check(hotplug.Enumerate().Single() is { Id: "vanished", IsActive: false });
    hotplug.Enumerate(); Check(metadataAttempts == 2);
}
{
    // Most PCs never install the owned transport: an idle helper must not probe the driver every few seconds.
    var clock = new TestClock();
    var idleWindows = new FakeWindows();
    using var idle = new OwnedEndpointBackend(idleWindows, () => throw new UnauthorizedAccessException("Broker unavailable"), _ => null, clock);
    idle.Enumerate(); idle.Enumerate();
    Check(idle.TransportOpenAttempts == 1);
    clock.Now += OwnedEndpointBackend.InitialRetryDelay; idle.Enumerate();
    Check(idle.TransportOpenAttempts == 2);
    clock.Now += OwnedEndpointBackend.InitialRetryDelay; idle.Enumerate();
    Check(idle.TransportOpenAttempts == 2); // second failure doubled the delay
    for (int i = 0; i < 20; i++) { clock.Now += OwnedEndpointBackend.MaximumRetryDelay; idle.Enumerate(); }
    Check(idle.TransportOpenAttempts == 22); // capped at one attempt per five minutes
    idleWindows.Endpoints = [new("new-device", "Newly installed endpoint", false, true)];
    idle.Enumerate();
    Check(idle.TransportOpenAttempts == 23); // a Windows endpoint change (e.g. installing the driver) retries at once
}
{
    // The router's pacing thread and each owned stream input register with MMCSS; every start/stop cycle must release both
    // (fake scheduler: no real MMCSS calls), so restarting streams all day never accumulates registrations.
    var scheduler = new FakeScheduler();
    var previous = AudioThreadScheduling.Scheduler;
    AudioThreadScheduling.Scheduler = scheduler;
    try
    {
        for (int cycle = 0; cycle < 40; cycle++)
        {
            var cycleDevice = new FakeDevice();
            var router = new OwnedAudioRouter(new OwnedAudioTransport(cycleDevice));
            var input = new OwnedWaveIn(router, OwnedAudioBus.Chat);
            input.StartRecording();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (scheduler.Outstanding < 2 && wait.ElapsedMilliseconds < 2000) Thread.Sleep(1);
            Check(scheduler.Outstanding == 2); // worker and input thread both registered
            input.Dispose(); router.Dispose();
            Check(scheduler.Outstanding == 0);
        }
        Check(scheduler.Granted == 80 && scheduler.Reverted == 80 && scheduler.MaximumOutstanding == 2);
    }
    finally { AudioThreadScheduling.Scheduler = previous; }
}
{
    // The router pumps every 10 ms on its high-resolution timer (the plain wait it replaced landed on the 15.6 ms timer
    // tick: about 64 pumps a second instead of 100).
    var pacedDevice = new FakeDevice();
    using var paced = new OwnedAudioRouter(new OwnedAudioTransport(pacedDevice));
    using var subscription = paced.Subscribe(OwnedAudioBus.Game);
    Thread.Sleep(200);
    int startReads = pacedDevice.Reads; var clock = System.Diagnostics.Stopwatch.StartNew();
    Thread.Sleep(1000);
    double pumpsPerSecond = (pacedDevice.Reads - startReads) / clock.Elapsed.TotalSeconds;
    Check(paced.HighResolutionPacing);
    Check(pumpsPerSecond is >= 75 and <= 110);
    Console.WriteLine($"  Router paced at {pumpsPerSecond:F0} pumps per second on the high-resolution timer.");
}
Console.WriteLine($"PASS {checks} owned endpoint adapter checks; fake transport only, no live audio.");

sealed class FakeScheduler : AudioThreadScheduling.IScheduler
{
    private readonly object sync = new();
    private readonly HashSet<nint> held = [];
    private long next;
    public int Granted { get; private set; }
    public int Reverted { get; private set; }
    public int MaximumOutstanding { get; private set; }
    public int Outstanding { get { lock (sync) return held.Count; } }
    public nint Register(string taskName)
    {
        lock (sync) { nint handle = (nint)(++next); held.Add(handle); Granted++; MaximumOutstanding = Math.Max(MaximumOutstanding, held.Count); return handle; }
    }
    public void Revert(nint handle)
    {
        lock (sync) { if (!held.Remove(handle)) throw new InvalidOperationException("Unknown or already reverted MMCSS handle."); Reverted++; }
    }
}

sealed class ConstantSource(float value) : ISampleProvider
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(float[] buffer, int offset, int count) { buffer.AsSpan(offset, count).Fill(value); return count; }
}
sealed class FakeWindows : IHostAudioBackend
{
    public HostEndpoint[] Endpoints { get; set; } = [];
    public int DisposeCount { get; private set; }
    public IReadOnlyList<HostEndpoint> Enumerate() => Endpoints;
    public IReadOnlyDictionary<string, float?> ReadInputPeaks(IReadOnlyCollection<string> captureIds) => new Dictionary<string, float?>();
    public IHostAudioPath Create(string captureId, string renderId, DspSettings settings) => throw new InvalidOperationException("Unexpected physical audio access.");
    public void Dispose() => DisposeCount++;
}
sealed class FakeDevice : IOwnedAudioDevice
{
    private readonly ulong[] produced = new ulong[6];
    public int[] Buffered { get; } = new int[6];
    public short[] LastWrites { get; } = new short[6];
    public int Reads { get; private set; }
    public int Writes { get; private set; }
    public int DisposeCount { get; private set; }
    public byte[] Exchange(uint controlCode, byte[] request, int responseCapacity)
    {
        int bus = (int)BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8));
        int frames = (int)BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12));
        bool read = controlCode == OwnedAudioTransport.ReadControl;
        if (controlCode == OwnedAudioTransport.WriteControl)
        {
            if (BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(16)) != produced[bus] || BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(24)) != 1) throw new IOException("Stale sequence/generation.");
            LastWrites[bus] = BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(32)); Writes++; produced[bus] += (ulong)frames;
        }
        if (read) { Reads++; frames = 1; }
        byte[] response = new byte[56 + (read ? 4 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 1); BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(4), 56);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(8), (uint)bus); BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(12), (uint)frames);
        BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(16), produced[bus]); BinaryPrimitives.WriteUInt64LittleEndian(response.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(48), 1); BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(52), (uint)Buffered[bus]);
        if (read) { BinaryPrimitives.WriteInt16LittleEndian(response.AsSpan(56), 8192); BinaryPrimitives.WriteInt16LittleEndian(response.AsSpan(58), -8192); }
        if (response.Length > responseCapacity) throw new InvalidOperationException("Response capacity mismatch.");
        return response;
    }
    public void Dispose() => DisposeCount++;
}

sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
