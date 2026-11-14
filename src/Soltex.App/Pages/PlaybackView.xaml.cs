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
        InitializeComponent();
        ChannelList.ItemsSource = workspace.Channels.Where(c => c.Bus != AudioBus.Microphone).ToArray();
        IndependentMixerChannel mic = workspace.Channels.Single(c => c.Bus == AudioBus.Microphone);
        MicStrip.DataContext = mic;
        MicFader.SetBinding(RangeBase.ValueProperty, new Binding(nameof(IndependentMixerChannel.GainDb)) { Mode = BindingMode.TwoWay });
        MicMute.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(IndependentMixerChannel.Mute)) { Mode = BindingMode.TwoWay });
        MicMeter.SetBinding(LevelMeter.LevelProperty, new Binding(nameof(IndependentMixerChannel.Peak)) { Converter = new PeakLevelConverter() });
        MicValue.SetBinding(TextBlock.TextProperty, new Binding(nameof(IndependentMixerChannel.GainDb)) { Converter = new DecibelTextConverter() });
        MicCurve.SetBinding(EqCurve.SettingsProperty, new Binding(nameof(IndependentMixerChannel.Dsp)));
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

    /// <summary>Opens a channel's sound editor and returns the result, or null when cancelled. Replaceable for tests.</summary>
    public Func<string, DspSettings, DspSettings?> EditSound { get; set; } = (title, settings) =>
    {
        ProcessingWindow window = new(title + " sound", settings, microphone: false) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    };

    internal void Sync()
    {
        syncing = true;
        try
        {
            MasterSlider.Value = workspace.MasterGainDb;
            MasterValue.Text = MicrophonePath.Decibels(workspace.MasterGainDb);
            MasterMuteBox.IsChecked = workspace.MasterMuted;
            BalanceSlider.Value = workspace.GameChatBalance;
            BalanceSlider.IsEnabled = workspace.CanBalance;
            BalanceHint.Text = workspace.CanBalance ? "Center leaves both unchanged; only the other side is turned down." : workspace.BalanceHint;
            bool running = workspace.CanStop;
            StartStopButton.Content = running ? "Stop audio" : "Start audio";
            StartStopButton.IsEnabled = workspace.IsHostInitialized && (running || workspace.CanStart);
            if (localStatus is not null && DateTime.UtcNow >= localUntil) localStatus = null;
            // While edits are staged the staged bar already says what to do, so routine status is hidden.
            // A failed or refused change is still shown, or the click would seem to do nothing.
            StatusLine.Text = localStatus ?? (workspace.LastOperationFailed || !workspace.RoutingStaged ? workspace.StatusText : "");
            if (workspace.LatestSnapshot is { } snapshot)
            {
                MicrophonePathModel model = MicrophonePath.Describe(snapshot);
                MicState.Text = model.StateText;
                // The strip is narrow: short words when nothing usable is chosen; the tooltip has the full text.
                bool chosen = snapshot.Profile.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone)?.CaptureEndpointId is not null;
                MicSource.Text = model.SourceAvailable ? model.SourceName : chosen ? "Not connected" : "Not chosen";
                MicReceiving.Text = model.ReceivingInputName ?? "Not set up";
                MicSource.ToolTip = model.SourceName + " · " + model.SourceDetail;
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

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MasterValue is not null) MasterValue.Text = MicrophonePath.Decibels(e.NewValue);
        if (!syncing) workspace.MasterGainDb = e.NewValue;
    }

    private void MasterMute_Changed(object sender, RoutedEventArgs e) { if (!syncing) workspace.MasterMuted = MasterMuteBox.IsChecked == true; }

    private void BalanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!syncing) workspace.GameChatBalance = e.NewValue; }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        localStatus = null;
        StartStopButton.IsEnabled = false;
        if (workspace.CanStop) await workspace.StopAudioAsync();
        else if (workspace.RoutingStaged) ShowStatus("Apply your changes first so audio starts with the devices you chose.");
        else await workspace.StartAudioAsync();
        Sync();
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

    private void StripScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The strips only scroll sideways. Shift + wheel moves them; a plain wheel scrolls the page, as it does elsewhere.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            StripScroll.ScrollToHorizontalOffset(StripScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        if (VisualTreeHelper.GetParent(StripScroll) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = StripScroll });
    }

    private async void Sound_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IndependentMixerChannel row) await AdjustSoundAsync(row.Bus);
    }

    /// <summary>Edits and saves one channel's processing. Returns true when the helper confirmed it.</summary>
    internal async Task<bool> AdjustSoundAsync(AudioBus bus)
    {
        IndependentMixerChannel row = workspace.Channels.Single(c => c.Bus == bus);
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
