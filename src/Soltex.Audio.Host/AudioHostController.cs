using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

/// <summary>Serialized graph owner. Explicit start only; requested profile is separate from confirmed paths.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "IAsyncDisposable drains the MTA owner before releasing its queue.")]
public sealed partial class AudioHostController : IAsyncDisposable
{
    private sealed record ActivePath(string Capture, string Render, IHostAudioPath Path, DspSettings Settings);
    private readonly BlockingCollection<Action> commands = new(64);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifetime = new();
    private readonly string? profilePath;
    private readonly Func<IHostAudioBackend> factory;
    private readonly bool automaticPolling;
    private readonly Dictionary<AudioBus, ActivePath> active = [];
    private readonly Dictionary<AudioBus, string> reasons = [];
    private readonly Dictionary<(AudioBus Bus, string Endpoint), DateTimeOffset> openFailures = [];
    private readonly HashSet<AudioBus> failedRetirement = [];
    private IHostAudioBackend? backend;
    private AudioHostProfile profile = AudioHostProfile.CreateDefault();
    private IReadOnlyList<HostEndpoint> endpoints = [];
    private IReadOnlyDictionary<string, float?> peaks = new Dictionary<string, float?>();
    private bool running, disposing;
    private string? lastError;
    private Task? disposal;
    private IMicrophoneProbe? microphoneProbe;
    private MicrophoneProbeStatus? lastProbe;
    private DateTimeOffset probeDeadline;
    private readonly TimeProvider clock;
    private DateTimeOffset lastRequest;
    private bool observing = true;

    public AudioHostController(string? profilePath = null, Func<IHostAudioBackend>? backendFactory = null, bool automaticPolling = true, TimeProvider? timeProvider = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        this.profilePath = profilePath; factory = backendFactory ?? (() => new WindowsHostAudioBackend());
        this.automaticPolling = automaticPolling;
        Thread thread = new(Work) { IsBackground = true, Name = "Soltex independent audio graph" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }

    public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Enqueue(() => { Tick(); return Snapshot(); }, cancellationToken);
    public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) => Enqueue(() =>
    { if (streamMix is not null || streamFailure is not null) throw new InvalidOperationException("Stop stream mix before starting normal channels."); if (failedRetirement.Count > 0) throw new InvalidOperationException("Restart the audio host before opening audio; previous resources did not confirm shutdown."); running = true; openFailures.Clear(); Tick(); return Snapshot(); }, cancellationToken);
    public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => Enqueue(() =>
    { running = false; StopAll(); return Snapshot(); }, cancellationToken);
    public Task<HostSnapshot> ApplyProfileAsync(AudioHostProfile requested, CancellationToken cancellationToken = default)
    {
        AudioHostProfile copy = HostProfileStore.Validate(requested);
        return Enqueue(() =>
        {
            ValidateStreamChange(copy);
            // Persist validated desired state before changing any streams. A save failure changes neither.
            if (profilePath is not null) HostProfileStore.Save(profilePath, copy);
            var changedSelections = copy.Channels.Where(c => profile.Channels.Single(old => old.Bus == c.Bus).RenderEndpointId != c.RenderEndpointId)
                .Select(c => c.Bus).ToHashSet();
            profile = copy; openFailures.Clear(); Tick(changedSelections); return Snapshot();
        }, cancellationToken);
    }

    public Task<HostSnapshot> StartMicrophoneTestAsync(string endpointId, CancellationToken cancellationToken = default) => Enqueue(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (streamMix is not null || active.Values.Any(p => p.Capture == endpointId)) throw new InvalidOperationException("Stop audio before testing this capture input; its active stream already owns the source.");
        endpoints = backend!.Enumerate();
        var input = endpoints.FirstOrDefault(e => e.Id == endpointId && e.IsCapture && e.IsActive);
        if (input is null) throw new ArgumentException("The selected capture input is unavailable.", nameof(endpointId));
        if (input.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose the physical microphone input, not Sonar.", nameof(endpointId));
        StopProbe();
        if (failedRetirement.Contains(AudioBus.Microphone)) throw new InvalidOperationException("Restart the audio host before testing again; the previous microphone resource did not confirm shutdown.");
        var probe = backend.CreateMicrophoneProbe(endpointId);
        try { probe.Start(); microphoneProbe = probe; probeDeadline = clock.GetUtcNow().AddSeconds(15); }
        catch { probe.Dispose(); throw; }
        return Snapshot();
    }, cancellationToken);
    public Task<HostSnapshot> StopMicrophoneTestAsync(CancellationToken cancellationToken = default) => Enqueue(() => { StopProbe(); return Snapshot(); }, cancellationToken);
    private void StopProbe()
    {
        if (microphoneProbe is not { } probe) return;
        microphoneProbe = null;
        try { lastProbe = probe.Status with { Running = false }; }
        finally { try { probe.Dispose(); } catch (Exception error) { failedRetirement.Add(AudioBus.Microphone); lastError = "Microphone test did not confirm shutdown: " + error.Message; } }
    }

    private async Task<HostSnapshot> Enqueue(Func<HostSnapshot> operation, CancellationToken token)
    {
        await ready.Task.WaitAsync(token).ConfigureAwait(false);
        TaskCompletionSource<HostSnapshot> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(disposing, this);
            token.ThrowIfCancellationRequested();
            if (!commands.TryAdd(() =>
            {
                if (token.IsCancellationRequested) { result.TrySetCanceled(token); return; }
                try { ResumeObservation(); result.TrySetResult(operation()); } catch (Exception error) { result.TrySetException(error); }
            })) throw new InvalidOperationException("Audio host command queue is busy.");
        }
        return await result.Task.ConfigureAwait(false);
    }

    private void Work()
    {
        try
        {
            backend = factory();
            if (profilePath is not null && File.Exists(profilePath))
            {
                try { profile = HostProfileStore.Load(profilePath); }
                catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException)
                { lastError = "Saved profile was not loaded: " + error.Message; }
            }
            lastRequest = clock.GetUtcNow();
            ready.TrySetResult();
            while (!commands.IsCompleted)
            {
                // Paused and idle: nothing to keep current, so wait for the next request without ticking.
                int wait = automaticPolling && (observing || HasActiveWork) ? PollingDelayMilliseconds : Timeout.Infinite;
                if (commands.TryTake(out var command, wait)) command();
                else if (automaticPolling && !commands.IsCompleted)
                {
                    if (ShouldPauseObservation()) PauseObservation();
                    else Tick();
                }
            }
        }
        catch (Exception error) { ready.TrySetException(error); lastError = error.Message; completed.TrySetException(error); }
        finally
        {
            StopAll();
            try { backend?.Dispose(); } catch (Exception error) { completed.TrySetException(error); }
            if (failedRetirement.Count > 0) completed.TrySetException(new IOException("One or more audio resources did not confirm clean shutdown."));
            completed.TrySetResult();
        }
    }

    /// <summary>With audio stopped and no request for this long, the helper stops listening for device changes.</summary>
    internal static readonly TimeSpan ObservationIdleTimeout = TimeSpan.FromSeconds(30);
    internal bool IsObserving => Volatile.Read(ref observing);
    private bool ShouldPauseObservation() => observing && !HasActiveWork && clock.GetUtcNow() - lastRequest >= ObservationIdleTimeout;

    private void PauseObservation()
    {
        try { backend!.SetObserving(false); Volatile.Write(ref observing, false); }
        catch (Exception error) { lastError = "Device observation could not pause: " + error.Message; lastRequest = clock.GetUtcNow(); }
    }

    private void ResumeObservation()
    {
        lastRequest = clock.GetUtcNow();
        if (observing) return;
        backend!.SetObserving(true);
        Volatile.Write(ref observing, true);
    }

    /// <summary>Owned audio needs prompt fallback and meter updates; an idle helper only keeps its device view current.</summary>
    internal const int ActivePollingMilliseconds = 500, IdlePollingMilliseconds = 2000;
    private bool HasActiveWork => running || streamMix is not null || streamFailure is not null || microphoneProbe is not null || active.Count > 0;
    private int PollingDelayMilliseconds => HasActiveWork ? ActivePollingMilliseconds : IdlePollingMilliseconds;

    private void Tick(HashSet<AudioBus>? changedSelections = null)
    {
        try { TickCore(changedSelections); }
        catch (Exception error) { lastError = "Audio graph operation failed: " + error.Message; }
    }

    private void TickCore(HashSet<AudioBus>? changedSelections)
    {
        if (microphoneProbe is not null && (clock.GetUtcNow() >= probeDeadline || !microphoneProbe.Status.Running)) StopProbe();
        // Failed stream callbacks must drain even when device enumeration is also failing.
        UpdateStreamMix();
        try { endpoints = backend!.Enumerate(); }
        catch (Exception error) { lastError = "Device observation failed: " + error.Message; peaks = new Dictionary<string, float?>(); return; }
        if (running)
        {
            foreach (var channel in profile.Channels)
            {
                try { Reconcile(channel, changedSelections?.Contains(channel.Bus) == true); }
                catch (Exception error) { reasons[channel.Bus] = "Channel update failed: " + error.Message; }
            }
            // Both Game and Chat must have confirmed matching destinations before balance applies.
            foreach (var channel in profile.Channels.Where(c => c.Bus is AudioBus.Game or AudioBus.Chat))
            {
                if (!active.TryGetValue(channel.Bus, out var path)) continue;
                var desired = EffectiveFor(channel, path.Render);
                if (desired != path.Settings)
                {
                    try { path.Path.Update(desired); active[channel.Bus] = path with { Settings = desired }; }
                    catch (Exception error) { reasons[channel.Bus] = "Balance update failed: " + error.Message; }
                }
            }
        }
        try { peaks = backend!.ReadInputPeaks(active.Values.Select(p => p.Capture).Distinct(StringComparer.Ordinal).ToArray()); }
        catch (Exception error) { peaks = new Dictionary<string, float?>(); lastError = "Input meters unavailable: " + error.Message; }
    }

    private void Reconcile(HostChannelProfile channel, bool manualChange)
    {
        active.TryGetValue(channel.Bus, out var old);
        if (failedRetirement.Contains(channel.Bus)) { reasons[channel.Bus] = "An audio resource failed to close. Stop and restart the host before reopening this channel."; return; }
        if (!channel.Enabled) { Retire(channel.Bus); reasons[channel.Bus] = "Channel is disabled."; return; }
        if (old is not null && (!old.Path.Status.Running || old.Path.Status.Failure is not null))
        { openFailures[(channel.Bus, old.Render)] = DateTimeOffset.UtcNow.AddSeconds(5); Retire(channel.Bus); old = null; }
        HostEndpoint? capture = endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId && e.IsCapture && e.IsActive);
        var eligible = endpoints.Where(e => !openFailures.TryGetValue((channel.Bus, e.Id), out var until) || DateTimeOffset.UtcNow >= until).ToArray();
        string? selected = SelectOutput(channel, eligible, old?.Render, manualChange);
        HostEndpoint? output = endpoints.FirstOrDefault(e => e.Id == selected && !e.IsCapture && e.IsActive);
        string? invalid = GraphError(channel, capture, output);
        if (invalid is not null)
        {
            Retire(channel.Bus); reasons[channel.Bus] = invalid; return;
        }
        if (old is not null && old.Capture == capture!.Id && old.Render == output!.Id)
        {
            DspSettings desired = EffectiveFor(channel, output.Id);
            if (desired != old.Settings) { old.Path.Update(desired); active[channel.Bus] = old with { Settings = desired }; }
            reasons[channel.Bus] = output.Id == channel.RenderEndpointId ? "Selected destination." : "Using an eligible fallback destination.";
            return;
        }
        IHostAudioPath? replacement = null;
        try
        {
            DspSettings settings = EffectiveFor(channel, output!.Id);
            replacement = backend!.Create(capture!.Id, output.Id, settings with { Mute = true });
            replacement.Start();
            if (!replacement.Status.Running || replacement.Status.Failure is not null)
                throw new IOException(replacement.Status.Failure ?? "New destination did not start.");
            // New path opens silent. Ramp down the old path before releasing it, then ramp in the new one.
            if (old is not null)
            {
                old.Path.Update(old.Settings with { Mute = true, GainSmoothingMs = 20 });
                Thread.Sleep(25);
                if (!Retire(channel.Bus)) throw new IOException("Previous audio resources could not be fully closed.");
            }
            replacement.Update(settings);
            active[channel.Bus] = new(capture.Id, output.Id, replacement, settings); replacement = null;
            reasons[channel.Bus] = output.Id == channel.RenderEndpointId ? "Selected destination." : "Using an eligible fallback destination.";
            openFailures.Remove((channel.Bus, output.Id));
        }
        catch (Exception error)
        {
            try { replacement?.Dispose(); } catch (Exception cleanup) { lastError = cleanup.Message; }
            openFailures[(channel.Bus, output!.Id)] = DateTimeOffset.UtcNow.AddSeconds(5);
            if (old is not null && active.ContainsKey(channel.Bus) && !channel.ExcludedOutputIds.Contains(old.Render, StringComparer.Ordinal))
            { old.Path.Update(old.Settings); active[channel.Bus] = old;
                reasons[channel.Bus] = "Switch failed; previous destination retained: " + error.Message; }
            else { Retire(channel.Bus); reasons[channel.Bus] = "Destination unavailable: " + error.Message; }
        }
    }

    private string? GraphError(HostChannelProfile channel, HostEndpoint? capture, HostEndpoint? output)
    {
        if (capture is null) return "Select an available capture source; independent virtual endpoints are supplied separately.";
        if (output is null) return "No eligible output is available. Device preferences were retained.";
        if (capture.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase) || output.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase))
            return "Sonar endpoints are excluded from the independent graph.";
        if (capture.TransportId is not null && capture.TransportId == output.TransportId)
            return "Capture and output share one cable transport; feedback route rejected.";
        foreach (var other in profile.Channels.Where(c => c.Enabled && c.Bus != channel.Bus))
        {
            HostEndpoint? otherCapture = endpoints.FirstOrDefault(e => e.Id == other.CaptureEndpointId);
            active.TryGetValue(other.Bus, out var otherActive);
            string? otherSelected = SelectOutput(other, endpoints, otherActive?.Render);
            HostEndpoint? otherOutput = endpoints.FirstOrDefault(e => e.Id == otherSelected);
            HostEndpoint? otherConfirmedOutput = endpoints.FirstOrDefault(e => e.Id == otherActive?.Render);
            if (capture.TransportId is not null && capture.TransportId == otherCapture?.TransportId)
                return "Two channels cannot claim the same cable transport.";
            if ((output.TransportId is not null && output.TransportId == otherCapture?.TransportId) ||
                (capture.TransportId is not null && (capture.TransportId == otherOutput?.TransportId || capture.TransportId == otherConfirmedOutput?.TransportId)))
                return "Cross-channel cable feedback or monitoring route rejected.";
        }
        return null;
    }

    public static string? SelectOutput(HostChannelProfile channel, IReadOnlyList<HostEndpoint> endpoints, string? currentId, bool manualChange = false)
    {
        bool Eligible(string? id) => id is not null && !channel.ExcludedOutputIds.Contains(id, StringComparer.Ordinal) &&
            endpoints.Any(e => e.Id == id && !e.IsCapture && e.IsActive);
        if (manualChange && Eligible(channel.RenderEndpointId)) return channel.RenderEndpointId;
        if (!channel.AutoReturn && Eligible(currentId)) return currentId;
        if (channel.ManualPin && Eligible(channel.RenderEndpointId)) return channel.RenderEndpointId;
        foreach (string id in channel.OutputPriorityIds) if (Eligible(id)) return id;
        if (Eligible(channel.RenderEndpointId)) return channel.RenderEndpointId;
        return Eligible(currentId) ? currentId : null;
    }

    public static DspSettings EffectiveDsp(AudioHostProfile profile, HostChannelProfile channel, string confirmedOutput)
    {
        double balance = 1;
        var game = profile.Channels.Single(c => c.Bus == AudioBus.Game);
        var chat = profile.Channels.Single(c => c.Bus == AudioBus.Chat);
        // Balance requires an explicitly shared configured output, not unrelated hardware.
        if (channel.Bus is AudioBus.Game or AudioBus.Chat && game.Enabled && chat.Enabled &&
            game.RenderEndpointId == chat.RenderEndpointId && game.RenderEndpointId == confirmedOutput)
        { var gains = ChatMix.Gains(profile.ChatMix); balance = channel.Bus == AudioBus.Game ? gains.Game : gains.Chat; }
        bool playback = channel.Bus != AudioBus.Microphone;
        double gain = channel.Dsp.GainDb + channel.GainDb + (playback ? profile.MasterGainDb : 0) + (balance > 0 ? 20 * Math.Log10(balance) : -80);
        return channel.Dsp with { GainDb = Math.Clamp(gain, -80, 24),
            Mute = channel.Dsp.Mute || channel.Mute || (playback && profile.MasterMute) || balance < .0000001 };
    }

    private DspSettings EffectiveFor(HostChannelProfile channel, string output)
    {
        AudioBus other = channel.Bus == AudioBus.Game ? AudioBus.Chat : AudioBus.Game;
        bool shared = channel.Bus is AudioBus.Game or AudioBus.Chat && active.TryGetValue(other, out var peer) &&
            peer.Render == output && peer.Path.Status.Running && peer.Path.Status.Failure is null;
        return EffectiveDsp(shared ? profile : profile with { ChatMix = 0 }, channel, output);
    }

    private bool Retire(AudioBus bus)
    {
        if (active.Remove(bus, out var path))
        { try { path.Path.Dispose(); } catch (Exception error) { lastError = "Closing " + bus + ": " + error.Message; failedRetirement.Add(bus); return false; } }
        return true;
    }
    private void StopAll() { StopStreamMix(); streamFailure = null; StopProbe(); foreach (var bus in active.Keys.ToArray()) Retire(bus); peaks = new Dictionary<string, float?>(); }
    private HostSnapshot Snapshot() => new(streamMix?.Status.Running == true || active.Values.Any(p => p.Path.Status.Running && p.Path.Status.Failure is null), profile with { StreamMix = profile.StreamMix with { Sends = profile.StreamMix.Sends.ToArray() }, Channels = profile.Channels.Select(c => c with
        { OutputPriorityIds = c.OutputPriorityIds.ToArray(), ExcludedOutputIds = c.ExcludedOutputIds.ToArray(), Dsp = c.Dsp with { Bands = c.Dsp.Bands.ToArray() } }).ToArray() }, profile.Channels.Select(c =>
    {
        active.TryGetValue(c.Bus, out var path); BridgeStatus? status = path?.Path.Status;
        string reason = reasons.GetValueOrDefault(c.Bus) ?? "Stopped. Start explicitly to open configured audio paths.";
        return new HostChannelStatus(c.Bus, path is not null ? (status!.Failure is null ? "Running" : "Failed") :
            !running || !c.Enabled ? "Stopped" : "Unavailable", path?.Capture, path?.Render, reason,
            path is not null && peaks.TryGetValue(path.Capture, out var peak) ? peak : null, status);
    }).ToArray(), endpoints.ToArray(), lastError) { StartRequested = running || streamMix is not null, StreamMix = streamMix?.Status ?? streamFailure, MicrophoneTest = microphoneProbe?.Status ?? lastProbe };

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            if (disposal is not null) return new(disposal);
            disposing = true; commands.CompleteAdding();
            disposal = Finish(); return new(disposal);
        }
    }
    private async Task Finish() { try { await completed.Task.ConfigureAwait(false); } finally { commands.Dispose(); } }
}
