using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Views;
using Soltex.Audio.Sessions;

namespace Soltex.App.Tests;

internal static class IntegrationChecks
{
    public static void LaunchOptions()
    {
        var defaults = SuiteLaunchOptions.Parse([]);
        Check(defaults.ProfileDirectory is null && defaults.CapturePath is null && defaults.Panel is null &&
            !defaults.Mixer && !defaults.Tray && !defaults.Quit, "Empty arguments must retain normal suite startup.");
        string profile = Path.Combine(Path.GetTempPath(), "soltex-fixture", "nested", "..", "profile");
        string capture = Path.Combine(Path.GetTempPath(), "soltex-fixture", "captures", "..", "mixer.png");
        var launch = SuiteLaunchOptions.Parse(["--tray", "--profile-dir", profile, "--mixer", "--render-smoke", capture, "--panel", "audio", "--theme", "dark", "--scale", "150"]);
        Check(launch.Mixer && launch.Audio && launch.Tray && !launch.Quit && launch.Panel == "audio", "Audio/tray/capture flags lost their meaning.");
        Check(launch.Theme == Soltex.App.Settings.ThemePreference.Dark && launch.Scale == 150, "Render theme and scale were not parsed.");
        Check(SuiteLaunchOptions.Parse(["--audio"]).Audio, "--audio did not open audio.");
        Check(launch.CaptureWidth is null && launch.CaptureHeight is null, "Without --size the capture must keep the canonical size.");
        foreach (string panel in new[] { "audio", "audio-mic", "audio-apps", "audio-sonar" })
            Check(SuiteLaunchOptions.Parse(["--render-smoke", capture, "--panel", panel]).Panel == panel, "Audio capture panel was not accepted: " + panel);
        var sized = SuiteLaunchOptions.Parse(["--render-smoke", capture, "--panel", "audio-sonar", "--size", "760x820", "--scale", "200"]);
        Check(sized is { CaptureWidth: 760, CaptureHeight: 820, Scale: 200 }, "Render size was not parsed.");
        Check(launch.ProfileDirectory == Path.GetFullPath(profile) && launch.CapturePath == Path.GetFullPath(capture),
            "Explicit profile and capture paths must be normalized without using the current profile.");
        Check(SuiteLaunchOptions.Parse(["--quit"]).Quit, "Explicit quit was not parsed.");
        foreach (string[] invalid in new string[][]
        {
            ["--unknown"], ["--profile-dir"], ["--render-smoke"], ["--panel"],
            ["--profile-dir", "relative"], ["--render-smoke", "relative.png"],
            ["--profile-dir", @"D:relative"], ["--render-smoke", @"\root-relative.png"],
            ["--profile-dir", "--mixer"], ["--panel", "mixer"],
            ["--render-smoke", capture, "--panel", "--tray"],
            ["--render-smoke", capture, "--panel", " "],
            ["--render-smoke", capture, "--panel", "mixer"],
            ["--theme", "dark"], ["--scale", "150"],
            ["--render-smoke", capture, "--theme", "sepia"],
            ["--render-smoke", capture, "--scale", "250"],
            ["--render-smoke", capture, "--scale", "99"],
            ["--render-smoke", capture, "--scale", "1.5"],
            ["--size", "1280x820"], ["--render-smoke", capture, "--size"], ["--render-smoke", capture, "--size", "1280"],
            ["--render-smoke", capture, "--size", "1280x"], ["--render-smoke", capture, "--size", "x820"], ["--render-smoke", capture, "--size", "1280*820"],
            ["--render-smoke", capture, "--size", "100x820"], ["--render-smoke", capture, "--size", "1280x9999"], ["--render-smoke", capture, "--size", "-1280x820"],
            ["--render-smoke", capture, "--panel", "audio-unknown"]
        })
        {
            try { SuiteLaunchOptions.Parse(invalid); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Malformed launch arguments were accepted: " + string.Join(' ', invalid));
        }
    }

    public static void MixerConstructionAndResources()
    {
        ResourceDictionary hostResources = Application.Current.Resources;
        object[] keys = hostResources.Keys.Cast<object>().ToArray();
        object[] values = keys.Select(key => hostResources[key]).ToArray();
        ResourceDictionary[] merged = hostResources.MergedDictionaries.ToArray();
        bool hadButtonStyle = keys.Contains(typeof(Button));
        object? previousStyle = hadButtonStyle ? hostResources[typeof(Button)] : null;
        Style hostStyle = new(typeof(Button));
        hostStyle.Setters.Add(new Setter(FrameworkElement.TagProperty, "host-style-sentinel"));
        hostResources[typeof(Button)] = hostStyle;
        WindowsMixerView? view = null;
        try
        {
            view = new WindowsMixerView();
            Check(ReferenceEquals(view.DataContext, view) && view.Rows.Count == 0, "Constructor must expose an empty bound workspace.");
            Check(Field(view, "_sessions") is null && Field(view, "_devices") is null && Field(view, "_readingTask") is null,
                "Constructing an inactive mixer must not create native services or start an audio read.");
            Check(Field(view, "_timer") is DispatcherTimer { IsEnabled: false }, "Constructor must not start polling.");
            Check(ReferenceEquals(hostResources[typeof(Button)], hostStyle), "Embedded audio replaced the host button style.");
            // The Apps view is part of the suite and uses its Fluent styles instead of carrying the old dark audio theme.
            Check(ReferenceEquals(view.FindResource(typeof(Button)), hostStyle), "The Apps view must use the suite's own styles.");
            Check(hostResources.MergedDictionaries.SequenceEqual(merged), "Embedded audio modified application theme dictionaries.");
            for (int index = 0; index < keys.Length; index++)
                if (!Equals(keys[index], typeof(Button)))
                    Check(ReferenceEquals(hostResources[keys[index]], values[index]), "Embedded audio replaced a host resource: " + keys[index]);
            Check(hostResources.Keys.Cast<object>().All(key => Equals(key, typeof(Button)) || keys.Contains(key)),
                "Embedded audio leaked new application-wide resources.");
            view.Deactivate();
            view.DisposeAsync().AsTask().GetAwaiter().GetResult();
            view.DisposeAsync().AsTask().GetAwaiter().GetResult();
            view.Activate(); // A disposed view must not resurrect the native service worker.
            Check(Field(view, "_sessions") is null && Field(view, "_devices") is null && Field(view, "_timer") is DispatcherTimer { IsEnabled: false },
                "Disposal/repeated activation resurrected an inactive native service.");
        }
        finally
        {
            view?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (hadButtonStyle) hostResources[typeof(Button)] = previousStyle!;
            else hostResources.Remove(typeof(Button));
        }
    }

    public static void SessionUpdates()
    {
        WindowsMixerView view = new();
        try
        {
            var birth = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var first = Session(new("physical-a", "instance-a", 123, birth), 0.3, 0.1);
            var second = Session(new("physical-a", "instance-b", 123, birth), 0.8, 0.2);
            Apply(view, Snapshot(first, second));
            view.Measure(new Size(980, 720));
            view.Arrange(new Rect(0, 0, 980, 720));
            view.UpdateLayout();
            RenderTargetBitmap rendered = new(980, 720, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(view);
            Check(Descendants(view).OfType<TextBox>().Count() == 2,
                "The real populated session item templates were not instantiated during rendering.");
            Soltex.Audio.UI.SignalMeter[] sessionMeters = Descendants(view).OfType<Soltex.Audio.UI.SignalMeter>().Where(meter => meter.DataContext is WindowsSessionRow).ToArray();
            Check(sessionMeters.Length == 2 && sessionMeters.All(meter => BindingOperations.GetBinding(meter, Soltex.Audio.UI.SignalMeter.LevelProperty)?.Mode == BindingMode.OneWay),
                "Session meters must bind one-way to read-only observed signal properties.");
            Check(view.Rows.Count == 2 && view.Rows[0].Key != view.Rows[1].Key,
                "Same-name and same-PID sessions were merged despite distinct instance identities.");
            WindowsSessionRow firstRow = view.Rows.Single(row => row.Key == first.Key);
            WindowsSessionRow secondRow = view.Rows.Single(row => row.Key == second.Key);
            Check(firstRow.CanRoute && ReferenceEquals(firstRow.Observation, first), "Verified executable identity must remain available for explicit routing.");
            Check(Math.Abs(firstRow.Level - 30) < 0.001 && Math.Abs(secondRow.Level - 80) < 0.001, "Rows lost independent volume values.");

            List<string?> notifications = [];
            firstRow.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
            firstRow.Editing = true;
            Apply(view, Snapshot(second, first with { Volume = 0.6, Peak = 0.75, Muted = true }));
            Check(ReferenceEquals(firstRow, view.Rows.Single(row => row.Key == first.Key)) && ReferenceEquals(secondRow, view.Rows.Single(row => row.Key == second.Key)),
                "Snapshot reordering replaced existing session rows and would lose in-progress focus.");
            Check(Math.Abs(firstRow.Level - 30) < 0.001 && Math.Abs(firstRow.Peak - 0.75) < 0.001 && firstRow.Muted,
                "Polling must preserve the edited level while still updating independent meter/mute state.");
            Check(!notifications.Any(name => string.IsNullOrEmpty(name) || name is nameof(WindowsSessionRow.Level) or nameof(WindowsSessionRow.LevelText)),
                "Polling during an edit must not issue a broad/level notification that overwrites the text binding.");
            Check(notifications.Contains(nameof(WindowsSessionRow.Peak)) && notifications.Contains(nameof(WindowsSessionRow.Muted)),
                "Independent peak and mute changes must still notify their bindings during an edit.");
            notifications.Clear();
            (typeof(WindowsMixerView).GetMethod("Confirm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Mixer confirmation boundary was not found.")).Invoke(view,
                [firstRow, new AudioSessionControlResult(true, true, first with { Volume = 0.9 }, null)]);
            Check(Math.Abs(firstRow.Level - 30) < 0.001 && !notifications.Any(name => string.IsNullOrEmpty(name) || name is nameof(WindowsSessionRow.Level) or nameof(WindowsSessionRow.LevelText)),
                "An older completed write must not overwrite a newer numeric edit through readback or binding notification.");
            notifications.Clear();
            firstRow.Editing = false;
            Apply(view, Snapshot(first with { Volume = 0.6, Peak = null, CanControl = false }, second with { State = AudioSessionState.Expired }));
            Check(view.Rows.Count == 1 && ReferenceEquals(view.Rows[0], firstRow), "Expired session removal disturbed the surviving row.");
            Check(Math.Abs(firstRow.Level - 60) < 0.001 && firstRow.Peak == 0 && !firstRow.CanControl,
                "Completed edits must accept confirmed levels and unavailable signal/control state.");
            Check(notifications.Contains(nameof(WindowsSessionRow.Level)) && notifications.Contains(nameof(WindowsSessionRow.LevelText)),
                "A newly accepted level must refresh both slider and numeric text bindings.");

            var restarted = first with { Key = first.Key with { ProcessStartTimeUtc = birth.AddSeconds(1) } };
            Apply(view, Snapshot(restarted));
            Check(view.Rows.Count == 1 && view.Rows[0].Key == restarted.Key && !ReferenceEquals(view.Rows[0], firstRow),
                "Reused PID/session instance must not inherit a different process lifetime's editable row.");
            Check(Field(view, "_sessions") is null && Field(view, "_devices") is null,
                "Applying presentation snapshots must not create control services.");
            Apply(view, Snapshot(first with { Name = "Audio process 0", IsSystemSounds = true, CanControl = false }));
            Check(view.Rows.Single().Name == "System sounds" && !view.Rows.Single().CanControl,
                "System sound sessions must have a recognizable label and remain read-only.");
            Check(!view.Rows.Single().CanRoute, "System sessions must never offer application routing.");
        }
        finally { view.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public static void RoutingWindowIsInert()
    {
        var application = Application.Current;
        ShutdownMode previousMode = application.ShutdownMode;
        Window? previousMainWindow = application.MainWindow;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var session = Session(new("physical-a", "instance-a", 123, DateTimeOffset.UtcNow), .5, .1);
        var migrated = session with { Key = session.Key with { EndpointId = "physical-b", SessionInstanceId = "replacement-stream" } };
        Check(WindowsApplicationRoutingWindow.ResolveSelection(session, Snapshot(migrated)) == migrated,
            "Undo must refresh a migrated stream while retaining the exact original process identity.");
        foreach (var invalid in new[]
        {
            migrated with { Key = migrated.Key with { ProcessStartTimeUtc = migrated.Key.ProcessStartTimeUtc!.Value.AddSeconds(1) } },
            migrated with { Key = migrated.Key with { ProcessId = 456 } },
            migrated with { CanControl = false },
            migrated with { ExecutablePath = @"D:\fixture\different.exe" }
        })
        {
            bool rejected = false;
            try { WindowsApplicationRoutingWindow.ResolveSelection(session, Snapshot(invalid)); }
            catch (IOException) { rejected = true; }
            Check(rejected, "Refreshing a route must reject a reused PID, new process, inaccessible session or changed executable.");
        }
        AudioSessionEndpoint[] endpoints =
        [
            new("physical-a", "Same speakers", AudioEndpointClassification.Unknown, .5, false, 0, [session]),
            new("physical-b", "Same speakers", AudioEndpointClassification.Unknown, .5, false, 0, [])
        ];
        WindowsApplicationRoutingWindow window = new(session, endpoints, Path.Combine(Path.GetTempPath(), "soltex-routing-ui-" + Guid.NewGuid().ToString("N")));
        try
        {
            object? Read(string name) => typeof(WindowsApplicationRoutingWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window);
            Check(Read("_service") is null, "Opening routing UI must not activate native policy or perform any mutation.");
            Check(Read("_apply") is Button { IsEnabled: false }, "Apply must require successful preflight.");
            Check(Read("_restore") is Button { IsEnabled: false }, "Undo must require an owned completed journal.");
            var target = (ComboBox)Read("_target")!;
            Check(target.Items.Count == 3, "Default plus both same-name identities must remain selectable.");
            Check(target.Items[1].ToString() != target.Items[2].ToString(), "Same-name output choices must be disambiguated by identity.");
            target.SelectedIndex = 2;
            Check(Read("_service") is null && Read("_apply") is Button { IsEnabled: false },
                "Choosing an output alone must not activate routing or enable an unchecked apply.");
            if (window.Content is not FrameworkElement content) throw new InvalidOperationException("Routing surface is missing.");
            content.Measure(new Size(550, 390)); content.Arrange(new Rect(0, 0, 550, 390)); content.UpdateLayout();
            RenderTargetBitmap image = new(550, 390, 96, 96, PixelFormats.Pbgra32); image.Render(content);
        }
        finally
        {
            window.ShutdownAsync().GetAwaiter().GetResult();
            application.MainWindow = previousMainWindow;
            application.ShutdownMode = previousMode;
        }
    }

    public static void InitialDeviceSelection()
    {
        AudioSessionEndpoint virtualDefault = new("virtual-default", "Virtual default", AudioEndpointClassification.KnownVirtual, 0.5, false, 0, []);
        AudioSessionEndpoint physicalA = new("physical-a", "Playback A", AudioEndpointClassification.Unknown, 0.4, false, 0, []);
        AudioSessionEndpoint physicalB = new("physical-b", "Playback B", AudioEndpointClassification.Unknown, 0.6, false, 0, []);
        AudioSessionSnapshot full = new(DateTimeOffset.UtcNow, [virtualDefault, physicalA, physicalB], []);
        WindowsMixerView view = new();
        try
        {
            SetField(view, "_initialDefaultId", virtualDefault.Id);
            Apply(view, full);
            Check(Equals(view.DeviceSelector.SelectedValue, physicalA.Id) && view.DeviceSelector.Items.Count == 2 && view.EndpointLevel.IsEnabled,
                "A hidden virtual default must initially expose the first available visible device controls.");
            view.DeviceSelector.SelectedValue = physicalB.Id;
            Check(Equals(Field(view, "_selectedId"), physicalB.Id), "An explicit device selector gesture did not retain the selected ID.");
            Apply(view, new(DateTimeOffset.UtcNow, [virtualDefault, physicalA], []));
            Check(Equals(view.DeviceSelector.SelectedValue, physicalB.Id) && view.DeviceSelector.SelectedItem is not null && view.DeviceSelector.Items.Count == 2,
                "An explicitly selected disconnected device must remain selected as an unavailable placeholder.");
            Check(!view.EndpointLevel.IsEnabled && !view.EndpointMute.IsEnabled && view.Rows.Count == 0,
                "Missing device controls must be disabled rather than silently operating another endpoint.");
            Apply(view, full);
            Check(Equals(view.DeviceSelector.SelectedValue, physicalB.Id) && view.EndpointLevel.IsEnabled,
                "A reappearing explicitly selected device must restore its own controls.");
            Check(Field(view, "_devices") is null && Field(view, "_sessions") is null,
                "Viewing/selecting synthetic endpoints must not create native services or change routing.");
        }
        finally { view.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        WindowsMixerView delayed = new();
        try
        {
            SetField(delayed, "_initialDefaultId", virtualDefault.Id);
            Apply(delayed, new(DateTimeOffset.UtcNow, [], []));
            Check(Field(delayed, "_initialSelectionComplete") is false && delayed.DeviceSelector.SelectedValue is null,
                "An empty snapshot must not finalize initial selection.");
            Apply(delayed, new(DateTimeOffset.UtcNow, [virtualDefault], []));
            Check(Field(delayed, "_initialSelectionComplete") is false && delayed.DeviceSelector.Items.Count == 0,
                "Hidden-only endpoints must not create an unusable initial placeholder.");
            Apply(delayed, full);
            Check(Equals(delayed.DeviceSelector.SelectedValue, physicalA.Id) && Field(delayed, "_initialSelectionComplete") is true,
                "Initial selection must recover when a visible endpoint arrives.");
        }
        finally { delayed.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        WindowsMixerView visibleDefault = new();
        try
        {
            SetField(visibleDefault, "_initialDefaultId", physicalB.Id);
            Apply(visibleDefault, full);
            Check(Equals(visibleDefault.DeviceSelector.SelectedValue, physicalB.Id), "A visible default must be preferred over enumeration order initially.");
            SetField(visibleDefault, "_initialDefaultId", physicalA.Id);
            Apply(visibleDefault, full);
            Check(Equals(visibleDefault.DeviceSelector.SelectedValue, physicalB.Id), "Later default observations must not override an established view selection.");
        }
        finally { visibleDefault.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static AudioSessionInfo Session(AudioSessionKey key, double volume, double peak) =>
        new(key, "Same application", @"D:\fixture\application.exe", AudioSessionState.Active, volume, false, peak, false, true);

    private static AudioSessionSnapshot Snapshot(params AudioSessionInfo[] sessions) =>
        new(DateTimeOffset.UtcNow, [new("physical-a", "Fixture playback", AudioEndpointClassification.Unknown, 0.5, false, 0.25, sessions)], []);

    private static void Apply(WindowsMixerView view, AudioSessionSnapshot snapshot) =>
        (typeof(WindowsMixerView).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mixer snapshot presentation boundary was not found.")).Invoke(view, [snapshot]);

    private static object? Field(WindowsMixerView view, string name) =>
        (typeof(WindowsMixerView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mixer lifecycle field was not found: " + name)).GetValue(view);

    private static void SetField(WindowsMixerView view, string name, object value) =>
        (typeof(WindowsMixerView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Mixer lifecycle field was not found: " + name)).SetValue(view, value);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
