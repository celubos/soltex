using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace
{
    public static readonly DependencyProperty IsCompactMixerLayoutProperty = DependencyProperty.Register(
        nameof(IsCompactMixerLayout), typeof(bool), typeof(MixerWorkspace), new PropertyMetadata(false));

    public bool IsCompactMixerLayout
    {
        get => (bool)GetValue(IsCompactMixerLayoutProperty);
        set => SetValue(IsCompactMixerLayoutProperty, value);
    }

    private void MixerWindowSizeChanged(object sender, SizeChangedEventArgs e) =>
        IsCompactMixerLayout = e.NewSize.Width < 920;

    private void ChannelViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ChannelScrollHint is not null)
            ChannelScrollHint.Visibility = ChannelViewport.ScrollableWidth > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChannelViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || ChannelViewport.ScrollableWidth <= 0) return;
        ChannelViewport.ScrollToHorizontalOffset(ChannelViewport.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

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
        // Existing faders deliberately ignore the wheel. On constrained viewports, wheel-over-fader
        // scrolls the workspace instead of trapping the gesture or accidentally changing volume.
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
}
