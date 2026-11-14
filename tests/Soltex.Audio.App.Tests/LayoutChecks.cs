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
            var horizontal = Checks.Named<ScrollViewer>(window, "ChannelViewport");
            var linked = Checks.Named<ComboBox>(window, "LinkedDevice");
            var peer = UIElementAutomationPeer.CreatePeerForElement(linked);
            var automationBounds = peer.GetBoundingRectangle();
            Checks.That(!automationBounds.IsEmpty && automationBounds.Width >= 100 && automationBounds.Height >= 30,
                "Playback selector must expose useful automation geometry.");
            Checks.That(peer.GetPattern(PatternInterface.ExpandCollapse) is not null,
                "Playback selector must expose the standard expansion automation pattern.");

            var faders = Checks.Descendants(window).OfType<Slider>().Where(s => s.Orientation == Orientation.Vertical).ToArray();
            Checks.That(faders.Length == 6, "All six current channel faders must remain in the layout.");
            Checks.That(faders.All(s => s.ActualHeight >= 70), "Short windows must scroll instead of collapsing usable fader travel.");
            if (width == 1240 && height == 810)
                Checks.That(horizontal.ScrollableWidth < 1 && vertical.ScrollableHeight < 1, "Normal size must show the complete mixer without scrolling.");

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
            Checks.Render(window, renderDirectory, $"mixer-{width}x{height}-top.png");

            horizontal.ScrollToRightEnd(); vertical.ScrollToEnd(); window.UpdateLayout();
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

            horizontal.ScrollToLeftEnd(); vertical.ScrollToTop(); window.UpdateLayout();
            var lastInput = Checks.Descendants(Checks.Named<ItemsControl>(window, "ChannelStrips")).OfType<ComboBox>().Last();
            lastInput.BringIntoView(); window.UpdateLayout();
            var lastBounds = lastInput.TransformToAncestor(horizontal).TransformBounds(new Rect(lastInput.RenderSize));
            Checks.That(lastBounds.Left >= -1 && lastBounds.Right <= horizontal.ActualWidth + 1,
                "A request to reveal the final channel must make its device control reachable.");
            Console.WriteLine($"  Simulated {width}x{height} DIP; fader minimum {faders.Min(s => s.ActualHeight):F0}; selector UIA {automationBounds.Width:F0}x{automationBounds.Height:F0}");
        }
        finally { await window.Workspace.ShutdownAsync(); window.Close(); }
    }
}
