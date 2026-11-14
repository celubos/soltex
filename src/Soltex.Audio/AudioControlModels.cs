namespace Soltex.Audio;

public sealed record AudioDefaultEndpoint(
    AudioEndpointDirection Direction,
    AudioDeviceRole Role,
    string? EndpointId,
    int HResult);

public sealed record AudioDeviceSnapshot(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AudioEndpoint> Endpoints,
    IReadOnlyList<AudioDefaultEndpoint> Defaults,
    AudioObservationState State,
    IReadOnlyList<string> Errors,
    bool NotificationsAvailable);

public sealed record AudioControlResult(
    bool Succeeded,
    string EndpointId,
    string Operation,
    AudioEndpoint? ConfirmedState,
    int? HResult,
    string? Error);

/// <summary>Windows endpoint peak, not bus gain or DSP output. Null means unavailable.</summary>
public sealed record AudioPeakResult(
    string EndpointId,
    DateTimeOffset CapturedAtUtc,
    float? Peak,
    int? HResult,
    string? Error);

public sealed record AudioDefaultRestoreResult(AudioEndpointDirection Direction, AudioDeviceRole Role,
    AudioRestoreDecision Decision, bool Applied, string? ConfirmedEndpointId, int? HResult, string? Error);

public sealed record AudioEndpointRestoreResult(AudioRestoreDecision Decision, AudioControlResult? Result);
