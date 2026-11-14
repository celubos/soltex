namespace Soltex.Audio.Sessions;

public sealed record AudioSessionKey(string EndpointId, string SessionInstanceId, int ProcessId,
    DateTimeOffset? ProcessStartTimeUtc);

public enum AudioSessionState { Inactive, Active, Expired }
public enum AudioEndpointClassification { Unknown, KnownVirtual }

public sealed record AudioSessionInfo(AudioSessionKey Key, string Name, string? ExecutablePath,
    AudioSessionState State, double? Volume, bool? Muted, double? Peak, bool IsSystemSounds, bool CanControl);

public sealed record AudioSessionEndpoint(string Id, string Name, AudioEndpointClassification Classification,
    double? Volume, bool? Muted, double? Peak, IReadOnlyList<AudioSessionInfo> Sessions)
{
    public bool IsVirtual => Classification == AudioEndpointClassification.KnownVirtual;
}

public sealed record AudioSessionSnapshot(DateTimeOffset CapturedAtUtc,
    IReadOnlyList<AudioSessionEndpoint> Endpoints, IReadOnlyList<string> Errors);

/// <summary>WriteIssued means the native setter was invoked, even if Windows rejected it or readback failed.</summary>
public sealed record AudioSessionControlResult(bool Applied, bool WriteIssued, AudioSessionInfo? ConfirmedState,
    string? Error);

internal sealed record SessionDiagnostics(long Discoveries, int EndpointLeases, int SessionLeases);

internal static class SessionPolicy
{
    private static readonly string[] VirtualNames = ["SteelSeries Sonar", "VB-Audio", "VB-Cable", "Virtual Audio Cable", "Voicemeeter", "Virtual Audio Driver by MTT"];
    internal static string? Reject(AudioSessionKey expected, AudioSessionInfo actual)
    {
        if (actual.IsSystemSounds || actual.Key.ProcessId <= 0) return "System and multi-process sessions are read-only.";
        if (!actual.CanControl || actual.Key.ProcessStartTimeUtc is null || expected.ProcessStartTimeUtc is null)
            return "The process identity cannot be verified; refresh the session.";
        if (actual.State == AudioSessionState.Expired || expected != actual.Key)
            return "The session or process changed; refresh before controlling it.";
        return null;
    }

    internal static void ValidateVolume(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Volume must be finite and between zero and one.");
    }

    internal static AudioEndpointClassification Classify(string name) =>
        VirtualNames
            .Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase))
            ? AudioEndpointClassification.KnownVirtual : AudioEndpointClassification.Unknown;
}
