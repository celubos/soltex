using System.Diagnostics;
using Soltex.Audio;

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine("Default: deterministic tests with no audio device access. --live-read-only adds strict endpoint, meter and native-worker lifecycle probes; requires actual Windows audio devices.");
    return 0;
}
if (args.Length > 1 || args.Length == 1 && args[0] != "--live-read-only")
{
    Console.Error.WriteLine("Use no arguments for deterministic tests, or --live-read-only for explicit hardware observation.");
    return 2;
}
bool liveReadOnly = args.Length == 1;
List<(string Name, Func<Task> Run)> tests =
[
    ("Preferred output follows saved identity, not matching name", () => Check(() =>
    {
        OutputSelection result = OutputDevicePolicy.Select(new(["b", "a"], []), [Endpoint("a"), Endpoint("b")]);
        Equal("b", result.EndpointId); Equal(OutputSelectionReason.Preferred, result.Reason);
    })),
    ("Disconnect falls back and reconnect returns automatically", () => Check(() =>
    {
        OutputDevicePreferences preferences = new(["a", "b"], []);
        Equal("b", OutputDevicePolicy.Select(preferences, [Endpoint("a", AudioEndpointState.Unplugged), Endpoint("b")], "a").EndpointId);
        Equal("a", OutputDevicePolicy.Select(preferences, [Endpoint("a"), Endpoint("b")], "b").EndpointId);
        Equal(2, preferences.PriorityEndpointIds.Count);
    })),
    ("Return disabled retains available lower-priority output", () => Check(() =>
        Equal("b", OutputDevicePolicy.Select(new(["a", "b"], [], ReturnToPreferred: false), [Endpoint("a"), Endpoint("b")], "b").EndpointId))),
    ("Manual selection overrides automatic priority and survives absence", () => Check(() =>
    {
        OutputDevicePreferences preferences = new(["a", "b"], [], "b");
        Equal("b", OutputDevicePolicy.Select(preferences, [Endpoint("a"), Endpoint("b")]).EndpointId);
        OutputSelection absent = OutputDevicePolicy.Select(preferences, [Endpoint("a")]);
        Equal("a", absent.EndpointId); Equal("b", absent.UnavailableManualEndpointId);
        Equal("b", OutputDevicePolicy.Select(preferences, [Endpoint("a"), Endpoint("b")], "a").EndpointId);
    })),
    ("Exclusions override manual pin and priorities", () => Check(() =>
        Equal("b", OutputDevicePolicy.Select(new(["a", "b"], ["a"], "a"), [Endpoint("a"), Endpoint("b")]).EndpointId))),
    ("New devices, capture devices and unavailable outputs are never silently chosen", () => Check(() =>
    {
        OutputSelection result = OutputDevicePolicy.Select(new(["a", "capture"], []),
            [Endpoint("a", AudioEndpointState.Disabled), Endpoint("new"), Endpoint("capture") with { Direction = AudioEndpointDirection.Capture }]);
        Equal<string?>(null, result.EndpointId); Equal(OutputSelectionReason.NoAvailableDevice, result.Reason);
    })),
    ("Empty preference list is explicit no output", () => Check(() =>
        Equal<string?>(null, OutputDevicePolicy.Select(new([], []), [Endpoint("new")]).EndpointId))),
    ("Opaque IDs compare consistently independent of name and case", () => Check(() =>
        Equal("DEVICE", OutputDevicePolicy.Select(new(["DEVICE"], []), [Endpoint("device")]).EndpointId))),
    ("Volume recovery preserves later changes and rejects unknown state", () => Check(() =>
    {
        Equal(AudioRestoreDecision.Restore, AudioRestorePolicy.Volume(.7, .4, .4));
        Equal(AudioRestoreDecision.AlreadyRestored, AudioRestorePolicy.Volume(.7, .4, .7));
        Equal(AudioRestoreDecision.CurrentValueChanged, AudioRestorePolicy.Volume(.7, .4, .4001));
        Equal(AudioRestoreDecision.NoOwnedChange, AudioRestorePolicy.Volume(.7, .7, .4));
        Equal(AudioRestoreDecision.Unavailable, AudioRestorePolicy.Volume(null, .4, .4));
        Equal(AudioRestoreDecision.Unavailable, AudioRestorePolicy.Volume(.7, double.NaN, .4));
    })),
    ("Mute recovery only restores a confirmed owned delta", () => Check(() =>
    {
        Equal(AudioRestoreDecision.Restore, AudioRestorePolicy.Mute(false, true, true));
        Equal(AudioRestoreDecision.AlreadyRestored, AudioRestorePolicy.Mute(false, true, false));
        Equal(AudioRestoreDecision.NoOwnedChange, AudioRestorePolicy.Mute(true, true, false));
        Equal(AudioRestoreDecision.Unavailable, AudioRestorePolicy.Mute(false, true, null));
    })),
    ("Default recovery never overwrites a later selection", () => Check(() =>
    {
        Equal(AudioRestoreDecision.Restore, AudioRestorePolicy.Default("old", "owned", "owned"));
        Equal(AudioRestoreDecision.CurrentValueChanged, AudioRestorePolicy.Default("old", "owned", "new"));
        Equal(AudioRestoreDecision.AlreadyRestored, AudioRestorePolicy.Default("old", "owned", "old"));
        Equal(AudioRestoreDecision.NoOwnedChange, AudioRestorePolicy.Default("old", "old", "new"));
        Equal(AudioRestoreDecision.Unavailable, AudioRestorePolicy.Default(null, "owned", "owned"));
    })),
    ("Legacy positional construction and volume presentation remain compatible", () => Check(() =>
    {
        AudioEndpoint endpoint = new("name", AudioEndpointDirection.Render, AudioEndpointState.Active, true, .42, false);
        Equal(42d, endpoint.VolumePercent); Equal("name", endpoint.FullName); Equal("", endpoint.EndpointId);
    })),
    ("Meter cache bounds ownership and evicts least-recently-used endpoints", MeterCacheOwnership)
];
if (liveReadOnly)
{
    tests.Add(("Live worker reads stable identities, all roles, real meter and rejects invalid controls without writes", LiveReadOnly));
    tests.Add(("Meter reuse, targeted notification invalidation and failed-device retry are bounded", LiveMeterCache));
    tests.Add(("Worker disposal drains pending reads and rejects new work", Lifetime));
}
int failed = 0;
foreach ((string name, Func<Task> run) in tests)
{
    Stopwatch elapsed = Stopwatch.StartNew();
    try { await run(); Console.WriteLine($"PASS {name} ({elapsed.Elapsed.TotalMilliseconds:F1} ms)"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL {name}: {exception}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed. " + (liveReadOnly
    ? "Live probes were read-only; no control success or signal-path proof is claimed."
    : "Deterministic mode: no audio device access. Three strict hardware/native-worker probes require --live-read-only."));
return failed == 0 ? 0 : 1;

static Task Check(Action check) { check(); return Task.CompletedTask; }
static AudioEndpoint Endpoint(string id, AudioEndpointState state = AudioEndpointState.Active) =>
    new("identical name", AudioEndpointDirection.Render, state, false, .7, false) { EndpointId = id };
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task LiveReadOnly()
{
    await using AudioDeviceService service = new();
    AudioDeviceSnapshot snapshot = await service.GetSnapshotAsync();
    Assert(snapshot.State != AudioObservationState.Unavailable, string.Join("; ", snapshot.Errors));
    Equal(6, snapshot.Defaults.Count);
    Equal(6, snapshot.Defaults.Select(item => (item.Direction, item.Role)).Distinct().Count());
    Assert(snapshot.NotificationsAvailable, "Notification registration failed.");
    Equal(snapshot.Endpoints.Count, snapshot.Endpoints.Select(endpoint => endpoint.EndpointId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    foreach (AudioEndpoint endpoint in snapshot.Endpoints)
    {
        Assert(endpoint.EndpointId.Length > 0 && endpoint.FullName.Length > 0, "Missing identity/full name.");
        Assert(!endpoint.FullName.Any(char.IsControl), "Name contains controls.");
        foreach (AudioDeviceRole role in endpoint.DefaultRoles)
            Equal(endpoint.EndpointId, snapshot.Defaults.Single(item => item.Direction == endpoint.Direction && item.Role == role).EndpointId);
    }
    AudioEndpoint? active = snapshot.Endpoints.FirstOrDefault(endpoint => endpoint.State == AudioEndpointState.Active);
    Assert(active is not null, "No active endpoint available for the read-only probe.");
    AudioControlResult read = await service.ReadEndpointAsync(active!.EndpointId);
    Assert(read.Succeeded, read.Error ?? "Read failed.");
    foreach (double value in new[] { double.NaN, double.PositiveInfinity, -.001, 1.001 })
        Assert(!(await service.SetVolumeAsync(active.EndpointId, value)).Succeeded, "Invalid value accepted.");
    Assert(!(await service.SetMuteAsync("soltex-test-missing-endpoint", true)).Succeeded, "Missing endpoint accepted.");
    AudioPeakResult peak = await service.ReadPeakAsync(active.EndpointId);
    Assert(peak.Peak is >= 0 and <= 1 || peak.Error is not null, "Meter supplied neither observation nor reason.");
    Console.WriteLine($"OBSERVED endpoints={snapshot.Endpoints.Count}, notifications={snapshot.NotificationsAvailable}, peak={peak.Peak?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}; endpoint={active.EndpointId}");
    AudioDefaultEndpoint roleDefault = snapshot.Defaults.First(item => item.EndpointId is not null);
    AudioDefaultRestoreResult dry = await service.RestoreDefaultAsync(roleDefault.Direction, roleDefault.Role, roleDefault.EndpointId!, roleDefault.EndpointId!);
    Equal(AudioRestoreDecision.NoOwnedChange, dry.Decision); Assert(!dry.Applied, "Dry-run wrote default.");
}
static async Task Lifetime()
{
    AudioDeviceService service = new();
    Task<AudioDeviceSnapshot> pending = service.GetSnapshotAsync();
    await service.DisposeAsync();
    _ = await pending;
    await service.DisposeAsync();
    try { _ = await service.GetSnapshotAsync(); throw new InvalidOperationException("Disposed service accepted work."); }
    catch (ObjectDisposedException) { }
}

static Task MeterCacheOwnership()
{
    using OwnedEndpointCache<OwnedProbe> cache = new(16);
    var probes = Enumerable.Range(0, 17).Select(_ => new OwnedProbe()).ToArray();
    for (int i = 0; i < 16; i++) cache.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture), probes[i]);
    Assert(cache.TryGet("0", out _), "Recently used endpoint absent.");
    cache.Add("16", probes[16]);
    Equal(16, cache.Count); Equal(1, probes[1].Disposals); Equal(0, probes[0].Disposals);
    Assert(!cache.TryGet("1", out _), "Least recently used endpoint retained past capacity.");
    var replacement = new OwnedProbe(); cache.Add("0", replacement);
    Equal(1, probes[0].Disposals); Equal(0, replacement.Disposals);
    cache.Remove("0"); Equal(1, replacement.Disposals);
    cache.Clear(); cache.Clear();
    Assert(probes.All(probe => probe.Disposals == 1), "Resources were leaked or disposed more than once.");
    Equal(0, cache.Count);
    return Task.CompletedTask;
}

static async Task LiveMeterCache()
{
    await using AudioDeviceService service = new();
    var snapshot = await service.GetSnapshotAsync();
    AudioEndpoint? selected = null;
    foreach (var endpoint in snapshot.Endpoints.Where(endpoint => endpoint.State == AudioEndpointState.Active))
    {
        if ((await service.ReadPeakAsync(endpoint.EndpointId)).Peak is not null) { selected = endpoint; break; }
    }
    Assert(selected is not null, "No active endpoint supports the read-only meter cache probe.");
    string id = selected!.EndpointId;
    var before = await service.ReadMeterDiagnosticsAsync();
    for (int i = 0; i < 32; i++) Assert((await service.ReadPeakAsync(id)).Peak is >= 0 and <= 1, "Cached native read failed.");
    var reused = await service.ReadMeterDiagnosticsAsync();
    Equal(before.ConnectionAttempts, reused.ConnectionAttempts);
    Assert(reused.CacheHits >= before.CacheHits + 32, "Native meter was not reused.");
    // Inject the same observed vendor-property callback; this changes no Windows state.
    service.QueueRefresh(id, "property", new PropertyKey { FormatId = new("74e4357a-7959-47a4-a8b2-feb2ffd48427"), PropertyId = 2 });
    _ = await service.ReadPeakAsync(id);
    Equal(reused.ConnectionAttempts, (await service.ReadMeterDiagnosticsAsync()).ConnectionAttempts);
    Assert(AudioDeviceService.RelevantProperty(CoreAudio.FriendlyNameKey), "Friendly-name changes must still refresh inventory.");
    Assert(AudioDeviceService.RelevantProperty(new PropertyKey { FormatId = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), PropertyId = 0 }), "Device-format changes must invalidate the meter.");
    service.QueueRefresh("soltex-test-other-endpoint", "removed", null);
    _ = await service.ReadPeakAsync(id);
    Equal(reused.ConnectionAttempts, (await service.ReadMeterDiagnosticsAsync()).ConnectionAttempts);
    service.QueueRefresh(id, "state", null);
    _ = await service.ReadPeakAsync(id);
    Equal(reused.ConnectionAttempts + 1, (await service.ReadMeterDiagnosticsAsync()).ConnectionAttempts);
    var failureBefore = await service.ReadMeterDiagnosticsAsync();
    for (int i = 0; i < 16; i++) Assert((await service.ReadPeakAsync("soltex-test-missing-meter")).Peak is null, "Missing device produced a meter.");
    Equal(failureBefore.ConnectionAttempts + 1, (await service.ReadMeterDiagnosticsAsync()).ConnectionAttempts);
    await Task.Delay(1100);
    _ = await service.ReadPeakAsync("soltex-test-missing-meter");
    Equal(failureBefore.ConnectionAttempts + 2, (await service.ReadMeterDiagnosticsAsync()).ConnectionAttempts);
    var final = await service.ReadMeterDiagnosticsAsync();
    Assert(final.CachedEntries <= 16, "Selected endpoint cache exceeded its bound.");
    var batch = await service.ReadPeaksAsync(new[] { id, id, "soltex-test-missing-meter" });
    Equal(2, batch.Count);
    Assert(batch.Single(reading => reading.EndpointId == id).Peak is >= 0 and <= 1, "Batched real meter observation missing.");
    Assert(batch.Single(reading => reading.EndpointId == "soltex-test-missing-meter").Peak is null, "Batched missing device was reported as a signal.");
    try { _ = service.ReadPeaksAsync(Enumerable.Range(0, 17).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()); throw new InvalidOperationException("Oversized meter frame was admitted."); }
    catch (ArgumentOutOfRangeException) { }
    Console.WriteLine($"METER_CACHE entries={final.CachedEntries} connections={final.ConnectionAttempts} activations={final.SuccessfulActivations} hits={final.CacheHits}");
}

internal sealed class OwnedProbe : IDisposable
{
    public int Disposals { get; private set; }
    public void Dispose() => Disposals++;
}
