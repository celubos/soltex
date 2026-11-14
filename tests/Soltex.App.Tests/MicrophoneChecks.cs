using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Pages;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.App.Tests;

/// <summary>The microphone card against a scripted helper: route text, guided test, no audio device.</summary>
internal static class MicrophoneChecks
{
    private sealed class FakeSource : IMicrophonePathSource
    {
        private HostSnapshot latest;
        private string? probe;
        private long frames;
        public List<string?> TestCalls { get; } = [];
        public int Starts { get; private set; }
        public float[] VoicePeaks { get; set; } = [.0005f, .3f];
        public FakeSource(HostSnapshot initial) => latest = initial;
        public HostSnapshot? Latest => latest;
        public event EventHandler? Changed;
        public Task RefreshAsync()
        {
            if (probe is not null)
            {
                frames += 4800;
                latest = latest with { MicrophoneTest = new MicrophoneProbeStatus(probe, true, 48000, 2, frames, VoicePeaks, VoicePeaks, null) };
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
        public Task SetMicrophoneTestAsync(string? captureEndpointId)
        {
            TestCalls.Add(captureEndpointId);
            probe = captureEndpointId;
            if (probe is null && latest.MicrophoneTest is { } last) latest = latest with { MicrophoneTest = last with { Running = false } };
            return RefreshAsync();
        }
        /// <summary>When set, Start is refused with this message, as the helper does while a stream mix runs.</summary>
        public string? StartFailure { get; set; }
        public Task StartAudioAsync()
        {
            if (StartFailure is { } failure) throw new InvalidOperationException(failure);
            Starts++; latest = latest with { StartRequested = true }; return RefreshAsync();
        }
        public Task StopAudioAsync() { latest = latest with { StartRequested = false }; return RefreshAsync(); }
        public List<HostChannelProfile> Saved { get; } = [];
        public Task ApplyMicrophoneAsync(HostChannelProfile profile)
        {
            Saved.Add(profile);
            latest = latest with { Profile = latest.Profile with { Channels = latest.Profile.Channels.Select(c => c.Bus == profile.Bus ? profile : c).ToArray() } };
            return RefreshAsync();
        }
    }

    /// <summary>A connection that has no state yet, for example because the audio engine is missing.</summary>
    private sealed class OfflineSource : IMicrophonePathSource
    {
        public string StatusText { get; set; } = "";
        public int Refreshes { get; private set; }
        public HostSnapshot? Latest => null;
        public event EventHandler? Changed { add { } remove { } }
        public Task RefreshAsync() { Refreshes++; return Task.CompletedTask; }
        public Task SetMicrophoneTestAsync(string? captureEndpointId) => Task.CompletedTask;
        public Task StartAudioAsync() => Task.CompletedTask;
        public Task StopAudioAsync() => Task.CompletedTask;
        public Task ApplyMicrophoneAsync(HostChannelProfile profile) => Task.CompletedTask;
    }

    private static HostSnapshot InterfaceToCable(int? inputChannel)
    {
        AudioHostProfile profile = AudioHostProfile.CreateDefault();
        profile = profile with
        {
            Channels = profile.Channels.Select(c => c.Bus != AudioBus.Microphone ? c : c with
            {
                Enabled = true, CaptureEndpointId = "interface", RenderEndpointId = "cable-in",
                Dsp = c.Dsp with { InputChannel = inputChannel, Compressor = new CompressorSettings(Enabled: true) }
            }).ToArray()
        };
        HostEndpoint[] endpoints =
        [
            new("interface", "Analog 1 + 2 (USB Audio Interface)", true, true),
            new("cable-in", "CABLE Input (VB-Audio Virtual Cable)", false, true) { TransportId = "vb-audio-base-cable" },
            new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" }
        ];
        return new HostSnapshot(false, profile, [], endpoints, null);
    }

    public static void CardDescribesRouteAndGuidesTest()
    {
        FakeSource source = new(InterfaceToCable(inputChannel: null));
        MicrophonePathView view = new();
        Window window = Host(view);
        try
        {
            view.Attach(source);
            Pump();
            Program.True(view.ReceivingName.Text == "CABLE Output (VB-Audio Virtual Cable)", "The card did not name CABLE Output as the input other apps choose.");
            Program.True(view.SourceName.Text.Contains("USB Audio Interface", StringComparison.Ordinal) && view.DestinationName.Text.StartsWith("CABLE Input", StringComparison.Ordinal),
                "The card did not show the interface source and the CABLE Input destination.");
            Program.True(view.CopyName.Visibility == Visibility.Visible && view.TestButton.IsEnabled && (string)view.StartStopButton.Content == "Start audio",
                "A stopped, configured path must offer copy, test and start.");
            Program.True(view.StateText.Text == "Stopped", "A stopped path must say Stopped, not " + view.StateText.Text);

            MicrophoneTestResult? result = Wait(view.RunTestAsync(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(40)));
            Program.True(result?.Verdict == MicrophoneTestVerdict.OneSideOnly && result.LoudestInput == 1,
                "Voice only on input 2 must be reported as one-sided; got " + (result?.Verdict.ToString() ?? "no result"));
            Program.True(source.TestCalls.SequenceEqual(["interface", null]), "The capture-only test must open the configured input and always close it afterwards.");
            Program.True(source.Starts == 0, "Testing must never start audio.");
            Program.True(view.TestPanel.Visibility == Visibility.Visible && (string)view.TestPanel.Tag == "Caution" &&
                view.TestTitle.Text.Contains("input 2", StringComparison.Ordinal), "The verdict was not shown with the input to choose.");
            SaveRender(window, "mic-card-after-test-dark");
            global::Soltex.App.Shell.ThemeService.Apply(Application.Current, new global::Soltex.App.Settings.AppSettings { Theme = global::Soltex.App.Settings.ThemePreference.Light });
            window.Background = (System.Windows.Media.Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush");
            Pump();
            SaveRender(window, "mic-card-after-test-light");
            global::Soltex.App.Shell.ThemeService.Apply(Application.Current, new global::Soltex.App.Settings.AppSettings());

            view.StartStopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(); Pump();
            Program.True(source.Starts == 1 && (string)view.StartStopButton.Content == "Stop audio", "Start audio did not start through the helper.");

            view.Active = false;
            Program.True(!Polling(view), "The card kept polling the helper while hidden.");
            view.Active = true;
            Program.True(Polling(view), "The card did not resume polling when shown.");
        }
        finally { window.Close(); }
    }

    public static void CardExplainsConnectionAndRefusals()
    {
        OfflineSource offline = new() { StatusText = "Audio isn't available: the audio engine is missing." };
        MicrophonePathView view = new();
        Window window = Host(view);
        try
        {
            view.Attach(offline);
            Pump();
            Program.True(view.StateText.Text == "Connecting…" && view.Explanation.Text == offline.StatusText && !view.StartStopButton.IsEnabled,
                "Without a connection the card must say why instead of only 'Connecting'; got: " + view.Explanation.Text);
            offline.StatusText = "Audio isn't available: the engine did not answer.";
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.Explanation.Text != offline.StatusText && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(20); }
            Program.True(offline.Refreshes > 0 && view.Explanation.Text == offline.StatusText, "Polling must keep retrying and show the newest reason.");

            FakeSource source = new(InterfaceToCable(inputChannel: null)) { StartFailure = "Stop stream mix before starting normal channels." };
            view.Attach(source);
            Pump();
            view.StartStopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(); Pump();
            Program.True(view.TestPanel.Visibility == Visibility.Visible && (string)view.TestPanel.Tag == "Critical" &&
                view.TestTitle.Text == "Audio did not change" && view.TestAdvice.Text.Contains("stream mix", StringComparison.Ordinal),
                "A refused Start must show the helper's reason on the card.");
            Program.True(source.Starts == 0 && (string)view.StartStopButton.Content == "Start audio", "A refused Start must leave the card stopped.");
        }
        finally { window.Close(); }
    }

    public static void SetupSavesRouteAndFixesInput()
    {
        HostEndpoint[] endpoints =
        [
            new("interface", "Analog 1 + 2 (USB Audio Interface)", true, true),
            new("webcam", "Microphone (USB2.0 Device)", true, true),
            new("cable-in", "CABLE Input (VB-Audio Virtual Cable)", false, true) { TransportId = "vb-audio-base-cable" },
            new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" },
            new("sonar-mic", "SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", true, true),
            new("speakers", "Speakers (USB Audio Interface)", false, true)
        ];
        FakeSource source = new(new HostSnapshot(false, AudioHostProfile.CreateDefault(), [], endpoints, null));
        MicrophonePathView view = new();
        Window window = Host(view);
        try
        {
            view.Attach(source);
            Pump();
            Program.True(view.StateText.Text == "Not set up" && (string)view.ChangeButton.Content == "Set up my microphone" && !view.SoundButton.IsEnabled,
                "A new PC must lead with setting up the microphone.");
            view.OpenSetup();
            Pump();
            Program.True(view.IsSetupOpen && view.MicrophoneChoice.SelectedItem is SetupEndpointOption { Id: "interface" } &&
                view.DestinationChoice.SelectedItem is SetupEndpointOption { Id: "cable-in" } &&
                view.SoundChoice.SelectedItem is MicrophoneSoundChoice { Sound: MicrophoneSound.Natural },
                "First setup must suggest the physical microphone, the cable and Natural voice.");
            Program.True(view.MicrophoneChoice.Items.Count == 2, "Sonar and cable outputs must not be offered as the microphone.");
            Program.True(view.DestinationHint.Text.Contains("CABLE Output (VB-Audio Virtual Cable)", StringComparison.Ordinal),
                "The setup must name the input other apps will choose.");
            SaveRender(window, "mic-setup-open-dark");

            Program.True(Wait(view.SaveSetupAsync()), "Saving a valid setup failed.");
            HostChannelProfile saved = source.Saved.Single();
            Program.True(saved is { Enabled: true, CaptureEndpointId: "interface", RenderEndpointId: "cable-in" } && saved.Dsp.InputChannel is null &&
                MicrophoneSetup.Identify(saved.Dsp) == MicrophoneSound.Natural, "The saved channel does not match the choices.");
            Program.True(source.Starts == 0, "Saving the setup must never start audio.");
            Program.True(!view.IsSetupOpen && view.StateText.Text == "Stopped" && view.ReceivingName.Text == "CABLE Output (VB-Audio Virtual Cable)",
                "After saving, the card must show the stopped route and the receiving name.");

            MicrophoneTestResult? result = Wait(view.RunTestAsync(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(40)));
            Program.True(result?.Verdict == MicrophoneTestVerdict.OneSideOnly && view.FixButton.Visibility == Visibility.Visible &&
                (string)view.FixButton.Content == "Use input 2 only", "A one-sided voice must offer the one-click input fix.");
            SaveRender(window, "mic-test-offers-fix-dark");
            Program.True(Wait(view.ApplyTestFixAsync()), "The input fix was not saved.");
            Program.True(source.Saved[^1].Dsp.InputChannel == 1 && source.Saved[^1].CaptureEndpointId == "interface" &&
                MicrophoneSetup.Identify(source.Saved[^1].Dsp) == MicrophoneSound.Natural, "The fix must change only the input.");
            Program.True(view.SourceDetail.Text.StartsWith("Input 2 only", StringComparison.Ordinal) && view.FixButton.Visibility == Visibility.Collapsed,
                "The card must show the new input and drop the fix.");

            view.EditSound = dsp => dsp with { Gate = new GateSettings(Enabled: true, ThresholdDb: -50) };
            Program.True(Wait(view.AdjustSoundAsync()), "Adjusting the sound failed.");
            Program.True(source.Saved[^1].Dsp is { Gate.Enabled: true, InputChannel: 1 } && view.ProcessingText.Text.Split('\n').Contains("Noise gate"),
                "Sound changes must keep the input choice and show in the processing step.");
            view.EditSound = _ => null;
            int saves = source.Saved.Count;
            Program.True(!Wait(view.AdjustSoundAsync()) && source.Saved.Count == saves, "Cancelling the sound editor must not save anything.");
        }
        finally { window.Close(); }
    }

    public static void ProcessingEditorKeepsPresetsAndEdits()
    {
        DspSettings natural = MicrophoneSetup.ApplySound(MicrophoneSound.Natural, new DspSettings { InputChannel = 1, GainDb = 3 });
        ProcessingEditor editor = new(natural, microphone: true);
        Window window = Host(new System.Windows.Controls.ScrollViewer { Content = editor });
        try
        {
            Pump();
            DspSettings untouched = editor.Result();
            Program.True(MicrophoneSetup.SameSound(untouched, natural) && untouched.GainDb == 3 && untouched.InputChannel == 1,
                "Opening and saving without changes must keep every value exactly.");
            Program.True(editor.PresetChoice?.SelectedItem is MicrophoneSoundChoice { Sound: MicrophoneSound.Natural }, "The editor must show the current preset.");
            SaveRender(window, "processing-editor-dark");

            System.Windows.Controls.Slider threshold = Find<System.Windows.Controls.Slider>(editor, "Starts at");
            threshold.Value = -30;
            DspSettings edited = editor.Result();
            Program.True(edited.Compressor.ThresholdDb == -30 && edited.Compressor.Ratio == natural.Compressor.Ratio && edited.OutputCeiling == natural.OutputCeiling,
                "Only the edited value may change.");
            Program.True(editor.PresetChoice?.SelectedItem is MicrophoneSoundChoice { Sound: MicrophoneSound.Custom }, "After a hand edit the preset label must say Custom.");

            editor.PresetChoice!.SelectedItem = MicrophoneSetup.Sounds.First(s => s.Sound == MicrophoneSound.Broadcast);
            Pump();
            DspSettings broadcast = editor.Result();
            Program.True(MicrophoneSetup.Identify(broadcast) == MicrophoneSound.Broadcast && broadcast.InputChannel == 1 && broadcast.GainDb == 3,
                "Choosing a preset must load it and keep the input and level.");

            ProcessingEditor playback = new(new DspSettings(), microphone: false);
            Program.True(playback.PresetChoice is null && Find<System.Windows.Controls.Slider>(playback, "Width") is not null,
                "Playback channels get stereo controls and no microphone presets.");
        }
        finally { window.Close(); }
    }

    private static T Find<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        Queue<DependencyObject> queue = new([root]);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (node is T element && System.Windows.Automation.AutomationProperties.GetName(element) == name) return element;
            foreach (object child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) queue.Enqueue(d);
        }
        throw new InvalidOperationException("No " + typeof(T).Name + " named " + name);
    }

    private static Window Host(FrameworkElement content)
    {
        Window window = new()
        {
            Content = content, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush")
        };
        global::Soltex.App.RenderSmokeCapture.ConfigureWindow(window);
        window.Show();
        return window;
    }

    private static bool Polling(MicrophonePathView view) =>
        ((DispatcherTimer)typeof(MicrophonePathView).GetField("polling", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!).IsEnabled;

    private static void SaveRender(Window window, string name)
    {
        // Saves a render only when SOLTEX_RENDER_DIR is set; the test never depends on it.
        if (Environment.GetEnvironmentVariable("SOLTEX_RENDER_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        RenderTargetBitmap bitmap = global::Soltex.App.RenderSmokeCapture.Capture(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static T Wait<T>(Task<T> task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The microphone test did not finish.");
            Pump();
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
