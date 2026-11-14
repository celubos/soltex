using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soltex.Audio.Host;
using Soltex.Audio.Engine;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? renderDirectory = args is ["--render-dir", var path] ? Path.GetFullPath(path) : null;
        if (args.Length != 0 && renderDirectory is null) throw new ArgumentException("Use --render-dir <absolute directory> or no arguments.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            int code = 0;
            try { await RunAsync(renderDirectory); }
            catch (Exception error) { Console.Error.WriteLine(error); code = 1; }
            finally { app.Shutdown(code); }
        };
        return app.Run();
    }

    private static async Task RunAsync(string? renderDirectory)
    {
        int checks = 0;
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks++; Console.WriteLine("PASS " + text); }
        FakeConnection fake = new();
        await using IndependentMixerWorkspace view = new();
        await view.InitializeAsync(fake);
        Check(fake.Connects == 1 && fake.Starts == 0 && fake.Applies == 0 && view.Channels.Count == 5, "opening the five-channel workspace neither starts audio nor applies a profile");
        Check(view.Channels.All(c => c.Peak == -1 && c.State == "Stopped"), "unavailable processed meters remain unknown rather than fabricated silence");
        var game = view.Channels.Single(c => c.Bus == AudioBus.Game);
        Check(game.RenderChoices.Count(c => c.Label.Contains("Speakers", StringComparison.Ordinal)) == 2 &&
            game.RenderChoices.Where(c => c.Label.Contains("Speakers", StringComparison.Ordinal)).Select(c => c.Label).Distinct().Count() == 2,
            "duplicate friendly names retain distinct visible endpoint identities");
        game.CaptureEndpointId = "capture-game"; game.RenderEndpointId = "render-one"; game.Enabled = true;
        Check(fake.Applies == 0, "editing endpoint choices remains staged until Apply changes");
        await view.ApplyRoutingAsync();
        Check(fake.Applies == 1 && fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).RenderEndpointId == "render-one",
            "Apply changes sends the selected opaque endpoint and enabled channel to the host");
        game.RenderEndpointId = "render-two"; game.GainDb = -6;
        await Task.Delay(350);
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game) is { RenderEndpointId: "render-one", GainDb: -6 },
            "live fader changes do not accidentally apply a staged destination");
        await view.StartAudioAsync();
        Check(fake.Starts == 1 && fake.Running, "only an explicit start starts the owned graph");
        view.Activate(); await Task.Delay(600); view.Deactivate(); int reads = fake.Reads; await Task.Delay(450);
        Check(reads > 0 && reads == fake.Reads && fake.Running, "hiding stops presentation polling while the owned graph continues");
        await view.StopAudioAsync();
        Check(fake.Stops == 1 && !fake.Running, "explicit Stop drains the graph without deleting its profile");
        fake.FailNextApply = true;
        await view.ApplyRoutingAsync();
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).RenderEndpointId == "render-one" &&
            view.StatusText.Contains("not confirmed", StringComparison.Ordinal), "a rejected route preserves the confirmed destination and surfaces the failure");
        await view.ApplyRoutingAsync();
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).RenderEndpointId == "render-two", "retry after a concrete operation failure applies the retained draft");
        var chat = view.Channels.Single(c => c.Bus == AudioBus.Chat);
        fake.FailNextApply = true; game.GainDb = -4;
        await Task.Delay(350);
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).GainDb != -4 && game.GainDb == -4 && view.LastOperationFailed &&
            view.StatusText.Contains("not confirmed", StringComparison.Ordinal), "a failed level write stays visible and keeps the unapplied level pending");
        chat.GainDb = -2;
        await Task.Delay(350);
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).GainDb == -4 && fake.Profile.Channels.Single(c => c.Bus == AudioBus.Chat).GainDb == -2 &&
            !view.LastOperationFailed, "the next level change resends a level whose write failed");
        fake.DelayNextApply();
        Task delayed = view.ApplyRoutingAsync(); await fake.ApplyEntered.Task;
        game.RenderEndpointId = "render-one"; game.GainDb = -9;
        fake.ReleaseApply.SetResult(); await delayed; await Task.Delay(350);
        Check(game.RenderEndpointId == "render-one" && game.GainDb == -9 &&
            fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game) is { RenderEndpointId: "render-two", GainDb: -9 },
            "delayed routing confirmation preserves newer endpoint and gain edits without applying the staged route");
        await view.ApplyRoutingAsync();
        game.OutputPriorityIds = ["render-one", "render-two"]; game.ExcludedOutputIds = ["render-two"];
        var microphone = view.Channels.Single(c => c.Bus == AudioBus.Microphone); microphone.RenderEndpointId = "saved-microphone-return";
        await view.ApplyRoutingAsync();
        await view.SelectPlaybackOutputAsync("render-two");
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).RenderEndpointId == "render-one" &&
            view.StatusText.Contains("excluded", StringComparison.Ordinal), "bulk playback switch refuses a channel exclusion without changing the graph");
        game.ExcludedOutputIds = []; await view.ApplyRoutingAsync(); await view.SelectPlaybackOutputAsync("render-two");
        Check(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game) is { RenderEndpointId: "render-two", ManualPin: true } &&
            fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).OutputPriorityIds.SequenceEqual(["render-one", "render-two"]) &&
            fake.Profile.Channels.Single(c => c.Bus == AudioBus.Microphone).RenderEndpointId == "saved-microphone-return",
            "bulk switch pins enabled playback while preserving saved priorities and microphone routing");
        string profilePath = Path.Combine(Path.GetTempPath(), "soltex-inert-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            HostProfileStore.Save(profilePath, AudioHostProfile.CreateDefault() with { Name = "Imported" });
            int appliesBeforeImport = fake.Applies; await view.ImportProfileAsync(profilePath);
            Check(fake.Applies == appliesBeforeImport && !game.Enabled && fake.Profile.Channels.Single(c => c.Bus == AudioBus.Game).Enabled,
                "import stages a profile without changing the host or starting audio");
            await view.ApplyRoutingAsync();
        }
        finally { File.Delete(profilePath); }
        foreach ((int width, int height) in new[] { (980, 720), (700, 520) })
        {
            view.Width = width; view.Height = height; view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            Check(Descendants<Button>(view).Any(b => Equals(b.Content, "Apply changes")) &&
                Descendants<ComboBox>(view).Count() >= 10 && view.ActualWidth == width,
                $"inert {width}×{height} layout retains routing, source and destination controls");
            if (renderDirectory is not null)
            {
                Directory.CreateDirectory(renderDirectory);
                RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
                PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(renderDirectory, $"independent-inert-fixture-{width}x{height}.png")); png.Save(file);
            }
        }
        var processing = new IndependentMixerDspWindow("Microphone", new DspSettings());
        var effectsContent = (FrameworkElement)processing.Content; processing.Content = null;
        var effects = new UserControl { Content = effectsContent, Width = 810, Height = 770 }; AudioTheme.Apply(effects);
        effects.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
        effects.Measure(new Size(810, 770)); effects.Arrange(new Rect(0, 0, 810, 770)); effects.UpdateLayout();
        Check(Descendants<DataGrid>(effects).Single().Items.Count == 10 && Descendants<TextBox>(effects).Count() >= 19,
            "channel processing exposes ten EQ bands and editable dynamics parameters without opening audio");
        if (renderDirectory is not null)
        {
            RenderTargetBitmap bitmap = new(810, 770, 96, 96, PixelFormats.Pbgra32); bitmap.Render(effects);
            PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(renderDirectory, "independent-effects-inert-fixture-810x770.png")); png.Save(file);
        }
        processing.Close();
        await view.ApplyMicrophoneProfileAsync(new() { Bus = AudioBus.Microphone, Enabled = true, CaptureEndpointId = "capture-game", RenderEndpointId = "render-one", Dsp = new() { InputChannel = 1 } });
        Check(!view.StatusText.Contains("aren't applied yet", StringComparison.Ordinal) && fake.Profile.Channels.Single(c => c.Bus == AudioBus.Microphone).Dsp.InputChannel == 1,
            "guided microphone apply confirms its route without leaving an already-saved routing draft");
        int startsBeforeMic = fake.Starts;
        Descendants<Button>(view).Single(b => Equals(b.Content, "Start audio")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(30);
        Check(fake.Starts == startsBeforeMic + 1, "Start audio is usable immediately after guided microphone apply without a redundant Apply changes");
        await view.StopAudioAsync();
        game.RenderEndpointId = "render-one";
        await view.ApplyMicrophoneProfileAsync(fake.Profile.Channels.Single(c => c.Bus == AudioBus.Microphone));
        Check(view.StatusText.Contains("aren't applied yet", StringComparison.Ordinal), "guided microphone apply preserves a genuinely separate staged playback edit");
        await view.ApplyRoutingAsync();
        int probeRequests = 0;
        HostEndpoint[] micDevices = [new("interface-input", "Analog 1 + 2 (USB Audio Interface)", true, true),
            new("interface-speaker", "Speakers (USB Audio Interface)", false, true), new("sonar-input", "Sonar Microphone", true, true),
            new("cable-in", "CABLE Input", false, true) { TransportId = "cable" }, new("cable-out", "CABLE Output", true, true) { TransportId = "cable" }];
        var micDialog = new IndependentMixerMicrophoneWindow(new() { Bus = AudioBus.Microphone, CaptureEndpointId = "interface-input", RenderEndpointId = "cable-in" }, micDevices,
            _ => { probeRequests++; return Task.CompletedTask; });
        var micContent = (FrameworkElement)micDialog.Content; micDialog.Content = null;
        var micFixture = new UserControl { Content = micContent, Width = 640, Height = 630 }; AudioTheme.Apply(micFixture); micFixture.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
        micFixture.Measure(new Size(640, 630)); micFixture.Arrange(new Rect(0, 0, 640, 630)); micFixture.UpdateLayout();
        var selectors = Descendants<ComboBox>(micFixture).ToArray();
        Check(probeRequests == 0 && selectors.Length == 3, "microphone setup opens no stream and provides input, hardware channel and virtual return choices");
        var inputOptions = ((IEnumerable<IndependentEndpointChoice>)selectors[0].ItemsSource).ToArray();
        Check(inputOptions.Any(c => c.Id == "interface-input") && inputOptions.All(c => c.Id != "interface-speaker" && c.Id != "sonar-input"),
            "microphone capture selector includes the interface input and excludes its speakers and Sonar");
        Check(((IEnumerable<IndependentEndpointChoice>)selectors[2].ItemsSource).Single().Id == "cable-in" &&
            Descendants<TextBlock>(micFixture).Any(t => t.Text.Contains("CABLE Output", StringComparison.Ordinal)), "virtual return selector and voice-app capture hint preserve paired cable direction");
        micDialog.Observe(new(false, AudioHostProfile.CreateDefault(), [], micDevices, null) { MicrophoneTest = new("interface-input", true, 96000, 2, 960, [.00001f, .25f], [.000005f, .1f], null) });
        Check(Descendants<TextBlock>(micFixture).Any(t => t.Text.Contains("Signal detected on Input 2", StringComparison.Ordinal)) && selectors[1].SelectedValue as string == "all",
            "measured input-two signal recommends centered input without silently changing the user's channel selection");
        Descendants<Button>(micFixture).Single(b => Equals(b.Content, "Test input for 15 seconds")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(probeRequests == 1, "capture-only microphone test requires an explicit test button action");
        if (renderDirectory is not null)
        {
            micFixture.UpdateLayout(); RenderTargetBitmap bitmap = new(640, 630, 96, 96, PixelFormats.Pbgra32); bitmap.Render(micFixture);
            PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(renderDirectory, "microphone-setup-inert-fixture-640x630.png")); png.Save(file);
        }
        micDialog.Close();
        int streamApplies = 0, streamStarts = 0, streamStops = 0;
        var streamProfile = AudioHostProfile.CreateDefault() with { Channels = AudioHostProfile.CreateDefault().Channels.Select(c => c.Bus == AudioBus.Game ? c with { Enabled = true, CaptureEndpointId = "capture-game" } : c).ToArray(),
            StreamMix = new() { PersonalRenderEndpointId = "speaker-a", StreamRenderEndpointId = "cable-in" } };
        HostSnapshot streamSnapshot = new(false, streamProfile, [], [new("capture-game", "Game capture", true, true), new("speaker-a", "Headphones", false, true), new("cable-in", "Stream return", false, true)], null);
        var streamDialog = new IndependentMixerStreamWindow(streamSnapshot,
            mix => { streamApplies++; streamSnapshot = streamSnapshot with { Profile = streamSnapshot.Profile with { StreamMix = mix } }; return Task.FromResult(streamSnapshot); },
            start => { if (start) streamStarts++; else streamStops++; streamSnapshot = streamSnapshot with { Running = start, StartRequested = start }; return Task.FromResult(streamSnapshot); });
        var streamContent = (FrameworkElement)streamDialog.Content; streamDialog.Content = null;
        var streamFixture = new UserControl { Content = streamContent, Width = 800, Height = 650 }; AudioTheme.Apply(streamFixture); streamFixture.SetResourceReference(Control.BackgroundProperty, "CanvasBrush");
        streamFixture.Measure(new Size(800, 650)); streamFixture.Arrange(new Rect(0, 0, 800, 650)); streamFixture.UpdateLayout();
        Check(streamApplies == 0 && streamStarts == 0 && streamDialog.Draft().Sends.Single(s => s.Bus == AudioBus.Microphone).PersonalMute,
            "stream panel construction is inert and microphone monitoring defaults muted");
        var sendSliders = Descendants<Slider>(streamFixture).ToArray(); sendSliders[0].Value = -9; sendSliders[1].Value = -3;
        Check(streamApplies == 0 && !Descendants<Button>(streamFixture).Single(b => Equals(b.Content, "Start stream mix")).IsEnabled,
            "personal and stream send edits are staged and block start until applied");
        Descendants<Button>(streamFixture).Single(b => Equals(b.Content, "Apply sends and outputs")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(streamApplies == 1 && streamSnapshot.Profile.StreamMix.Sends.Single(s => s.Bus == AudioBus.Game) is { PersonalGainDb: -9, StreamGainDb: -3 },
            "stream apply transfers independent personal and stream send values");
        Descendants<Button>(streamFixture).Single(b => Equals(b.Content, "Start stream mix")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(streamStarts == 1 && Descendants<Button>(streamFixture).Single(b => Equals(b.Content, "Stop audio")).IsEnabled,
            "stream start is explicit and enables stop after confirmed mode state");
        Descendants<Button>(streamFixture).Single(b => Equals(b.Content, "Stop audio")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(streamStops == 1, "stream stop dispatches to the owned host control");
        if (renderDirectory is not null)
        {
            streamFixture.UpdateLayout(); RenderTargetBitmap bitmap = new(800, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(streamFixture);
            PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(renderDirectory, "stream-mix-inert-fixture-800x650.png")); png.Save(file);
        }
        streamDialog.Close();
        await view.ShutdownAsync(); await view.ShutdownAsync();
        Check(fake.Disposals == 1, "workspace shutdown drains the owned connection exactly once");
        MicrophonePathChecks.Run(Check);
        MicrophoneSetupChecks.Run(Check);
        Console.WriteLine($"{checks}/{checks} independent UI checks passed. Inert host fixture only; no process launch, device stream or Windows audio write.");
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

    private sealed class FakeConnection : IIndependentMixerConnection
    {
        public int Connects { get; private set; }
        public int Reads { get; private set; }
        public int Applies { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public bool FailNextApply { get; set; }
        public TaskCompletionSource ApplyEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApply { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool delayNextApply;
        public bool Running { get; private set; }
        public AudioHostProfile Profile { get; private set; } = AudioHostProfile.CreateDefault();
        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Connects++; return Task.FromResult(Snapshot()); }
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Snapshot()); }
        public void DelayNextApply() { delayNextApply = true; ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); ReleaseApply = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<HostSnapshot> ApplyAsync(AudioHostProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextApply) { FailNextApply = false; throw new IOException("Fixture destination could not be opened."); }
            if (delayNextApply) { delayNextApply = false; ApplyEntered.SetResult(); await ReleaseApply.Task.WaitAsync(cancellationToken); }
            Profile = HostProfileStore.Validate(profile); Applies++; return Snapshot();
        }
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Starts++; Running = true; return Task.FromResult(Snapshot()); }
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Stops++; Running = false; return Task.FromResult(Snapshot()); }
        public ValueTask DisposeAsync() { Disposals++; Running = false; return ValueTask.CompletedTask; }
        private HostSnapshot Snapshot() => new(Running, Profile,
            Profile.Channels.Select(c => new HostChannelStatus(c.Bus, "Stopped", null, null, "Inert layout fixture; no audio opened.", null, null)).ToArray(),
            [new("capture-game", "Cable source", true, true), new("render-one", "Speakers", false, true), new("render-two", "Speakers", false, true)], null);
    }
}
