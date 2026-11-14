namespace Soltex.Audio;

/// <summary>Saved identities remain intact when a device is absent. List order is priority order.</summary>
public sealed record OutputDevicePreferences(
    IReadOnlyList<string> PriorityEndpointIds,
    IReadOnlyList<string> ExcludedEndpointIds,
    string? ManualEndpointId = null,
    bool ReturnToPreferred = true);

public enum OutputSelectionReason { Manual, Preferred, Retained, Fallback, NoAvailableDevice }
public sealed record OutputSelection(string? EndpointId, OutputSelectionReason Reason, string? UnavailableManualEndpointId);

/// <summary>Pure selection only: it never changes Windows defaults or silently admits a new endpoint.</summary>
public static class OutputDevicePolicy
{
    public static OutputSelection Select(OutputDevicePreferences preferences, IEnumerable<AudioEndpoint> endpoints, string? currentEndpointId = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(endpoints);
        HashSet<string> excluded = new(preferences.ExcludedEndpointIds, StringComparer.OrdinalIgnoreCase);
        HashSet<string> active = endpoints.Where(endpoint => endpoint.Direction == AudioEndpointDirection.Render &&
            endpoint.State == AudioEndpointState.Active && !excluded.Contains(endpoint.EndpointId))
            .Select(endpoint => endpoint.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? pin = preferences.ManualEndpointId;
        if (!string.IsNullOrWhiteSpace(pin) && active.Contains(pin)) return new(pin, OutputSelectionReason.Manual, null);
        string? missingPin = string.IsNullOrWhiteSpace(pin) ? null : pin;
        HashSet<string> eligible = preferences.PriorityEndpointIds.Where(active.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!preferences.ReturnToPreferred && currentEndpointId is not null && eligible.Contains(currentEndpointId))
            return new(currentEndpointId, OutputSelectionReason.Retained, missingPin);
        string? selected = preferences.PriorityEndpointIds.FirstOrDefault(active.Contains);
        if (selected is null) return new(null, OutputSelectionReason.NoAvailableDevice, missingPin);
        bool first = preferences.PriorityEndpointIds.Count > 0 && string.Equals(selected, preferences.PriorityEndpointIds[0], StringComparison.OrdinalIgnoreCase);
        return new(selected, first && missingPin is null ? OutputSelectionReason.Preferred : OutputSelectionReason.Fallback, missingPin);
    }
}

public enum AudioRestoreDecision { Restore, AlreadyRestored, CurrentValueChanged, Unavailable, NoOwnedChange }

/// <summary>Only a recorded, confirmed change still present in Windows is eligible for restoration.</summary>
public static class AudioRestorePolicy
{
    public static AudioRestoreDecision Volume(double? before, double? after, double? current)
    {
        if (before is not double b || after is not double a || current is not double c ||
            !double.IsFinite(b) || !double.IsFinite(a) || !double.IsFinite(c) ||
            b is < 0 or > 1 || a is < 0 or > 1 || c is < 0 or > 1) return AudioRestoreDecision.Unavailable;
        if (Equal(b, a)) return AudioRestoreDecision.NoOwnedChange;
        if (Equal(c, b)) return AudioRestoreDecision.AlreadyRestored;
        return Equal(c, a) ? AudioRestoreDecision.Restore : AudioRestoreDecision.CurrentValueChanged;
    }
    public static AudioRestoreDecision Mute(bool? before, bool? after, bool? current)
    {
        if (before is null || after is null || current is null) return AudioRestoreDecision.Unavailable;
        if (before == after) return AudioRestoreDecision.NoOwnedChange;
        if (current == before) return AudioRestoreDecision.AlreadyRestored;
        return current == after ? AudioRestoreDecision.Restore : AudioRestoreDecision.CurrentValueChanged;
    }
    public static AudioRestoreDecision Default(string? before, string? after, string? current)
    {
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || string.IsNullOrEmpty(current)) return AudioRestoreDecision.Unavailable;
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return AudioRestoreDecision.NoOwnedChange;
        if (string.Equals(current, before, StringComparison.OrdinalIgnoreCase)) return AudioRestoreDecision.AlreadyRestored;
        return string.Equals(current, after, StringComparison.OrdinalIgnoreCase) ? AudioRestoreDecision.Restore : AudioRestoreDecision.CurrentValueChanged;
    }
    private static bool Equal(double left, double right) => Math.Abs(left - right) <= 0.000001;
}
