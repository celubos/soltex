using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

internal sealed class IndependentMixerMicrophoneWindow : Window
{
    private readonly ComboBox input = new(), destination = new(), inputChannel = new();
    private readonly TextBlock measurement = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock outputStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock returnHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button test = new() { Content = "Test input for " + MicrophonePath.TestDurationText, Margin = new Thickness(0, 10, 8, 10) };
    private readonly Func<string?, Task> testInput;
    private readonly IReadOnlyList<HostEndpoint> endpoints;
    private readonly TimeSpan testDuration;
    private CancellationTokenSource? autoStop;
    private bool closing;
    public HostChannelProfile Profile { get; private set; }
    /// <param name="testDuration">How long a test listens before this window stops it; <see cref="MicrophonePath.TestDuration"/> unless a test needs it shorter.</param>
    internal IndependentMixerMicrophoneWindow(HostChannelProfile profile, IReadOnlyList<HostEndpoint> devices, Func<string?, Task> testInput, TimeSpan? testDuration = null)
    {
        Profile = profile; endpoints = devices; this.testInput = testInput; this.testDuration = testDuration ?? MicrophonePath.TestDuration;
        Title = "Set up your microphone"; Width = 680; Height = 660; MinWidth = 540; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AudioTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        DockPanel root = new() { Margin = new Thickness(24) }; Content = root;
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Button save = new() { Content = "Use microphone route", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        footer.Children.Add(save); footer.Children.Add(new Button { Content = "Close", IsCancel = true });
        StackPanel body = new(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        AddText(body, "Set up your microphone", 24);
        AddText(body, "1 · Choose the capture input", 17);
        AddText(body, "For an audio interface, choose its input (for example Analog 1 + 2), not its speakers or headphones.");
        var inputs = devices.Where(e => e.IsCapture && e.IsActive && !IsSonar(e)).OrderBy(e => e.TransportId is not null)
            .Select(e => Choice(e, devices)).ToArray();
        SetupChoice(input, inputs, profile.CaptureEndpointId); body.Children.Add(input);
        StackPanel actions = new() { Orientation = Orientation.Horizontal }; body.Children.Add(actions); actions.Children.Add(test);
        Button stop = new() { Content = "Stop input test", Margin = new Thickness(0, 10, 0, 10) }; actions.Children.Add(stop);
        body.Children.Add(measurement);
        measurement.Text = "Test opens only this capture input. Nothing plays, no audio file is saved, and the test stops after " + MicrophonePath.TestDurationText + ".";
        AddText(body, "2 · Choose the hardware input", 17);
        SetupChoice(inputChannel, [new("all", "All inputs (keep the source channels)"), new("0", "Input 1 → centered voice"), new("1", "Input 2 → centered voice")], profile.Dsp.InputChannel?.ToString(CultureInfo.InvariantCulture) ?? "all");
        body.Children.Add(inputChannel);
        AddText(body, "Select the socket your microphone uses. A single input is copied to both output channels so your voice is centered.");
        AddText(body, "3 · Choose the virtual microphone return", 17);
        var returns = devices.Where(e => !e.IsCapture && e.IsActive && !IsSonar(e) && e.TransportId is not null).Select(e => Choice(e, devices)).ToArray();
        SetupChoice(destination, returns, profile.RenderEndpointId); body.Children.Add(destination); body.Children.Add(returnHint); body.Children.Add(outputStatus);
        destination.SelectionChanged += (_, _) => UpdateReturnHint(); UpdateReturnHint();
        AddText(body, "Use microphone route saves only this microphone channel. Start audio in Soltex when ready. If audio is already started, this applies the microphone route immediately.");
        test.Click += async (_, _) =>
        {
            if (input.SelectedValue is not string id) { measurement.Text = "Choose an available capture input first."; return; }
            test.IsEnabled = false;
            try { await testInput(id); StopAfterTestDuration(); }
            catch (Exception error) { measurement.Text = "Input test unavailable: " + error.Message; }
            finally { if (!closing) test.IsEnabled = true; }
        };
        stop.Click += async (_, _) => { CancelAutoStop(); try { await testInput(null); } catch (Exception error) { measurement.Text = error.Message; } };
        input.SelectionChanged += async (_, _) => { CancelAutoStop(); try { await testInput(null); } catch (Exception error) { measurement.Text = error.Message; } };
        save.Click += (_, _) =>
        {
            if (input.SelectedValue is not string source || destination.SelectedValue is not string target)
            { measurement.Text = "Choose a capture input and an installed virtual microphone return. Input testing works even without a return."; return; }
            var capture = devices.Single(e => e.Id == source); var render = devices.Single(e => e.Id == target);
            if (capture.TransportId is not null && capture.TransportId == render.TransportId)
            { measurement.Text = "That input and return share one cable. Choose the physical microphone to prevent feedback."; return; }
            Profile = profile with { Enabled = true, CaptureEndpointId = source, RenderEndpointId = target, ManualPin = true,
                Dsp = profile.Dsp with { InputChannel = inputChannel.SelectedValue is string selected && selected != "all" ? int.Parse(selected, CultureInfo.InvariantCulture) : null } };
            DialogResult = true;
        };
        Closed += async (_, _) => { closing = true; CancelAutoStop(); try { await testInput(null); } catch (Exception) { /* The host also bounds every probe as a backstop. */ } };
    }

    /// <summary>
    /// The window ends the test after <see cref="testDuration"/>, so the length the button and note name is the length
    /// it runs (the host's own limit on a probe is only a longer backstop).
    /// </summary>
    private async void StopAfterTestDuration()
    {
        CancelAutoStop();
        using var cancel = new CancellationTokenSource();
        autoStop = cancel;
        try { await Task.Delay(testDuration, cancel.Token); }
        catch (OperationCanceledException) { return; }
        finally { if (ReferenceEquals(autoStop, cancel)) autoStop = null; }
        if (closing) return;
        try { await testInput(null); }
        catch (Exception error) { measurement.Text = "The input test could not stop: " + error.Message; }
    }

    private void CancelAutoStop()
    {
        autoStop?.Cancel();
        autoStop = null;
    }
    private static bool IsSonar(HostEndpoint e) => e.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase);
    private static IndependentEndpointChoice Choice(HostEndpoint endpoint, IReadOnlyList<HostEndpoint> devices) => new(endpoint.Id,
        IndependentEndpointChoice.Describe(endpoint, IndependentMixerWorkspace.DuplicateSuffix(devices.Where(e => e.IsCapture == endpoint.IsCapture).ToArray(), endpoint)));
    private static void SetupChoice(ComboBox control, IReadOnlyList<IndependentEndpointChoice> choices, string? selected)
    {
        control.ItemsSource = choices; control.DisplayMemberPath = "Label"; control.SelectedValuePath = "Id";
        control.SelectedValue = selected; control.MinHeight = 30; control.Margin = new Thickness(0, 7, 0, 7);
    }
    private static void AddText(Panel parent, string text, double size = 13)
    {
        TextBlock label = new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, size > 13 ? 14 : 6, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); parent.Children.Add(label);
    }
    private void UpdateReturnHint()
    {
        var selected = endpoints.FirstOrDefault(e => e.Id == destination.SelectedValue as string);
        var consumers = selected?.TransportId is string transport ? endpoints.Where(e => e.IsCapture && e.IsActive && e.TransportId == transport).ToArray() : [];
        returnHint.Text = consumers.Length > 0 ? "In Discord, games or recording software, choose: " + string.Join(", ", consumers.Select(e => e.Name))
            : "No virtual return selected. A virtual cable/driver is needed to deliver processed voice to another app. You can test your physical input now.";
    }
    internal void Observe(HostSnapshot snapshot)
    {
        var mic = snapshot.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone);
        outputStatus.Text = mic?.Bridge?.OutputPeak is float peak ? $"Processed microphone output: {(peak > 0 ? 20 * Math.Log10(peak) : -120):0.0} dBFS · {mic.State}" : "Processed microphone output is not running. The input test does not play audio.";
        if (snapshot.MicrophoneTest is not { } probe || input.SelectedValue as string != probe.EndpointId) return;
        string Db(float value) => value > 0 ? (20 * Math.Log10(value)).ToString("0.0", CultureInfo.CurrentCulture) + " dBFS" : "silence";
        string[] levels = probe.Peaks.Select((peak, i) => $"Input {i + 1}: peak {Db(peak)}, RMS {Db(probe.Rms.ElementAtOrDefault(i))}").ToArray();
        int strongest = probe.Peaks.Count > 0 ? Enumerable.Range(0, probe.Peaks.Count).MaxBy(i => probe.Peaks[i]) : -1;
        measurement.Text = (probe.Running ? "Listening · " : "Test stopped · ") + $"{probe.SampleRate / 1000.0:0.#} kHz · {probe.Frames} frames\n" + string.Join("\n", levels) +
            (strongest >= 0 && probe.Peaks[strongest] > .0001f ? $"\nSignal detected on Input {strongest + 1}. Select that input below for centered voice." : "\nSpeak into the mic. If no input moves, check the interface gain and the connected input socket.") +
            (probe.Error is null ? "" : "\nCapture failed: " + probe.Error);
    }
}
