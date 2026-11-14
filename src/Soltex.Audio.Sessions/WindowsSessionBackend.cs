using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Soltex.Audio.Sessions;

internal sealed class WindowsSessionBackend : ISessionBackend
{
    private const int MaximumEndpoints = 64, MaximumSessions = 256;
    private readonly IMMDeviceEnumerator _enumerator = CoreAudio.CreateEnumerator();
    private readonly List<EndpointLease> _endpoints = [];
    private readonly List<string> _discoveryErrors = [];
    private readonly HashSet<AudioSessionKey> _observedKeys = [];
    private long _nextDiscovery;
    private long _discoveryCount;
    internal SessionDiagnostics Diagnostics => new(_discoveryCount, _endpoints.Count, _endpoints.Sum(endpoint => endpoint.Sessions.Count));

    public AudioSessionSnapshot Capture()
    {
        if (Stopwatch.GetTimestamp() >= _nextDiscovery) Discover();
        List<AudioSessionEndpoint> endpoints = [];
        List<string> errors = [.. _discoveryErrors];
        foreach (EndpointLease endpoint in _endpoints.ToArray())
        {
            try
            {
                Check(endpoint.Device.GetState(out uint state));
                if (state != DeviceStateMask.Active) throw new InvalidOperationException("Endpoint is no longer active.");
                List<AudioSessionInfo> sessions = [];
                foreach (SessionLease session in endpoint.Sessions.ToArray())
                {
                    try { sessions.Add(session.Read(endpoint.Id)); }
                    catch (Exception exception) when (IsAudioFailure(exception))
                    {
                        errors.Add($"Session on {endpoint.Name}: {exception.Message}");
                        endpoint.Sessions.Remove(session); session.Dispose();
                    }
                }
                double? volume = null, peak = null; bool? muted = null;
                if (endpoint.Volume is { } control)
                {
                    Check(control.GetMasterVolumeLevelScalar(out float value)); volume = Scalar(value);
                    Check(control.GetMute(out bool mute)); muted = mute;
                }
                if (endpoint.Meter is { } meter) { Check(meter.GetPeakValue(out float value)); peak = Scalar(value); }
                endpoints.Add(new(endpoint.Id, endpoint.Name, SessionPolicy.Classify(endpoint.Name), volume, muted, peak, sessions.AsReadOnly()));
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                errors.Add($"{endpoint.Name}: {exception.Message}");
                _endpoints.Remove(endpoint); endpoint.Dispose();
            }
        }
        _observedKeys.Clear();
        foreach (AudioSessionInfo session in endpoints.SelectMany(endpoint => endpoint.Sessions)) _observedKeys.Add(session.Key);
        return new(DateTimeOffset.UtcNow, endpoints.AsReadOnly(), errors.AsReadOnly());
    }

    private void Discover()
    {
        _discoveryCount++;
        _nextDiscovery = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        foreach (EndpointLease old in _endpoints) old.Dispose();
        _endpoints.Clear(); _discoveryErrors.Clear();
        IMMDeviceCollection? collection = null;
        try
        {
            Check(_enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateMask.Active, out collection));
            Check(collection.GetCount(out uint count));
            if (count > MaximumEndpoints) _discoveryErrors.Add($"Endpoint limit {MaximumEndpoints} reached; inventory is partial.");
            int sessionBudget = MaximumSessions;
            for (uint index = 0; index < Math.Min(count, MaximumEndpoints); index++)
            {
                IMMDevice? device = null;
                EndpointLease? lease = null;
                try
                {
                    Check(collection.Item(index, out device));
                    lease = new(device); device = null;
                    lease.Connect(ref sessionBudget, _discoveryErrors);
                    _endpoints.Add(lease); lease = null;
                }
                catch (Exception exception) when (IsAudioFailure(exception)) { _discoveryErrors.Add($"Endpoint {index}: {exception.Message}"); }
                finally { lease?.Dispose(); Release(device); }
            }
        }
        catch (Exception exception) when (IsAudioFailure(exception)) { _discoveryErrors.Add(exception.Message); }
        finally { Release(collection); _nextDiscovery = Stopwatch.GetTimestamp() + Stopwatch.Frequency; }
    }

    public AudioSessionControlResult Control(AudioSessionKey key, double? volume, bool? mute)
    {
        bool writeIssued = false;
        IMMDevice? device = null;
        object? managerObject = null;
        ISessionEnumerator? sessions = null;
        try
        {
            if (!_observedKeys.Contains(key) || string.IsNullOrWhiteSpace(key.EndpointId) || string.IsNullOrWhiteSpace(key.SessionInstanceId) ||
                key.ProcessId <= 0 || key.ProcessStartTimeUtc is null)
                return new(false, false, null, "A verified session key from a current snapshot is required.");
            Check(_enumerator.GetDevice(key.EndpointId, out device));
            Check(device.GetState(out uint state));
            if (state != DeviceStateMask.Active) return new(false, false, null, "The endpoint is no longer active.");
            // This fresh manager/enumerator is deliberate for writes; meter polling reuses owned leases.
            managerObject = Activate(device, typeof(ISessionManager2).GUID);
            Check(((ISessionManager2)managerObject).GetSessionEnumerator(out sessions));
            Check(sessions.GetCount(out int count));
            if (count > MaximumSessions) return new(false, false, null, "Too many sessions to safely resolve the target.");
            for (int index = 0; index < count; index++)
            {
                object? raw = null;
                try
                {
                    Check(sessions.GetSession(index, out raw));
                    using SessionLease session = new(raw); raw = null;
                    Check(session.Control.GetSessionInstanceIdentifier(out string id));
                    if (!string.Equals(id, key.SessionInstanceId, StringComparison.Ordinal)) continue;
                    AudioSessionInfo before = session.Read(key.EndpointId, refreshIdentity: true);
                    string? rejected = SessionPolicy.Reject(key, before);
                    if (rejected is not null) return new(false, false, before, rejected);
                    // A second process lookup catches exit/PID reuse immediately before the setter.
                    if (ProcessIdentity(key.ProcessId).Start != key.ProcessStartTimeUtc)
                        return new(false, false, before, "The process exited or its PID was reused.");
                    Guid context = Guid.Empty;
                    writeIssued = true;
                    if (volume is { } scalar) Check(session.Volume.SetMasterVolume((float)scalar, ref context));
                    else Check(session.Volume.SetMute(mute!.Value, ref context));
                    AudioSessionInfo after = session.Read(key.EndpointId, refreshIdentity: true);
                    bool matches = SessionPolicy.Reject(key, after) is null &&
                        (volume is null || after.Volume is { } actual && Math.Abs(actual - volume.Value) <= 0.0001) &&
                        (mute is null || after.Muted == mute);
                    return new(matches, true, after, matches ? null : "Windows accepted the request but readback differs; refresh before retrying.");
                }
                finally { Release(raw); }
            }
            return new(false, false, null, "The session no longer exists on this endpoint.");
        }
        catch (Exception exception) when (IsAudioFailure(exception))
        {
            return new(false, writeIssued, null, $"Windows session control failed: {exception.Message}");
        }
        finally { Release(sessions); Release(managerObject); Release(device); }
    }

    private sealed class EndpointLease(IMMDevice device) : IDisposable
    {
        internal IMMDevice Device { get; } = device;
        internal string Id { get; private set; } = "";
        internal string Name { get; private set; } = "";
        internal IAudioEndpointVolume? Volume { get; private set; }
        internal IAudioMeterInformation? Meter { get; private set; }
        internal List<SessionLease> Sessions { get; } = [];

        internal void Connect(ref int budget, List<string> errors)
        {
            Check(Device.GetId(out string id)); Id = id; Name = ReadName(Device) ?? id;
            Volume = (IAudioEndpointVolume)Activate(Device, typeof(IAudioEndpointVolume).GUID);
            try { Meter = (IAudioMeterInformation)Activate(Device, typeof(IAudioMeterInformation).GUID); }
            catch (COMException exception) { errors.Add($"Meter unavailable for {Name}: {exception.Message}"); }
            object? manager = null; ISessionEnumerator? sessions = null;
            try
            {
                manager = Activate(Device, typeof(ISessionManager2).GUID);
                Check(((ISessionManager2)manager).GetSessionEnumerator(out sessions));
                Check(sessions.GetCount(out int count));
                if (count > budget) errors.Add($"Session limit {MaximumSessions} reached; inventory is partial.");
                int readCount = Math.Min(count, budget); budget -= readCount;
                for (int index = 0; index < readCount; index++)
                {
                    object? raw = null;
                    try { Check(sessions.GetSession(index, out raw)); Sessions.Add(new(raw)); raw = null; }
                    catch (COMException exception) { errors.Add($"Session {index} on {Name}: {exception.Message}"); }
                    finally { Release(raw); }
                }
            }
            finally { Release(sessions); Release(manager); }
        }

        public void Dispose()
        {
            foreach (SessionLease session in Sessions) session.Dispose();
            Sessions.Clear(); Release(Meter); Release(Volume); Release(Device);
        }
    }

    private sealed class SessionLease : IDisposable
    {
        private readonly object _raw;
        private (DateTimeOffset? Start, string? Path, string? Name) _identity;
        private int _identityPid = -1;
        internal ISessionControl2 Control { get; }
        internal ISessionVolume Volume { get; }
        private IAudioMeterInformation? Meter { get; }

        internal SessionLease(object raw)
        {
            _raw = raw; Control = (ISessionControl2)raw; Volume = (ISessionVolume)raw;
            Meter = raw as IAudioMeterInformation;
        }

        internal AudioSessionInfo Read(string endpointId, bool refreshIdentity = false)
        {
            Check(Control.GetSessionInstanceIdentifier(out string instance));
            Check(Control.GetState(out AudioSessionState state));
            int processHr = Control.GetProcessId(out uint pid);
            Check(processHr);
            int processId = pid <= int.MaxValue ? (int)pid : 0;
            int systemHr = Control.IsSystemSoundsSession(); Check(systemHr);
            bool system = systemHr == 0;
            if (_identityPid != processId || refreshIdentity)
            { _identity = ProcessIdentity(processId); _identityPid = processId; }
            Check(Control.GetDisplayName(out string name));
            Check(Volume.GetMasterVolume(out float volume));
            Check(Volume.GetMute(out bool mute));
            double? peak = null;
            if (Meter is not null) { Check(Meter.GetPeakValue(out float value)); peak = Scalar(value); }
            bool canControl = !system && processHr == 0 && processId > 0 && _identity.Start is not null && state != AudioSessionState.Expired;
            return new(new(endpointId, instance, processId, _identity.Start),
                string.IsNullOrWhiteSpace(name) ? system ? "System sounds" : _identity.Name ?? $"Process {processId}" : name,
                _identity.Path, state, Scalar(volume), mute, peak, system, canControl);
        }

        // All queried session interfaces share one RCW; release its one acquired native reference exactly once.
        public void Dispose() => Release(_raw);
    }

    private static (DateTimeOffset? Start, string? Path, string? Name) ProcessIdentity(int pid)
    {
        if (pid <= 0) return default;
        try
        {
            using Process process = Process.GetProcessById(pid);
            DateTimeOffset start = process.StartTime.ToUniversalTime();
            string? path = null;
            try { path = process.MainModule?.FileName; } catch (Win32Exception) { }
            return process.HasExited ? default : (start, path, process.ProcessName);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException) { return default; }
    }

    private static string? ReadName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            Check(device.OpenPropertyStore(CoreAudio.StorageRead, out store));
            PropertyKey key = CoreAudio.FriendlyNameKey;
            Check(store.GetValue(ref key, out PropVariant value));
            try { return value.ReadString(); }
            finally { _ = CoreAudio.PropVariantClear(ref value); }
        }
        finally { Release(store); }
    }

    private static object Activate(IMMDevice device, Guid iid)
    {
        Check(device.Activate(ref iid, CoreAudio.ClassContextAll, IntPtr.Zero, out object instance));
        return instance;
    }
    private static double? Scalar(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : null;
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static bool IsAudioFailure(Exception exception) => exception is COMException or InvalidCastException or InvalidOperationException or Win32Exception;
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    public void Dispose()
    {
        foreach (EndpointLease endpoint in _endpoints) endpoint.Dispose();
        _endpoints.Clear(); Release(_enumerator);
    }
}
