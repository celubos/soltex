using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;

if (args.Length == 10 && args[0] == "--pipe")
{
    // Owned failure fixture for the client shutdown test. No Windows backend is constructed.
    await using NamedPipeServerStream fixture = new(args[1], PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[5])!, "fake-child-pid.txt"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    // Record how the secret arrived so the parent can prove it never appeared on the command line.
    string stdinSecret = args[3] == AudioHostServer.TokenFromStandardInput ? await AudioHostServer.ReadTokenAsync(Console.In, CancellationToken.None) : "";
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[5])!, "fake-child-token.txt"), args[3] + "|" + stdinSecret.Length.ToString(CultureInfo.InvariantCulture));
    await fixture.WaitForConnectionAsync();
    HostRequest request = await HostProtocol.ReadAsync<HostRequest>(fixture, CancellationToken.None);
    await HostProtocol.WriteAsync(fixture, new HostReply(1, request.Id, new(false, AudioHostProfile.CreateDefault(), [], [], null), null), CancellationToken.None);
    _ = await HostProtocol.ReadAsync<HostRequest>(fixture, CancellationToken.None);
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++; Console.WriteLine("PASS " + message);
}
AudioHostProfile Channel(AudioHostProfile profile, AudioBus bus, Func<HostChannelProfile, HostChannelProfile> change) =>
    profile with { Channels = profile.Channels.Select(c => c.Bus == bus ? change(c) : c).ToArray() };
AudioHostProfile enabled = Channel(AudioHostProfile.CreateDefault(), AudioBus.Game, c => c with
{ Enabled = true, CaptureEndpointId = "capture-game", RenderEndpointId = "speaker-a", OutputPriorityIds = ["speaker-a", "speaker-b"] });
FakeBackend probeBackend = new();
ProbeClock probeClock = new();
await using (AudioHostController host = new(backendFactory: () => probeBackend, automaticPolling: false, timeProvider: probeClock))
{
    var before = await host.ReadAsync();
    Check(before.MicrophoneTest is null && probeBackend.Probes.Count == 0, "observation never opens a microphone test");
    try { await host.StartMicrophoneTestAsync("speaker-a"); throw new InvalidOperationException("Expected capture rejection"); }
    catch (ArgumentException) { Check(probeBackend.Probes.Count == 0, "input test rejects playback endpoints before opening resources"); }
    var state = await host.StartMicrophoneTestAsync("capture-mic");
    Check(state.MicrophoneTest is { Running: true, Frames: 960 } && !state.StartRequested && !state.Running && probeBackend.Created.Count == 0,
        "explicit microphone test reports measured frames without starting any render path");
    Check(state.MicrophoneTest!.Peaks.SequenceEqual([.001f, .25f]) && state.MicrophoneTest.Rms.SequenceEqual([.0005f, .1f]), "microphone test preserves individual input peak and RMS observations");
    Check(state.Profile.Channels.All(c => !c.Enabled), "microphone test changes no channel profile");
    await host.StartMicrophoneTestAsync("capture-chat");
    Check(probeBackend.Probes[0].Disposed && !probeBackend.Probes[1].Disposed, "changing test source drains previous capture before replacing it");
    probeClock.Now += TimeSpan.FromSeconds(16);
    state = await host.ReadAsync();
    Check(state.MicrophoneTest is { Running: false, Frames: 960 } && probeBackend.Probes[1].Disposed, "bounded input test stops after fifteen seconds and retains numeric results");
    await host.StartMicrophoneTestAsync("capture-mic"); await host.StopAsync();
    Check(probeBackend.Probes[2].Disposed, "global stop drains capture-only testing as well as normal paths");
    await host.StartMicrophoneTestAsync("capture-mic");
}
Check(probeBackend.Probes.All(p => p.Disposed), "host disposal drains an active microphone test");
FakeBackend streamBackend = new();
var streamingProfile = enabled with { StreamMix = new() { PersonalRenderEndpointId = "speaker-a", StreamRenderEndpointId = "speaker-b" } };
await using (AudioHostController host = new(backendFactory: () => streamBackend, automaticPolling: false))
{
    await host.ApplyProfileAsync(streamingProfile);
    Check(streamBackend.Mixes.Count == 0, "saving stream choices never starts capture or render");
    await host.StartAsync();
    try { await host.StartStreamMixAsync(); throw new InvalidOperationException("Expected mode rejection"); }
    catch (InvalidOperationException error) when (error.Message.StartsWith("Stop the current", StringComparison.Ordinal)) { Check(streamBackend.Mixes.Count == 0, "normal audio and stream mix cannot own captures simultaneously"); }
    await host.StopAsync();
    var state = await host.StartStreamMixAsync();
    Check(state.StreamMix?.Running == true && state.Running && state.StartRequested && streamBackend.Mixes.Single().Configuration.Sources.Count == 1,
        "explicit stream start reports a real backend mix with one capture for its enabled source");
    try { await host.StartAsync(); throw new InvalidOperationException("Expected mode rejection"); }
    catch (InvalidOperationException error) when (error.Message.StartsWith("Stop stream", StringComparison.Ordinal)) { Check(true, "normal start also rejects while stream mode owns the graph"); }
    try { await host.StartMicrophoneTestAsync("capture-game"); throw new InvalidOperationException("Expected probe rejection"); }
    catch (InvalidOperationException error) when (error.Message.StartsWith("Stop audio", StringComparison.Ordinal)) { Check(true, "capture probe cannot duplicate a stream-mix capture"); }
    var changed = streamingProfile with { MasterMute = true, StreamMix = streamingProfile.StreamMix with { Sends = streamingProfile.StreamMix.Sends.Select(s => s.Bus == AudioBus.Game ? s with { StreamGainDb = -12 } : s).ToArray() } };
    await host.ApplyProfileAsync(changed);
    var update = streamBackend.Mixes[0].Updates.Single();
    Check(update.Personal.Mute && !update.Stream.Mute && update.Stream.GainDb == -12, "personal master mute and stream send gain remain independent");
    try { await host.ApplyProfileAsync(changed with { StreamMix = changed.StreamMix with { PersonalRenderEndpointId = "speaker-b" } }); throw new InvalidOperationException("Expected route rejection"); }
    catch (InvalidOperationException error) when (error.Message.StartsWith("Stop stream", StringComparison.Ordinal)) { Check((await host.ReadAsync()).Profile.StreamMix.PersonalRenderEndpointId == "speaker-a", "live route replacement is rejected before profile persistence"); }
    streamBackend.Mixes[0].Failure = "Fixture render unplugged"; streamBackend.ThrowEnumeration = true;
    state = await host.ReadAsync();
    Check(!state.Running && state.StreamMix?.Failure == "Fixture render unplugged" && streamBackend.Mixes[0].Disposed, "stream failure drains graph even when endpoint observation also fails");
    streamBackend.ThrowEnumeration = false;
    await host.StopAsync();
    await host.ApplyProfileAsync(streamingProfile with { StreamMix = streamingProfile.StreamMix with { StreamRenderEndpointId = "speaker-a" } });
    try { await host.StartStreamMixAsync(); throw new InvalidOperationException("Expected duplicate output rejection"); }
    catch (ArgumentException) { Check(streamBackend.Mixes.Count == 1, "duplicate personal and stream outputs rejected before capture"); }
    streamBackend.Endpoints = streamBackend.Endpoints.Select(e => e.Id is "speaker-b" or "capture-game" ? e with { TransportId = "same-cable" } : e).ToList();
    await host.ApplyProfileAsync(streamingProfile);
    try { await host.StartStreamMixAsync(); throw new InvalidOperationException("Expected feedback rejection"); }
    catch (ArgumentException) { Check(streamBackend.Mixes.Count == 1, "stream return to a source cable is rejected as feedback"); }
}
FakeBackend fake = new();
await using (AudioHostController host = new(backendFactory: () => fake, automaticPolling: false))
{
    var state = await host.ReadAsync();
    Check(!state.Running && fake.Created.Count == 0, "construction and observation never start audio");
    await host.ApplyProfileAsync(enabled);
    Check(fake.Created.Count == 0, "applying profile while stopped does not open streams");
    state = await host.StartAsync();
    Check(state.Channels.Single(c => c.Bus == AudioBus.Game).ActiveRenderId == "speaker-a", "explicit start opens the chosen path");
    Check(fake.Created.Single().FirstSettings.Mute && !fake.Created.Single().Settings.Mute, "replacement opens muted then ramps to requested DSP state");
    int updates = fake.Created.Single().Updates;
    await host.ReadAsync(); await host.ReadAsync();
    Check(fake.Created.Single().Updates == updates, "unchanged polling does not rebuild DSP settings");
    state = await host.ReadAsync();
    Check(state.Channels.Single(c => c.Bus == AudioBus.Game).Peak == .125f, "input meter is actual backend observation");
    ((HostChannelProfile[])state.Profile.Channels)[0] = new() { Bus = AudioBus.Game, GainDb = 12 };
    Check((await host.ReadAsync()).Profile.Channels[0].Enabled, "caller cannot mutate the controller through snapshot arrays");
    var requested = Channel(enabled, AudioBus.Game, c => c with { RenderEndpointId = "speaker-b" });
    fake.FailCreate.Add("speaker-b");
    state = await host.ApplyProfileAsync(requested);
    Check(state.Channels[0].ActiveRenderId == "speaker-a" && !fake.Created[0].Disposed, "failed replacement preserves confirmed previous audio path");
    fake.FailCreate.Clear();
    state = await host.ApplyProfileAsync(requested);
    Check(state.Channels[0].ActiveRenderId == "speaker-b" && fake.Created[0].Disposed, "successful replacement retires old path after new start");
    Check(fake.Events.IndexOf("start:speaker-b") < fake.Events.IndexOf("dispose:speaker-a"), "open and start precede previous path retirement");
    fake.Endpoints = fake.Endpoints.Select(e => e.Id == "speaker-b" ? e with { IsActive = false } : e).ToList();
    state = await host.ReadAsync();
    Check(state.Channels[0].ActiveRenderId == "speaker-a", "removing current output selects an explicit eligible fallback");
    requested = Channel(requested, AudioBus.Game, c => c with { AutoReturn = false });
    await host.ApplyProfileAsync(requested);
    fake.Endpoints = fake.Endpoints.Select(e => e with { IsActive = true }).ToList();
    Check((await host.ReadAsync()).Channels[0].ActiveRenderId == "speaker-a", "preferred return does not override a retained fallback when auto-return is off");
    requested = Channel(requested, AudioBus.Game, c => c with { AutoReturn = true });
    Check((await host.ApplyProfileAsync(requested)).Channels[0].ActiveRenderId == "speaker-b", "explicit auto-return restores pinned preferred output");
    requested = Channel(requested, AudioBus.Game, c => c with { ExcludedOutputIds = ["speaker-a", "speaker-b"] });
    state = await host.ApplyProfileAsync(requested);
    Check(state.Channels[0].State == "Unavailable" && state.Channels[0].ActiveRenderId is null, "excluded or missing devices are never silently selected");
    Check(state.StartRequested && !state.Running, "armed host with no live paths does not claim audio is running");
    requested = Channel(requested, AudioBus.Game, c => c with { ExcludedOutputIds = [], RenderEndpointId = "speaker-b" });
    fake.FailCreate.Add("speaker-b");
    await host.ApplyProfileAsync(requested);
    state = await host.ReadAsync();
    Check(state.Channels[0].ActiveRenderId == "speaker-a", "an endpoint that cannot open is skipped for another configured fallback");
    fake.FailCreate.Clear();
    fake.ThrowEnumeration = true;
    state = await host.ReadAsync();
    Check(state.LastError is not null && state.Channels[0].ActiveRenderId == "speaker-a", "transient observation failure does not strand commands or kill a running path");
    fake.ThrowEnumeration = false;
    fake.Created.Last().ThrowUpdate = true;
    state = await host.ApplyProfileAsync(Channel(requested, AudioBus.Game, c => c with { RenderEndpointId = "speaker-a", GainDb = -3 }));
    Check(state.Channels[0].Reason.Contains("failed", StringComparison.OrdinalIgnoreCase), "DSP update exception remains visible without terminating worker");
    fake.Created.Last().ThrowUpdate = false;
    state = await host.StopAsync();
    Check(!state.Running && state.Channels.All(c => c.ActiveRenderId is null), "stop closes every owned audio path");
}
Check(fake.Disposed && fake.ThreadIds.Count == 1, "backend and stream lifecycle share one drained MTA owner");
FakeBackend queued = new();
AudioHostController draining = new(backendFactory: () => queued, automaticPolling: false);
await draining.ReadAsync();
Task<HostSnapshot>[] acceptedReads = Enumerable.Range(0, 20).Select(_ => draining.ReadAsync()).ToArray();
Task drain = draining.DisposeAsync().AsTask();
await Task.WhenAll(acceptedReads).WaitAsync(TimeSpan.FromSeconds(3)); await drain.WaitAsync(TimeSpan.FromSeconds(3));
Check(queued.Disposed, "shutdown drains already accepted queued commands before backend disposal");
try { await draining.ReadAsync(); throw new IOException("Expected disposed rejection"); }
catch (ObjectDisposedException) { Check(true, "commands after disposal reject without hanging"); }

var invalid = Channel(enabled, AudioBus.Chat, c => c with { Enabled = true, CaptureEndpointId = "capture-game", RenderEndpointId = "speaker-b" });
try { HostProfileStore.Validate(invalid); throw new IOException("Expected duplicate rejection"); }
catch (ArgumentException) { Check(true, "duplicate capture across enabled channels is rejected before audio"); }
try { HostProfileStore.Validate(enabled with { MasterGainDb = double.NaN }); throw new IOException("Expected invalid scalar rejection"); }
catch (ArgumentException) { Check(true, "nonfinite master gain rejected"); }
try { HostProfileStore.Validate(enabled with { Version = 42 }); throw new IOException("Expected version rejection"); }
catch (ArgumentException) { Check(true, "future incompatible profile version rejected"); }

var common = Channel(enabled, AudioBus.Chat, c => c with { Enabled = true, CaptureEndpointId = "capture-chat", RenderEndpointId = "speaker-a" });
common = common with { ChatMix = 1, MasterGainDb = -3 };
Check(AudioHostController.EffectiveDsp(common, common.Channels[0], "speaker-a").Mute, "ChatMix chat extreme mutes only Game at a shared destination");
Check(!AudioHostController.EffectiveDsp(common, common.Channels[1], "speaker-a").Mute, "ChatMix favored channel remains audible");
Check(!AudioHostController.EffectiveDsp(common, common.Channels[0], "speaker-b").Mute, "ChatMix does not attenuate unrelated destination");
Check(AudioHostController.EffectiveDsp(common with { MasterMute = true }, common.Channels[4], "speaker-a").Mute == common.Channels[4].Mute,
    "playback master mute does not silently mute microphone return");

FakeBackend formatSwitch = new();
formatSwitch.MaximumFrequencyByCapture["capture-game"] = 8000;
await using (AudioHostController host = new(backendFactory: () => formatSwitch, automaticPolling: false))
{
    var lowRate = Channel(enabled, AudioBus.Game, c => c with { Dsp = c.Dsp with { Bands = c.Dsp.Bands.Select(b => b with { FrequencyHz = Math.Min(b.FrequencyHz, 4000) }).ToArray() } });
    await host.ApplyProfileAsync(lowRate); await host.StartAsync();
    var highRate = Channel(enabled, AudioBus.Game, c => c with { CaptureEndpointId = "capture-chat" });
    var state = await host.ApplyProfileAsync(highRate);
    Check(state.Channels[0].ActiveCaptureId == "capture-chat" && formatSwitch.Created[0].Disposed,
        "capture format switch ramps old path using old valid DSP settings");
}
FakeBackend balance = new();
balance.FailCapture.Add("capture-chat");
await using (AudioHostController host = new(backendFactory: () => balance, automaticPolling: false))
{
    await host.ApplyProfileAsync(common); await host.StartAsync();
    Check(!balance.Created.Single().Settings.Mute, "ChatMix remains neutral when its peer did not start");
}

FakeBackend graph = new();
graph.Endpoints.Add(new("cable-in", "CABLE Input", false, true) { TransportId = "same-cable" });
graph.Endpoints.Add(new("cable-out", "CABLE Output", true, true) { TransportId = "same-cable" });
await using (AudioHostController host = new(backendFactory: () => graph, automaticPolling: false))
{
    var loop = Channel(enabled, AudioBus.Game, c => c with { CaptureEndpointId = "cable-out", RenderEndpointId = "cable-in", OutputPriorityIds = [] });
    await host.ApplyProfileAsync(loop);
    var state = await host.StartAsync();
    Check(state.Channels[0].State == "Unavailable" && graph.Created.Count == 0, "same-transport feedback rejected even though endpoint IDs differ");
    loop = Channel(enabled, AudioBus.Game, c => c with { CaptureEndpointId = "cable-out" });
    loop = Channel(loop, AudioBus.Microphone, c => c with { Enabled = true, CaptureEndpointId = "capture-mic", RenderEndpointId = "missing", OutputPriorityIds = ["cable-in"] });
    state = await host.ApplyProfileAsync(loop);
    Check(state.Channels.Where(c => c.Bus is AudioBus.Game or AudioBus.Microphone).All(c => c.ActiveRenderId is null), "cross-channel loop through fallback cable pin rejected");
}

string root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? throw new IOException("TEMP must be set."), "soltex-host-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string path = Path.Combine(root, "profile.json");
    HostProfileStore.Save(path, enabled); var loaded = HostProfileStore.Load(path);
    Check(loaded.Channels[0].CaptureEndpointId == "capture-game", "versioned profile roundtrip preserves opaque IDs");
    File.WriteAllText(path, "{\"Version\":999}");
    FakeBackend stopped = new();
    await using (AudioHostController host = new(path, () => stopped, false))
    {
        var state = await host.ReadAsync();
        Check(!state.Running && state.LastError is not null && stopped.Created.Count == 0, "corrupt saved profile remains stopped with explicit error");
        Check(File.ReadAllText(path).Contains("999", StringComparison.Ordinal), "corrupt profile is preserved until explicit replacement");
    }
    string directoryAsFile = Path.Combine(root, "not-a-file"); Directory.CreateDirectory(directoryAsFile);
    await using (AudioHostController host = new(directoryAsFile, () => new FakeBackend(), false))
    {
        try { await host.ApplyProfileAsync(enabled); throw new IOException("Expected save failure"); }
        catch (UnauthorizedAccessException) { Check(!(await host.ReadAsync()).Profile.Channels[0].Enabled, "failed persistence leaves prior requested graph intact"); }
    }
    await PipeChecks(root);
    using (Process owner = Process.GetCurrentProcess())
    {
        FakeBackend abandoned = new();
        string[] orphanArgs = ["--pipe", "Soltex.Audio.Host.orphan-" + Guid.NewGuid().ToString("N"), "--token", new('a', 64),
            "--profile", Path.Combine(root, "orphan.json"), "--owner-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--owner-start", (owner.StartTime.ToUniversalTime().Ticks - 1).ToString(CultureInfo.InvariantCulture)];
        Check(await AudioHostServer.RunAsync(orphanArgs, () => abandoned) == 0 && abandoned.Disposed && abandoned.Created.Count == 0,
            "owner PID reuse or start identity mismatch drains host without audio");
    }
    AudioHostClient absent = new(root, Path.Combine(root, "missing.exe"));
    try { await absent.ReadAsync(); throw new IOException("Expected missing helper"); }
    catch (IOException) { Check(true, "missing packaged helper reports failure without launching audio"); }
    await absent.DisposeAsync();
    string fakeExe = Path.Combine(AppContext.BaseDirectory, "Soltex.Audio.Host.Tests.exe");
    AudioHostClient unresponsive = new(root, fakeExe);
    await unresponsive.ReadAsync();
    int ownedPid = int.Parse(File.ReadAllText(Path.Combine(root, "fake-child-pid.txt")), CultureInfo.InvariantCulture);
    Check(File.ReadAllText(Path.Combine(root, "fake-child-token.txt")) == AudioHostServer.TokenFromStandardInput + "|64",
        "helper session secret arrives on stdin and never appears in the command line");
    foreach (string bad in new[] { "", "short\n", new string('z', 64) + "\n", new string('a', 70) + "\n" })
    {
        try { await AudioHostServer.ReadTokenAsync(new StringReader(bad), CancellationToken.None); Check(false, "malformed stdin secret must fail closed"); }
        catch (ArgumentException) { }
    }
    Check(await AudioHostServer.ReadTokenAsync(new StringReader(new string('b', 64) + "\r\n"), CancellationToken.None) == new string('b', 64),
        "well-formed stdin secret is accepted and malformed secrets fail closed");
    bool forcedReported = false;
    try { await unresponsive.DisposeAsync(); }
    catch (IOException error) { forcedReported = error.Message.Contains("forced stop", StringComparison.Ordinal); }
    Check(forcedReported, "unresponsive helper shutdown reports forced owned-process fallback");
    bool gone;
    try { using Process candidate = Process.GetProcessById(ownedPid); gone = candidate.HasExited; }
    catch (ArgumentException) { gone = true; }
    Check(gone, "shutdown timeout cannot orphan the owned audio helper");
    await unresponsive.DisposeAsync();
}
finally { Directory.Delete(root, true); }
{
    // Endpoint observation is the helper's dominant idle cost: it must follow notifications, not a timer.
    ProbeClock cacheClock = new();
    EndpointObservationCache cache = new(cacheClock);
    Check(cache.NeedsRead(), "endpoint cache reads once at startup");
    cache.BeginRead(); cache.Completed(notificationsAvailable: true);
    cacheClock.Now += TimeSpan.FromSeconds(30);
    Check(!cache.NeedsRead(), "with endpoint notifications an idle helper does not re-read devices on a timer");
    cache.MarkChanged();
    Check(cache.NeedsRead(), "a Windows endpoint notification schedules one re-read");
    cache.BeginRead(); cache.MarkChanged(); cache.Completed(notificationsAvailable: true);
    Check(cache.NeedsRead(), "a notification that races a read schedules another read instead of being lost");
    cache.BeginRead(); cache.Completed(notificationsAvailable: true);
    cacheClock.Now += EndpointObservationCache.ReconcileInterval;
    Check(cache.NeedsRead() && cache.Reads == 3, "a slow safety reconciliation still runs with notifications available");
    cache.BeginRead(); cache.Completed(notificationsAvailable: false);
    cacheClock.Now += EndpointObservationCache.PollingInterval;
    Check(cache.NeedsRead(), "without endpoint notifications the helper falls back to one-second polling");
    cache.BeginRead(); cache.Failed();
    Check(!cache.NeedsRead(), "a failed read is not retried in a tight loop");
    cacheClock.Now += EndpointObservationCache.PollingInterval;
    Check(cache.NeedsRead(), "a failed read is retried at the polling rate");
}
{
    // Receiving Windows device notifications costs CPU even when every one is ignored (about 16 a second with SteelSeries GG
    // installed). With audio stopped and nobody asking, the helper stops listening and reads afresh on the next request.
    FakeBackend idleBackend = new();
    ProbeClock idleClock = new();
    await using AudioHostController host = new(backendFactory: () => idleBackend, automaticPolling: true, timeProvider: idleClock);
    await host.ReadAsync();
    Check(host.IsObserving && idleBackend.ObservingChanges.Count == 0, "a helper that was just asked keeps observing devices");
    idleClock.Now += AudioHostController.ObservationIdleTimeout + TimeSpan.FromSeconds(1);
    DateTime pauseDeadline = DateTime.UtcNow.AddSeconds(6);
    while (host.IsObserving && DateTime.UtcNow < pauseDeadline) await Task.Delay(50);
    Check(!host.IsObserving && idleBackend.ObservingChanges.SequenceEqual([false]), "after 30 idle seconds with audio stopped the helper stops listening for device changes");
    int enumerations = idleBackend.Enumerations;
    await Task.Delay(TimeSpan.FromMilliseconds(AudioHostController.IdlePollingMilliseconds + 600));
    Check(idleBackend.Enumerations == enumerations, "a paused idle helper neither ticks nor enumerates");
    idleBackend.Endpoints.Add(new("late", "Plugged in while paused", true, true));
    HostSnapshot fresh = await host.ReadAsync();
    Check(host.IsObserving && idleBackend.ObservingChanges.SequenceEqual([false, true]) && fresh.Endpoints.Any(e => e.Id == "late"),
        "the next request resumes observation before reading, so it sees a device that changed meanwhile");
    await host.ApplyProfileAsync(enabled);
    await host.StartAsync();
    idleClock.Now += AudioHostController.ObservationIdleTimeout * 3;
    await Task.Delay(TimeSpan.FromMilliseconds(AudioHostController.ActivePollingMilliseconds * 3));
    Check(host.IsObserving && idleBackend.ObservingChanges.Count == 2, "running audio keeps observing devices so fallback still works");
}
Console.WriteLine($"{checks}/{checks} host checks passed. Fake backend only; no real audio endpoint or settings changed.");

async Task PipeChecks(string root)
{
    string pipeName = "Soltex.Audio.Host.test-" + Guid.NewGuid().ToString("N"), secret = new('a', 64);
    using Process process = Process.GetCurrentProcess();
    string[] args = ["--pipe", pipeName, "--token", secret, "--profile", Path.Combine(root, "pipe-profile.json"), "--owner-pid",
        Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--owner-start", process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)];
    FakeBackend fake = new();
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    Task<int> server = AudioHostServer.RunAsync(args, () => fake, timeout.Token);
    async Task<NamedPipeClientStream> Connect()
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token); return pipe;
    }
    async Task<HostReply> Request(NamedPipeClientStream pipe, string operation, AudioHostProfile? profile = null, string? endpoint = null)
    {
        var request = new HostRequest(1, Guid.NewGuid(), secret, operation, profile) { CaptureEndpointId = endpoint };
        await HostProtocol.WriteAsync(pipe, request, timeout.Token);
        var reply = await HostProtocol.ReadAsync<HostReply>(pipe, timeout.Token);
        Check(reply.Id == request.Id && reply.Version == 1, "IPC response is bound to request identity");
        return reply;
    }
    try
    {
        bool duplicateBackendOpened = false;
        try { await AudioHostServer.RunAsync(args, () => { duplicateBackendOpened = true; return new FakeBackend(); }, timeout.Token); throw new InvalidOperationException("Expected owner rejection"); }
        catch (IOException) { Check(!duplicateBackendOpened, "one user/profile host owner is enforced before creating another backend"); }
        await using (var pipe = await Connect())
        {
            Check((await Request(pipe, "read")).Snapshot?.Running == false, "authenticated pipe starts stopped");
            Check((await Request(pipe, "mic-test", endpoint: "capture-mic")).Snapshot?.MicrophoneTest is { Running: true, Channels: 2 }, "authenticated pipe transports capture-only test command and channel measurements");
            Check((await Request(pipe, "mic-test-stop")).Snapshot?.MicrophoneTest?.Running == false, "pipe microphone stop confirms bounded capture resource closure");
            await Request(pipe, "apply", streamingProfile);
            Check((await Request(pipe, "stream-start")).Snapshot?.StreamMix is { Running: true, PersonalRenderEndpointId: "speaker-a", StreamRenderEndpointId: "speaker-b" }, "pipe stream mode reports confirmed dual destinations");
            await Request(pipe, "stop");
            Check((await Request(pipe, "apply", enabled)).Snapshot?.Profile.Channels[0].Enabled == true, "pipe apply persists requested profile");
            Check((await Request(pipe, "start")).Snapshot?.Channels[0].ActiveRenderId == "speaker-a", "pipe start reaches fake graph owner");
            Check((await Request(pipe, "stop")).Snapshot?.Channels.All(c => c.ActiveRenderId is null) == true, "pipe stop drains fake streams");
            Check((await Request(pipe, "unknown")).Error is not null, "unknown protocol operation receives bounded error");
        }
        // Clients that connect and hang up at once can land before the accept completes ("pipe is being closed").
        // Before the fix that error escaped the accept loop and the helper exited, stopping audio.
        for (int i = 0; i < 40; i++) { using var early = await Connect(); }
        await using (var pipe = await Connect())
            Check((await Request(pipe, "read")).Snapshot is not null && !server.IsCompleted, "repeated immediate client hang-ups do not stop the helper");
        foreach (var bad in new[] { new HostRequest(2, Guid.NewGuid(), secret, "start", null), new HostRequest(1, Guid.NewGuid(), new('b', 64), "start", null) })
        {
            await using var pipe = await Connect();
            await HostProtocol.WriteAsync(pipe, bad, timeout.Token);
            byte[] one = new byte[1]; Check(await pipe.ReadAsync(one, timeout.Token) == 0, "wrong version or token disconnects without mutation");
        }
        await using (var pipe = await Connect())
        {
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, HostProtocol.MaximumMessageBytes + 1);
            await pipe.WriteAsync(prefix, timeout.Token); await pipe.FlushAsync(timeout.Token);
            byte[] one = new byte[1]; Check(await pipe.ReadAsync(one, timeout.Token) == 0, "oversized protocol frame rejected before body allocation");
        }
        await using (var pipe = await Connect())
        { Check((await Request(pipe, "read")).Snapshot?.Running == false, "server reconnects after malformed clients without replaying starts"); await Request(pipe, "shutdown"); }
        Check(await server == 0 && fake.Disposed, "graceful IPC shutdown drains the graph backend");
    }
    finally { timeout.Cancel(); await server; }
}

internal sealed class FakeBackend : IHostAudioBackend
{
    internal List<HostEndpoint> Endpoints { get; set; } = [new("capture-game", "Game input", true, true), new("capture-chat", "Chat input", true, true),
        new("capture-mic", "Microphone", true, true), new("speaker-a", "Same name", false, true), new("speaker-b", "Same name", false, true)];
    internal List<FakePath> Created { get; } = [];
    internal List<FakeProbe> Probes { get; } = [];
    internal List<FakeMix> Mixes { get; } = [];
    public IHostStreamMix CreateStreamMix(WasapiStreamMixConfiguration configuration) { FakeMix mix = new(configuration); Mixes.Add(mix); return mix; }
    public IMicrophoneProbe CreateMicrophoneProbe(string endpointId) { FakeProbe probe = new(endpointId); Probes.Add(probe); return probe; }
    internal List<string> Events { get; } = [];
    internal HashSet<int> ThreadIds { get; } = [];
    internal HashSet<string> FailCreate { get; } = [];
    internal HashSet<string> FailCapture { get; } = [];
    internal Dictionary<string, double> MaximumFrequencyByCapture { get; } = [];
    internal bool ThrowEnumeration { get; set; }
    internal bool Disposed { get; private set; }
    internal List<bool> ObservingChanges { get; } = [];
    internal int Enumerations { get; private set; }
    public void SetObserving(bool observing) => ObservingChanges.Add(observing);
    public IReadOnlyList<HostEndpoint> Enumerate() { ThreadIds.Add(Environment.CurrentManagedThreadId); Enumerations++; if (ThrowEnumeration) throw new IOException("Fixture unplug event"); return Endpoints.ToArray(); }
    public IReadOnlyDictionary<string, float?> ReadInputPeaks(IReadOnlyCollection<string> captureIds) => captureIds.ToDictionary(id => id, _ => (float?).125f);
    public IHostAudioPath Create(string captureId, string renderId, DspSettings settings)
    {
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        if (FailCreate.Contains(renderId) || FailCapture.Contains(captureId)) throw new IOException("Fixture cannot open endpoint");
        FakePath path = new(this, renderId, settings, MaximumFrequencyByCapture.GetValueOrDefault(captureId, 96000)); Created.Add(path); return path;
    }
    public void Dispose() { ThreadIds.Add(Environment.CurrentManagedThreadId); Disposed = true; }
}
internal sealed class FakePath(FakeBackend owner, string output, DspSettings settings, double maximumFrequency = 96000) : IHostAudioPath
{
    internal DspSettings FirstSettings { get; } = settings;
    internal DspSettings Settings { get; private set; } = settings;
    internal bool Disposed { get; private set; }
    internal bool ThrowUpdate { get; set; }
    internal int Updates { get; private set; }
    private bool running;
    public BridgeStatus Status => new(running, 0, 0, 0, 0, null);
    public void Start() { owner.ThreadIds.Add(Environment.CurrentManagedThreadId); owner.Events.Add("start:" + output); running = true; }
    public void Update(DspSettings value) { if (ThrowUpdate || value.Bands.Any(b => b.FrequencyHz >= maximumFrequency)) throw new IOException("Fixture invalid actual format"); Settings = value; Updates++; }
    public void Dispose() { owner.ThreadIds.Add(Environment.CurrentManagedThreadId); owner.Events.Add("dispose:" + output); running = false; Disposed = true; }
}

internal sealed class ProbeClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class FakeProbe(string id) : IMicrophoneProbe
{
    private bool running;
    internal bool Disposed { get; private set; }
    public MicrophoneProbeStatus Status => new(id, running, 96000, 2, 960, [.001f, .25f], [.0005f, .1f], null);
    public void Start() => running = true;
    public void Dispose() { running = false; Disposed = true; }
}


internal sealed class FakeMix(WasapiStreamMixConfiguration configuration) : IHostStreamMix
{
    internal WasapiStreamMixConfiguration Configuration { get; } = configuration;
    internal List<(string Id, DspSettings Dsp, StreamMixSendSettings Personal, StreamMixSendSettings Stream)> Updates { get; } = [];
    internal bool Disposed { get; private set; }
    internal string? Failure { get; set; }
    private bool running;
    public WasapiStreamMixStatus Status => new(running && Failure is null, Failure, .1f, .2f, 0, 0, Configuration.Sources.Select(s => new StreamMixSourceStatus(s.Id, 123, new(0,0,0), new(0,0,0)) { CaptureEndpointId = s.CaptureEndpointId }).ToArray())
    { PersonalRenderEndpointId = Configuration.PersonalRenderEndpointId, StreamRenderEndpointId = Configuration.StreamRenderEndpointId };
    public void Start() => running = true;
    public void UpdateSource(string id, DspSettings dsp, StreamMixSendSettings personal, StreamMixSendSettings stream) => Updates.Add((id,dsp,personal,stream));
    public void Dispose() { running = false; Disposed = true; }
}
