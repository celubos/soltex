using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

internal sealed class IndependentMixerStreamWindow : Window
{
    private readonly ComboBox personalOutput = new(), streamOutput = new();
    private readonly Dictionary<AudioBus, (Slider Personal, CheckBox PersonalMute, Slider Stream, CheckBox StreamMute)> sends = [];
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock meters = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel body = new();
    private readonly Button apply = new() { Content = "Apply sends and outputs", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button start = new() { Content = "Start stream mix", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button stop = new() { Content = "Stop audio", Margin = new Thickness(0, 0, 8, 0) };
    private readonly Func<HostStreamMixProfile, Task<HostSnapshot>> applyProfile;
    private readonly Func<bool, Task<HostSnapshot>> control;
    private bool dirty, busy;
    private HostSnapshot latest;

    internal IndependentMixerStreamWindow(HostSnapshot snapshot, Func<HostStreamMixProfile, Task<HostSnapshot>> applyProfile, Func<bool, Task<HostSnapshot>> control)
    {
        latest = snapshot; this.applyProfile = applyProfile; this.control = control;
        Title = "Personal and stream mixes"; Width = 840; Height = 690; MinWidth = 660; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AudioTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        DockPanel root = new() { Margin = new Thickness(22) }; Content = root;
        StackPanel bottom = new(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(meters); bottom.Children.Add(feedback);
        WrapPanel buttons = new() { Margin = new Thickness(0, 12, 0, 0) }; bottom.Children.Add(buttons);
        buttons.Children.Add(apply); buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(new Button { Content = "Close", IsCancel = true });
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Text("Personal and stream mixes", 23);
        Text("Each channel that is on is processed once and sent to two mixes: what you hear and what your stream gets. By default you don't hear your own microphone.");
        Text("Outputs", 17);
        var outputs = snapshot.Endpoints.Where(e => !e.IsCapture && e.IsActive && !e.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var outputChoices = outputs.Select(e => new IndependentEndpointChoice(e.Id, e.Name + IndependentMixerWorkspace.DuplicateSuffix(outputs, e))).ToArray();
        LabelChoice("What you hear · headphones or speakers", personalOutput, outputChoices, snapshot.Profile.StreamMix.PersonalRenderEndpointId);
        LabelChoice("What your stream gets · a virtual cable or a separate output", streamOutput, outputChoices, snapshot.Profile.StreamMix.StreamRenderEndpointId);
        Text("Choose two different outputs. In your streaming app, capture the stream output. Stop audio before you change devices or turn channels on or off.");
        Text("Channel levels", 17);
        foreach (var channel in snapshot.Profile.Channels)
        {
            var send = snapshot.Profile.StreamMix.Sends.Single(s => s.Bus == channel.Bus);
            string source = snapshot.Endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId)?.Name ?? "No source";
            Text(channel.Bus + (channel.Enabled ? " · " + source : " · off"), 14);
            WrapPanel row = new() { IsEnabled = channel.Enabled, Margin = new Thickness(0, 0, 0, 8) }; body.Children.Add(row);
            var personal = SendControls(row, "You hear", send.PersonalGainDb, send.PersonalMute);
            var stream = SendControls(row, "Stream", send.StreamGainDb, send.StreamMute);
            sends.Add(channel.Bus, (personal.Gain, personal.Mute, stream.Gain, stream.Mute));
        }
        Text("Channel level, mute and sound settings change both mixes. The overall level and the Game and Chat balance change only what you hear. Closing this window keeps audio running.");
        personalOutput.SelectionChanged += Changed; streamOutput.SelectionChanged += Changed;
        apply.Click += async (_, _) => await Run(async () => { var result = await applyProfile(Draft()); dirty = false; return result; });
        start.Click += async (_, _) => await Run(() => control(true));
        stop.Click += async (_, _) => await Run(() => control(false));
        Observe(snapshot);
    }
    private void LabelChoice(string label, ComboBox combo, IReadOnlyList<IndependentEndpointChoice> options, string? selected)
    {
        Text(label); combo.ItemsSource = options; combo.DisplayMemberPath = "Label"; combo.SelectedValuePath = "Id"; combo.SelectedValue = selected;
        combo.Margin = new Thickness(0, 3, 0, 6); combo.MinHeight = 29; body.Children.Add(combo);
    }
    private void Text(string text, double size = 13)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, size > 13 ? 12 : 4, 0, 5) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); body.Children.Add(block);
    }
    private (Slider Gain, CheckBox Mute) SendControls(Panel parent, string name, double gain, bool muted)
    {
        StackPanel group = new() { Width = 350, Margin = new Thickness(0, 0, 14, 0) }; parent.Children.Add(group);
        TextBlock label = new() { Text = name + " · " + gain.ToString("0.0", CultureInfo.CurrentCulture) + " dB" }; label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); group.Children.Add(label);
        DockPanel line = new(); group.Children.Add(line);
        CheckBox mute = new() { Content = "Mute", IsChecked = muted, Margin = new Thickness(10, 0, 0, 0) }; DockPanel.SetDock(mute, Dock.Right); line.Children.Add(mute);
        Slider slider = new() { Minimum = -80, Maximum = 12, Value = gain, SmallChange = 1, LargeChange = 5 };
        System.Windows.Automation.AutomationProperties.SetName(slider, name + " send gain"); line.Children.Add(slider);
        slider.ValueChanged += (_, _) => { label.Text = name + " · " + slider.Value.ToString("0.0", CultureInfo.CurrentCulture) + " dB"; Changed(slider, new RoutedEventArgs()); };
        mute.Checked += Changed; mute.Unchecked += Changed;
        return (slider, mute);
    }
    private void Changed(object sender, RoutedEventArgs args) { dirty = true; feedback.Text = "Choose Apply sends and outputs to use your changes."; Observe(latest); }
    internal HostStreamMixProfile Draft() => new()
    {
        PersonalRenderEndpointId = personalOutput.SelectedValue as string, StreamRenderEndpointId = streamOutput.SelectedValue as string,
        Sends = sends.Select(pair => new HostStreamSendProfile { Bus = pair.Key, PersonalGainDb = pair.Value.Personal.Value, PersonalMute = pair.Value.PersonalMute.IsChecked == true,
            StreamGainDb = pair.Value.Stream.Value, StreamMute = pair.Value.StreamMute.IsChecked == true }).ToArray()
    };
    private async Task Run(Func<Task<HostSnapshot>> operation)
    {
        if (busy) return; busy = true; body.IsEnabled = false; Observe(latest);
        try { var result = await operation(); feedback.Text = result.LastError ?? "Audio state confirmed."; Observe(result); }
        catch (Exception error) { feedback.Text = "Change was not confirmed: " + error.Message; }
        finally { busy = false; body.IsEnabled = true; Observe(latest); }
    }
    internal void Observe(HostSnapshot snapshot)
    {
        latest = snapshot; apply.IsEnabled = !busy;
        start.IsEnabled = !busy && !dirty && !snapshot.StartRequested && !snapshot.Running && snapshot.StreamMix is null;
        stop.IsEnabled = !busy && (snapshot.StartRequested || snapshot.Running || snapshot.StreamMix is not null);
        var status = snapshot.StreamMix;
        string Db(float value) => value > 0 ? (20 * Math.Log10(value)).ToString("0.0", CultureInfo.CurrentCulture) + " dBFS" : "silence";
        meters.Text = status is null ? "Stream mix is off. Apply your choices, then choose Start stream mix."
            : status.Failure is not null ? "Stream mix failed: " + status.Failure + ". Stop audio before retrying."
            : (status.Running ? "On" : "Off") + " · You hear " + Db(status.PersonalPeak) + " · Stream " + Db(status.StreamPeak);
    }
}
