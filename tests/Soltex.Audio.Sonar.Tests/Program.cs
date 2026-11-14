using System.Text.Json;
using Soltex.Audio.Sonar;

var roles = new[] { "game", "chatRender", "media", "aux", "chatCapture" };
var buses = new[] { "game", "chat", "media", "aux", "mic" };
var devices = JsonSerializer.Serialize(roles.Select((role, i) => new
{
    id = "virtual-" + i, friendlyName = "Duplicate friendly name", dataFlow = i == 4 ? "capture" : "render",
    role, state = "active", isVad = true
}));
// Sonar exposes a microphone render endpoint with the same role as its capture endpoint.
devices = "[{\"id\":\"mic-monitor\",\"friendlyName\":\"Duplicate friendly name\",\"dataFlow\":\"render\",\"role\":\"chatCapture\",\"state\":\"active\",\"isVad\":true}," + devices[1..];
var routes = JsonSerializer.Serialize(buses.Select(id => new { id, deviceId = "physical", isRunning = true }));
var volumes = JsonSerializer.Serialize(new
{
    masters = new { classic = new { volume = 0.73, muted = true } },
    devices = roles.ToDictionary(role => role, _ => new { classic = new { volume = 0.41, muted = false } })
});
const string applications = """
[{"deviceId":"virtual-0","dataFlow":"render","audioSessions":[
{"id":"session-a","processId":42,"displayName":"Example","processName":"example.exe","state":"active","isSystemSound":false},
{"id":"session-b","processId":42,"displayName":"","processName":"example.exe","state":"inactive","isSystemSound":false,"routingErrorDetected":true}]}]
""";
var snapshot = SonarProtocol.ParseSnapshot(devices, routes, volumes, applications);
Check(snapshot.IsReady && snapshot.Channels.Count == 6, "Ready graph contains five buses and master");
Check(snapshot.Devices.Select(d => d.Id).Distinct().Count() == 6, "Duplicate names preserve endpoint identities");
Check(snapshot.Channels.Single(c => c.Id == "chat").Role == "chatRender", "Chat route and volume roles differ");
Check(snapshot.Channels.Single(c => c.Id == "mic").VirtualDeviceId == "virtual-4", "Capture virtual endpoint is mapped");
Check(snapshot.Channels.Single(c => c.Id == "master").Muted, "Master mute is independent of bus mute");
Check(snapshot.Applications.Count == 2 && snapshot.Applications[1].Name == "example.exe" && snapshot.Applications[1].RoutingErrorDetected,
    "Multiple process sessions and fallback name survive parsing");
Check(!SonarProtocol.ParseSnapshot(devices.Replace("active", "disabled", StringComparison.Ordinal), routes, volumes, "[]").IsReady,
    "Disabled virtual endpoints cannot report ready");
Check(!SonarProtocol.ParseSnapshot(devices, routes.Replace("true", "false", StringComparison.Ordinal), volumes, "[]").IsReady,
    "Stopped redirections cannot report ready");
Reject(() => SonarProtocol.ParseSnapshot(devices, "[]", volumes, "[]"), "Missing routes fail closed");
Reject(() => SonarProtocol.ParseSnapshot(devices, routes, volumes.Replace("0.41", "1.1", StringComparison.Ordinal), "[]"), "Out-of-range gain rejected");
Reject(() => SonarProtocol.ParseSnapshot("{}", routes, volumes, "[]"), "Wrong endpoint shape rejected");
Check(SonarProtocol.ParsePolicyDeviceId("") == "", "Unset routing policy is an empty plain response");
Check(SonarProtocol.ParsePolicyDeviceId("{endpoint}") == "{endpoint}", "Plain endpoint identity remains opaque");
Check(SonarProtocol.ParsePolicyDeviceId("\"{endpoint}\"") == "{endpoint}", "JSON string policy supported");
Console.WriteLine("PASS: 14 protocol assertions; no live mutations.");

if (args.Length > 0 && args[0] == "--live")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var client = await SonarClient.DiscoverAsync(timeout.Token);
    var actual = await client.GetSnapshotAsync(timeout.Token);
    if (args.Length > 1)
        await File.WriteAllTextAsync(Path.GetFullPath(args[1]), JsonSerializer.Serialize(actual), timeout.Token);
    Console.WriteLine($"LIVE READ ONLY: ready={actual.IsReady}; channels={actual.Channels.Count}; endpoints={actual.Devices.Count}; sessions={actual.Applications.Count}");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try { await client.GetSnapshotAsync(canceled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
    catch (OperationCanceledException) { Console.WriteLine("PASS: snapshot cancellation."); }
    using var eventsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    var events = 0;
    try
    {
        await foreach (var notification in client.WatchEventsAsync(eventsTimeout.Token))
        {
            Check(notification.Payload.ValueKind == JsonValueKind.Object, "Event payload is an object");
            events++;
        }
    }
    catch (OperationCanceledException) when (eventsTimeout.IsCancellationRequested)
    {
        Console.WriteLine($"PASS: bounded read-only event observation canceled; received={events}.");
    }
    client.Dispose();
    try { await client.GetSnapshotAsync(); throw new InvalidOperationException("Disposed client accepted request."); }
    catch (ObjectDisposedException) { Console.WriteLine("PASS: disposed client rejected request."); }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { return; }
    throw new InvalidOperationException(message);
}
