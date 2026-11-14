using System.Collections.ObjectModel;

namespace Soltex.Audio;

/// <summary>Direction of an audio endpoint, matching Core Audio's EDataFlow.</summary>
public enum AudioEndpointDirection
{
    Render,
    Capture
}

/// <summary>Endpoint availability, matching the DEVICE_STATE_* values Windows reports.</summary>
public enum AudioEndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
    Unknown
}

public enum AudioObservationState
{
    Current,
    Partial,
    Unavailable
}

public enum AudioDeviceRole
{
    Console,
    Multimedia,
    Communications
}

/// <summary>
/// One observed audio endpoint. Volume is the endpoint's current master scalar as
/// reported by Windows. The suite provider remains read-only; AudioDeviceService exposes explicit controls.
/// </summary>
public sealed record AudioEndpoint(
    string Name,
    AudioEndpointDirection Direction,
    AudioEndpointState State,
    bool IsDefault,
    double? VolumeScalar,
    bool? IsMuted)
{
    /// <summary>Opaque Windows identity; never use the friendly name as a routing key.</summary>
    public string EndpointId { get; init; } = string.Empty;

    /// <summary>Complete sanitized name, retained independently of a compact display label.</summary>
    public string FullName { get; init; } = Name;

    public IReadOnlyList<AudioDeviceRole> DefaultRoles { get; init; } = Array.Empty<AudioDeviceRole>();

    /// <summary>Volume as a whole percentage, or null when the endpoint did not report one.</summary>
    public double? VolumePercent => VolumeScalar is double scalar
        ? Math.Clamp(scalar * 100, 0, 100)
        : null;
}

public sealed class AudioEndpointSnapshot
{
    internal AudioEndpointSnapshot(
        DateTimeOffset capturedAtUtc,
        TimeSpan captureDuration,
        AudioObservationState state,
        IReadOnlyList<AudioEndpoint> endpoints,
        int inaccessibleEndpointCount,
        string provenance,
        IReadOnlyList<string> limitations)
    {
        CapturedAtUtc = capturedAtUtc;
        CaptureDuration = captureDuration;
        State = state;
        Endpoints = new ReadOnlyCollection<AudioEndpoint>(endpoints.ToArray());
        InaccessibleEndpointCount = inaccessibleEndpointCount;
        Provenance = provenance;
        Limitations = new ReadOnlyCollection<string>(limitations.ToArray());
    }

    public DateTimeOffset CapturedAtUtc { get; }

    public TimeSpan CaptureDuration { get; }

    public AudioObservationState State { get; }

    public IReadOnlyList<AudioEndpoint> Endpoints { get; }

    public int InaccessibleEndpointCount { get; }

    public string Provenance { get; }

    public IReadOnlyList<string> Limitations { get; }

    public IEnumerable<AudioEndpoint> Render =>
        Endpoints.Where(endpoint => endpoint.Direction == AudioEndpointDirection.Render);

    public IEnumerable<AudioEndpoint> Capture =>
        Endpoints.Where(endpoint => endpoint.Direction == AudioEndpointDirection.Capture);
}
