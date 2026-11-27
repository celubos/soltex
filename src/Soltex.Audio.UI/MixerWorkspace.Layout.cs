using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace
{
    public static readonly DependencyProperty IsCompactMixerLayoutProperty = DependencyProperty.Register(
        nameof(IsCompactMixerLayout), typeof(bool), typeof(MixerWorkspace), new PropertyMetadata(false));

    /// <summary>
    /// True below 920 px. The layout reflows on its own (rows stack, strips wrap); hosts may still read this to adapt
    /// their own surroundings.
    /// </summary>
    public bool IsCompactMixerLayout
    {
        get => (bool)GetValue(IsCompactMixerLayoutProperty);
        set => SetValue(IsCompactMixerLayoutProperty, value);
    }

    private void MixerWindowSizeChanged(object sender, SizeChangedEventArgs e) =>
        IsCompactMixerLayout = e.NewSize.Width < 920;

    private void MixerViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (MixerViewport.ScrollableHeight <= 0)
        {
            // Inside a scrolling host page the mixer is never taller than its viewport, so its own scroll viewer
            // would swallow the wheel. Pass it to the page unless a list under the pointer can scroll itself.
            if (_hostOptions.Embedded && !InnerListCanScroll(e.OriginalSource as DependencyObject)
                && VisualTreeHelper.GetParent(this) is UIElement host)
            {
                e.Handled = true;
                host.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
            }
            return;
        }
        // Faders deliberately ignore the wheel. On constrained viewports, wheel-over-fader scrolls the workspace
        // instead of trapping the gesture or accidentally changing volume.
        for (var source = e.OriginalSource as DependencyObject; source is not null;)
        {
            if (source is Slider)
            {
                MixerViewport.ScrollToVerticalOffset(MixerViewport.VerticalOffset - e.Delta);
                e.Handled = true;
                return;
            }
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }

    private bool InnerListCanScroll(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, MixerViewport))
        {
            if (source is ScrollViewer { ScrollableHeight: > 0 }) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private enum SonarConnection { Connecting, Connected, Off, Unavailable }

    /// <summary>The badge beside the title and, when Sonar can't be used, the notice with the next step.</summary>
    private void ShowConnection(SonarConnection state)
    {
        (string text, string background, string foreground) = state switch
        {
            SonarConnection.Connected => ("Connected", "GoodSurfaceBrush", "SignalBrush"),
            SonarConnection.Off => ("Sonar is off", "WarningSurfaceBrush", "WarningBrush"),
            SonarConnection.Unavailable => ("Not running", "DangerSurfaceBrush", "DangerBrush"),
            _ => ("Connecting…", "QuietBrush", "TextBrush")
        };
        ConnectionText.Text = text;
        ConnectionBadge.SetResourceReference(Border.BackgroundProperty, background);
        ConnectionText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        SonarNotice.Visibility = state is SonarConnection.Off or SonarConnection.Unavailable ? Visibility.Visible : Visibility.Collapsed;
        if (state == SonarConnection.Off)
        {
            SonarNoticeTitle.Text = "Sonar is off";
            SonarNoticeText.Text = "Turn on Sonar in SteelSeries GG to use its channels here, then choose Try again.";
        }
        else if (state == SonarConnection.Unavailable)
        {
            SonarNoticeTitle.Text = "Sonar isn't running";
            SonarNoticeText.Text = "Open SteelSeries GG and turn on Sonar, then choose Try again. Your Sonar settings are kept.";
        }
    }
}
