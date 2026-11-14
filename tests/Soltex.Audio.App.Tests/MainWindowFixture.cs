using System.Windows;
using Soltex.Audio.UI;

namespace Soltex.Audio.App.Tests;

/// <summary>
/// Hosts the actual reusable control without initializing its backend. Synthetic model values
/// exercise layout through the same production bindings; no source or action handlers are copied.
/// </summary>
internal sealed class MixerFixture : Window
{
    public MixerWorkspace Workspace { get; } = new();

    public MixerFixture()
    {
        Content = Workspace;
        MinWidth = 640; MinHeight = 420;
        AudioTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        var model = (MixerModel)Workspace.DataContext;
        var output = new DeviceChoice("fixture-render", "Speakers (layout fixture output)");
        model.Outputs.Add(output);
        foreach (var channel in model.Channels)
        {
            channel.Devices.Add(new("fixture-render", channel.Id == "mic" ? "Microphone (layout fixture input)" : output.Label));
            channel.DeviceId = output.Id;
            channel.DeviceName = output.Label;
            channel.Level = channel.Id == "master" ? 75 : 80;
            channel.IsReady = channel.CanSelectDevice = true;
            channel.Peak = -1; // No fabricated live signal.
            channel.Status = "Layout fixture · not live";
        }
        var application = new ApplicationModel("fixture", 123, "Example application with a long name", "render")
        { Status = "Fixture routing state — no actual session or audio mutation" };
        application.Routes.Add(new("game", "Game"));
        application.Routes.Add(new("media", "Media"));
        application.RouteId = "media";
        model.Applications.Add(application);

        Checks.Named<System.Windows.Controls.ComboBox>(this, "LinkedDevice").SelectedItem = output;
        Checks.Named<System.Windows.Controls.Slider>(this, "ChatMixSlider").IsEnabled = true;
        Checks.Named<System.Windows.Controls.TextBlock>(this, "ChatMixStatus").Text = "Centered · fixture";
        Checks.Named<System.Windows.Controls.TextBlock>(this, "StatusText").Text = "SIMULATED VIEWPORT • layout fixture, no audio connection";
    }

}
