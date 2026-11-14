using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Soltex.Audio.Sessions;

namespace Soltex.Audio.WindowsRouting;

public sealed record AppRoutePreview(bool Allowed, string Message, IReadOnlyList<ProcessPolicy> BeforePolicies, string? TargetEndpointId);
public sealed record AppRouteResult(bool PolicyConfirmed, bool SessionObserved, bool RequiresStreamRestart, string Message, string? JournalPath);
public sealed record AppRouteHistory(string JournalPath, DateTimeOffset CreatedAtUtc, string ExecutablePath,
    RoutingDirection Direction, string? TargetEndpointId, string State);
internal sealed record AppRouteJournal(int Version, Guid Id, DateTimeOffset CreatedAtUtc, string ExecutablePath,
    AudioSessionKey Selection, RoutingDirection Direction, string? TargetEndpointId,
    IReadOnlyList<ProcessPolicy> BeforePolicies, AppRoutingRule? BeforeRule, IReadOnlyList<RoutingRole> AttemptedRoles, string State, IReadOnlyList<string> Notes);

/// <summary>
/// User-invoked application policy changes, serialized and journaled before each write.
/// A confirmed Windows policy is distinct from an active stream reaching that endpoint.
/// No startup replay, process restart, default-device setter or global policy reset is performed.
/// </summary>
public sealed class AppRoutingService : IAsyncDisposable
{
    private readonly string journalDirectory;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Func<IAppRoutingBackend> factory;
    private readonly object lifetime = new();
    private IAppRoutingBackend? backend;
    private Task? disposal;
    private volatile bool disposed;
    public RoutingProfileStore Profiles { get; }
    /// <summary>Returns up to 100 valid recent journals. Reading history never activates native audio policy.</summary>
    public Task<IReadOnlyList<AppRouteHistory>> GetHistoryAsync(CancellationToken cancellationToken = default) =>
        Serialized<IReadOnlyList<AppRouteHistory>>(async () =>
        {
            List<AppRouteHistory> history = [];
            if (!Directory.Exists(journalDirectory)) return history.AsReadOnly();
            foreach (string file in Directory.EnumerateFiles(journalDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = await RoutingProfileStore.ReadBounded<AppRouteJournal>(file, cancellationToken).ConfigureAwait(false);
                ValidateJournal(entry);
                history.Add(new(file, entry.CreatedAtUtc, entry.ExecutablePath, entry.Direction, entry.TargetEndpointId, entry.State));
            }
            return history.OrderByDescending(entry => entry.CreatedAtUtc).ToArray();
        }, cancellationToken);
    public AppRoutingService(string preferencesDirectory) : this(preferencesDirectory, () => new AppRoutingBackend()) { }
    internal AppRoutingService(string preferencesDirectory, Func<IAppRoutingBackend> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencesDirectory);
        this.factory = factory;
        journalDirectory = Path.Combine(Path.GetFullPath(preferencesDirectory), "routing-journals");
        Profiles = new(preferencesDirectory);
    }
    public Task<AppRoutePreview> PreviewAsync(AudioSessionInfo selected, string? targetEndpointId,
        RoutingDirection direction = RoutingDirection.Render, CancellationToken cancellationToken = default) =>
        Serialized(() =>
        {
            try
            {
                ValidateRequest(selected, targetEndpointId, direction);
                var current = GetBackend();
                ValidateDestination(current.Devices(), targetEndpointId, direction);
                using var target = current.Open(selected);
                var before = target.Read(); ValidatePolicies(before);
                return Task.FromResult(new AppRoutePreview(true,
                    "Applies to this executable's Windows audio policy for all three roles. Existing streams may need to be reopened by the application.", before, targetEndpointId));
            }
            catch (Exception error) when (Expected(error))
            { return Task.FromResult(new AppRoutePreview(false, error.Message, [], targetEndpointId)); }
        }, cancellationToken);

    public Task<AppRouteResult> RouteAsync(AudioSessionInfo selected, string? targetEndpointId,
        RoutingDirection direction = RoutingDirection.Render, CancellationToken cancellationToken = default) =>
        Serialized<AppRouteResult>(async () =>
        {
            string? path = null; AppRouteJournal? journal = null;
            IAppRoutingTarget? target = null;
            bool confirmed = false;
            try
            {
                ValidateRequest(selected, targetEndpointId, direction);
                var profile = await Profiles.LoadAsync(cancellationToken).ConfigureAwait(false); // Detect corrupt intent before touching Windows.
                var current = GetBackend();
                ValidateDestination(current.Devices(), targetEndpointId, direction);
                target = current.Open(selected);
                var before = target.Read(); ValidatePolicies(before);
                journal = new(1, Guid.NewGuid(), DateTimeOffset.UtcNow, Path.GetFullPath(selected.ExecutablePath!),
                    selected.Key, direction, targetEndpointId, before.ToArray(), profile.Rules.FirstOrDefault(r => r.Direction == direction &&
                        string.Equals(r.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase)), [], "Prepared", []);
                path = Path.Combine(journalDirectory, journal.Id.ToString("N") + ".json");
                await SaveJournal(path, journal).ConfigureAwait(false);
                string? desired = Raw(targetEndpointId, direction);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var role in Enum.GetValues<RoutingRole>())
                {
                    string? previous = before.Single(p => p.Direction == direction && p.Role == role).RawDeviceId;
                    if (string.Equals(previous, desired, StringComparison.Ordinal)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateDestination(current.Devices(), targetEndpointId, direction);
                    journal = journal with { AttemptedRoles = journal.AttemptedRoles.Append(role).ToArray(), State = "Applying" };
                    await SaveJournal(path, journal).ConfigureAwait(false); // Durable intent precedes a setter with unknown possible outcome.
                    var observed = target.CompareSet(direction, role, previous, desired);
                    if (!string.Equals(observed.RawDeviceId, desired, StringComparison.Ordinal))
                        throw new IOException("Windows accepted the request but policy readback did not match.");
                }
                var after = target.Read(); ValidatePolicies(after);
                confirmed = after.Where(p => p.Direction == direction).All(p => string.Equals(p.RawDeviceId, desired, StringComparison.Ordinal));
                if (!confirmed) throw new IOException("Policy changed before final confirmation; the operation will be selectively rolled back.");
                journal = journal with { State = "Confirmed" };
                await SaveJournal(path, journal).ConfigureAwait(false);
                bool observedSession = Observe(current, selected, targetEndpointId, direction);
                string message = observedSession ? "Policy saved; an active session is observed on the selected destination."
                    : "Policy saved. Existing audio may stay on its old device until the application reopens its stream.";
                try { await Profiles.UpsertAsync(new(journal.ExecutablePath, direction, targetEndpointId)).ConfigureAwait(false); }
                catch (Exception error) when (Expected(error)) { message += " The routing preference could not be saved: " + error.Message; }
                return new(true, observedSession, !observedSession, message, path);
            }
            catch (Exception error) when (Expected(error))
            {
                List<string> notes = [error.Message];
                if (!confirmed && target is not null && journal is not null && path is not null)
                {
                    notes.AddRange(Rollback(target, journal));
                    journal = journal with { State = "ApplyFailed", Notes = notes.AsReadOnly() };
                    try { await SaveJournal(path, journal).ConfigureAwait(false); }
                    catch (Exception failure) when (Expected(failure)) { notes.Add("Could not checkpoint recovery: " + failure.Message); }
                }
                return new(confirmed, false, confirmed, (confirmed ? "Windows policy was confirmed, but finishing the operation failed: " : "") + string.Join(" ", notes), path);
            }
            finally { target?.Dispose(); }
        }, cancellationToken);

    public Task<AppRouteResult> RestoreAsync(string journalPath, AudioSessionInfo selected, CancellationToken cancellationToken = default) =>
        Serialized<AppRouteResult>(async () =>
        {
            string full = Path.GetFullPath(journalPath);
            try
            {
                if (!string.Equals(Path.GetDirectoryName(full), journalDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Only this profile's own routing journal can be restored.");
                var journal = await RoutingProfileStore.ReadBounded<AppRouteJournal>(full, cancellationToken).ConfigureAwait(false);
                ValidateJournal(journal);
                ValidateRequest(selected, journal.TargetEndpointId, journal.Direction);
                if (!string.Equals(Path.GetFullPath(selected.ExecutablePath!), journal.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose a current session belonging to the journal's executable.");
                using var target = GetBackend().Open(selected);
                cancellationToken.ThrowIfCancellationRequested();
                journal = journal with { State = "Restoring" };
                await SaveJournal(full, journal).ConfigureAwait(false);
                var notes = Rollback(target, journal);
                var after = target.Read(); ValidatePolicies(after);
                bool restored = journal.AttemptedRoles.All(role =>
                    after.Single(p => p.Direction == journal.Direction && p.Role == role).RawDeviceId ==
                    journal.BeforePolicies.Single(p => p.Direction == journal.Direction && p.Role == role).RawDeviceId);
                journal = journal with { State = restored ? "Restored" : "RestoreConflict", Notes = notes };
                await SaveJournal(full, journal).ConfigureAwait(false);
                if (restored) await Profiles.RestoreIfOwnedAsync(new(journal.ExecutablePath, journal.Direction, journal.TargetEndpointId), journal.BeforeRule).ConfigureAwait(false);
                return new(restored, false, restored, restored ? "Previous per-application policies restored. Reopen the application's audio stream if necessary."
                    : "Some policies changed elsewhere or became unavailable; newer choices were preserved. " + string.Join(" ", notes), full);
            }
            catch (Exception error) when (Expected(error)) { return new(false, false, false, error.Message, full); }
        }, cancellationToken);

    private static List<string> Rollback(IAppRoutingTarget target, AppRouteJournal journal)
    {
        List<string> notes = [];
        string? owned = Raw(journal.TargetEndpointId, journal.Direction);
        foreach (var role in journal.AttemptedRoles.Reverse())
        {
            try
            {
                var current = target.Read().Single(p => p.Direction == journal.Direction && p.Role == role);
                var before = journal.BeforePolicies.Single(p => p.Direction == journal.Direction && p.Role == role);
                if (current.RawDeviceId == before.RawDeviceId) continue;
                if (current.RawDeviceId != owned) { notes.Add($"{role}: newer policy preserved."); continue; }
                var restored = target.CompareSet(journal.Direction, role, owned, before.RawDeviceId);
                if (restored.RawDeviceId != before.RawDeviceId) notes.Add($"{role}: restoration readback did not match.");
            }
            catch (Exception error) when (Expected(error)) { notes.Add($"{role}: {error.Message}"); }
        }
        return notes;
    }
    private static bool Observe(IAppRoutingBackend current, AudioSessionInfo selected, string? targetEndpointId, RoutingDirection direction)
    {
        // The session backend observes render sessions only. A policy readback cannot establish capture delivery.
        if (direction != RoutingDirection.Render) return false;
        string? destination = targetEndpointId ?? current.Devices().Defaults.FirstOrDefault(d =>
            d.Direction == AudioEndpointDirection.Render && d.Role == AudioDeviceRole.Multimedia)?.EndpointId;
        var snapshot = current.Sessions();
        if (snapshot.Errors.Count != 0) return false;
        var active = snapshot.Endpoints.SelectMany(e => e.Sessions).Where(s => s.State == AudioSessionState.Active &&
            s.Key.ProcessId == selected.Key.ProcessId && s.Key.ProcessStartTimeUtc == selected.Key.ProcessStartTimeUtc).ToArray();
        return active.Length != 0 && active.All(s => s.Key.EndpointId == destination);
    }
    internal static void ValidateSelection(AudioSessionInfo selected, AudioSessionSnapshot snapshot)
    {
        ValidateRequest(selected, null, RoutingDirection.Render);
        if (snapshot.Errors.Count != 0) throw new IOException("Current audio-session ownership could not be fully inspected.");
        var matches = snapshot.Endpoints.SelectMany(e => e.Sessions).Where(s => s.Key == selected.Key).ToArray();
        if (matches.Length != 1 || !matches[0].CanControl || matches[0].IsSystemSounds || matches[0].State == AudioSessionState.Expired ||
            !string.Equals(matches[0].ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected session changed, is ambiguous or is no longer available. Refresh the application list.");
    }
    private static void ValidateRequest(AudioSessionInfo selected, string? endpointId, RoutingDirection direction)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Key.ProcessId <= 0 || selected.IsSystemSounds || !selected.CanControl || selected.State == AudioSessionState.Expired ||
            selected.Key.ProcessStartTimeUtc is null || string.IsNullOrWhiteSpace(selected.Key.SessionInstanceId))
            throw new InvalidOperationException("System, expired, shared or unverifiable application sessions cannot be routed.");
        RoutingProfileStore.Validate(new(1, [new(selected.ExecutablePath!, direction, endpointId)]));
    }
    private static void ValidateDestination(AudioDeviceSnapshot devices, string? id, RoutingDirection direction)
    {
        if (devices.State != AudioObservationState.Current || devices.Errors.Count != 0) throw new IOException("Endpoint availability is incomplete. Refresh before routing.");
        if (id is not null && !devices.Endpoints.Any(e => e.EndpointId == id && e.State == AudioEndpointState.Active && (int)e.Direction == (int)direction))
            throw new IOException("The selected destination is unavailable or has the wrong direction. No substitute was selected.");
    }
    private static void ValidatePolicies(IReadOnlyList<ProcessPolicy> policies)
    {
        if (policies is null || policies.Count != 6 || policies.Any(p => p is null || !Enum.IsDefined(p.Direction) || !Enum.IsDefined(p.Role) || p.RawDeviceId is { Length: > 4096 }) ||
            policies.Select(p => (p.Direction, p.Role)).Distinct().Count() != 6)
            throw new InvalidDataException("A complete six-role policy snapshot is required.");
    }
    private static void ValidateJournal(AppRouteJournal journal)
    {
        if (journal.Version != 1 || journal.Id == Guid.Empty || journal.AttemptedRoles is null || journal.AttemptedRoles.Count > 3 ||
            journal.AttemptedRoles.Any(r => !Enum.IsDefined(r)) || journal.AttemptedRoles.Distinct().Count() != journal.AttemptedRoles.Count)
            throw new InvalidDataException("Invalid routing journal.");
        RoutingProfileStore.Validate(new(1, [new(journal.ExecutablePath, journal.Direction, journal.TargetEndpointId)]));
        if (journal.BeforeRule is not null)
        {
            RoutingProfileStore.Validate(new(1, [journal.BeforeRule]));
            if (journal.BeforeRule.Direction != journal.Direction || !string.Equals(journal.BeforeRule.ExecutablePath, journal.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The prior saved rule belongs to a different executable or direction.");
        }
        ValidatePolicies(journal.BeforePolicies);
    }
    private static string? Raw(string? id, RoutingDirection direction) => id is null ? null : WindowsAudioPolicy.ToRawDeviceId(id, direction);
    private static Task SaveJournal(string path, AppRouteJournal journal) => RoutingProfileStore.WriteAtomic(path, journal, CancellationToken.None);
    private IAppRoutingBackend GetBackend() => backend ??= factory();
    private async Task<T> Serialized<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }
    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or OperationCanceledException or PlatformNotSupportedException or COMException or Win32Exception or JsonException;
    public ValueTask DisposeAsync()
    {
        lock (lifetime) { disposed = true; return new(disposal ??= DisposeCore()); }
    }
    private async Task DisposeCore()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => backend?.Dispose()).ConfigureAwait(false); }
        finally { operations.Release(); }
    }
}
