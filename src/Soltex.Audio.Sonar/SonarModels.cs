using System.Text.Json;

namespace Soltex.Audio.Sonar;

public sealed record SonarDevice(string Id, string FriendlyName, string DataFlow, string Role, string State, bool IsVad);

public sealed record SonarChannel(string Id, string Name, double Volume, bool Muted, string? DeviceId, bool IsRunning, string Role)
{
    public string? VirtualDeviceId { get; init; }
}

public sealed record SonarApplication(uint ProcessId, string Name, string DeviceId, string DataFlow)
{
    public string SessionId { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsSystemSound { get; init; }
    public bool RoutingErrorDetected { get; init; }
}

public sealed record SonarSnapshot(IReadOnlyList<SonarDevice> Devices, IReadOnlyList<SonarChannel> Channels, IReadOnlyList<SonarApplication> Applications)
{
    public DateTimeOffset Timestamp { get; init; }
    public Uri? Endpoint { get; init; }
    public bool IsReady => Channels.Count(c => c.Id != "master") == 5 &&
        Channels.Where(c => c.Id != "master").All(c => c.IsRunning &&
            Devices.Any(d => d.Id == c.VirtualDeviceId && d.IsVad && d.State == "active"));
}

/// <summary>Policy confirmation does not prove that a running application moved its active stream.</summary>
public sealed record SonarRoutingResult(string RequestedDeviceId, string? ConfirmedPolicyDeviceId, bool PolicyConfirmed, bool ActiveSessionsMatch, SonarSnapshot Snapshot);

/// <summary>Sonar state notification. Signal peaks are obtained separately from Windows Core Audio.</summary>
public sealed record SonarEvent(DateTimeOffset Timestamp, JsonElement Payload);

public sealed class SonarUnavailableException(string message, Exception? innerException = null) : IOException(message, innerException);

public sealed class SonarCommandException(string message) : IOException(message);

/// <summary>Parses the observed Sonar 1.106 local protocol without treating requested values as confirmed state.</summary>
public static class SonarProtocol
{
    /// <summary>The routing controller returns a plain string by default, including empty for an unset policy.</summary>
    public static string? ParsePolicyDeviceId(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = response.Trim();
        if (!text.StartsWith('"')) return text;
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetString();
    }

    private static readonly (string Id, string Name, string Role)[] Buses =
    [
        ("game", "Game", "game"), ("chat", "Chat", "chatRender"),
        ("media", "Media", "media"), ("aux", "Aux", "aux"), ("mic", "Mic", "chatCapture")
    ];

    public static SonarSnapshot ParseSnapshot(string devicesJson, string redirectionsJson, string volumesJson, string applicationsJson)
    {
        using var deviceDocument = JsonDocument.Parse(devicesJson);
        using var routeDocument = JsonDocument.Parse(redirectionsJson);
        using var volumeDocument = JsonDocument.Parse(volumesJson);
        using var appDocument = JsonDocument.Parse(applicationsJson);
        var devices = deviceDocument.RootElement.EnumerateArray().Select(d => new SonarDevice(
            RequiredString(d, "id"), RequiredString(d, "friendlyName"), RequiredString(d, "dataFlow"),
            RequiredString(d, "role"), RequiredString(d, "state"), d.GetProperty("isVad").GetBoolean())).ToArray();
        var routes = routeDocument.RootElement.EnumerateArray().ToDictionary(r => RequiredString(r, "id"), StringComparer.OrdinalIgnoreCase);
        var volumes = volumeDocument.RootElement;
        var master = volumes.GetProperty("masters").GetProperty("classic");
        var channels = new List<SonarChannel>
        {
            new("master", "Master", ReadVolume(master), master.GetProperty("muted").GetBoolean(), null,
                routes.Values.Where(r => RequiredString(r, "id") != "mic").All(r => r.GetProperty("isRunning").GetBoolean()), "Master")
        };
        foreach (var bus in Buses)
        {
            var volume = volumes.GetProperty("devices").GetProperty(bus.Role).GetProperty("classic");
            if (!routes.TryGetValue(bus.Id, out var route)) throw new JsonException($"Sonar omitted the {bus.Id} redirection.");
            channels.Add(new(bus.Id, bus.Name, ReadVolume(volume), volume.GetProperty("muted").GetBoolean(),
                OptionalString(route, "deviceId"), route.GetProperty("isRunning").GetBoolean(), bus.Role)
            {
                VirtualDeviceId = devices.FirstOrDefault(d => d.IsVad && d.DataFlow == (bus.Id == "mic" ? "capture" : "render") &&
                    d.Role.Equals(bus.Role, StringComparison.OrdinalIgnoreCase))?.Id
            });
        }
        var applications = new List<SonarApplication>();
        foreach (var group in appDocument.RootElement.EnumerateArray())
        {
            var deviceId = RequiredString(group, "deviceId");
            var flow = RequiredString(group, "dataFlow");
            foreach (var app in group.GetProperty("audioSessions").EnumerateArray())
            {
                var name = OptionalString(app, "displayName");
                applications.Add(new(app.GetProperty("processId").GetUInt32(),
                    string.IsNullOrWhiteSpace(name) ? RequiredString(app, "processName") : name, deviceId, flow)
                {
                    SessionId = RequiredString(app, "id"), State = RequiredString(app, "state"),
                    IsSystemSound = app.GetProperty("isSystemSound").GetBoolean(),
                    RoutingErrorDetected = app.TryGetProperty("routingErrorDetected", out var error) && error.GetBoolean()
                });
            }
        }
        return new(devices, channels, applications) { Timestamp = DateTimeOffset.UtcNow };
    }

    internal static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new JsonException($"Sonar response is missing {name}.");

    internal static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double ReadVolume(JsonElement value)
    {
        var volume = value.GetProperty("volume").GetDouble();
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new JsonException("Sonar returned an invalid volume.");
        return volume;
    }
}
