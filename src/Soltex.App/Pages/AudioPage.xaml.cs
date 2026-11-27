using System.IO;
using System.Windows;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>
/// One audio surface: Mixer, Microphone and Apps, plus SteelSeries Sonar when it is installed. Every channel is
/// processed by the helper process (one engine); the in-window processing engine is not reachable from here.
/// Hiding this page only stops presentation polling; audio keeps running until Stop or Quit.
/// </summary>
public partial class AudioPage : UserControl, IShellPage, IAsyncDisposable
{
    // Every location names a tab that exists: Mixer, Microphone, Apps, and Sonar only where it is installed.
    internal static SearchEntry[] Entries => SonarInstalled ? AllEntries : AllEntries.Where(e => e.Anchor != "sonar").ToArray();

    private static readonly SearchEntry[] AllEntries =
    [
        new("Playback channels: game, chat, media", "Audio › Mixer", Destination.Audio, "playback", "mixer", "volume", "channels", "game chat", "headset", "balance", "sound"),
        new("Set up and test my microphone", "Audio › Microphone", Destination.Audio, "microphone", "mic", "audio interface", "usb mic", "input", "voice", "discord", "test mic", "cable output", "vb-cable"),
        new("Microphone sound: AI noise removal, curve EQ, gate", "Audio › Microphone", Destination.Audio, "microphone", "noise", "ai noise", "noise removal", "rnnoise", "keyboard noise", "gate", "compressor", "equalizer", "eq", "curve", "suppression", "broadcast", "noisy room", "preset"),
        new("Stream mix: separate mix for streaming", "Audio › Mixer", Destination.Audio, "stream", "obs", "streaming"),
        new("App volume and output per app", "Audio › Apps", Destination.Audio, "apps", "app volume", "per app", "route app", "volume mixer"),
        new("Start or stop audio", "Audio › Mixer", Destination.Audio, "playback", "start", "stop audio", "turn on audio"),
        new("SteelSeries Sonar compatibility", "Audio › Sonar", Destination.Audio, "sonar", "steelseries", "gg")
    ];

    private sealed record Tab(string Key, string Label);

    /// <summary>Sonar appears only where SteelSeries Sonar is installed; elsewhere it would be an empty, confusing tab.</summary>
    internal static bool SonarInstalled { get; set; } = File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SteelSeries", "GG", "apps", "sonar", "SteelSeriesSonar.exe"));

    private readonly Tab[] tabs;

    private readonly ShellContext context;
    private IndependentMixerWorkspace? mixer;
    private MicrophonePathView? microphone;
    private Views.WindowsMixerView? apps;
    private PlaybackView? playback;
    private MixerWorkspace? sonar;
    private Task? mixerInitialization, sonarInitialization;
    private string selected = "playback";
    private bool shown, disposed;

    internal AudioPage(ShellContext context)
    {
        this.context = context;
        // Audio dialogs (stream mix, device order) use the app's theme rather than the standalone audio palette.
        AudioTheme.UseHostResources = true;
        InitializeComponent();
        tabs = SonarInstalled
            ? [new("playback", "Mixer"), new("microphone", "Microphone"), new("apps", "Apps"), new("sonar", "Sonar")]
            : [new("playback", "Mixer"), new("microphone", "Microphone"), new("apps", "Apps")];
        Tabs.ItemsSource = tabs;
        Tabs.SelectedIndex = 0;
        StartSonarInBackground();
    }

    private string SonarDirectory => Path.Combine(context.Services.AudioPreferencesDirectory, "Channels");

    /// <summary>
    /// True when Sonar is installed and its device shortcuts (Ctrl+Alt+1/2) or automatic output selection are on. Those
    /// must work from the moment the app starts, before (or without) the Sonar tab being opened, so the shell creates this
    /// page at startup when this is true; with both off nothing about Sonar starts until its tab is opened.
    /// </summary>
    internal static bool SonarRunsInBackground(string audioPreferencesDirectory) =>
        SonarInstalled && !AudioPreview.Enabled && MixerWorkspace.NeedsBackgroundService(Path.Combine(audioPreferencesDirectory, "Channels"), globalShortcuts: true);

    /// <summary>
    /// Initializes the Sonar workspace in its collapsed tab without activating it, so its shortcuts, device watcher and
    /// output policy run. Only presentation (meters, ChatMix polling) waits for the tab.
    /// </summary>
    private void StartSonarInBackground()
    {
        if (!SonarRunsInBackground(context.Services.AudioPreferencesDirectory)) return;
        _ = StartSonarInBackgroundAsync();
    }

    private async Task StartSonarInBackgroundAsync()
    {
        try { await EnsureSonarAsync(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
        {
            context.Services.Report(NoticeSeverity.Caution, "Audio", "Sonar shortcuts and automatic output selection could not start: " + error.Message);
        }
    }

    public Destination Destination => Destination.Audio;
    public string Title => "Audio";
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => Entries;

    internal bool IsEngineReady => mixer?.IsHostInitialized == true;
    internal bool IsAudioRunning => mixer?.IsAudioRunning == true;
    internal IReadOnlyList<(string Id, string Label)> Outputs =>
        mixer?.AvailableOutputs.Select(o => (o.Id, o.Label)).ToArray() ?? [];

    private string MixerDirectory => Path.Combine(context.Services.AudioPreferencesDirectory, "Independent");

    public bool Reveal(string anchor)
    {
        string tab = anchor switch { "apps" => "apps", "sonar" when SonarInstalled => "sonar", "microphone" => "microphone", _ => "playback" };
        Tabs.SelectedItem = tabs.First(t => t.Key == tab);
        if (anchor == "stream") Dispatcher.InvokeAsync(async () => { await EnsureMixerAsync(); mixer?.ShowStreamMix(); });
        Tabs.Focus();
        return true;
    }

    /// <summary>The narrowest content the audio sections are laid out for; below it the page scrolls sideways.</summary>
    internal const double MinimumContentWidth = 228;

    /// <summary>
    /// The padding is Shell.PagePadding, as on every other page (the shell narrows it in a narrow window), so the content
    /// column lines up with theirs. Only below the narrowest usable width does the page scroll sideways, with its scroll
    /// bar showing, instead of crushing controls.
    /// </summary>
    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Thickness padding = PageContent.Margin;
        double used = padding.Left + padding.Right;
        bool scroll = e.NewSize.Width - used < MinimumContentWidth;
        // A fixed width while scrolling: the column would otherwise take its full width once the width is unlimited.
        // Otherwise the content fills the page (the column stops at its cap, left-aligned like every page's).
        PageContent.Width = scroll ? MinimumContentWidth + used : double.NaN;
        PageContent.HorizontalAlignment = scroll ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        PageScroll.HorizontalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    public void OnShown()
    {
        shown = true;
        _ = ShowSelectedAsync();
    }

    public void OnHidden()
    {
        shown = false;
        mixer?.Deactivate(); apps?.Deactivate(); sonar?.Deactivate();
        if (microphone is not null) microphone.Active = false;
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tabs.SelectedItem is Tab tab) { selected = tab.Key; await ShowSelectedAsync(); }
    }

    private async Task ShowSelectedAsync()
    {
        if (disposed) return;
        MicHost.Visibility = selected == "microphone" ? Visibility.Visible : Visibility.Collapsed;
        PlaybackHost.Visibility = selected == "playback" ? Visibility.Visible : Visibility.Collapsed;
        AppsHost.Visibility = selected == "apps" ? Visibility.Visible : Visibility.Collapsed;
        SonarHost.Visibility = selected == "sonar" ? Visibility.Visible : Visibility.Collapsed;
        if (selected != "microphone" && microphone is not null) microphone.Active = false;
        // The Microphone tab polls the shared connection itself (and reconnects through it), so the mixer's own
        // polling runs only while the Mixer tab is shown.
        if (selected != "playback") mixer?.Deactivate();
        if (selected != "apps") apps?.Deactivate();
        if (selected != "sonar") sonar?.Deactivate();
        if (!shown) return;
        try
        {
            switch (selected)
            {
                case "microphone":
                    if (microphone is null) { microphone = new MicrophonePathView(); MicHost.Content = microphone; }
                    await EnsureMixerAsync();
                    if (mixer is not null && shown && selected == "microphone") { microphone.Attach(new WorkspaceMicrophoneSource(mixer)); microphone.Active = true; }
                    break;
                case "playback":
                    await EnsureMixerAsync();
                    if (mixer is not null && playback is null)
                    {
                        playback = new PlaybackView(mixer);
                        playback.MicrophoneSetupRequested += (_, _) =>
                        {
                            Tabs.SelectedItem = tabs.First(t => t.Key == "microphone");
                            Dispatcher.InvokeAsync(() => microphone?.OpenSetup(), System.Windows.Threading.DispatcherPriority.Background);
                        };
                        PlaybackHost.Content = playback;
                    }
                    if (shown && selected == "playback") { mixer?.Activate(); playback?.Sync(); }
                    break;
                case "apps":
                    if (apps is null)
                    {
                        apps = new Views.WindowsMixerView();
                        apps.ConfigureRoutingDirectory(Path.Combine(context.Services.AudioPreferencesDirectory, "AppRouting"));
                        AppsHost.Content = apps;
                        if (AudioPreview.Enabled) apps.ShowPreview(AudioPreview.Sessions());
                    }
                    if (!AudioPreview.Enabled) apps.Activate();
                    break;
                case "sonar":
                    await EnsureSonarAsync();
                    if (shown && selected == "sonar") sonar?.Activate();
                    break;
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "This audio section could not open: " + error.Message);
        }
    }

    private Task EnsureMixerAsync()
    {
        if (mixer is null)
        {
            // Owns the helper connection and the staging rules; the Microphone and Mixer views present it.
            mixer = new IndependentMixerWorkspace();
            mixer.OutputsChanged += (_, _) => (context.Owner as ShellWindow)?.RefreshTray();
        }
        // A render capture or layout check shows made-up state and never reaches the audio engine.
        return mixerInitialization ??= AudioPreview.Enabled ? mixer.InitializeAsync(AudioPreview.CreateMixerConnection()) : InitializeMixerAsync();
    }

    private async Task InitializeMixerAsync()
    {
        // A partial install or an antivirus quarantine removes the engine. Say so plainly; the views still open and
        // show that audio isn't available, and they connect on their own if the engine comes back.
        if (!File.Exists(IndependentMixerWorkspace.DefaultHostExecutablePath))
            context.ShowNotice(NoticeSeverity.Critical, "The audio engine is missing from this installation. Reinstall Soltex to restore it.");
        LoadingText.Visibility = Visibility.Visible;
        try { await mixer!.InitializeAsync(MixerDirectory); }
        finally { LoadingText.Visibility = Visibility.Collapsed; }
    }

    private Task EnsureSonarAsync()
    {
        if (sonar is null)
        {
            sonar = new MixerWorkspace(new MixerHostOptions
            {
                // A section of this page: no header of its own, the app's theme and the page's scrolling.
                Embedded = true,
                PreferencesDirectory = SonarDirectory,
                EnableGlobalShortcuts = true,
                // Sign-in start is owned by Settings now; the Sonar view must not register its own.
                GetStartupEnabled = null,
                SetStartupEnabled = null,
                // Processing lives on each playback channel (one engine); Sonar's button leads there.
                ProcessingRequested = () =>
                {
                    Reveal("playback");
                    context.ShowNotice(NoticeSeverity.Information, "Sound processing is on each Mixer channel: choose Sound on Game, Chat, Media or Aux.");
                }
            });
            sonar.QuitRequested += (_, _) => (context.Owner as ShellWindow)?.RequestQuit();
            SonarHost.Content = sonar;
            if (AudioPreview.Enabled)
            {
                // Never contacts Sonar: the preview goes through the same presentation path as a real snapshot.
                sonar.ShowPreview(AudioPreview.Sonar(), AudioPreview.SonarChatMix(), AudioPreview.SonarPeaks());
                sonarInitialization = Task.CompletedTask;
            }
        }
        return sonarInitialization ??= sonar.InitializeAsync();
    }

    internal async Task StartAudioInBackgroundAsync()
    {
        await EnsureMixerAsync();
        if (!mixer!.IsHostInitialized)
        {
            // The status already says why, for example that the audio engine is missing.
            context.Services.Report(NoticeSeverity.Caution, "Audio", mixer.StatusText);
            return;
        }
        await mixer.StartAudioAsync();
        if (mixer.LastOperationFailed)
        {
            context.Services.Report(NoticeSeverity.Caution, "Audio", mixer.StatusText);
            return;
        }
        await mixer.RefreshStateAsync();
        context.Services.Report(mixer.IsAudioRunning ? NoticeSeverity.Success : NoticeSeverity.Caution, "Audio",
            mixer.IsAudioRunning ? "Audio started." : "Audio is starting. Open Audio to see each channel.");
    }

    internal async Task StopAudioAsync()
    {
        if (mixer is null) return;
        await mixer.StopAudioAsync();
        await mixer.RefreshStateAsync();
    }

    internal Task ToggleMuteAsync() => mixer?.ToggleMasterMuteAsync() ?? Task.CompletedTask;
    internal Task SelectOutputAsync(string id) => mixer?.SelectPlaybackOutputAsync(id) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (mixer is not null) await mixer.ShutdownAsync();
        if (sonar is not null) await sonar.ShutdownAsync();
        if (apps is not null) await apps.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
