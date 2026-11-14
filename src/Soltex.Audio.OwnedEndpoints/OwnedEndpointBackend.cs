using Soltex.Audio.Broker;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.Transport;

namespace Soltex.Audio.OwnedEndpoints;

/// <summary>Composes Windows explicit endpoints with a separately installed, authorized Soltex transport.</summary>
public sealed class OwnedEndpointBackend : IHostAudioBackend
{
    public const string Prefix = "soltex-owned:";
    private readonly IHostAudioBackend windows;
    private readonly Func<OwnedAudioTransport> open;
    private readonly Func<string, OwnedAudioBus?> resolveIdentity;
    private readonly Dictionary<string, OwnedAudioBus?> identities = new(StringComparer.Ordinal);
    private OwnedAudioTransport? transport;
    private OwnedAudioRouter? router;
    private readonly TimeProvider clock;
    private DateTimeOffset nextAttempt = DateTimeOffset.MinValue;
    private TimeSpan retryDelay = InitialRetryDelay;
    private IReadOnlyList<HostEndpoint>? lastWindowsList;
    /// <summary>
    /// The owned transport is optional (most PCs never install it). Failed opens back off to five minutes so an idle
    /// helper does not probe the driver every few seconds; a change in Windows endpoints (such as installing it) retries at once.
    /// </summary>
    public static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5), MaximumRetryDelay = TimeSpan.FromMinutes(5);
    public string? TransportUnavailableReason { get; private set; }
    public long TransportOpenAttempts { get; private set; }
    public OwnedEndpointBackend() : this(new WindowsHostAudioBackend(), () => new OwnedAudioTransport(new BrokerAudioDevice())) { }
    public OwnedEndpointBackend(IHostAudioBackend windows, Func<OwnedAudioTransport> open, Func<string, OwnedAudioBus?>? resolveIdentity = null, TimeProvider? clock = null)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows)); this.open = open ?? throw new ArgumentNullException(nameof(open));
        this.resolveIdentity = resolveIdentity ?? ReadIdentity; this.clock = clock ?? TimeProvider.System;
    }
    public IReadOnlyList<HostEndpoint> Enumerate()
    {
        var windowsList = windows.Enumerate();
        // The Windows backend returns the same list instance until an endpoint notification produces a new one.
        bool endpointsChanged = lastWindowsList is not null && !ReferenceEquals(windowsList, lastWindowsList);
        lastWindowsList = windowsList;
        var endpoints = windowsList.ToList();
        for (int i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            OwnedAudioBus? identity;
            try { if (!identities.TryGetValue(endpoint.Id, out identity)) identities[endpoint.Id] = identity = resolveIdentity(endpoint.Id); }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            {
                // A disappearing endpoint must not invalidate the entire device list. Retry its metadata next time.
                endpoints[i] = endpoint with { IsActive = false }; continue;
            }
            if (identity is { } bus) endpoints[i] = endpoint with { TransportId = Prefix + bus };
        }
        if (transport is null && (endpointsChanged || clock.GetUtcNow() >= nextAttempt))
        {
            if (endpointsChanged) retryDelay = InitialRetryDelay;
            TransportOpenAttempts++;
            try { transport = open(); transport.Query(OwnedAudioBus.Game); router = new(transport); TransportUnavailableReason = null; retryDelay = InitialRetryDelay; }
            catch (Exception error)
            {
                transport?.Dispose(); transport = null; TransportUnavailableReason = error.Message;
                nextAttempt = clock.GetUtcNow() + retryDelay;
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaximumRetryDelay.Ticks));
            }
        }
        if (router?.Failure is { } failure) TransportUnavailableReason = failure + " Restart the audio workspace to reconnect the failed transport.";
        if (transport is null || router is null || router.Failure is not null) return endpoints;
        foreach (var bus in Enum.GetValues<OwnedAudioBus>())
        {
            bool paired = endpoints.Any(e => e.TransportId == Prefix + bus && e.IsActive && e.IsCapture == (bus >= OwnedAudioBus.Microphone));
            endpoints.Add(new(Prefix + bus, "Soltex " + bus + " · owned transport", bus < OwnedAudioBus.Microphone, paired) { TransportId = Prefix + bus });
        }
        return endpoints;
    }
    public IReadOnlyDictionary<string, float?> ReadInputPeaks(IReadOnlyCollection<string> captureIds)
    {
        var result = windows.ReadInputPeaks(captureIds.Where(id => !id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray()).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (string id in captureIds.Where(id => id.StartsWith(Prefix, StringComparison.Ordinal))) result[id] = null;
        return result;
    }
    public IHostAudioPath Create(string captureId, string renderId, DspSettings settings)
    {
        bool inputOwned = TryBus(captureId, out var input), outputOwned = TryBus(renderId, out var output);
        var inputBus = inputOwned ? input : identities.GetValueOrDefault(captureId);
        var outputBus = outputOwned ? output : identities.GetValueOrDefault(renderId);
        if (inputBus is not null && inputBus == outputBus) throw new InvalidOperationException("The selected input and output share one owned transport and would feed back.");
        if (!inputOwned && !outputOwned) return windows.Create(captureId, renderId, settings);
        if (router is null) throw new InvalidOperationException(TransportUnavailableReason ?? "Soltex owned transport is unavailable.");
        if (inputOwned && outputOwned) throw new InvalidOperationException("Owned endpoint feedback routes are not allowed.");
        if (inputOwned && input < OwnedAudioBus.Microphone) return new OwnedRenderPath(router, input, renderId, settings);
        if (outputOwned && output == OwnedAudioBus.Microphone) return new OwnedMicrophonePath(router, captureId, settings);
        throw new ArgumentException("Owned endpoint direction does not match the requested route.");
    }
    public IMicrophoneProbe CreateMicrophoneProbe(string endpointId) => windows.CreateMicrophoneProbe(endpointId);
    public void SetObserving(bool observing) => windows.SetObserving(observing);
    public IHostStreamMix CreateStreamMix(WasapiStreamMixConfiguration configuration)
    {
        var sourceBuses = configuration.Sources.Select(s => TryBus(s.CaptureEndpointId, out var bus) ? bus : identities.GetValueOrDefault(s.CaptureEndpointId)).Where(b => b is not null).ToHashSet();
        foreach (string destination in new[] { configuration.PersonalRenderEndpointId, configuration.StreamRenderEndpointId })
        {
            var destinationBus = TryBus(destination, out var bus) ? bus : identities.GetValueOrDefault(destination);
            if (destinationBus is not null && sourceBuses.Contains(destinationBus)) throw new InvalidOperationException("Stream output cannot feed its own owned transport source.");
        }
        if (router is null)
        {
            if (configuration.Sources.Any(s => TryBus(s.CaptureEndpointId, out _)) || TryBus(configuration.PersonalRenderEndpointId, out _) || TryBus(configuration.StreamRenderEndpointId, out _))
                throw new InvalidOperationException(TransportUnavailableReason ?? "Owned transport unavailable.");
            return windows.CreateStreamMix(configuration);
        }
        return new OwnedStreamMix(new WasapiStreamMixer(configuration,
            (id, _) => TryBus(id, out var bus) ? new OwnedWaveIn(router, bus) : null,
            (id, _) => TryBus(id, out var bus) ? new OwnedWavePlayer(router, bus) : null), router);
    }
    private sealed class OwnedStreamMix(WasapiStreamMixer mixer, OwnedAudioRouter router) : IHostStreamMix
    {
        public WasapiStreamMixStatus Status => router.Failure is { } error ? mixer.Status with { Running = false, Failure = error } : mixer.Status;
        public void Start() => mixer.Start();
        public void UpdateSource(string id, DspSettings dsp, StreamMixSendSettings personal, StreamMixSendSettings stream) => mixer.UpdateSource(id, dsp, personal, stream);
        public void Dispose() => mixer.Dispose();
    }
    public void Dispose() { try { router?.Dispose(); } finally { windows.Dispose(); } }
    private static bool TryBus(string id, out OwnedAudioBus bus)
    { bus = default; return id.StartsWith(Prefix, StringComparison.Ordinal) && Enum.TryParse(id[Prefix.Length..], out bus) && Enum.IsDefined(bus); }
    private static OwnedAudioBus? ReadIdentity(string endpointId)
    {
        using var enumerator = new MMDeviceEnumerator(); using var endpoint = enumerator.GetDevice(endpointId);
        var key = new PropertyKey(new Guid("f7c49f60-0d6c-4f42-9a51-9277315aa100"), 1);
        if (!endpoint.Properties.TryGetValue<string>(key, out var value)) return null;
        return Enum.TryParse<OwnedAudioBus>(value, out var bus) && Enum.IsDefined(bus) ? bus : null;
    }
}
