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
        new("Microphone sound: noise gate, noise reduction, EQ", "Audio › Microphone", Destination.Audio, "microphone", "noise", "gate", "compressor", "equalizer", "eq", "suppression", "broadcast", "preset"),
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
                    }
                    apps.Activate();
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
        return mixerInitialization ??= InitializeMixerAsync();
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
                PreferencesDirectory = Path.Combine(context.Services.AudioPreferencesDirectory, "Channels"),
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
