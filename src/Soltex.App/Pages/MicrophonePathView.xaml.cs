using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>What the microphone card needs from the audio helper connection.</summary>
public interface IMicrophonePathSource
{
    HostSnapshot? Latest { get; }
    event EventHandler? Changed;
    Task RefreshAsync();
    Task SetMicrophoneTestAsync(string? captureEndpointId);
    Task StartAudioAsync();
    Task StopAudioAsync();
    /// <summary>Saves the microphone channel (used at once if audio runs). Throws when the helper rejects it.</summary>
    Task ApplyMicrophoneAsync(HostChannelProfile profile);
    /// <summary>The connection's latest status, shown while there is no state yet (for example why it can't connect).</summary>
    string StatusText => "";
    /// <summary>True when the connection stopped answering: <see cref="Latest"/> is then old and may no longer be true.</summary>
    bool ConnectionLost => false;
}

/// <summary>Adapts the independent mixer workspace (which owns the helper connection) to the microphone card.</summary>
internal sealed class WorkspaceMicrophoneSource(IndependentMixerWorkspace workspace) : IMicrophonePathSource
{
    public HostSnapshot? Latest => workspace.LatestSnapshot;
    public event EventHandler? Changed { add => workspace.StatusApplied += value; remove => workspace.StatusApplied -= value; }
    public Task RefreshAsync() => workspace.RefreshStateAsync();
    public Task SetMicrophoneTestAsync(string? captureEndpointId) => workspace.SetMicrophoneTestAsync(captureEndpointId);
    // The throwing variants, so the card can show why the helper refused (for example while a stream mix is on).
    public Task StartAudioAsync() => workspace.StartAudioOrThrowAsync();
    public Task StopAudioAsync() => workspace.StopAudioOrThrowAsync();
    public Task ApplyMicrophoneAsync(HostChannelProfile profile) => workspace.ApplyChannelAsync(profile);
    public string StatusText => workspace.StatusText;
    public bool ConnectionLost => workspace.ConnectionLost;
}

/// <summary>
/// The microphone route as one picture: microphone → processing → destination → the name other apps pick,
/// with live levels and a guided test. Polls the helper only while visible.
/// </summary>
public partial class MicrophonePathView : UserControl
{
    /// <summary>The shared test length, so this card and the setup window always say and do the same.</summary>
    public static readonly TimeSpan TestDuration = MicrophonePath.TestDuration;
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromMilliseconds(250) };
    /// <summary>Puts "Copy name" back after the confirmation has been seen.</summary>
    private readonly DispatcherTimer copyReset = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? copiedName;
    private IMicrophonePathSource? source;
    private MicrophonePathModel? model;
    private MicrophoneTestResult? lastResult;
    private bool reading, testing, busy, stopRequested, active = true;

    public MicrophonePathView()
    {
        InitializeComponent();
        TestButton.ToolTip = "Listens for " + MicrophonePath.TestDurationText + " and tells you what it heard. Nothing is played or sent.";
        EditSound = settings =>
        {
            ProcessingWindow window = new("Microphone sound", settings, microphone: true) { Owner = Window.GetWindow(this) };
            return window.ShowDialog() == true ? window.Result : null;
        };
        polling.Tick += async (_, _) => await PollAsync();
        copyReset.Tick += (_, _) => ResetCopyButton();
        IsVisibleChanged += (_, _) => UpdatePolling();
        Unloaded += (_, _) => polling.Stop();
        Render(null);
    }

    public MicrophonePathModel? Model => model;
    public bool IsSetupOpen => SetupPanel.Visibility == Visibility.Visible;

    /// <summary>Shows the processing editor and returns the edited settings, or null when cancelled. Replaceable for tests.</summary>
    public Func<DspSettings, DspSettings?> EditSound { get; set; }

    /// <summary>False while the window is minimized or the page is hidden: the helper is not polled at all.</summary>
    public bool Active
    {
        get => active;
        set { active = value; UpdatePolling(); }
    }
    public bool IsTesting => testing;

    public void Attach(IMicrophonePathSource attached)
    {
        ArgumentNullException.ThrowIfNull(attached);
        if (source is not null) source.Changed -= SourceChanged;
        source = attached;
        source.Changed += SourceChanged;
        Render(source.Latest);
        UpdatePolling();
    }

    private void SourceChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) Render(source?.Latest);
        else Dispatcher.InvokeAsync(() => Render(source?.Latest));
    }

    private void UpdatePolling()
    {
        if (source is not null && active && IsVisible) polling.Start(); else polling.Stop();
    }

    private async Task PollAsync()
    {
        if (source is null || reading || testing) return;
        reading = true;
        try
        {
            await source.RefreshAsync();
            // No new state means no Changed event; refresh the text so a failed connection shows its reason.
            if (source.Latest is null || source.ConnectionLost) Render(source.Latest);
        }
        finally { reading = false; }
    }

    internal void Render(HostSnapshot? snapshot)
    {
        // A connection that stopped answering leaves an old snapshot behind: show the connection, not that state.
        bool lost = snapshot is not null && source?.ConnectionLost == true;
        model = snapshot is null || lost ? null : MicrophonePath.Describe(snapshot);
        if (model is null)
        {
            StateText.Text = lost ? "Not connected" : "Connecting…";
            StateBadge.Background = (System.Windows.Media.Brush)FindResource(lost ? "SystemFillColorCriticalBackgroundBrush" : "SubtleFillColorSecondaryBrush");
            Explanation.Text = source?.StatusText is { Length: > 0 } status ? status : "Connecting to audio…";
            SourceName.Text = DestinationName.Text = ReceivingName.Text = "—";
            SourceDetail.Text = ReceivingDetail.Text = InputLevel.Text = OutputLevel.Text = "";
            ProcessingText.Text = "";
            InputMeter.Level = OutputMeter.Level = -1;
            CopyName.Visibility = Visibility.Collapsed;
            TestButton.IsEnabled = StartStopButton.IsEnabled = ChangeButton.IsEnabled = SoundButton.IsEnabled = false;
            return;
        }
        StateText.Text = model.StateText;
        StateBadge.Background = (System.Windows.Media.Brush)FindResource(model.State switch
        {
            MicrophonePathState.Running => "SystemFillColorSuccessBackgroundBrush",
            MicrophonePathState.Problem => "SystemFillColorCriticalBackgroundBrush",
            MicrophonePathState.Muted => "SystemFillColorCautionBackgroundBrush",
            _ => "SubtleFillColorSecondaryBrush"
        });
        Explanation.Text = model.Explanation;
        SourceName.Text = model.SourceName;
        SourceDetail.Text = model.SourceDetail;
        ProcessingText.Text = model.Processing.Count == 0 ? "Not set up" : string.Join("\n", model.Processing);
        DestinationName.Text = model.DestinationName;
        ReceivingName.Text = model.ReceivingInputName ?? "Not available";
        ReceivingDetail.Text = model.ReceivingDetail;
        CopyName.Visibility = model.ReceivingInputName is null ? Visibility.Collapsed : Visibility.Visible;
        if (copiedName is not null && copiedName != model.ReceivingInputName) ResetCopyButton();

        bool live = model.State is MicrophonePathState.Running or MicrophonePathState.Muted;
        if (!testing)
        {
            ShowLevel(InputMeter, InputLevel, live ? model.InputPeak : null, live ? "Not measured" : "Measured while audio runs or during a test");
        }
        ShowLevel(OutputMeter, OutputLevel, live ? model.OutputPeak : null, live ? "Not measured" : "Measured while audio runs");

        bool audioOn = snapshot!.StartRequested;
        StartStopButton.Content = audioOn ? "Stop audio" : "Start audio";
        StartStopButton.IsEnabled = !busy && !testing && (audioOn || model.State != MicrophonePathState.NotSetUp);
        ChangeButton.Content = model.State == MicrophonePathState.NotSetUp ? "Set up my microphone" : "Change setup";
        ChangeButton.IsEnabled = !testing && !busy && !IsSetupOpen;
        SoundButton.IsEnabled = !testing && !busy && model.State != MicrophonePathState.NotSetUp;
        // The primary action is setup until there is something to test.
        if (model.State == MicrophonePathState.NotSetUp && !IsSetupOpen)
        {
            ChangeButton.Style = (Style)FindResource("AccentButtonStyle");
            TestButton.ClearValue(StyleProperty);
        }
        else
        {
            ChangeButton.ClearValue(StyleProperty);
            TestButton.Style = (Style)FindResource("AccentButtonStyle");
        }
        TestButton.IsEnabled = !busy && (testing || model.CanTest);
        TestButton.Content = testing ? "Stop test" : "Test my microphone";
    }

    private static void ShowLevel(SignalMeter meter, TextBlock label, float? peak, string unknown)
    {
        // The meter rises at once and falls back smoothly between readings; no reading means an empty track.
        meter.Level = peak is { } measured && float.IsFinite(measured) ? measured : -1;
        // A true minus sign ("−9 dBFS peak"), like every other level in the app.
        string text = peak is { } value ? MicrophonePath.Dbfs(MicrophonePath.PeakDbfs(value)) + " dBFS peak" : unknown;
        label.Text = text;
        AutomationProperties.SetItemStatus(meter, text);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (testing) { stopRequested = true; return; }
        await RunTestAsync();
    }

    /// <summary>
    /// Ten-second guided test. With audio stopped it opens the microphone alone (no playback) and measures every hardware input;
    /// with the path running it watches the live meter, since the running stream already owns the device.
    /// </summary>
    public async Task<MicrophoneTestResult?> RunTestAsync(TimeSpan? duration = null, TimeSpan? interval = null)
    {
        if (source is null || model is not { CanTest: true } start || testing) return null;
        testing = true;
        stopRequested = false;
        lastResult = null;
        FixButton.Visibility = Visibility.Collapsed;
        MicrophoneTestAssessment assessment = new();
        bool live = start.State is MicrophonePathState.Running or MicrophonePathState.Muted;
        string? probeId = live ? null : start.CaptureEndpointId;
        ShowTest("Information", "Speak normally…", live
            ? "Talk as you would on a call. The meter shows what reaches the microphone."
            : "Talk as you would on a call. Only the microphone is opened; nothing is played or sent.");
        Render(source.Latest);
        MicrophoneTestResult? result = null;
        try
        {
            if (probeId is not null) await source.SetMicrophoneTestAsync(probeId);
            DateTime end = DateTime.UtcNow + (duration ?? TestDuration);
            TimeSpan step = interval ?? TimeSpan.FromMilliseconds(200);
            while (DateTime.UtcNow < end && !stopRequested)
            {
                await Task.Delay(step);
                await source.RefreshAsync();
                HostSnapshot? latest = source.Latest;
                if (latest is null) continue;
                int remaining = Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds));
                // Beside the title, not in it: the title is a live region and must not be re-announced every second.
                TestCountdown.Text = remaining.ToString(CultureInfo.CurrentCulture);
                TestCountdown.Visibility = Visibility.Visible;
                if (probeId is not null)
                {
                    if (latest.MicrophoneTest is not { } probe || probe.EndpointId != probeId) continue;
                    if (probe.Error is { } error) throw new IOException(error);
                    assessment.Observe(probe.Peaks, probe.Frames);
                    float? shown = probe.Peaks.Count == 0 ? null : start.InputChannel is int index && index < probe.Peaks.Count ? probe.Peaks[index] : probe.Peaks.Max();
                    ShowLevel(InputMeter, InputLevel, shown, "Listening…");
                    if (!probe.Running) break;
                }
                else
                {
                    MicrophonePathModel now = MicrophonePath.Describe(latest);
                    assessment.Observe(now.InputPeak);
                    ShowLevel(InputMeter, InputLevel, now.InputPeak, "Listening…");
                }
            }
            if (stopRequested) { ShowTest("Information", "Test stopped", "Nothing was changed."); return null; }
            result = assessment.Result(probeId is null ? null : start.InputChannel);
            ShowTest(result.Verdict switch
            {
                MicrophoneTestVerdict.Good => "Success",
                MicrophoneTestVerdict.Silent or MicrophoneTestVerdict.NoSignal => "Critical",
                _ => "Caution"
            }, result.Title, result.Advice);
            lastResult = result;
            if (result is { Verdict: MicrophoneTestVerdict.WrongInput or MicrophoneTestVerdict.OneSideOnly, LoudestInput: int voice })
            {
                string input = (voice + 1).ToString(CultureInfo.InvariantCulture);
                FixButton.Content = "Use input " + input + " only";
                AutomationProperties.SetName(FixButton, "Use input " + input + " only for the microphone");
                FixButton.Visibility = Visibility.Visible;
            }
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            ShowTest("Critical", "The test could not run", error.Message);
            return null;
        }
        finally
        {
            if (probeId is not null)
            {
                try { await source.SetMicrophoneTestAsync(null); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
            }
            testing = false;
            Render(source.Latest);
        }
    }

    private void ShowTest(string severity, string title, string advice)
    {
        TestPanel.Tag = severity;
        TestPanel.Visibility = Visibility.Visible;
        TestCountdown.Visibility = Visibility.Collapsed;
        TestTitle.Text = title;
        TestAdvice.Text = advice;
    }

    private void Steps_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateArrows();

    /// <summary>
    /// Four cards in a row: each arrow points at the next card. Wrapped (two by two), the arrow at the end of a row would
    /// point at nothing, so it is hidden; in a single column the arrows point down at the next card.
    /// </summary>
    private void UpdateArrows()
    {
        int columns = Math.Max(1, Steps.Columns);
        TextBlock[] arrows = [SourceArrow, ProcessingArrow, DestinationArrow];
        for (int i = 0; i < arrows.Length; i++)
        {
            bool endOfRow = columns > 1 && i % columns == columns - 1;
            arrows[i].Visibility = endOfRow ? Visibility.Collapsed : Visibility.Visible;
            arrows[i].LayoutTransform = columns == 1 ? new System.Windows.Media.RotateTransform(90) : System.Windows.Media.Transform.Identity;
        }
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (source is null || busy) return;
        busy = true; Render(source.Latest);
        try
        {
            if (source.Latest?.StartRequested == true) await source.StopAudioAsync(); else await source.StartAudioAsync();
            await source.RefreshAsync();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException
            or UnauthorizedAccessException or NotSupportedException or AggregateException or System.Text.Json.JsonException)
        {
            ShowTest("Critical", "Audio did not change", error.Message);
        }
        finally { busy = false; Render(source.Latest); }
    }

    private void ChangeButton_Click(object sender, RoutedEventArgs e) => OpenSetup();

    private HostChannelProfile CurrentMicrophone() =>
        source?.Latest?.Profile.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone) ?? new HostChannelProfile { Bus = AudioBus.Microphone };

    /// <summary>Opens the inline setup with the saved choices, or suggestions on first setup.</summary>
    internal void OpenSetup()
    {
        if (source?.Latest is not { } snapshot) return;
        HostChannelProfile current = CurrentMicrophone();
        IReadOnlyList<SetupEndpointOption> microphones = MicrophoneSetup.Microphones(snapshot.Endpoints, current.CaptureEndpointId);
        IReadOnlyList<SetupEndpointOption> destinations = MicrophoneSetup.Destinations(snapshot.Endpoints, current.RenderEndpointId);
        MicrophoneChoice.ItemsSource = microphones;
        MicrophoneChoice.SelectedItem = microphones.FirstOrDefault(m => m.Id == MicrophoneSetup.SuggestMicrophone(microphones, current.CaptureEndpointId));
        InputChoice.ItemsSource = MicrophoneSetup.Inputs;
        InputChoice.SelectedItem = MicrophoneSetup.Inputs.FirstOrDefault(i => i.Channel == current.Dsp.InputChannel) ?? MicrophoneSetup.Inputs[0];
        DestinationChoice.ItemsSource = destinations;
        DestinationChoice.SelectedItem = destinations.FirstOrDefault(d => d.Id == MicrophoneSetup.SuggestDestination(destinations, current.RenderEndpointId));
        bool firstSetup = current.CaptureEndpointId is null || current.RenderEndpointId is null || !current.Enabled;
        MicrophoneSound sound = firstSetup ? MicrophoneSound.Natural : MicrophoneSetup.Identify(current.Dsp);
        List<MicrophoneSoundChoice> sounds = [.. MicrophoneSetup.Sounds];
        if (sound == MicrophoneSound.Custom) sounds.Insert(0, MicrophoneSetup.Custom);
        SoundChoice.ItemsSource = sounds;
        SoundChoice.SelectedItem = sounds.First(c => c.Sound == sound);
        UpdateDestinationHint();
        SetupError.Visibility = Visibility.Collapsed;
        SetupPanel.Visibility = Visibility.Visible;
        Render(source.Latest);
        MicrophoneChoice.Focus();
        SetupPanel.BringIntoView();
    }

    private async void SaveSetup_Click(object sender, RoutedEventArgs e) => await SaveSetupAsync();

    /// <summary>Validates and saves the setup. Returns true when the helper confirmed it.</summary>
    internal async Task<bool> SaveSetupAsync()
    {
        if (source?.Latest is not { } snapshot || busy) return false;
        (HostChannelProfile? profile, string? problem) = MicrophoneSetup.Build(CurrentMicrophone(), snapshot.Endpoints,
            (MicrophoneChoice.SelectedItem as SetupEndpointOption)?.Id, (InputChoice.SelectedItem as MicrophoneInputOption)?.Channel,
            (DestinationChoice.SelectedItem as SetupEndpointOption)?.Id, (SoundChoice.SelectedItem as MicrophoneSoundChoice)?.Sound ?? MicrophoneSound.Custom);
        if (profile is null) { ShowSetupError(problem!); return false; }
        busy = true; SaveSetup.IsEnabled = false; Render(source.Latest);
        try
        {
            await source.ApplyMicrophoneAsync(profile);
            SetupPanel.Visibility = Visibility.Collapsed;
            string receiving = (DestinationChoice.SelectedItem as SetupEndpointOption)?.Receiving ?? "";
            ShowTest("Success", "Microphone saved", (snapshot.StartRequested ? "The new setup is in use now." : "Choose Test my microphone to check your level, then Start audio.") +
                (receiving.Length > 0 ? " In Discord, OBS or a game, choose “" + receiving + "” as the microphone." : ""));
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            ShowSetupError("Not saved: " + error.Message);
            return false;
        }
        finally { busy = false; SaveSetup.IsEnabled = true; Render(source.Latest); }
    }

    private void CancelSetup_Click(object sender, RoutedEventArgs e)
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        Render(source?.Latest);
        ChangeButton.Focus();
    }

    private void ShowSetupError(string message)
    {
        SetupErrorText.Text = message;
        SetupError.Visibility = Visibility.Visible;
    }

    private void DestinationChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDestinationHint();

    private void UpdateDestinationHint()
    {
        if (DestinationChoice.ItemsSource is IReadOnlyList<SetupEndpointOption> { Count: 0 })
            DestinationHint.Text = "No virtual cable is installed. Other apps need one to receive your processed voice; install a virtual cable such as VB-CABLE, then come back.";
        else
            DestinationHint.Text = DestinationChoice.SelectedItem is SetupEndpointOption { Receiving: { } receiving }
                ? "Other apps then choose “" + receiving + "” as their microphone."
                : "Choose a virtual cable so other apps can pick your processed voice.";
    }

    private void SoundChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SoundHint.Text = (SoundChoice.SelectedItem as MicrophoneSoundChoice)?.Description ?? "";

    private async void FixButton_Click(object sender, RoutedEventArgs e) => await ApplyTestFixAsync();

    /// <summary>Applies the input the test heard the voice on (one click from the verdict).</summary>
    internal async Task<bool> ApplyTestFixAsync()
    {
        if (source is null || busy || lastResult is not { LoudestInput: int voice }) return false;
        HostChannelProfile current = CurrentMicrophone();
        busy = true; FixButton.IsEnabled = false; Render(source.Latest);
        try
        {
            await source.ApplyMicrophoneAsync(current with { Dsp = current.Dsp with { InputChannel = voice } });
            FixButton.Visibility = Visibility.Collapsed;
            ShowTest("Success", "Now using input " + (voice + 1).ToString(CultureInfo.InvariantCulture) + " only",
                "Your voice is sent to both sides. Run the test again to confirm.");
            lastResult = null;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            ShowTest("Critical", "The input was not changed", error.Message);
            return false;
        }
        finally { busy = false; FixButton.IsEnabled = true; Render(source.Latest); }
    }

    private async void SoundButton_Click(object sender, RoutedEventArgs e) => await AdjustSoundAsync();

    /// <summary>Opens the processing editor for the microphone and saves the result.</summary>
    internal async Task<bool> AdjustSoundAsync()
    {
        if (source is null || busy) return false;
        HostChannelProfile current = CurrentMicrophone();
        if (EditSound(current.Dsp) is not { } edited) return false;
        busy = true; Render(source.Latest);
        try
        {
            HostChannelProfile saved = current with { Dsp = edited with { InputChannel = current.Dsp.InputChannel } };
            await source.ApplyMicrophoneAsync(saved);
            ShowTest("Success", "Sound saved", "Your microphone now uses: " + string.Join(", ", MicrophonePath.DescribeProcessing(saved)) + ".");
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            ShowTest("Critical", "The sound was not changed", error.Message);
            return false;
        }
        finally { busy = false; Render(source.Latest); }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (model?.ReceivingInputName is not { } name) return;
        copiedName = name;
        try { Clipboard.SetText(name); CopyName.Content = "Copied"; }
        catch (System.Runtime.InteropServices.COMException) { CopyName.Content = "Copy failed — try again"; }
        copyReset.Stop();
        copyReset.Start();
    }

    private void ResetCopyButton()
    {
        copyReset.Stop();
        copiedName = null;
        CopyName.Content = "Copy name";
    }
}
