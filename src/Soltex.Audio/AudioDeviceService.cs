using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Soltex.Audio;

/// <summary>
/// Per-user endpoint control. Every COM reference belongs to one MTA worker.
/// Notifications enqueue one reconciliation; no audio configuration changes on construction.
/// SnapshotChanged runs on the worker: subscribers should dispatch presentation work promptly.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "IAsyncDisposable joins the owning worker before releasing its queue.")]
public sealed class AudioDeviceService : IAsyncDisposable
{
    private static readonly PropertyKey NameProperty = CoreAudio.FriendlyNameKey;
    private static readonly Guid DeviceFormatProperty = new("f19f064d-082c-4e27-bc73-6882a1bb8e4c");
    private readonly BlockingCollection<Action> _commands = new(128);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifetime = new();
    private readonly OwnedEndpointCache<MeterLease> _meters = new(16);
    private readonly HashSet<string> _changedMeterIds = new(StringComparer.OrdinalIgnoreCase);
    private int _meterInvalidationPending;
    private long _meterConnectionAttempts, _meterSuccessfulActivations, _meterCacheHits;
    private readonly Dictionary<string, AudioEndpointDirection> _directions = new(StringComparer.OrdinalIgnoreCase);
    private readonly NotificationSink _notifications;
    private readonly ConcurrentDictionary<string, long> _notificationCounts = new(StringComparer.Ordinal);
    private long _ignoredPropertyNotifications;
    private IMMDeviceEnumerator? _enumerator;
    private AudioDeviceSnapshot? _latest;
    private Exception? _startupFailure;
    private bool _registered;
    private bool _closing;
    private int _refreshQueued;
    private Task? _disposeTask;

    public AudioDeviceService()
    {
        _notifications = new NotificationSink(QueueRefresh, () => Interlocked.Increment(ref _ignoredPropertyNotifications));
        Thread worker = new(Work) { IsBackground = true, Name = "Soltex audio devices" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public event EventHandler<AudioDeviceSnapshot>? SnapshotChanged;

    public Task<AudioDeviceSnapshot> GetSnapshotAsync() => Enqueue(Capture);

    /// <summary>
    /// Stops or resumes Windows endpoint notifications. Receiving them has a cost even when every one is ignored
    /// (vendor software can send dozens a second), so an idle owner can pause them and read afresh on resume.
    /// Returns whether notifications are registered afterwards.
    /// </summary>
    public Task<bool> SetNotificationsEnabledAsync(bool enabled) => Enqueue(() =>
    {
        if (_enumerator is null) return false;
        if (enabled && !_registered) _registered = _enumerator.RegisterEndpointNotificationCallback(_notifications) == CoreAudio.Ok;
        else if (!enabled && _registered) { _ = _enumerator.UnregisterEndpointNotificationCallback(_notifications); _registered = false; }
        return _registered;
    });

    public Task<AudioControlResult> SetVolumeAsync(string endpointId, double scalar) =>
        Enqueue(() => Control(endpointId, "volume", scalar, null));

    public Task<AudioControlResult> SetMuteAsync(string endpointId, bool muted) =>
        Enqueue(() => Control(endpointId, "mute", null, muted));

    public Task<AudioPeakResult> ReadPeakAsync(string endpointId) => Enqueue(() => ReadPeak(endpointId));

    /// <summary>Read one UI frame on the owning worker, once per distinct endpoint; no signal samples are reused.</summary>
    public Task<IReadOnlyList<AudioPeakResult>> ReadPeaksAsync(IReadOnlyCollection<string> endpointIds)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endpointIds.Count, 16);
        string[] ids = endpointIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Enqueue<IReadOnlyList<AudioPeakResult>>(() => Array.AsReadOnly(ids.Select(ReadPeak).ToArray()));
    }

    internal Task<MeterCacheDiagnostics> ReadMeterDiagnosticsAsync() => Enqueue(() => new MeterCacheDiagnostics(
        _meters.Count, _meterConnectionAttempts, _meterSuccessfulActivations, _meterCacheHits,
        new Dictionary<string, long>(_notificationCounts, StringComparer.Ordinal) { ["property:ignored"] = Interlocked.Read(ref _ignoredPropertyNotifications) }));

    /// <summary>Refresh one endpoint's effective state without changing it.</summary>
    public Task<AudioControlResult> ReadEndpointAsync(string endpointId) =>
        Enqueue(() => Control(endpointId, "read", null, null));

    /// <summary>Explicit recovery only. Compares the current role to a recorded owned change immediately before restoring.</summary>
    public Task<AudioDefaultRestoreResult> RestoreDefaultAsync(AudioEndpointDirection direction, AudioDeviceRole role,
        string beforeEndpointId, string afterEndpointId, bool dryRun = true) =>
        Enqueue(() => RestoreDefault(direction, role, beforeEndpointId, afterEndpointId, dryRun));

    public Task<AudioEndpointRestoreResult> RestoreEndpointAsync(AudioEndpoint before, AudioEndpoint after,
        string operation, bool dryRun = true) => Enqueue(() =>
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.EndpointId != after.EndpointId || operation is not ("volume" or "mute"))
            throw new ArgumentException("Recovery must describe one endpoint and one owned volume or mute change.");
        AudioControlResult read = Control(before.EndpointId, "read", null, null);
        AudioRestoreDecision decision = operation == "volume"
            ? AudioRestorePolicy.Volume(before.VolumeScalar, after.VolumeScalar, read.ConfirmedState?.VolumeScalar)
            : AudioRestorePolicy.Mute(before.IsMuted, after.IsMuted, read.ConfirmedState?.IsMuted);
        return new AudioEndpointRestoreResult(decision, dryRun || decision != AudioRestoreDecision.Restore ? null :
            Control(before.EndpointId, operation, operation == "volume" ? before.VolumeScalar : null, operation == "mute" ? before.IsMuted : null));
    });

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (_lifetime)
        {
            if (_disposeTask is null)
            {
                _closing = true;
                _commands.CompleteAdding();
                _disposeTask = FinishDisposeAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task FinishDisposeAsync()
    {
        await _finished.Task.ConfigureAwait(false);
        _commands.Dispose();
    }

    private Task<T> Enqueue<T>(Func<T> operation)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifetime)
        {
            if (_closing)
            {
                completion.SetException(new ObjectDisposedException(nameof(AudioDeviceService)));
            }
            else if (!_commands.TryAdd(() =>
            {
                try { completion.SetResult(operation()); }
                catch (Exception exception) { completion.SetException(exception); }
            }))
            {
                completion.SetException(new InvalidOperationException("Audio command queue is busy. Try again after the pending change."));
            }
        }
        return completion.Task;
    }

    private void Work()
    {
        try
        {
            try
            {
                _enumerator = CoreAudio.CreateEnumerator();
                _registered = _enumerator.RegisterEndpointNotificationCallback(_notifications) == CoreAudio.Ok;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException)
            {
                _startupFailure = exception;
            }
            foreach (Action action in _commands.GetConsumingEnumerable()) { InvalidateChangedMeters(); action(); }
        }
        finally
        {
            ReleaseMeters();
            if (_enumerator is not null)
            {
                if (_registered) _ = _enumerator.UnregisterEndpointNotificationCallback(_notifications);
                Marshal.ReleaseComObject(_enumerator);
                _enumerator = null;
            }
            _finished.TrySetResult();
        }
    }

    internal void QueueRefresh(string? deviceId, string kind, PropertyKey? key)
    {
        // Vendor/runtime properties can change whenever interfaces are activated. Re-enumerating
        // all endpoints for fields we never read creates a notification/activation feedback loop;
        // they are only counted, without formatting or allocation.
        if (kind == "property" && key is { } changedProperty && !RelevantProperty(changedProperty))
        {
            Interlocked.Increment(ref _ignoredPropertyNotifications);
            return;
        }
        string category = key is { } property ? $"{kind}:{property.FormatId:D}:{property.PropertyId}" : kind;
        if (_notificationCounts.Count < 64 || _notificationCounts.ContainsKey(category)) _notificationCounts.AddOrUpdate(category, 1, (_, count) => count + 1);
        lock (_lifetime)
        {
            if (_closing) return;
            if (deviceId is not null && (kind is "state" or "added" or "removed" || key is { } format && IsDeviceFormat(format)))
            {
                _changedMeterIds.Add(deviceId);
                Volatile.Write(ref _meterInvalidationPending, 1);
            }
            if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
            if (_closing || !_commands.TryAdd(() =>
            {
                Interlocked.Exchange(ref _refreshQueued, 0);
                try { Publish(Capture()); }
                catch (Exception exception)
                {
                    Publish(new(DateTimeOffset.UtcNow, Array.Empty<AudioEndpoint>(), Array.Empty<AudioDefaultEndpoint>(),
                        AudioObservationState.Unavailable, new[] { exception.Message }, _registered));
                }
            })) Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    internal static bool RelevantProperty(PropertyKey key) =>
        (key.FormatId == NameProperty.FormatId && key.PropertyId == NameProperty.PropertyId) || IsDeviceFormat(key);
    private static bool IsDeviceFormat(PropertyKey key) => key.FormatId == DeviceFormatProperty && key.PropertyId == 0;

    private void InvalidateChangedMeters()
    {
        if (Volatile.Read(ref _meterInvalidationPending) == 0) return;
        string[] changed;
        lock (_lifetime)
        {
            changed = _changedMeterIds.ToArray(); _changedMeterIds.Clear();
            Volatile.Write(ref _meterInvalidationPending, 0);
        }
        foreach (string id in changed) _meters.Remove(id);
    }

    private void Publish(AudioDeviceSnapshot snapshot)
    {
        EventHandler<AudioDeviceSnapshot>? handlers = SnapshotChanged;
        if (handlers is null) return;
        foreach (EventHandler<AudioDeviceSnapshot> handler in handlers.GetInvocationList().Cast<EventHandler<AudioDeviceSnapshot>>())
        {
            try { handler(this, snapshot); }
            catch (Exception) { /* A presentation subscriber cannot stop endpoint discovery. */ }
        }
    }

    private AudioDeviceSnapshot Capture()
    {
        List<AudioEndpoint> endpoints = [];
        List<AudioDefaultEndpoint> defaults = [];
        List<string> errors = [];
        if (_enumerator is null)
        {
            return _latest = new(DateTimeOffset.UtcNow, endpoints.AsReadOnly(), defaults.AsReadOnly(),
                AudioObservationState.Unavailable, new[] { _startupFailure?.Message ?? "Windows audio is unavailable." }, false);
        }
        if (!_registered) errors.Add("Endpoint notifications are unavailable; use Refresh to reconcile devices.");
        int successfulDirections = 0;
        _directions.Clear();
        foreach (AudioEndpointDirection direction in Enum.GetValues<AudioEndpointDirection>())
        {
            foreach (AudioDeviceRole role in Enum.GetValues<AudioDeviceRole>())
            {
                int hr = _enumerator.GetDefaultAudioEndpoint((DataFlow)direction, (DeviceRole)role, out IMMDevice device);
                string? id = null;
                if (hr == CoreAudio.Ok)
                {
                    try { hr = device.GetId(out string currentId); if (hr == CoreAudio.Ok) id = currentId; }
                    finally { Release(device); }
                }
                else Release(device);
                defaults.Add(new(direction, role, id, hr));
            }
            int enumerateHr = _enumerator.EnumAudioEndpoints((DataFlow)direction, DeviceStateMask.All, out IMMDeviceCollection collection);
            if (enumerateHr != CoreAudio.Ok)
            {
                Release(collection);
                errors.Add($"{direction} enumeration failed ({Hex(enumerateHr)}).");
                continue;
            }
            try
            {
                int countHr = collection.GetCount(out uint count);
                if (countHr != CoreAudio.Ok) { errors.Add($"{direction} count failed ({Hex(countHr)})."); continue; }
                successfulDirections++;
                const uint maximumDevices = 512;
                if (count > maximumDevices) errors.Add($"{direction} inventory exceeds {maximumDevices} devices; remaining devices were not examined.");
                for (uint index = 0; index < Math.Min(count, maximumDevices); index++)
                {
                    int itemHr = collection.Item(index, out IMMDevice device);
                    if (itemHr != CoreAudio.Ok) { Release(device); errors.Add($"{direction} endpoint {index} is unavailable ({Hex(itemHr)})."); continue; }
                    try
                    {
                        AudioEndpoint endpoint = ReadDevice(device, direction, defaults);
                        endpoints.Add(endpoint);
                        _directions[endpoint.EndpointId] = direction;
                    }
                    catch (COMException exception) { errors.Add($"{direction} endpoint {index}: {Hex(exception.HResult)}."); }
                    finally { Release(device); }
                }
            }
            finally { Release(collection); }
        }
        foreach (string id in _meters.Keys.ToArray())
        {
            AudioEndpoint? observed = endpoints.FirstOrDefault(endpoint => string.Equals(endpoint.EndpointId, id, StringComparison.OrdinalIgnoreCase));
            if ((observed is not null && observed.State != AudioEndpointState.Active) || (observed is null && successfulDirections == 2)) _meters.Remove(id);
        }
        return _latest = new(DateTimeOffset.UtcNow, endpoints.AsReadOnly(), defaults.AsReadOnly(),
            successfulDirections == 0 ? AudioObservationState.Unavailable : errors.Count != 0 ? AudioObservationState.Partial : AudioObservationState.Current,
            errors.AsReadOnly(), _registered);
    }

    private static AudioEndpoint ReadDevice(IMMDevice device, AudioEndpointDirection direction, IReadOnlyList<AudioDefaultEndpoint> defaults)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out string id));
        Marshal.ThrowExceptionForHR(device.GetState(out uint state));
        string name = "Unavailable";
        int propertyHr = device.OpenPropertyStore(CoreAudio.StorageRead, out IPropertyStore store);
        if (propertyHr == CoreAudio.Ok)
        {
            try
            {
                PropertyKey key = CoreAudio.FriendlyNameKey;
                if (store.GetValue(ref key, out PropVariant value) == CoreAudio.Ok)
                {
                    try
                    {
                        string? raw = value.ReadString();
                        if (raw is not null)
                        {
                            string clean = new(raw.Where(character => !char.IsControl(character)).ToArray());
                            if (!string.IsNullOrWhiteSpace(clean)) name = clean.Trim();
                        }
                    }
                    finally { _ = CoreAudio.PropVariantClear(ref value); }
                }
            }
            finally { Release(store); }
        }
        else Release(store);
        double? scalar = null;
        bool? mute = null;
        Guid volumeId = CoreAudio.AudioEndpointVolumeId;
        int activateHr = device.Activate(ref volumeId, CoreAudio.ClassContextAll, IntPtr.Zero, out object instance);
        try
        {
            if (activateHr == CoreAudio.Ok && instance is IAudioEndpointVolume volume)
            {
                if (volume.GetMasterVolumeLevelScalar(out float level) == CoreAudio.Ok && float.IsFinite(level)) scalar = Math.Clamp(level, 0, 1);
                if (volume.GetMute(out bool muted) == CoreAudio.Ok) mute = muted;
            }
        }
        finally { Release(instance); }
        AudioDeviceRole[] roles = defaults.Where(item => item.Direction == direction &&
            string.Equals(item.EndpointId, id, StringComparison.OrdinalIgnoreCase)).Select(item => item.Role).ToArray();
        return new AudioEndpoint(AudioEndpointProvider.SanitizeName(name), direction, state switch
        {
            DeviceStateMask.Active => AudioEndpointState.Active,
            DeviceStateMask.Disabled => AudioEndpointState.Disabled,
            DeviceStateMask.NotPresent => AudioEndpointState.NotPresent,
            DeviceStateMask.Unplugged => AudioEndpointState.Unplugged,
            _ => AudioEndpointState.Unknown
        }, roles.Contains(AudioDeviceRole.Multimedia), scalar, mute)
        {
            EndpointId = id, FullName = name, DefaultRoles = Array.AsReadOnly(roles)
        };
    }

    private AudioControlResult Control(string endpointId, string operation, double? scalar, bool? muted)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return new(false, endpointId, operation, null, null, "Select an endpoint by its Windows identity.");
        if (scalar is double value && (!double.IsFinite(value) || value is < 0 or > 1))
            return new(false, endpointId, operation, null, null, "Volume must be a finite scalar between 0 and 1.");
        if (_enumerator is null) return new(false, endpointId, operation, null, null, "Windows audio is unavailable.");
        if (!_directions.ContainsKey(endpointId)) _ = Capture();
        if (!_directions.TryGetValue(endpointId, out AudioEndpointDirection direction))
            return new(false, endpointId, operation, null, null, "The selected endpoint is no longer present. Its saved preference can be retained.");
        int hr = _enumerator.GetDevice(endpointId, out IMMDevice device);
        if (hr != CoreAudio.Ok) { Release(device); return Failure(endpointId, operation, hr); }
        try
        {
            AudioEndpoint before = ReadDevice(device, direction, _latest?.Defaults ?? Array.Empty<AudioDefaultEndpoint>());
            if (operation == "read") return new(true, endpointId, operation, before, 0, null);
            if (before.State != AudioEndpointState.Active) return new(false, endpointId, operation, before, null, "The selected device is not active.");
            Guid volumeId = CoreAudio.AudioEndpointVolumeId;
            hr = device.Activate(ref volumeId, CoreAudio.ClassContextAll, IntPtr.Zero, out object instance);
            try
            {
                if (hr != CoreAudio.Ok) return Failure(endpointId, operation, hr);
                if (instance is not IAudioEndpointVolume volume) return new(false, endpointId, operation, before, null, "Endpoint volume control is unsupported.");
                Guid context = new("C22CB8D7-954A-4A37-AC6C-78EF23080AD2");
                hr = scalar is double level ? volume.SetMasterVolumeLevelScalar((float)level, ref context) : volume.SetMute(muted!.Value, ref context);
                if (hr != CoreAudio.Ok) return Failure(endpointId, operation, hr);
            }
            finally { Release(instance); }
            AudioEndpoint confirmed = ReadDevice(device, direction, _latest?.Defaults ?? Array.Empty<AudioDefaultEndpoint>());
            bool matches = scalar is double expected
                ? confirmed.VolumeScalar is double actual && Math.Abs(actual - expected) <= 0.002
                : confirmed.IsMuted == muted;
            return new(matches, endpointId, operation, confirmed, 0,
                matches ? null : "Windows accepted the command but readback did not confirm the requested value. Another controller may have changed it.");
        }
        catch (COMException exception) { return Failure(endpointId, operation, exception.HResult); }
        finally { Release(device); }
    }

    private AudioPeakResult ReadPeak(string endpointId)
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(endpointId) || _enumerator is null) return new(endpointId, capturedAt, null, null, "Select an available endpoint.");
        try
        {
            bool found = _meters.TryGet(endpointId, out MeterLease? lease);
            if (found && lease!.Meter is null && capturedAt >= lease.RetryAfterUtc)
            { _meters.Remove(endpointId); found = false; }
            if (!found)
            {
                lease = ConnectMeter(endpointId);
                _meters.Add(endpointId, lease);
            }
            else _meterCacheHits++;
            if (lease!.Meter is null) return new(endpointId, capturedAt, null, lease.HResult, lease.Error);
            if (!_registered)
            {
                int stateHr = lease.Device!.GetState(out uint state);
                if (stateHr != CoreAudio.Ok || state != DeviceStateMask.Active)
                    return CacheMeterFailure(endpointId, capturedAt, stateHr, "The endpoint is not active.");
            }
            int peakHr = lease.Meter.GetPeakValue(out float peak);
            if (peakHr != CoreAudio.Ok || !float.IsFinite(peak))
                return CacheMeterFailure(endpointId, capturedAt, peakHr, "The peak reading is unavailable.");
            return new(endpointId, capturedAt, Math.Clamp(peak, 0, 1), 0, null);
        }
        catch (COMException exception)
        {
            return CacheMeterFailure(endpointId, capturedAt, exception.HResult, "The audio device changed while reading its signal.");
        }
    }

    private MeterLease ConnectMeter(string endpointId)
    {
        _meterConnectionAttempts++;
        IMMDevice? device = null;
        object? instance = null;
        bool transferred = false;
        try
        {
            int deviceHr = _enumerator!.GetDevice(endpointId, out device);
            if (deviceHr != CoreAudio.Ok) return MeterLease.Failed(deviceHr, "The endpoint is unavailable.");
            int stateHr = device.GetState(out uint state);
            if (stateHr != CoreAudio.Ok || state != DeviceStateMask.Active)
                return MeterLease.Failed(stateHr, "The endpoint is not active.");
            Guid meterId = typeof(IAudioMeterInformation).GUID;
            int hr = device.Activate(ref meterId, CoreAudio.ClassContextAll, IntPtr.Zero, out instance);
            if (hr != CoreAudio.Ok || instance is not IAudioMeterInformation meter)
                return MeterLease.Failed(hr, "Signal metering is unavailable.");
            _meterSuccessfulActivations++;
            transferred = true;
            return new(device, meter);
        }
        finally { if (!transferred) { Release(instance); Release(device); } }
    }

    private AudioPeakResult CacheMeterFailure(string endpointId, DateTimeOffset capturedAt, int hr, string error)
    {
        _meters.Add(endpointId, MeterLease.Failed(hr, error));
        return new(endpointId, capturedAt, null, hr, error);
    }

    private sealed class MeterLease(IMMDevice? device, IAudioMeterInformation? meter) : IDisposable
    {
        public IMMDevice? Device { get; } = device;
        public IAudioMeterInformation? Meter { get; } = meter;
        public int? HResult { get; private init; }
        public string? Error { get; private init; }
        public DateTimeOffset RetryAfterUtc { get; private init; }
        private bool _disposed;
        // Failed devices get a short retry backoff, not a synthetic reading. Real state/format
        // notifications invalidate this entry immediately, so a reconnect need not wait for it.
        public static MeterLease Failed(int hr, string error) => new(null, null)
        { HResult = hr, Error = error, RetryAfterUtc = DateTimeOffset.UtcNow.AddSeconds(1) };
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(Meter); Release(Device);
        }
    }
    private AudioDefaultRestoreResult RestoreDefault(AudioEndpointDirection direction, AudioDeviceRole role,
        string beforeEndpointId, string afterEndpointId, bool dryRun)
    {
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(role) || _enumerator is null)
            return new(direction, role, AudioRestoreDecision.Unavailable, false, null, null, "Windows audio or the requested role is unavailable.");
        object? policyObject = null;
        try
        {
            AudioDeviceSnapshot snapshot = Capture();
            AudioDefaultEndpoint current = snapshot.Defaults.Single(item => item.Direction == direction && item.Role == role);
            AudioRestoreDecision decision = AudioRestorePolicy.Default(beforeEndpointId, afterEndpointId, current.EndpointId);
            if (decision != AudioRestoreDecision.Restore)
                return new(direction, role, decision, false, current.EndpointId, current.HResult, null);
            AudioEndpoint? target = snapshot.Endpoints.FirstOrDefault(endpoint => endpoint.Direction == direction &&
                endpoint.State == AudioEndpointState.Active && string.Equals(endpoint.EndpointId, beforeEndpointId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return new(direction, role, AudioRestoreDecision.Unavailable, false, current.EndpointId, null, "The previous default endpoint is not active; no default changed.");
            if (dryRun) return new(direction, role, decision, false, current.EndpointId, 0, null);
            policyObject = new PolicyConfigObject();
            IPolicyConfig policy = (IPolicyConfig)policyObject;
            // Recheck after activating the optional adapter. Windows offers no atomic compare-and-set.
            int hr = _enumerator.GetDefaultAudioEndpoint((DataFlow)direction, (DeviceRole)role, out IMMDevice device);
            string? latestId = null;
            try { if (hr == CoreAudio.Ok) hr = device.GetId(out latestId); }
            finally { Release(device); }
            if (hr != CoreAudio.Ok || AudioRestorePolicy.Default(beforeEndpointId, afterEndpointId, latestId) != AudioRestoreDecision.Restore)
                return new(direction, role, AudioRestoreDecision.CurrentValueChanged, false, latestId, hr, "Default changed during recovery; no command issued.");
            hr = policy.SetDefaultEndpoint(beforeEndpointId, (DeviceRole)role);
            if (hr != CoreAudio.Ok) return new(direction, role, decision, false, latestId, hr, "Windows rejected default restoration.");
            AudioDefaultEndpoint confirmed = Capture().Defaults.Single(item => item.Direction == direction && item.Role == role);
            bool matches = string.Equals(confirmed.EndpointId, beforeEndpointId, StringComparison.OrdinalIgnoreCase);
            return new(direction, role, decision, matches, confirmed.EndpointId, confirmed.HResult,
                matches ? null : "Default restoration was accepted but readback differs; reconcile before retrying.");
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException)
        {
            return new(direction, role, AudioRestoreDecision.Unavailable, false, null, exception.HResult,
                "Optional Windows default-policy adapter is unavailable: " + exception.Message);
        }
        finally { Release(policyObject); }
    }

    private void ReleaseMeters()
    {
        _meters.Dispose();
    }

    private static AudioControlResult Failure(string id, string operation, int hr) =>
        new(false, id, operation, null, hr, $"Windows audio rejected {operation} ({Hex(hr)}). Refresh devices and try again.");
    private static string Hex(int hr) => $"0x{hr:X8}";
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class NotificationSink(Action<string?, string, PropertyKey?> changed, Action ignored) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) { changed(deviceId, "state", null); return 0; }
        public int OnDeviceAdded(string deviceId) { changed(deviceId, "added", null); return 0; }
        public int OnDeviceRemoved(string deviceId) { changed(deviceId, "removed", null); return 0; }
        public int OnDefaultDeviceChanged(DataFlow flow, DeviceRole role, string? deviceId) { changed(deviceId, "default", null); return 0; }
        public int OnPropertyValueChanged(IntPtr deviceId, PropertyKey key)
        {
            if (!RelevantProperty(key)) { ignored(); return 0; }
            changed(deviceId == IntPtr.Zero ? null : Marshal.PtrToStringUni(deviceId), "property", key);
            return 0;
        }
    }
}
