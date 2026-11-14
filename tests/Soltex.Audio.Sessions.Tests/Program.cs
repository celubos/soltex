using System.Text.Json;
using Soltex.Audio.Sessions;
using Soltex.Audio.Sessions.Tests;

if (args.Length > 0 && args[0] == "--record-child")
{
    Environment.ExitCode = await RecorderClient.RunChildAsync(args);
    return;
}

if (args.Length > 0 && args[0] == "--live-own-session")
{
    Environment.ExitCode = await OwnSessionRun.RunAsync(args);
    return;
}

if (args.Length > 0 && args[0] != "--live-read-only") throw new ArgumentException("Usage: [--live-read-only [snapshot.json]]");
int passed = 0;
Soltex.Audio.Transport.Tests.SignalMeasurement.SelfTest();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}"); passed++;
}

DateTimeOffset started = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
AudioSessionKey key = new("endpoint", "instance", 123, started);
AudioSessionInfo active = new(key, "probe", "probe.exe", AudioSessionState.Active, .5, false, .1, false, true);
RecorderReply recorder = new("ready", 124, started, "digest", key with { ProcessId = 124 }, 1, false, null, null);
RecorderReply measured = recorder with { Kind = "measurement", Amplitude = .001 };
Check(RecorderClient.IsIndependentMeasurement(recorder, measured), "independent unity recorder measurement accepted");
Check(!RecorderClient.IsIndependentMeasurement(recorder, measured with { Volume = .5 }), "attenuated recorder rejected instead of normalizing its result");
Check(!RecorderClient.IsIndependentMeasurement(recorder, measured with { SessionKey = key }), "measurement from controlled render session rejected");
Check(Math.Abs(Soltex.Audio.Transport.Tests.SignalMeasurement.Decibels((.002 * .25) / (.002 * .5)) + 6.020599913) < .000001 &&
    Math.Abs(Soltex.Audio.Transport.Tests.SignalMeasurement.Decibels((.002 * .25 * .25) / (.002 * .5 * .5)) + 12.041199826) < .000001,
    "one gain stage is -6.02 dB; shared renderer-recorder double attenuation is -12.04 dB");
Check(SessionPolicy.Reject(key, active) is null, "verified session permits explicit control");
Check(SessionPolicy.Reject(key, active with { Key = key with { ProcessStartTimeUtc = started.AddSeconds(1) } }) is not null, "reused PID rejected");
Check(SessionPolicy.Reject(key, active with { Key = key with { SessionInstanceId = "new instance" } }) is not null, "reopened session rejected");
Check(SessionPolicy.Reject(key, active with { Key = key with { EndpointId = "new device" } }) is not null, "moved endpoint rejected");
Check(SessionPolicy.Reject(key, active with { IsSystemSounds = true }) is not null, "system sounds rejected");
Check(SessionPolicy.Reject(key, active with { CanControl = false }) is not null, "multi-process or unverifiable session rejected");
Check(SessionPolicy.Reject(key, active with { State = AudioSessionState.Expired }) is not null, "expired session rejected");
Check(SessionPolicy.Reject(key with { ProcessStartTimeUtc = null }, active) is not null, "missing identity rejected");
foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.01, 1.01 })
{
    bool rejected = false;
    try { SessionPolicy.ValidateVolume(value); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, $"invalid scalar {value} rejected");
}
Check(SessionPolicy.Classify("SteelSeries Sonar - Gaming") == AudioEndpointClassification.KnownVirtual &&
    SessionPolicy.Classify("Speakers USB2.0") == AudioEndpointClassification.Unknown, "classification never invents physical proof");

FakeBackend fake = new(active);
AudioSessionService service = new(() => fake);
await service.GetSnapshotAsync();
Check(fake.Apartment == ApartmentState.MTA, "backend owned by MTA worker");
using (CancellationTokenSource canceled = new())
{
    canceled.Cancel();
    bool wasCanceled = false;
    try { await service.SetSessionMuteAsync(key, true, canceled.Token); } catch (OperationCanceledException) { wasCanceled = true; }
    Check(wasCanceled && fake.Writes == 0, "cancellation before command issues no write");
}
using (CancellationTokenSource duringWrite = new())
{
    fake.DuringWrite = duringWrite.Cancel;
    AudioSessionControlResult result = await service.SetSessionMuteAsync(key, true, duringWrite.Token);
    Check(result.Applied && result.WriteIssued && result.ConfirmedState?.Muted == true, "cancellation after command preserves confirmation");
}
fake.DuringWrite = null;
await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetSnapshotAsync()));
Check(fake.MaximumConcurrent == 1, "parallel callers serialize backend access");
await service.DisposeAsync(); await service.DisposeAsync();
Check(fake.Disposed && fake.DisposeThread == fake.OperationThread, "disposal joins owning worker and is idempotent");
bool disposedRejected = false;
try { await service.GetSnapshotAsync(); } catch (ObjectDisposedException) { disposedRejected = true; }
Check(disposedRejected, "post-disposal work rejected");

await using (AudioSessionService failed = new(() => throw new InvalidOperationException("fixture unavailable")))
{
    AudioSessionSnapshot snapshot = await failed.GetSnapshotAsync();
    Check(snapshot.Endpoints.Count == 0 && snapshot.Errors.Single() == "fixture unavailable", "startup failure remains observable");
}

if (args.Length > 0)
{
    await ComCoexistence.CheckAsync();
    Check(true, "Core endpoint/session and NAudio enumerators coexist in both activation/disposal orders");
    await using AudioSessionService live = new();
    AudioSessionSnapshot snapshot = await live.GetSnapshotAsync();
    SessionDiagnostics first = await live.GetDiagnosticsAsync();
    for (int frame = 0; frame < 5; frame++) { await live.GetSnapshotAsync(); await Task.Delay(100); }
    SessionDiagnostics last = await live.GetDiagnosticsAsync();
    Check(last.Discoveries == first.Discoveries, "five meter frames reuse discovered COM leases");
    Check(last.EndpointLeases <= 64 && last.SessionLeases <= 256, "live COM lease counts remain bounded");
    Check(snapshot.Endpoints.Select(endpoint => endpoint.Id).Distinct().Count() == snapshot.Endpoints.Count, "live endpoint identities unique");
    Check(snapshot.Endpoints.SelectMany(endpoint => endpoint.Sessions).All(session =>
        session.Volume is null or >= 0 and <= 1 && session.Peak is null or >= 0 and <= 1), "live values bounded without fabricated peaks");
    if (args.Length > 1) await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    // No endpoint/session presence assertion: an audio-less host is a valid observable state.
    Console.WriteLine($"READ ONLY: {snapshot.Endpoints.Count} endpoints, {snapshot.Endpoints.Sum(endpoint => endpoint.Sessions.Count)} sessions, {snapshot.Errors.Count} errors");
}
Console.WriteLine($"{passed}/{passed} passed. No live audio writes.");

sealed class FakeBackend(AudioSessionInfo session) : ISessionBackend
{
    private AudioSessionInfo _session = session;
    private int _concurrent;
    internal int MaximumConcurrent { get; private set; }
    internal int OperationThread { get; private set; }
    internal int DisposeThread { get; private set; }
    internal ApartmentState Apartment { get; private set; }
    internal bool Disposed { get; private set; }
    internal int Writes { get; private set; }
    internal Action? DuringWrite { get; set; }
    public AudioSessionSnapshot Capture()
    {
        OperationThread = Environment.CurrentManagedThreadId; Apartment = Thread.CurrentThread.GetApartmentState();
        MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _concurrent));
        try { return new(DateTimeOffset.UtcNow, [], []); }
        finally { Interlocked.Decrement(ref _concurrent); }
    }
    public AudioSessionControlResult Control(AudioSessionKey key, double? volume, bool? mute)
    {
        string? error = SessionPolicy.Reject(key, _session);
        if (error is not null) return new(false, false, _session, error);
        Writes++; DuringWrite?.Invoke();
        _session = _session with { Volume = volume ?? _session.Volume, Muted = mute ?? _session.Muted };
        return new(true, true, _session, null);
    }
    public void Dispose() { Disposed = true; DisposeThread = Environment.CurrentManagedThreadId; }
}
