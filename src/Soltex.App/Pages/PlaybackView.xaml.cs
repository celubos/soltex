using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Soltex.App.Controls;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>Linear peak (−1 when unknown) → 0–1 meter position over −60…0 dBFS.</summary>
public sealed class PeakToMeterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double peak && peak >= 0 ? MicrophonePath.MeterPosition((float)peak) : 0d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Game, Chat, Media and Aux in the suite's own style. The independent mixer workspace owns the helper connection and
/// its staging rules; this view only presents it, so there is one engine and one set of rules.
/// </summary>
public partial class PlaybackView : UserControl
{
    /// <summary>How long a message from this view (a refusal, a save result) stays before workspace status returns.</summary>
    private static readonly TimeSpan LocalStatusDuration = TimeSpan.FromSeconds(6);
    private readonly IndependentMixerWorkspace workspace;
    private bool syncing;
    private string? localStatus;
    private DateTime localUntil;
    /// <summary>The output the person picked in "Play every channel on"; kept across device list changes.</summary>
    private string? pickedOutputId;

    public PlaybackView(IndependentMixerWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        this.workspace = workspace;
        EditSound = OpenProcessingWindow;
        InitializeComponent();
        PlaybackChannels = workspace.Channels.Where(c => c.Bus != AudioBus.Microphone).OrderBy(c => c.Bus).ToArray();
        // One set of strips: the microphone first, then Game, Chat, Media and Aux. They wrap together into even rows.
        ChannelList.ItemsSource = workspace.Channels.Where(c => c.Bus == AudioBus.Microphone).Concat(PlaybackChannels).ToArray();
        OutputChoice.ItemsSource = workspace.OutputChoices;
        workspace.StatusApplied += (_, _) => Sync();
        workspace.StatusTextChanged += (_, _) =>
        {
            // A failed change outranks this view's own last message.
            if (workspace.LastOperationFailed) localStatus = null;
            Sync();
        };
        // The device list is rebuilt when devices change, which clears the selection; put the pick back.
        workspace.OutputsChanged += (_, _) => Sync();
        foreach (IndependentMixerChannel row in workspace.Channels) row.PropertyChanged += (_, _) => UpdateStaged();
        Sync();
    }

    /// <summary>Raised when the microphone strip's Setup is chosen; the Audio page opens the microphone card.</summary>
    public event EventHandler? MicrophoneSetupRequested;

    /// <summary>Game, Chat, Media and Aux, in that order (the microphone has its own strip and tab).</summary>
    public IReadOnlyList<IndependentMixerChannel> PlaybackChannels { get; }

    /// <summary>What the microphone strip shows, from the helper's confirmed state.</summary>
    public MicStripInfo Mic { get; } = new();

    /// <summary>Opens a channel's sound editor and returns the result, or null when cancelled. Replaceable for tests.</summary>
    public Func<string, DspSettings, DspSettings?> EditSound { get; set; }

    /// <summary>The channel the sound editor is open for; live changes go to it.</summary>
    private AudioBus? editingBus;

    private DspSettings? OpenProcessingWindow(string title, DspSettings settings)
    {
        ProcessingWindow window = new(title + " sound", settings, microphone: false, PreviewSoundAsync) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }

    private Task PreviewSoundAsync(DspSettings settings) =>
        editingBus is { } bus ? workspace.ApplyChannelDspAsync(bus, settings) : Task.CompletedTask;

    internal void Sync()
    {
        syncing = true;
        try
        {
            MasterSlider.Value = workspace.MasterGainDb;
            MasterValue.Text = MicrophonePath.Decibels(workspace.MasterGainDb);
            MasterMuteBox.IsChecked = workspace.MasterMuted;
            BalanceSlider.Value = workspace.GameChatBalance;
            BalanceValue.Text = BalanceText(workspace.GameChatBalance);
            BalanceSlider.IsEnabled = workspace.CanBalance;
            BalanceHint.Text = workspace.CanBalance ? "Center leaves both unchanged; only the other side is turned down." : workspace.BalanceHint;
            bool running = workspace.CanStop;
            StartStopButton.Content = running ? "Stop audio" : "Start audio";
            StartStopButton.IsEnabled = workspace.IsHostInitialized && (running || workspace.CanStart);
            ShowState();
            // Not connected (the engine is missing, did not answer, or stopped answering): the reason and a retry, instead
            // of dead controls or the last state shown as if it were still true.
            bool offline = workspace.ConnectionLost || (!workspace.IsHostInitialized && workspace.StatusText.StartsWith("Audio ", StringComparison.Ordinal) &&
                workspace.StatusText != "Connecting to audio…");
            OfflineBar.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
            OfflineReason.Text = offline ? workspace.StatusText : "";
            if (localStatus is not null && DateTime.UtcNow >= localUntil) localStatus = null;
            // While edits are staged the staged bar already says what to do, so routine status is hidden, and so is
            // "Audio is running.", which only repeats the Running badge. A failed or refused change is still shown, or the
            // click would seem to do nothing.
            string status = workspace.LastOperationFailed || !workspace.RoutingStaged ? workspace.StatusText : "";
            if (status == IndependentMixerWorkspace.RunningStatus) status = "";
            StatusLine.Text = offline ? "" : localStatus ?? status;
            if (workspace.LatestSnapshot is { } snapshot)
            {
                MicrophonePathModel model = MicrophonePath.Describe(snapshot);
                // The strip is narrow: short words when nothing usable is chosen; the tooltips have the full text.
                bool chosen = snapshot.Profile.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone)?.CaptureEndpointId is not null;
                bool lost = workspace.ConnectionLost;
                // The chips show the device's own name ("CABLE Output"); their tooltips keep the full name.
                Mic.Update(lost ? "Not connected" : model.StateText, lost ? workspace.StatusText : model.Explanation,
                    model.SourceAvailable ? DeviceNames.WithoutHardware(model.SourceName) : chosen ? "Not connected" : "Not chosen", model.SourceName + " · " + model.SourceDetail,
                    model.ReceivingInputName is { } receiving ? DeviceNames.WithoutHardware(receiving) : "Not set up", model.ReceivingInputName ?? "Not set up",
                    (Brush)FindResource(lost ? "Studio.TextTertiaryBrush" : model.State switch
                    {
                        MicrophonePathState.Running => "Studio.SuccessBrush",
                        MicrophonePathState.Problem => "Studio.CriticalBrush",
                        MicrophonePathState.Muted => "Studio.CautionBrush",
                        _ => "Studio.TextTertiaryBrush"
                    }));
            }
            SyncOutputChoice();
            UpdateStaged();
        }
        finally { syncing = false; }
    }

    /// <summary>
    /// Shows the person's pick, or else the output every playing channel already shares, so the box is not
    /// blank when there is an obvious answer. Nothing is selected when the channels use different outputs.
    /// </summary>
    private void SyncOutputChoice()
    {
        string? wanted = pickedOutputId ?? SharedOutput();
        IndependentEndpointChoice? item = wanted is null ? null : workspace.OutputChoices.FirstOrDefault(c => c.Id == wanted);
        if (!ReferenceEquals(OutputChoice.SelectedItem, item)) OutputChoice.SelectedItem = item;
        SwitchOutput.IsEnabled = workspace.IsHostInitialized && OutputChoice.SelectedItem is not null;
    }

    private string? SharedOutput()
    {
        if (workspace.LatestSnapshot is not { } snapshot) return null;
        string[] outputs = snapshot.Profile.Channels
            .Where(c => c.Bus != AudioBus.Microphone && c.Enabled && !string.IsNullOrEmpty(c.RenderEndpointId))
            .Select(c => c.RenderEndpointId!).Distinct(StringComparer.Ordinal).ToArray();
        return outputs.Length == 1 ? outputs[0] : null;
    }

    private void OutputChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        // Ignore the empty selection while the device list is rebuilt; only a real choice replaces the pick.
        if (OutputChoice.SelectedValue is string id) pickedOutputId = id;
        SwitchOutput.IsEnabled = workspace.IsHostInitialized && OutputChoice.SelectedItem is not null;
    }

    private void ShowStatus(string text)
    {
        localStatus = text;
        localUntil = DateTime.UtcNow + LocalStatusDuration;
        StatusLine.Text = text;
    }

    private void UpdateStaged() => StagedBar.Visibility = workspace.RoutingStaged ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The badge next to Start/Stop: what audio is doing right now, in one word or two.</summary>
    private void ShowState()
    {
        (string text, string background) = workspace.LatestSnapshot switch
        {
            null => (workspace.IsHostInitialized ? "Connecting…" : "Not connected", "SubtleFillColorSecondaryBrush"),
            { StreamMix.Running: true } => ("Stream mix on", "SystemFillColorAttentionBackgroundBrush"),
            { Running: true } => ("Running", "SystemFillColorSuccessBackgroundBrush"),
            { StartRequested: true } => ("Starting", "SystemFillColorCautionBackgroundBrush"),
            _ => ("Stopped", "SubtleFillColorSecondaryBrush")
        };
        if (workspace.LatestSnapshot is null && workspace.LastOperationFailed) (text, background) = ("Not connected", "SystemFillColorCriticalBackgroundBrush");
        // The engine stopped answering: the last state may no longer be true, so it isn't shown as "Running".
        if (workspace.ConnectionLost) (text, background) = ("Not connected", "SystemFillColorCriticalBackgroundBrush");
        StateText.Text = text;
        StateBadge.SetResourceReference(Border.BackgroundProperty, background);
    }

    /// <summary>"Centered", or how far the balance leans and which side stays at full level.</summary>
    internal static string BalanceText(double balance) => Math.Abs(balance) < 0.005
        ? "Centered"
        : (Math.Abs(balance) * 100).ToString("0", CultureInfo.CurrentCulture) + "% toward " + (balance < 0 ? "Game" : "Chat");

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MasterValue is not null) MasterValue.Text = MicrophonePath.Decibels(e.NewValue);
        if (!syncing) workspace.MasterGainDb = e.NewValue;
    }

    private void MasterMute_Changed(object sender, RoutedEventArgs e) { if (!syncing) workspace.MasterMuted = MasterMuteBox.IsChecked == true; }

    private void BalanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BalanceValue is not null) BalanceValue.Text = BalanceText(e.NewValue);
        if (!syncing) workspace.GameChatBalance = e.NewValue;
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        localStatus = null;
        StartStopButton.IsEnabled = false;
        if (workspace.CanStop) await workspace.StopAudioAsync();
        else if (workspace.RoutingStaged) ShowStatus("Apply your changes first so audio starts with the devices you chose.");
        else await workspace.StartAudioAsync();
        Sync();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try { await workspace.RefreshStateAsync(); }
        finally { RetryButton.IsEnabled = true; Sync(); }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        localStatus = null;
        ApplyButton.IsEnabled = false;
        try { await workspace.ApplyRoutingAsync(); }
        finally { ApplyButton.IsEnabled = true; Sync(); }
    }

    private async void SwitchOutput_Click(object sender, RoutedEventArgs e)
    {
        localStatus = null;
        if (OutputChoice.SelectedValue is not string id) { ShowStatus("Choose an output first."); return; }
        await workspace.SelectPlaybackOutputAsync(id);
        // Once switched, the box follows the output the channels share again.
        if (!workspace.LastOperationFailed) pickedOutputId = null;
        Sync();
    }

    private async void Sound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IndependentMixerChannel row) await AdjustSoundAsync(row.Bus);
    }

    /// <summary>Edits and saves one channel's processing. Returns true when the helper confirmed it.</summary>
    internal async Task<bool> AdjustSoundAsync(AudioBus bus)
    {
        IndependentMixerChannel row = workspace.Channels.Single(c => c.Bus == bus);
        editingBus = bus;
        if (EditSound(row.Title, workspace.ConfirmedChannel(bus).Dsp) is not { } edited) return false;
        try
        {
            await workspace.ApplyChannelDspAsync(bus, edited);
            ShowStatus(row.Title + " sound saved.");
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
        {
            ShowStatus(row.Title + " sound was not changed: " + error.Message);
            return false;
        }
    }

    private void DeviceOrderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ContextMenu menu = new() { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (IndependentMixerChannel row in workspace.Channels.Where(c => c.Bus != AudioBus.Microphone))
        {
            MenuItem item = new() { Header = row.Title };
            AudioBus bus = row.Bus;
            item.Click += (_, _) => { localStatus = null; if (workspace.EditDevicePriorities(bus)) Sync(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void MicSetup_Click(object sender, RoutedEventArgs e) => MicrophoneSetupRequested?.Invoke(this, EventArgs.Empty);

    private void StreamMix_Click(object sender, RoutedEventArgs e) { localStatus = null; workspace.ShowStreamMix(); Sync(); }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Title = "Import audio profile", Filter = "Audio profile (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        localStatus = null;
        await workspace.ImportProfileAsync(dialog.FileName);
        // An imported profile is staged, which hides routine status; say what happened.
        if (!workspace.LastOperationFailed) ShowStatus(workspace.StatusText);
        Sync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Title = "Export audio profile", Filter = "Audio profile (*.json)|*.json", DefaultExt = ".json", FileName = "audio-profile.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await workspace.ExportProfileAsync(dialog.FileName);
            ShowStatus("Profile exported. Its device choices only work on this PC.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { ShowStatus("Profile was not exported: " + error.Message); }
        Sync();
    }
}

/// <summary>The microphone strip's text, from the helper's confirmed state (the full names go in tooltips).</summary>
public sealed class MicStripInfo : INotifyPropertyChanged
{
    public string State { get; private set; } = "Connecting…";
    public string Explanation { get; private set; } = "Connecting to audio…";
    public string Source { get; private set; } = "—";
    public string SourceDetail { get; private set; } = "";
    public string Receiving { get; private set; } = "—";
    /// <summary>The full name of the input other apps choose (the chip may show a shorter one).</summary>
    public string ReceivingDetail { get; private set; } = "—";
    public Brush? StateBrush { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(string state, string explanation, string source, string sourceDetail, string receiving, string receivingDetail, Brush stateBrush)
    {
        if (State == state && Explanation == explanation && Source == source && SourceDetail == sourceDetail && Receiving == receiving &&
            ReceivingDetail == receivingDetail && ReferenceEquals(StateBrush, stateBrush)) return;
        (State, Explanation, Source, SourceDetail, Receiving, ReceivingDetail, StateBrush) = (state, explanation, source, sourceDetail, receiving, receivingDetail, stateBrush);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>The microphone gets its own strip; Game, Chat, Media and Aux share the channel strip.</summary>
public sealed class ChannelStripSelector : DataTemplateSelector
{
    public DataTemplate? Microphone { get; set; }
    public DataTemplate? Playback { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is IndependentMixerChannel { Bus: AudioBus.Microphone } ? Microphone : Playback;
}
