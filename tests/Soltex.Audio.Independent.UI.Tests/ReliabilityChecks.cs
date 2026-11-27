using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.Sonar;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

/// <summary>
/// The Sonar workspace and the shared audio controls, without Sonar, a device or the audio engine: Sonar state is shown
/// through the preview path (which never connects), the engine is a scripted connection, and settings go to a temp folder.
/// </summary>
internal static class ReliabilityChecks
{
    private static readonly string[] SonarStatusTexts = ["ConnectionText", "SonarNoticeTitle", "ChatMixStatus", "StatusText"];

    public static async Task RunAsync(Action<bool, string> check)
    {
        await SonarSelectionsSurviveDeviceChangesAsync(check);
        await SonarHasNoTabIndexAndReadableListsAsync(check);
        await LiveRegionsAnnounceAsync(check);
        await MeterSnapsWhenShownAsync(check);
        await LostConnectionClearsLevelsAsync(check);
        await SettingsAreWrittenOffTheUiThreadAsync(check);
        await BackgroundServiceRuleAndShortcutWindowAsync(check);
        StreamMixAndPolicyRowsNameTheirDevice(check);
        await MicrophoneSetupTestRunsForTheLengthItNamesAsync(check);
    }

    private static async Task MicrophoneSetupTestRunsForTheLengthItNamesAsync(Action<bool, string> check)
    {
        List<string?> calls = [];
        HostEndpoint[] devices = [new("mic", "Analog 1 + 2 (USB Audio Interface)", true, true), new("cable-in", "CABLE Input", false, true) { TransportId = "cable" }];
        var dialog = new IndependentMixerMicrophoneWindow(new() { Bus = AudioBus.Microphone, CaptureEndpointId = "mic", RenderEndpointId = "cable-in" }, devices,
            id => { calls.Add(id); return Task.CompletedTask; }, testDuration: TimeSpan.FromMilliseconds(150));
        var content = (FrameworkElement)dialog.Content; dialog.Content = null;
        var fixture = new UserControl { Content = content, Width = 640, Height = 630 }; AudioTheme.Apply(fixture);
        fixture.Measure(new Size(640, 630)); fixture.Arrange(new Rect(0, 0, 640, 630)); fixture.UpdateLayout();
        string label = "Test input for " + MicrophonePath.TestDurationText;
        check(label == "Test input for " + MicrophonePath.TestDuration.TotalSeconds.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " seconds" &&
            Descendants<TextBlock>(fixture).Any(t => t.Text.Contains("stops after " + MicrophonePath.TestDurationText, StringComparison.Ordinal)) &&
            !Descendants<TextBlock>(fixture).Any(t => t.Text.Contains("15 seconds", StringComparison.Ordinal)),
            "the setup window names the shared test length (" + MicrophonePath.TestDurationText + ")");
        Descendants<Button>(fixture).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (calls.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);
        check(calls.SequenceEqual(["mic", null]), "the setup window's input test stops itself after the length it names: " + string.Join(", ", calls.Select(c => c ?? "stop")));
        Descendants<Button>(fixture).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Descendants<Button>(fixture).Single(b => Equals(b.Content, "Stop input test")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(400);
        check(calls.SequenceEqual(["mic", null, "mic", null]), "stopping a test by hand cancels its automatic stop");
        dialog.Close();
    }

    private static SonarDevice Render(string id, string name) => new(id, name, "render", "", "active", false);

    /// <summary>Sonar with all channels running; <paramref name="extraOutputs"/> are added as more playback devices.</summary>
    internal static SonarSnapshot Sonar(params SonarDevice[] extraOutputs)
    {
        SonarDevice[] devices =
        [
            new("vad-game", "SteelSeries Sonar - Gaming", "render", "game", "active", true),
            new("vad-chat", "SteelSeries Sonar - Chat", "render", "chatRender", "active", true),
            new("vad-media", "SteelSeries Sonar - Media", "render", "media", "active", true),
            new("vad-aux", "SteelSeries Sonar - Aux", "render", "aux", "active", true),
            new("vad-mic", "SteelSeries Sonar - Microphone", "capture", "chatCapture", "active", true),
            Render("headset", "Headphones (Wireless Gaming Headset)"),
            Render("monitor", "27-inch Gaming Monitor (High Definition Audio Device)"),
            .. extraOutputs,
            new("headset-mic", "Microphone (Wireless Gaming Headset)", "capture", "", "active", false)
        ];
        SonarChannel Channel(string id, string name, string role, string device, string vad) => new(id, name, 0.7, false, device, true, role) { VirtualDeviceId = vad };
        SonarChannel[] channels =
        [
            new("master", "Master", 0.8, false, null, true, "Master"),
            Channel("game", "Game", "game", "headset", "vad-game"), Channel("chat", "Chat", "chatRender", "headset", "vad-chat"),
            Channel("media", "Media", "media", "monitor", "vad-media"), Channel("aux", "Aux", "aux", "headset", "vad-aux"),
            Channel("mic", "Mic", "chatCapture", "headset-mic", "vad-mic")
        ];
        SonarApplication[] applications =
        [
            new(4120, "Counter-Strike 2", "vad-game", "render") { SessionId = "session-4120", State = "active" },
            new(5230, "Discord", "vad-chat", "render") { SessionId = "session-5230", State = "active" }
        ];
        return new SonarSnapshot(devices, channels, applications);
    }

    private static (MixerWorkspace Workspace, Window Host) ShowSonar(string name)
    {
        var workspace = new MixerWorkspace(new MixerHostOptions { PreferencesDirectory = Path.Combine(Path.GetTempPath(), "soltex-sonar-" + name + "-" + Guid.NewGuid().ToString("N")) });
        var host = new Window
        {
            Content = workspace, Width = 1240, Height = 900, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000
        };
        host.Show();
        return (workspace, host);
    }

    private static async Task SonarSelectionsSurviveDeviceChangesAsync(Action<bool, string> check)
    {
        (MixerWorkspace workspace, Window host) = ShowSonar("selection");
        try
        {
            workspace.ShowPreview(Sonar(), new SonarChatMix(0, "enabled"));
            await Idle();
            ComboBox Strip(string id) => Descendants<ComboBox>(workspace).Single(c => c.DataContext is ChannelModel model && model.Id == id);
            ComboBox[] routes = Descendants<ComboBox>(workspace).Where(c => c.Name == "Route").ToArray();
            check(Equals(Strip("game").SelectedValue, "headset") && Equals(Strip("media").SelectedValue, "monitor") && routes.Length == 2 && routes.All(r => r.SelectedItem is not null),
                "Sonar strips and app rows show their device and channel");

            // A device arrives: every list is rebuilt, which clears a box's selection unless the workspace puts it back.
            workspace.ShowPreview(Sonar(Render("speakers", "Speakers (Onboard Audio)")), new SonarChatMix(0, "enabled"));
            await Idle();
            check(Equals(Strip("game").SelectedValue, "headset") && Strip("game").SelectedItem is not null && Equals(Strip("media").SelectedValue, "monitor") &&
                Strip("game").Items.Count == 3, "after a device change the Sonar channel boxes still show their device instead of going blank" +
                $" (game {Strip("game").SelectedValue ?? "none"} of {Strip("game").Items.Count}, media {Strip("media").SelectedValue ?? "none"})");
            check(Descendants<ComboBox>(workspace).Where(c => c.Name == "Route").All(r => r.SelectedItem is not null && r.DataContext is ApplicationModel app && Equals(r.SelectedValue, app.RouteId)),
                "after a device change the app rows still show their channel");
            // The app rows' channel lists are rebuilt when a channel becomes unavailable and comes back.
            SonarSnapshot withoutAux = Sonar() with { Channels = [.. Sonar().Channels.Select(c => c.Id == "aux" ? c with { IsRunning = false } : c)] };
            workspace.ShowPreview(withoutAux, new SonarChatMix(0, "enabled"));
            await Idle();
            workspace.ShowPreview(Sonar(), new SonarChatMix(0, "enabled"));
            await Idle();
            check(Descendants<ComboBox>(workspace).Where(c => c.Name == "Route").All(r => r.SelectedItem is not null),
                "after the channel list changes the app rows still show their channel");
            // Repeated names are numbered like the rest of the app, never shown with internal IDs.
            var choices = MixerWorkspace.Choices(Sonar(Render("headset-2", "Headphones (Wireless Gaming Headset)")), "render");
            check(choices.Select(c => c.Label).SequenceEqual(["Headphones (Wireless Gaming Headset)", "27-inch Gaming Monitor (High Definition Audio Device)", "Headphones (Wireless Gaming Headset) (2)"]) &&
                choices[2].Compact == "Headphones (2)" && choices[1].Compact == "27-inch Gaming Monitor",
                "Sonar numbers devices that share a name and its narrow boxes show the device's own name");
        }
        finally { await workspace.ShutdownAsync(); host.Close(); }
    }

    private static async Task SonarHasNoTabIndexAndReadableListsAsync(Action<bool, string> check)
    {
        (MixerWorkspace workspace, Window host) = ShowSonar("tabs");
        try
        {
            workspace.ShowPreview(Sonar(), new SonarChatMix(0, "enabled"));
            await Idle();
            Control[] indexed = Descendants<Control>(workspace).Where(c => c.ReadLocalValue(Control.TabIndexProperty) != DependencyProperty.UnsetValue).ToArray();
            check(indexed.Length == 0, "nothing in the Sonar workspace sets a TabIndex, so inside the app it follows the window's tab order" +
                (indexed.Length == 0 ? "" : ": " + string.Join(", ", indexed.Select(c => c.GetType().Name + " " + AutomationProperties.GetName(c)))));
            ComboBox linked = Descendants<ComboBox>(workspace).Single(c => c.Name == "LinkedDevice");
            ComboBox route = Descendants<ComboBox>(workspace).First(c => c.Name == "Route");
            check(linked.Items.Cast<object>().All(item => !item.ToString()!.Contains('{', StringComparison.Ordinal)) &&
                linked.Items.Cast<object>().First().ToString() == "Headphones (Wireless Gaming Headset)" && route.Items.Cast<object>().First().ToString() == "Game",
                "Sonar's device and channel list items read as their names, not as records");
            check(new MixerOutput("id", "Speakers").ToString() == "Speakers", "the tray's output choices read as their names");
            Button mute = Descendants<Button>(workspace).First(b => b.Name == "MuteButton");
            check(Equals(mute.Content, "Mute"), "a Sonar mute button says what it does");
        }
        finally { await workspace.ShutdownAsync(); host.Close(); }
    }

    private static async Task LiveRegionsAnnounceAsync(Action<bool, string> check)
    {
        List<UIElement> announced = [];
        void Record(UIElement element) => announced.Add(element);
        LiveRegion.Announced += Record;
        var text = new TextBlock { Text = "Connecting…" };
        LiveRegion.SetIsLive(text, true);
        var host = new Window { Content = text, Width = 300, Height = 120, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        (MixerWorkspace workspace, Window sonarHost) = ShowSonar("live");
        try
        {
            host.Show();
            await Idle();
            check(announced.Count == 0 && AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite, "a live region is polite and its starting text is not announced");
            text.Text = "Connected"; text.Text = "Connected to Sonar";
            await Idle();
            check(announced.Count(e => ReferenceEquals(e, text)) == 1, "a text change raises one announcement once the UI is idle (quick changes are read once)");
            text.Visibility = Visibility.Collapsed; text.Text = "Hidden change";
            await Idle();
            check(announced.Count(e => ReferenceEquals(e, text)) == 1, "a hidden live region stays quiet");

            workspace.ShowPreview(Sonar(), new SonarChatMix(0, "enabled"));
            await Idle();
            announced.Clear();
            workspace.ShowUnavailablePreview("No connection could be made.");
            await Idle();
            string[] names = announced.OfType<FrameworkElement>().Select(e => e.Name).ToArray();
            check(names.Contains("ConnectionText") && names.Contains("StatusText"),
                "Sonar's connection badge and status line are announced when Sonar goes away: " + string.Join(", ", names));
            check(SonarStatusTexts.All(n => Descendants<TextBlock>(workspace).Any(t => t.Name == n && LiveRegion.GetIsLive(t))),
                "every Sonar status text is a live region");
        }
        finally { LiveRegion.Announced -= Record; host.Close(); await workspace.ShutdownAsync(); sonarHost.Close(); }
    }

    private static async Task MeterSnapsWhenShownAsync(Action<bool, string> check)
    {
        var meter = new SignalMeter { Width = 8, Height = 120 };
        var host = new Window { Content = meter, Width = 100, Height = 200, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            host.Show();
            await Idle();
            meter.Level = 0.9;
            meter.Visibility = Visibility.Collapsed;
            await Idle();
            // While hidden (another tab), a loud reading and then a quiet one arrive.
            meter.Level = 0.8; meter.Level = 0.01;
            meter.Visibility = Visibility.Visible;
            await Idle();
            check(Math.Abs(meter.ShownDb + 40) < 0.5 && Math.Abs(meter.HoldDb + 40) < 0.5,
                $"a meter shown again starts from the current level, not a stale peak (shown {meter.ShownDb:0.0}, hold {meter.HoldDb:0.0} dB)");
        }
        finally { host.Close(); }
    }

    private static async Task LostConnectionClearsLevelsAsync(Action<bool, string> check)
    {
        FlakyConnection connection = new();
        await using IndependentMixerWorkspace view = new();
        await view.InitializeAsync(connection);
        check(view.Channels.Single(c => c.Bus == AudioBus.Game).Peak > 0 && !view.ConnectionLost, "a running engine shows measured levels");
        connection.Fail = true;
        await view.RefreshStateAsync();
        check(view.ConnectionLost && view.Channels.All(c => c.Peak < 0) && view.Channels.All(c => c.State == "Not connected") &&
            view.StatusText.StartsWith("Audio connection lost", StringComparison.Ordinal),
            "a failed read marks the connection lost, clears every meter to 'not measured' and says so");
        connection.Fail = false;
        view.Activate();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (view.ConnectionLost && DateTime.UtcNow < deadline) await Task.Delay(50);
        view.Deactivate();
        check(!view.ConnectionLost && view.Channels.Single(c => c.Bus == AudioBus.Game).Peak > 0, "the next answer restores the confirmed state and levels");
        connection.Fail = true;
        view.Activate();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!view.ConnectionLost && DateTime.UtcNow < deadline) await Task.Delay(50);
        view.Deactivate();
        check(view.ConnectionLost && view.Channels.All(c => c.Peak < 0), "a failed poll while shown also clears the meters");
        check(connection.Starts == 0, "no check here starts audio");
    }

    private static async Task SettingsAreWrittenOffTheUiThreadAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "soltex-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new SettingsFile(Path.Combine(folder, "preferences.json"));
            byte[] first = "{\"Linked\":true}"u8.ToArray();
            Task save = file.SaveAsync(first);
            check(!save.IsCompleted, "a settings save returns to the UI thread before the file is written");
            await save;
            await file.SaveAsync("{\"Linked\":true}"u8.ToArray());
            check(file.Writes == 1 && File.ReadAllBytes(file.Path).SequenceEqual(first), "saving unchanged settings writes nothing");
            await Task.WhenAll(file.SaveAsync("{\"Linked\":false}"u8.ToArray()), file.SaveAsync("{\"Linked\":true,\"Shortcuts\":true}"u8.ToArray()));
            check(file.Writes == 3 && File.ReadAllText(file.Path) == "{\"Linked\":true,\"Shortcuts\":true}", "queued saves land in order and the last one wins");
            check(Directory.GetFiles(folder).Length == 1, "no temporary file is left behind");
            File.WriteAllText(Path.Combine(folder, "device-policy.json"), "not json {");
            var unreadable = LinkedOutputPolicySettings.Load(Path.Combine(folder, "device-policy.json"));
            bool refused = false;
            try { unreadable.Serialize(); } catch (IOException) { refused = true; }
            check(refused, "device priorities that could not be read are never overwritten");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    private static async Task BackgroundServiceRuleAndShortcutWindowAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "soltex-sonar-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            check(!MixerWorkspace.NeedsBackgroundService(folder, globalShortcuts: true), "with nothing turned on, Sonar needs nothing at startup");
            File.WriteAllText(Path.Combine(folder, "preferences.json"), "{\"Version\":1,\"Shortcuts\":true}");
            check(MixerWorkspace.NeedsBackgroundService(folder, globalShortcuts: true) && !MixerWorkspace.NeedsBackgroundService(folder, globalShortcuts: false),
                "turned-on device shortcuts need Sonar at startup, where the host offers shortcuts");
            File.Delete(Path.Combine(folder, "preferences.json"));
            File.WriteAllText(Path.Combine(folder, "device-policy.json"), "{\"Version\":1,\"Enabled\":true}");
            check(MixerWorkspace.NeedsBackgroundService(folder, globalShortcuts: false), "automatic output selection needs Sonar at startup");

            // The shortcut window belongs to the workspace, so shortcuts work before (or without) a host window. With the
            // shortcuts off in the settings, no key is registered.
            var workspace = new MixerWorkspace(new MixerHostOptions { PreferencesDirectory = folder, EnableGlobalShortcuts = true });
            typeof(MixerWorkspace).GetMethod("StartShortcutWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(workspace, null);
            nint handle = (nint)typeof(MixerWorkspace).GetField("_shortcutHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace)!;
            check(handle != 0 && Window.GetWindow(workspace) is null, "Sonar's device shortcuts have their own window and need no host window");
            await workspace.ShutdownAsync();
            check(typeof(MixerWorkspace).GetField("_windowSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace) is null,
                "shutting the workspace down closes its shortcut window");
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch (IOException) { } }
    }

    private static void StreamMixAndPolicyRowsNameTheirDevice(Action<bool, string> check)
    {
        var profile = AudioHostProfile.CreateDefault() with { Channels = [.. AudioHostProfile.CreateDefault().Channels.Select(c => c.Bus == AudioBus.Game ? c with { Enabled = true, CaptureEndpointId = "capture-game" } : c)] };
        HostSnapshot snapshot = new(false, profile, [], [new("capture-game", "Game capture", true, true), new("speaker-a", "Headphones", false, true)], null)
        { StreamMix = new WasapiStreamMixStatus(true, null, 0.25f, 0.1f, 0, 0, []) };
        var stream = new IndependentMixerStreamWindow(snapshot, _ => Task.FromResult(snapshot), _ => Task.FromResult(snapshot));
        var content = (FrameworkElement)stream.Content; stream.Content = null;
        var fixture = new UserControl { Content = content, Width = 800, Height = 650 }; AudioTheme.Apply(fixture);
        fixture.Measure(new Size(800, 650)); fixture.Arrange(new Rect(0, 0, 800, 650)); fixture.UpdateLayout();
        string[] sliders = Descendants<Slider>(fixture).Select(s => AutomationProperties.GetName(s)).ToArray();
        string[] mutes = Descendants<CheckBox>(fixture).Select(c => AutomationProperties.GetName(c)).ToArray();
        check(sliders.Contains("Game level in your mix") && sliders.Contains("Game level in the stream mix") && sliders.Distinct().Count() == sliders.Length &&
            mutes.Contains("Mute Game in your mix") && mutes.Contains("Mute Microphone in the stream mix") && mutes.Distinct().Count() == mutes.Length,
            "every stream-mix level and mute names its channel and mix");
        check(Descendants<TextBlock>(fixture).Any(t => t.Text.Contains("−12.0 dBFS", StringComparison.Ordinal)) &&
            !Descendants<TextBlock>(fixture).Any(t => t.Text.Contains("-12", StringComparison.Ordinal)), "stream-mix levels use a true minus sign");
        stream.Close();

        var settings = new LinkedOutputPolicySettings { KnownDevices = [new("a", "Speakers"), new("b", "Speakers"), new("c", "Headphones")] };
        var window = new DevicePolicyWindow(settings, new HashSet<string>(["a", "b"], StringComparer.OrdinalIgnoreCase), _ => Task.FromResult<string?>(null))
        { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
        try
        {
            window.Show();
            window.UpdateLayout();
            PolicyDeviceRow[] rows = [.. Descendants<ListBox>(window).Single().Items.Cast<PolicyDeviceRow>()];
            check(rows.Select(r => r.Label).SequenceEqual(["Speakers", "Speakers (2)", "Headphones · unavailable"]),
                "device priorities number repeated names instead of showing IDs: " + string.Join(" | ", rows.Select(r => r.Label)));
            string[] names = Descendants<CheckBox>(window).Select(c => AutomationProperties.GetName(c)).Where(n => n.Length > 0).ToArray();
            check(names.Contains("Use Speakers (2)") && names.Contains("Exclude Speakers (2)") && names.Contains("Use Headphones") && rows[1].ToString() == "Speakers (2)",
                "each priority row's Use and Exclude boxes name their device: " + string.Join(", ", names));
        }
        finally { window.Close(); }
    }

    /// <summary>A running engine with a measured Game level that stops answering reads while <see cref="Fail"/> is set.</summary>
    private sealed class FlakyConnection : IIndependentMixerConnection
    {
        private readonly AudioHostProfile profile = AudioHostProfile.CreateDefault() with
        {
            Channels = [.. AudioHostProfile.CreateDefault().Channels.Select(c => c.Bus == AudioBus.Game ? c with { Enabled = true, CaptureEndpointId = "capture", RenderEndpointId = "render" } : c)]
        };
        public bool Fail { get; set; }
        public int Starts { get; private set; }
        private HostSnapshot Snapshot() => new(true, profile,
            [new HostChannelStatus(AudioBus.Game, "Running", "capture", "render", "Playing.", .4f, new BridgeStatus(true, 480, 0, 0, 0, null) { OutputPeak = .5f })],
            [new("capture", "Cable source", true, true), new("render", "Speakers", false, true)], null) { StartRequested = true };
        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<HostSnapshot>(new IOException("the audio engine stopped answering.")) : Task.FromResult(Snapshot());
        public Task<HostSnapshot> ApplyAsync(AudioHostProfile next, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.FromResult(Snapshot()); }
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task Idle()
    {
        for (int i = 0; i < 3; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T found) yield return found;
            foreach (var next in Descendants<T>(child)) yield return next;
        }
    }
}
