using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

public sealed partial class IndependentMixerWorkspace : UserControl, IAsyncDisposable
{
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer levels = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private IIndependentMixerConnection? connection;
    private AudioHostProfile confirmed = AudioHostProfile.CreateDefault();
    private IReadOnlyList<HostEndpoint> endpoints = [];
    private Task? initialization, shutdown;
    private IndependentMixerMicrophoneWindow? microphoneWindow;
    private IndependentMixerStreamWindow? streamWindow;
    private HostSnapshot? latestSnapshot;
    private long draftRevision;
    private readonly HashSet<AudioBus> pendingGains = [], pendingMutes = [];
    private bool pendingMasterGain, pendingMasterMute, pendingBalance;
    private bool applying, ready, active, reading, disposed, levelsPending, routingDirty, reconcileBeforeMutation;
    /// <summary>How long a failed or refused change stays on the status line before routine status replaces it.</summary>
    private static readonly TimeSpan ProblemNoticeDuration = TimeSpan.FromSeconds(8);
    private DateTime problemUntil;
    public ObservableCollection<IndependentMixerChannel> Channels { get; } = [];
    public ObservableCollection<IndependentEndpointChoice> OutputChoices { get; } = [];
    public IReadOnlyList<IndependentEndpointChoice> AvailableOutputs => OutputChoices.ToArray();
    public event EventHandler? ProcessingRequested;
    public event EventHandler? OutputsChanged;
    /// <summary>Raised on the UI thread after every confirmed host snapshot is applied.</summary>
    public event EventHandler? StatusApplied;
    /// <summary>The last snapshot confirmed by the host, or null before the first connection.</summary>
    public HostSnapshot? LatestSnapshot => latestSnapshot;
    public bool IsHostInitialized => ready;
    public string StatusText => Feedback.Text;
    /// <summary>
    /// True for a few seconds after a change failed or was refused. The status line then shows the reason, and
    /// routine status from polling does not replace it, so views that hide routine status should show it.
    /// </summary>
    public bool LastOperationFailed => DateTime.UtcNow < problemUntil;
    /// <summary>The default location of the audio engine next to the app.</summary>
    public static string DefaultHostExecutablePath => Path.Combine(AppContext.BaseDirectory, "Soltex.Audio.Host", "Soltex.Audio.Host.Service.exe");
    /// <summary>True when the owned graph or the stream mix is confirmed running by the host.</summary>
    public bool IsAudioRunning => latestSnapshot is { } s && (s.Running || s.StartRequested || s.StreamMix?.Running == true);
    /// <summary>
    /// Refreshes host state once without enabling presentation polling (tray, quit and the Microphone tab). When the
    /// first connection failed, this retries it, so a view that only reads state recovers on its own.
    /// </summary>
    public async Task RefreshStateAsync()
    {
        if (disposed || connection is null || initialization is { IsCompleted: false }) return;
        if (!ready) { await RefreshAsync(); return; }
        try { ApplyStatus(await connection.ReadAsync(lifetime.Token)); }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException) { if (!disposed) LoseConnection(error); }
    }

    /// <summary>
    /// True after a read of a connected engine failed, until the next read succeeds. <see cref="LatestSnapshot"/> then
    /// still holds the last confirmed state, which may no longer be true: views show the connection instead of it.
    /// </summary>
    public bool ConnectionLost { get; private set; }

    /// <summary>A read failed: meters go back to "not measured" and views are told the state is unknown.</summary>
    private void LoseConnection(Exception error)
    {
        if (!ready || error is OperationCanceledException && lifetime.IsCancellationRequested) return;
        foreach (var row in Channels)
        {
            row.Peak = -1;
            row.State = "Not connected";
            row.Reason = "The audio engine isn't answering, so this channel's state is unknown.";
        }
        bool changed = !ConnectionLost;
        ConnectionLost = true;
        if (!LastOperationFailed) Feedback.Text = "Audio connection lost: " + error.Message;
        if (changed) StatusApplied?.Invoke(this, EventArgs.Empty);
    }

    private void ReportProblem(string text) { problemUntil = DateTime.UtcNow + ProblemNoticeDuration; Feedback.Text = text; }
    private void ClearProblem() => problemUntil = default;

    public IndependentMixerWorkspace()
    {
        InitializeComponent(); DataContext = this;
        foreach (AudioBus bus in new[] { AudioBus.Microphone, AudioBus.Game, AudioBus.Chat, AudioBus.Media, AudioBus.Aux })
        {
            IndependentMixerChannel row = new(bus);
            row.PropertyChanged += RowChanged; Channels.Add(row);
        }
        polling.Tick += async (_, _) => { if (active && !reading) await RefreshAsync(); };
        levels.Tick += async (_, _) => { levels.Stop(); await ApplyLevelsAsync(); };
        Actions.IsEnabled = false;
    }

    public Task InitializeAsync(string preferencesDirectory, string? hostExecutablePath = null) =>
        initialization ??= InitializeCoreAsync(new IndependentMixerConnection(preferencesDirectory, hostExecutablePath ?? DefaultHostExecutablePath));

    public Task InitializeAsync(IIndependentMixerConnection ownedConnection)
    {
        ArgumentNullException.ThrowIfNull(ownedConnection);
        if (initialization is not null) throw new InvalidOperationException("This workspace already owns a connection.");
        return initialization = InitializeCoreAsync(ownedConnection);
    }

    private async Task InitializeCoreAsync(IIndependentMixerConnection ownedConnection)
    {
        connection = ownedConnection;
        try
        {
            HostSnapshot snapshot = await connection.ConnectAsync(lifetime.Token);
            if (disposed) return;
            Populate(snapshot.Profile); ready = true; ApplyStatus(snapshot);
        }
        catch (Exception error) when (Expected(error)) { Feedback.Text = "Audio isn't available: " + error.Message; }
        finally { if (!disposed) Actions.IsEnabled = true; }
    }

    public void Activate() { if (disposed) return; active = true; polling.Start(); }
    public void Deactivate() { active = false; polling.Stop(); }

    private async Task RefreshAsync()
    {
        if (disposed || connection is null || reading) return;
        reading = true;
        try
        {
            await operations.WaitAsync(lifetime.Token);
            try
            {
                var snapshot = ready ? await connection.ReadAsync(lifetime.Token) : await connection.ConnectAsync(lifetime.Token);
                if (disposed) return;
                if (!ready) { Populate(snapshot.Profile); ready = true; }
                ApplyStatus(snapshot);
            }
            finally { operations.Release(); }
        }
        catch (Exception error) when (Expected(error))
        {
            if (disposed) return;
            if (ready) LoseConnection(error);
            else if (!LastOperationFailed) Feedback.Text = "Audio isn't available: " + error.Message;
        }
        finally { reading = false; }
    }

    private void Populate(AudioHostProfile profile, bool adoptConfirmed = true)
    {
        applying = true;
        try
        {
            if (adoptConfirmed) confirmed = profile;
            ProfileName.Text = profile.Name; MasterGain.Value = profile.MasterGainDb;
            MasterMute.IsChecked = profile.MasterMute; Balance.Value = profile.ChatMix;
            foreach (var row in Channels) row.Load(profile.Channels.Single(c => c.Bus == row.Bus));
            MasterValue.Text = profile.MasterGainDb.ToString("F1", CultureInfo.CurrentCulture) + " dB";
            routingDirty = false; levelsPending = false; pendingGains.Clear(); pendingMutes.Clear();
            pendingMasterGain = false; pendingMasterMute = false; pendingBalance = false;
        }
        finally { applying = false; }
    }

    private void ApplyStatus(HostSnapshot snapshot)
    {
        ConnectionLost = false;
        confirmed = snapshot.Profile; endpoints = snapshot.Endpoints;
        latestSnapshot = snapshot; microphoneWindow?.Observe(snapshot); streamWindow?.Observe(snapshot);
        StatusApplied?.Invoke(this, EventArgs.Empty);
        HostEndpoint[] activeOutputs = endpoints.Where(e => !e.IsCapture && e.IsActive).ToArray();
        var outputs = activeOutputs.Select(e => new IndependentEndpointChoice(e.Id, IndependentEndpointChoice.Describe(e, DuplicateSuffix(activeOutputs, e)))).ToArray();
        if (!OutputChoices.SequenceEqual(outputs))
        {
            string? selected = PlaybackOutput.SelectedValue as string;
            OutputChoices.Clear(); foreach (var output in outputs) OutputChoices.Add(output);
            PlaybackOutput.SelectedValue = selected; OutputsChanged?.Invoke(this, EventArgs.Empty);
        }
        applying = true;
        try
        {
            foreach (var row in Channels)
            {
                string? capture = row.CaptureEndpointId, render = row.RenderEndpointId;
                UpdateChoices(row.CaptureChoices, true, capture);
                UpdateChoices(row.RenderChoices, false, render);
                row.CaptureEndpointId = capture; row.RenderEndpointId = render;
                var status = snapshot.Channels.FirstOrDefault(c => c.Bus == row.Bus);
                row.State = snapshot.StreamMix?.Running == true && snapshot.StreamMix.Sources.Any(s => s.Id == row.Bus.ToString()) ? "Mixing" : status?.State ?? "Unavailable";
                row.Reason = row.State == "Mixing" ? "Sent to both your own mix and the stream mix. Use Stream mix to change it." : status?.Reason ?? "No status for this channel yet.";
                row.Peak = status?.Bridge?.OutputPeak is float peak && float.IsFinite(peak) ? peak : -1;
                row.ActiveDestination = row.State == "Mixing" ? "Personal + stream outputs" : status?.ActiveRenderId is string id
                    ? "Active: " + (endpoints.FirstOrDefault(e => e.Id == id)?.Name ?? id) : "No active destination";
            }
            StartButton.IsEnabled = ready && !snapshot.Running && !snapshot.StartRequested && snapshot.StreamMix is null;
            StopButton.IsEnabled = ready && (snapshot.Running || snapshot.StartRequested || snapshot.StreamMix is not null);
            Balance.IsEnabled = snapshot.StreamMix?.Running == true ? snapshot.Profile.Channels.Any(c => c.Enabled && c.Bus == AudioBus.Game) && snapshot.Profile.Channels.Any(c => c.Enabled && c.Bus == AudioBus.Chat) : GameChatShareOutput(snapshot.Profile) && (!snapshot.StartRequested ||
                (snapshot.Channels.FirstOrDefault(c => c.Bus == AudioBus.Game)?.ActiveRenderId is string gameOutput &&
                 snapshot.Channels.FirstOrDefault(c => c.Bus == AudioBus.Chat)?.ActiveRenderId == gameOutput));
            Balance.ToolTip = Balance.IsEnabled ? "Turns Game or Chat down; the center leaves both as they are. With a stream mix on, only your own mix changes."
                : "Set Game and Chat to the same Plays on device, then choose Apply changes.";
            // A recent failure stays visible; routine status from polling would otherwise replace it within a quarter second.
            if (LastOperationFailed) { }
            else if (snapshot.LastError is not null) Feedback.Text = snapshot.LastError;
            else if (snapshot.StreamMix is { } mixStatus) Feedback.Text = mixStatus.Failure is string failure ? "Stream mix failed: " + failure : "Stream mix is " + (mixStatus.Running ? "on." : "off.");
            else if (!routingDirty && !levelsPending) Feedback.Text = snapshot.Running ? RunningStatus
                : snapshot.StartRequested ? "Waiting for your devices; no channel is running yet." : "Audio is stopped. Choose Start audio when you're ready.";
        }
        finally { applying = false; }
    }

    /// <summary>The routine status while audio runs; a view with its own Running badge need not repeat it.</summary>
    public const string RunningStatus = "Audio is running.";

    private void UpdateChoices(ObservableCollection<IndependentEndpointChoice> choices, bool capture, string? retained)
    {
        List<IndependentEndpointChoice> wanted = [new("", "Choose a device…")];
        var eligible = endpoints.Where(e => e.IsCapture == capture && !e.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase)).ToArray();
        // A narrow strip's closed box shows the device's own name ("Headphones"); the list and tooltip keep the full one.
        string[] compact = eligible.Select(e => DeviceNames.WithoutHardware(e.Name) + DuplicateSuffix(eligible, e) + (e.IsActive ? "" : " (unavailable)")).ToArray();
        for (int i = 0; i < eligible.Length; i++)
        {
            HostEndpoint endpoint = eligible[i];
            wanted.Add(new(endpoint.Id, IndependentEndpointChoice.Describe(endpoint, DuplicateSuffix(eligible, endpoint)) + (endpoint.IsActive ? "" : " (unavailable)"))
            { CompactLabel = compact.Count(c => c == compact[i]) > 1 ? null : compact[i] });
        }
        if (!string.IsNullOrWhiteSpace(retained) && wanted.All(c => c.Id != retained)) wanted.Add(new(retained, "Saved device (not connected)"));
        if (choices.SequenceEqual(wanted)) return;
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < choices.Count && choices[i] == wanted[i]) continue;
            int found = choices.ToList().FindIndex(c => c.Id == wanted[i].Id);
            if (found >= 0) { choices.Move(found, i); if (choices[i] != wanted[i]) choices[i] = wanted[i]; }
            else choices.Insert(i, wanted[i]);
        }
        while (choices.Count > wanted.Count) choices.RemoveAt(choices.Count - 1);
    }

    /// <summary>Devices that share a name are told apart as "(2)", "(3)" in list order instead of by internal IDs.</summary>
    internal static string DuplicateSuffix(IReadOnlyList<HostEndpoint> list, HostEndpoint endpoint)
    {
        HostEndpoint[] same = list.Where(e => e.Name == endpoint.Name).ToArray();
        int index = Array.FindIndex(same, e => e.Id == endpoint.Id);
        return same.Length < 2 || index < 1 ? "" : " (" + (index + 1).ToString(CultureInfo.CurrentCulture) + ")";
    }

    private static bool GameChatShareOutput(AudioHostProfile profile)
    {
        var game = profile.Channels.Single(c => c.Bus == AudioBus.Game);
        var chat = profile.Channels.Single(c => c.Bus == AudioBus.Chat);
        return game.Enabled && chat.Enabled && !string.IsNullOrEmpty(game.RenderEndpointId) && game.RenderEndpointId == chat.RenderEndpointId;
    }

    private AudioHostProfile Draft() => confirmed with
    {
        Name = ProfileName.Text.Trim(), MasterGainDb = MasterGain.Value, MasterMute = MasterMute.IsChecked == true,
        ChatMix = Balance.Value, Channels = Channels.Select(c => c.ToProfile()).ToArray()
    };

    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (applying || !ready || disposed) return;
        if (sender is IndependentMixerChannel row && e.PropertyName is nameof(IndependentMixerChannel.GainDb) or nameof(IndependentMixerChannel.Mute))
        {
            draftRevision++;
            if (e.PropertyName == nameof(IndependentMixerChannel.GainDb)) pendingGains.Add(row.Bus); else pendingMutes.Add(row.Bus);
            ScheduleLevels();
        }
        else if (e.PropertyName is nameof(IndependentMixerChannel.Enabled) or nameof(IndependentMixerChannel.CaptureEndpointId)
            or nameof(IndependentMixerChannel.RenderEndpointId) or nameof(IndependentMixerChannel.ManualPin) or nameof(IndependentMixerChannel.AutoReturn)) MarkRoutingDirty();
    }
    private void MarkRoutingDirty() { draftRevision++; routingDirty = true; Feedback.Text = "You have channel changes that aren't applied yet. Choose Apply changes to use them."; }
    private void ScheduleLevels()
    {
        if (applying || !ready || disposed) return;
        levelsPending = true; levels.Stop(); levels.Start();
    }
    private void MasterChanged(object sender, RoutedEventArgs e)
    {
        if (MasterValue is not null && MasterGain is not null) MasterValue.Text = MasterGain.Value.ToString("F1", CultureInfo.CurrentCulture) + " dB";
        if (applying || !ready || disposed) return;
        draftRevision++;
        if (ReferenceEquals(sender, MasterGain)) pendingMasterGain = true;
        else if (ReferenceEquals(sender, MasterMute)) pendingMasterMute = true;
        else if (ReferenceEquals(sender, Balance)) pendingBalance = true;
        ScheduleLevels();
    }
    private void ProfileNameChanged(object sender, RoutedEventArgs e) { if (ready && !applying) MarkRoutingDirty(); }
    private void CenterClicked(object sender, RoutedEventArgs e) => Balance.Value = 0;
    private void IgnoreWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

    private async Task ApplyLevelsAsync()
    {
        if (!ready || !levelsPending || disposed) return;
        await ExecuteAsync(async () =>
        {
            var changedGains = pendingGains.ToHashSet(); var changedMutes = pendingMutes.ToHashSet();
            AudioHostProfile profile = confirmed with
            {
                MasterGainDb = pendingMasterGain ? MasterGain.Value : confirmed.MasterGainDb,
                MasterMute = pendingMasterMute ? MasterMute.IsChecked == true : confirmed.MasterMute,
                ChatMix = pendingBalance ? Balance.Value : confirmed.ChatMix,
                Channels = confirmed.Channels.Select(c => c with { GainDb = changedGains.Contains(c.Bus) ? Channels.Single(r => r.Bus == c.Bus).GainDb : c.GainDb,
                    Mute = changedMutes.Contains(c.Bus) ? Channels.Single(r => r.Bus == c.Bus).Mute : c.Mute }).ToArray()
            };
            bool masterGain = pendingMasterGain, masterMute = pendingMasterMute, balance = pendingBalance;
            pendingGains.Clear(); pendingMutes.Clear(); pendingMasterGain = false; pendingMasterMute = false; pendingBalance = false; levelsPending = false;
            try { return await connection!.ApplyAsync(profile, lifetime.Token); }
            catch
            {
                // The helper did not take these values: keep them pending so the controls are not left showing levels
                // that were never applied, and the next level change sends them again.
                pendingGains.UnionWith(changedGains); pendingMutes.UnionWith(changedMutes);
                pendingMasterGain |= masterGain; pendingMasterMute |= masterMute; pendingBalance |= balance; levelsPending = true;
                throw;
            }
        }, "Levels saved.");
    }

    public Task ApplyRoutingAsync() => ExecuteAsync(async () =>
    {
        levels.Stop(); long revision = draftRevision; var snapshot = await connection!.ApplyAsync(Draft(), lifetime.Token);
        if (revision == draftRevision) Populate(snapshot.Profile); else routingDirty = true;
        return snapshot;
    }, "Changes applied and saved.");

    private const string StartedText = "Audio is starting.", StoppedText = "Audio stopped. Your channel settings are kept.";
    public Task StartAudioAsync() => ExecuteAsync(() => connection!.StartAsync(lifetime.Token), StartedText);
    public Task StopAudioAsync() => ExecuteAsync(() => connection!.StopAsync(lifetime.Token), StoppedText);

    /// <summary>Starts audio and throws when the helper refuses, so a view without this status line can say why.</summary>
    public Task StartAudioOrThrowAsync() => MutateOrThrowAsync(() => connection!.StartAsync(lifetime.Token), StartedText);
    /// <summary>Stops audio and throws when the helper refuses, so a view without this status line can say why.</summary>
    public Task StopAudioOrThrowAsync() => MutateOrThrowAsync(() => connection!.StopAsync(lifetime.Token), StoppedText);

    private async Task MutateOrThrowAsync(Func<Task<HostSnapshot>> operation, string success)
    {
        if (!ready || disposed || connection is null) throw new InvalidOperationException("Audio isn't connected yet.");
        await operations.WaitAsync(lifetime.Token);
        try
        {
            if (reconcileBeforeMutation) { ApplyStatus(await connection.ReadAsync(lifetime.Token)); reconcileBeforeMutation = false; }
            HostSnapshot snapshot = await operation();
            if (disposed) return;
            ClearProblem(); ApplyStatus(snapshot);
            Feedback.Text = snapshot.LastError ?? success;
        }
        catch (Exception error) when (Expected(error))
        {
            reconcileBeforeMutation = true;
            if (!disposed) ReportProblem("Audio change was not confirmed: " + error.Message);
            throw;
        }
        finally { operations.Release(); }
    }
    public async Task ToggleMasterMuteAsync() { MasterMute.IsChecked = MasterMute.IsChecked != true; levels.Stop(); await ApplyLevelsAsync(); }

    public Task SelectPlaybackOutputAsync(string endpointId)
    {
        if (routingDirty) { ReportProblem("Apply your channel changes before switching outputs."); return Task.CompletedTask; }
        return ExecuteAsync(async () =>
        {
            if (endpoints.All(e => e.Id != endpointId || e.IsCapture || !e.IsActive)) throw new InvalidOperationException("That output isn't available right now.");
            var playback = confirmed.Channels.Where(c => c.Enabled && c.Bus != AudioBus.Microphone).ToArray();
            if (playback.Length == 0) throw new InvalidOperationException("Turn on at least one playback channel first.");
            if (playback.Any(c => c.ExcludedOutputIds.Contains(endpointId))) throw new InvalidOperationException("This output is excluded by a channel that is on. Change that channel's device order first.");
            long revision = draftRevision;
            var snapshot = await connection!.ApplyAsync(confirmed with
            {
                Channels = confirmed.Channels.Select(c => c.Enabled && c.Bus != AudioBus.Microphone
                    ? c with { RenderEndpointId = endpointId, ManualPin = true } : c).ToArray()
            }, lifetime.Token);
            if (revision == draftRevision) Populate(snapshot.Profile); else routingDirty = true;
            return snapshot;
        }, "Output switched.");
    }

    private async void SwitchPlaybackClicked(object sender, RoutedEventArgs e)
    {
        if (PlaybackOutput.SelectedValue is not string id) { ReportProblem("Choose an output first."); return; }
        await ApplyLevelsAsync(); await SelectPlaybackOutputAsync(id);
    }

    private async Task ExecuteAsync(Func<Task<HostSnapshot>> operation, string success)
    {
        if (disposed || !ready || connection is null) return;
        try
        {
            await operations.WaitAsync(lifetime.Token);
            try
            {
                if (reconcileBeforeMutation) { ApplyStatus(await connection.ReadAsync(lifetime.Token)); reconcileBeforeMutation = false; }
                var snapshot = await operation();
                if (!disposed) { ClearProblem(); ApplyStatus(snapshot); Feedback.Text = snapshot.LastError ?? (routingDirty ? success + " Your other channel changes aren't applied yet." : success); }
            }
            finally { operations.Release(); }
        }
        catch (Exception error) when (Expected(error)) { reconcileBeforeMutation = true; if (!disposed) ReportProblem("Audio change was not confirmed: " + error.Message); }
    }

    private void StreamMixClicked(object sender, RoutedEventArgs e) => ShowStreamMix();
    public void ShowStreamMix()
    {
        if (!ready || disposed || latestSnapshot is null) return;
        if (routingDirty) { ReportProblem("Apply your channel changes before opening the stream mix."); return; }
        if (streamWindow is not null) { streamWindow.Activate(); return; }
        streamWindow = new IndependentMixerStreamWindow(latestSnapshot,
            mix => StreamOperationAsync(() => connection!.ApplyAsync(confirmed with { StreamMix = mix }, lifetime.Token)),
            start => StreamOperationAsync(() => start ? connection!.StartStreamMixAsync(lifetime.Token) : connection!.StopAsync(lifetime.Token))) { Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow };
        try { streamWindow.ShowDialog(); } finally { streamWindow = null; }
    }
    private async Task<HostSnapshot> StreamOperationAsync(Func<Task<HostSnapshot>> operation)
    {
        await operations.WaitAsync(lifetime.Token);
        try
        {
            if (reconcileBeforeMutation) { ApplyStatus(await connection!.ReadAsync(lifetime.Token)); reconcileBeforeMutation = false; }
            var snapshot = await operation(); ApplyStatus(snapshot); return snapshot;
        }
        catch { reconcileBeforeMutation = true; throw; }
        finally { operations.Release(); }
    }

    /// <summary>Starts (endpoint id) or stops (null) the helper's capture-only microphone test. Plays and sends nothing.</summary>
    public async Task SetMicrophoneTestAsync(string? endpointId)
    {
        if (!ready || disposed || connection is null) throw new InvalidOperationException("Audio isn't connected yet.");
        await operations.WaitAsync(lifetime.Token);
        try { ApplyStatus(endpointId is null ? await connection.StopMicrophoneTestAsync(lifetime.Token) : await connection.StartMicrophoneTestAsync(endpointId, lifetime.Token)); }
        finally { operations.Release(); }
    }

    /// <summary>
    /// Saves one channel through the helper (applied at once when audio runs) and throws when the helper rejects it,
    /// so the caller can say so. Other channels and staged edits are left as they are.
    /// </summary>
    public async Task ApplyChannelAsync(HostChannelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ready || disposed || connection is null) throw new InvalidOperationException("Audio isn't connected yet.");
        await operations.WaitAsync(lifetime.Token);
        try
        {
            if (reconcileBeforeMutation) { ApplyStatus(await connection.ReadAsync(lifetime.Token)); reconcileBeforeMutation = false; }
            HostSnapshot snapshot = await connection.ApplyAsync(confirmed with
            { Channels = confirmed.Channels.Select(c => c.Bus == profile.Bus ? profile : c).ToArray() }, lifetime.Token);
            if (disposed) return;
            IndependentMixerChannel row = Channels.Single(c => c.Bus == profile.Bus);
            applying = true;
            try { row.Load(snapshot.Profile.Channels.Single(c => c.Bus == profile.Bus)); }
            finally { applying = false; }
            pendingGains.Remove(profile.Bus); pendingMutes.Remove(profile.Bus);
            levelsPending = pendingGains.Count > 0 || pendingMutes.Count > 0 || pendingMasterGain || pendingMasterMute || pendingBalance;
            ApplyStatus(snapshot);
        }
        catch (Exception error) when (Expected(error)) { reconcileBeforeMutation = true; throw; }
        finally { operations.Release(); }
    }

    /// <summary>The confirmed (saved) settings for one channel.</summary>
    public HostChannelProfile ConfirmedChannel(AudioBus bus) => confirmed.Channels.Single(c => c.Bus == bus);

    /// <summary>Saves one channel's processing only; staged source/destination edits on that channel stay staged.</summary>
    public async Task ApplyChannelDspAsync(AudioBus bus, DspSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ready || disposed || connection is null) throw new InvalidOperationException("Audio isn't connected yet.");
        await operations.WaitAsync(lifetime.Token);
        try
        {
            if (reconcileBeforeMutation) { ApplyStatus(await connection.ReadAsync(lifetime.Token)); reconcileBeforeMutation = false; }
            HostSnapshot snapshot = await connection.ApplyAsync(confirmed with
            { Channels = confirmed.Channels.Select(c => c.Bus == bus ? c with { Dsp = settings } : c).ToArray() }, lifetime.Token);
            if (disposed) return;
            applying = true;
            try { Channels.Single(c => c.Bus == bus).Dsp = snapshot.Profile.Channels.Single(c => c.Bus == bus).Dsp; }
            finally { applying = false; }
            ApplyStatus(snapshot);
        }
        catch (Exception error) when (Expected(error)) { reconcileBeforeMutation = true; throw; }
        finally { operations.Release(); }
    }

    // Presentation hooks for a host-styled view (the suite's Playback section) that reuses this connection and logic.
    public double MasterGainDb { get => MasterGain.Value; set => MasterGain.Value = Math.Clamp(value, MasterGain.Minimum, MasterGain.Maximum); }
    public bool MasterMuted { get => MasterMute.IsChecked == true; set => MasterMute.IsChecked = value; }
    public double GameChatBalance { get => Balance.Value; set => Balance.Value = Math.Clamp(value, -1, 1); }
    public bool CanBalance => Balance.IsEnabled;
    public string BalanceHint => Balance.ToolTip as string ?? "";
    public bool RoutingStaged => routingDirty;
    public bool CanStart => StartButton.IsEnabled;
    public bool CanStop => StopButton.IsEnabled;

    /// <summary>Raised whenever the status line changes (results, errors, staged-edit notices).</summary>
    public event EventHandler? StatusTextChanged
    {
        add => System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(Feedback, value);
        remove => System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).RemoveValueChanged(Feedback, value);
    }

    public async Task ExportProfileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AudioHostProfile profile = Draft();
        await Task.Run(() => HostProfileStore.Save(path, profile));
        Feedback.Text = "Profile exported. Its device choices only work on this PC.";
    }

    /// <summary>Opens the device-priority editor for a playback channel; returns true when the order changed (staged until applied).</summary>
    public bool EditDevicePriorities(AudioBus bus)
    {
        IndependentMixerChannel row = Channels.Single(c => c.Bus == bus);
        IndependentMixerPolicyWindow dialog = new(row, endpoints) { Owner = Window.GetWindow(this) ?? Application.Current?.MainWindow };
        if (dialog.ShowDialog() != true) return false;
        MarkRoutingDirty();
        return true;
    }

    private void MicrophoneSetupClicked(object sender, RoutedEventArgs e) => ShowMicrophoneSetup();
    public async void ShowMicrophoneSetup()
    {
        if (!ready || disposed) { ReportProblem("Wait for audio to connect, then open microphone setup."); return; }
        if (microphoneWindow is not null) { microphoneWindow.Activate(); return; }
        var row = Channels.Single(c => c.Bus == AudioBus.Microphone);
        var dialog = new IndependentMixerMicrophoneWindow(row.ToProfile(), endpoints, async id =>
        {
            await operations.WaitAsync(lifetime.Token);
            try
            {
                var snapshot = id is null ? await connection!.StopMicrophoneTestAsync(lifetime.Token) : await connection!.StartMicrophoneTestAsync(id, lifetime.Token);
                ApplyStatus(snapshot);
            }
            finally { operations.Release(); }
        }) { Owner = Window.GetWindow(this) };
        microphoneWindow = dialog;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { microphoneWindow = null; }
        if (!accepted) return;
        await ApplyMicrophoneProfileAsync(dialog.Profile);
    }
    internal Task ApplyMicrophoneProfileAsync(HostChannelProfile profile)
    {
        if (profile.Bus != AudioBus.Microphone) throw new ArgumentException("Microphone setup can only apply the microphone channel.", nameof(profile));
        var row = Channels.Single(c => c.Bus == AudioBus.Microphone);
        applying = true;
        try { row.Load(profile); } finally { applying = false; }
        draftRevision++;
        return ExecuteAsync(async () =>
        {
            long revision = draftRevision;
            var snapshot = await connection!.ApplyAsync(confirmed with { Channels = confirmed.Channels.Select(c => c.Bus == AudioBus.Microphone ? profile : c).ToArray() }, lifetime.Token);
            if (revision == draftRevision)
            {
                routingDirty = HasStagedRouting(snapshot.Profile);
                pendingGains.Remove(AudioBus.Microphone); pendingMutes.Remove(AudioBus.Microphone);
                levelsPending = pendingGains.Count > 0 || pendingMutes.Count > 0 || pendingMasterGain || pendingMasterMute || pendingBalance;
            }
            return snapshot;
        }, "Microphone saved. If audio is stopped, choose Start audio. In your voice app, choose the cable's output as the microphone.");
    }
    private bool HasStagedRouting(AudioHostProfile actual) => ProfileName.Text.Trim() != actual.Name || Channels.Any(row =>
    {
        var saved = actual.Channels.Single(c => c.Bus == row.Bus);
        return row.Enabled != saved.Enabled || row.ToProfile().CaptureEndpointId != saved.CaptureEndpointId || row.ToProfile().RenderEndpointId != saved.RenderEndpointId ||
            row.ManualPin != saved.ManualPin || row.AutoReturn != saved.AutoReturn || !row.OutputPriorityIds.SequenceEqual(saved.OutputPriorityIds) || !row.ExcludedOutputIds.SequenceEqual(saved.ExcludedOutputIds);
    });

    private async void ApplyClicked(object sender, RoutedEventArgs e) => await ApplyRoutingAsync();
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (routingDirty) { ReportProblem("Apply your channel changes before starting so audio uses the devices you chose."); return; }
        await ApplyLevelsAsync(); await StartAudioAsync();
    }
    private async void StopClicked(object sender, RoutedEventArgs e) => await StopAudioAsync();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void ProcessingClicked(object sender, RoutedEventArgs e) => ProcessingRequested?.Invoke(this, EventArgs.Empty);

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import Soltex audio profile", Filter = "Soltex audio profile (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await ImportProfileAsync(dialog.FileName);
    }
    public async Task ImportProfileAsync(string path)
    {
        if (disposed) return;
        long revision = draftRevision;
        try
        {
            var profile = await Task.Run(() => HostProfileStore.Load(path));
            if (disposed) return;
            if (revision != draftRevision) { ReportProblem("The profile wasn't imported because you made newer changes. Import it again when they're applied."); return; }
            Populate(profile, adoptConfirmed: false); MarkRoutingDirty();
            Feedback.Text = "Profile imported. Choose Apply changes to use it; audio hasn't changed yet.";
        }
        catch (Exception error) when (Expected(error)) { ReportProblem("Profile was not imported: " + error.Message); }
    }
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export Soltex audio profile", Filter = "Soltex audio profile (*.json)|*.json", DefaultExt = ".json", FileName = "Soltex-audio-profile.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { var profile = Draft(); await Task.Run(() => HostProfileStore.Save(dialog.FileName, profile)); Feedback.Text = "Profile exported. Its device choices only work on this PC."; }
        catch (Exception error) when (Expected(error)) { ReportProblem("Profile was not exported: " + error.Message); }
    }
    private void PolicyClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IndependentMixerChannel row) return;
        var dialog = new IndependentMixerPolicyWindow(row, endpoints) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) MarkRoutingDirty();
    }
    private async void ChannelProcessingClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not IndependentMixerChannel row) return;
        var dialog = new IndependentMixerDspWindow(row.Title, row.Dsp) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var settings = dialog.Settings; row.Dsp = settings; draftRevision++;
        await ExecuteAsync(() => connection!.ApplyAsync(confirmed with
        { Channels = confirmed.Channels.Select(c => c.Bus == row.Bus ? c with { Dsp = settings } : c).ToArray() }, lifetime.Token), row.Title + " sound saved.");
    }

    public ValueTask DisposeAsync() => new(shutdown ??= ShutdownCoreAsync());
    public Task ShutdownAsync() => shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        if (disposed) return;
        active = false; polling.Stop(); levels.Stop();
        await ApplyLevelsAsync();
        disposed = true; lifetime.Cancel();
        if (initialization is not null) { try { await initialization; } catch (Exception error) when (Expected(error)) { } }
        await operations.WaitAsync();
        try { if (connection is not null) await connection.DisposeAsync(); }
        finally { operations.Release(); lifetime.Dispose(); }
    }
    private static bool Expected(Exception error) => error is IOException or InvalidOperationException or UnauthorizedAccessException
        or OperationCanceledException or ArgumentException or NotSupportedException or AggregateException or System.Text.Json.JsonException or TimeoutException;
}
