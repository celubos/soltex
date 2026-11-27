using System.Diagnostics;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

public interface IHostAudioPath : IDisposable
{
    BridgeStatus Status { get; }
    void Start();
    void Update(DspSettings settings);
    /// <summary>Starts a short fade to silence without waiting; Dispose then waits only for what is left of it.</summary>
    void BeginStop() { }
}

/// <summary>All methods execute on the controller's single MTA owner.</summary>
public interface IHostAudioBackend : IDisposable
{
    IReadOnlyList<HostEndpoint> Enumerate();
    IReadOnlyDictionary<string, float?> ReadInputPeaks(IReadOnlyCollection<string> captureIds);
    IHostStreamMix CreateStreamMix(WasapiStreamMixConfiguration configuration) => throw new NotSupportedException("Stream mix is unavailable in this backend.");
    IMicrophoneProbe CreateMicrophoneProbe(string endpointId) => throw new NotSupportedException("Input testing is unavailable in this backend.");
    IHostAudioPath Create(string captureId, string renderId, DspSettings settings);
    /// <summary>
    /// False while nothing needs device information (audio stopped, nobody asking): the backend may stop listening
    /// for device changes and keep returning its last list. True again before any request; the next read is fresh.
    /// </summary>
    void SetObserving(bool observing) { }
}

/// <summary>
/// Decides when the Windows endpoint list must be read again. With endpoint notifications the list is re-read only
/// after Windows reports a change (plus a slow safety reconciliation); without them it falls back to one-second polling.
/// Enumerating every endpoint and its properties is the helper's dominant idle cost, so it must not run on a timer.
/// </summary>
internal sealed class EndpointObservationCache(TimeProvider clock)
{
    public static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(1);
    private int changed = 1;
    private DateTimeOffset lastRead = DateTimeOffset.MinValue;
    public bool NotificationsAvailable { get; private set; }
    public long Reads { get; private set; }

    /// <summary>Called from the device notification thread.</summary>
    public void MarkChanged() => Volatile.Write(ref changed, 1);

    public bool NeedsRead()
    {
        if (Volatile.Read(ref changed) != 0) return true;
        TimeSpan age = clock.GetUtcNow() - lastRead;
        return age >= (NotificationsAvailable ? ReconcileInterval : PollingInterval);
    }

    /// <summary>Clears the change flag before reading so a notification that races the read schedules another one.</summary>
    public void BeginRead() { Volatile.Write(ref changed, 0); lastRead = clock.GetUtcNow(); Reads++; }

    public void Completed(bool notificationsAvailable) => NotificationsAvailable = notificationsAvailable;

    /// <summary>A failed read is retried at the polling rate, never in a tight loop.</summary>
    public void Failed() => NotificationsAvailable = false;
}

public sealed class WindowsHostAudioBackend : IHostAudioBackend
{
    private readonly AudioDeviceService devices = new();
    private readonly EndpointObservationCache observation = new(TimeProvider.System);
    private IReadOnlyList<HostEndpoint> cached = [];
    private Exception? enumerationFailure;
    private AudioDeviceSnapshot? published;
    private bool listening = true;

    public WindowsHostAudioBackend() => devices.SnapshotChanged += (_, snapshot) =>
    {
        // The device service already captured a fresh list for this notification; reuse it instead of reading twice.
        Volatile.Write(ref published, snapshot);
        observation.MarkChanged();
    };

    public void SetObserving(bool observing)
    {
        if (observing == listening) return;
        listening = observing;
        devices.SetNotificationsEnabledAsync(observing).GetAwaiter().GetResult();
        // Anything may have changed while notifications were paused.
        if (observing) observation.MarkChanged();
    }

    public IReadOnlyList<HostEndpoint> Enumerate()
    {
        if (!listening || !observation.NeedsRead())
        {
            if (enumerationFailure is not null) throw new IOException("Windows endpoint observation remains unavailable.", enumerationFailure);
            return cached;
        }
        observation.BeginRead();
        try
        {
            var snapshot = Interlocked.Exchange(ref published, null) ?? devices.GetSnapshotAsync().GetAwaiter().GetResult();
            if (snapshot.State != AudioObservationState.Current)
                throw new IOException("A complete Windows endpoint observation is unavailable.");
            cached = snapshot.Endpoints.Select(e => new HostEndpoint(e.EndpointId, e.FullName,
                e.Direction == AudioEndpointDirection.Capture, e.State == AudioEndpointState.Active)
            {
                // The base cable has alternate render pins sharing one capture transport.
                TransportId = e.FullName.Contains("(VB-Audio Virtual Cable)", StringComparison.OrdinalIgnoreCase) ? "vb-audio-base-cable" : null
            }).ToArray();
            enumerationFailure = null;
            observation.Completed(snapshot.NotificationsAvailable);
            return cached;
        }
        catch (Exception error) { enumerationFailure = error; observation.Failed(); throw; }
    }
    public IReadOnlyDictionary<string, float?> ReadInputPeaks(IReadOnlyCollection<string> captureIds) => captureIds.Count == 0
        ? new Dictionary<string, float?>(StringComparer.Ordinal)
        : devices.ReadPeaksAsync(captureIds).GetAwaiter().GetResult().ToDictionary(p => p.EndpointId, p => p.Peak, StringComparer.Ordinal);
    public IHostStreamMix CreateStreamMix(WasapiStreamMixConfiguration configuration) => new WindowsStreamMix(new(configuration));
    public IMicrophoneProbe CreateMicrophoneProbe(string endpointId) => new WindowsMicrophoneProbe(endpointId);
    public IHostAudioPath Create(string captureId, string renderId, DspSettings settings) => new WindowsPath(new(new(captureId, renderId) { Dsp = settings }));
    public void Dispose() => devices.DisposeAsync().AsTask().GetAwaiter().GetResult();
    private sealed class WindowsStreamMix(WasapiStreamMixer mixer) : IHostStreamMix
    {
        public WasapiStreamMixStatus Status => mixer.Status;
        public void Start() => mixer.Start();
        public void UpdateSource(string id, DspSettings dsp, StreamMixSendSettings personal, StreamMixSendSettings stream) => mixer.UpdateSource(id, dsp, personal, stream);
        public void Dispose() => mixer.Dispose();
    }
    private sealed class WindowsPath(WasapiBridge bridge) : IHostAudioPath
    {
        public BridgeStatus Status => bridge.Status;
        public void Start() => bridge.Start();
        public void Update(DspSettings settings) => bridge.UpdateSettings(settings);
        public void BeginStop() => bridge.BeginStop();
        public void Dispose() => bridge.Dispose();
    }
}
