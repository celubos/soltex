using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Soltex.Audio.App.Tests;

internal static class LayoutChecks
{
    public static async Task ViewportAsync(int width, int height, string? renderDirectory)
    {
        var window = new MixerFixture { Width = width, Height = height };
        Checks.PrepareOffscreen(window);
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            Checks.That(Math.Abs(window.ActualWidth - width) < 1 && Math.Abs(window.ActualHeight - height) < 1,
                "Window minimum constraints prevented the requested logical viewport.");
            var vertical = Checks.Named<ScrollViewer>(window, "MixerViewport");
            Checks.Render(window, renderDirectory, $"mixer-{width}x{height}-top.png");
            var linked = Checks.Named<ComboBox>(window, "LinkedDevice");
            var peer = UIElementAutomationPeer.CreatePeerForElement(linked);
            var automationBounds = peer.GetBoundingRectangle();
            Checks.That(!automationBounds.IsEmpty && automationBounds.Width >= 100 && automationBounds.Height >= 30,
                "Playback selector must expose useful automation geometry.");
            Checks.That(peer.GetPattern(PatternInterface.ExpandCollapse) is not null,
                "Playback selector must expose the standard expansion automation pattern.");

            var faders = Checks.Descendants(window).OfType<Slider>().Where(s => s.Orientation == Orientation.Vertical).ToArray();
            Checks.That(faders.Length == 6, "All six channel faders must remain in the layout.");
            Checks.That(faders.All(s => s.ActualHeight >= 100), "Short windows must scroll instead of collapsing usable fader travel.");
            Checks.That(vertical.ScrollableWidth < 1, "The mixer must never need sideways scrolling: strips wrap into rows instead.");

            // Strips wrap into even rows and never cover each other.
            var stripList = Checks.Named<ItemsControl>(window, "ChannelStrips");
            var strips = Enumerable.Range(0, stripList.Items.Count).Select(i => stripList.ItemContainerGenerator.ContainerFromIndex(i)).OfType<FrameworkElement>().ToArray();
            Checks.That(strips.Length == 6, "All six channel strips must be laid out.");
            var stripBounds = strips.Select(s => s.TransformToAncestor(vertical).TransformBounds(new Rect(s.RenderSize))).ToArray();
            for (int i = 0; i < stripBounds.Length; i++)
                for (int j = i + 1; j < stripBounds.Length; j++)
                    Checks.That(Rect.Intersect(stripBounds[i], stripBounds[j]) is not { IsEmpty: false, Width: > 1, Height: > 1 }, "Channel strips overlap.");
            if (width == 1240 && height == 810)
            {
                Checks.That(vertical.ScrollableHeight < 1, "Normal size must show the complete mixer without scrolling.");
                Checks.That(stripBounds.Select(b => Math.Round(b.Top)).Distinct().Count() == 1, "Normal size must show all six strips in one row.");
            }

            // Nothing past the right edge: every control fits the viewport's width.
            foreach (var control in Checks.Descendants(Checks.Named<Grid>(window, "MixerLayout")).OfType<Control>()
                .Where(c => c.IsVisible && c is Button or CheckBox or ComboBox or Slider or TextBox))
            {
                var bounds = control.TransformToAncestor(vertical).TransformBounds(new Rect(control.RenderSize));
                Checks.That(bounds.Left >= -1 && bounds.Right <= vertical.ViewportWidth + 1,
                    $"{control.GetType().Name} '{(control as ContentControl)?.Content ?? control.Name}' runs past the side of a {width} px window.");
            }
            foreach (string caption in new[] { "Devices", "Processing", "Reconnect", "Settings" })
            {
                var button = Checks.Descendants(window).OfType<Button>().Single(b => string.Equals(b.Content as string, caption, StringComparison.Ordinal));
                var bounds = button.TransformToAncestor(vertical).TransformBounds(new Rect(button.RenderSize));
                Checks.That(bounds.Left >= 0 && bounds.Right <= vertical.ActualWidth + 1 && bounds.Top >= 0,
                    $"Header action '{caption}' is outside the initial viewport.");
            }
            var deviceBounds = linked.TransformToAncestor(vertical).TransformBounds(new Rect(linked.RenderSize));
            var link = Checks.Named<CheckBox>(window, "LinkOutputs");
            var linkBounds = link.TransformToAncestor(vertical).TransformBounds(new Rect(link.RenderSize));
            Checks.That(!deviceBounds.IntersectsWith(linkBounds), "Device selector and linked-output toggle must not overlap.");

            vertical.ScrollToEnd(); window.UpdateLayout();
            var footer = Checks.Named<TextBlock>(window, "StatusText");
            var footerBounds = footer.TransformToAncestor(vertical).TransformBounds(new Rect(footer.RenderSize));
            Checks.That(footerBounds.Top >= -1 && footerBounds.Bottom <= vertical.ActualHeight + 1, "Footer must be reachable at scroll end.");
            foreach (var bar in Checks.Descendants(window).OfType<ScrollBar>().Where(b => b.IsVisible && b.ActualWidth > 0 && b.ActualHeight > 0))
            {
                var thumb = Checks.Descendants(bar).OfType<Thumb>().FirstOrDefault();
                Checks.That(thumb is not null && Checks.Descendants(thumb).OfType<Border>().Any(b => b.Background is not null),
                    "Visible scrollbars must have a painted thumb, including when theme token names change.");
            }
            Checks.Render(window, renderDirectory, $"mixer-{width}x{height}-end.png");
            vertical.ScrollToTop(); window.UpdateLayout();
            Console.WriteLine($"  Simulated {width}x{height} DIP; fader minimum {faders.Min(s => s.ActualHeight):F0}; strip rows {stripBounds.Select(b => Math.Round(b.Top)).Distinct().Count()}; selector UIA {automationBounds.Width:F0}x{automationBounds.Height:F0}");
        }
        finally { await window.Workspace.ShutdownAsync(); window.Close(); }
    }
}
