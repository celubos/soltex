using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Soltex.App.Pages;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Tests;

/// <summary>
/// Every page of the app in the real shell window at window widths 760 to 1920 and text scales 100, 150 and 200 percent:
/// the navigation pane becomes an icon rail exactly when the page would otherwise get too narrow, the rail keeps names,
/// tooltips and search, and no page (rail included) draws controls or text over each other, cuts them off or pushes
/// them past the right edge. The Audio page shows made-up audio state (<see cref="AudioPreview"/>).
/// </summary>
internal static class PageLayoutChecks
{
    public static void MainPagesFitEveryWidthAndScale()
    {
        List<string> problems = [];
        int layouts = 0;
        bool preview = AudioPreview.Enabled, sonar = AudioPage.SonarInstalled;
        AudioPreview.Enabled = true;
        AudioPage.SonarInstalled = true;
        string? renders = Environment.GetEnvironmentVariable("SOLTEX_RENDER_DIR") is { Length: > 0 } directory ? Path.Combine(directory, "page-layout") : null;
        bool renderAll = Environment.GetEnvironmentVariable("SOLTEX_RENDER_ALL") == "1";
        try
        {
            ThemeService.Apply(Application.Current, new AppSettings { Theme = ThemePreference.Dark });
            using AudioLayoutChecks.Shell shell = new(ThemePreference.Dark);
            ShellWindow window = shell.Window;
            foreach (int scale in AudioLayoutChecks.Scales)
            {
                ThemeService.ApplyScale(window.ScaleRoot, new AppSettings { ScalePercent = scale });
                foreach (Destination destination in Enum.GetValues<Destination>())
                {
                    window.Navigate(destination);
                    AudioLayoutChecks.Pump();
                    foreach (int width in AudioLayoutChecks.Widths)
                    {
                        RenderSmokeCapture.ConfigureWindow(window, width, AudioLayoutChecks.WindowHeight);
                        window.UpdateLayout();
                        AudioLayoutChecks.Pump();
                        layouts++;
                        string combo = $"{scale}% {width}px {destination}";
                        List<string> found = [];
                        bool compact = window.ScaleRoot.ActualWidth - ShellWindow.ExpandedRailWidth < ShellWindow.MinimumPageWidth;
                        if (window.IsRailCompact != compact)
                            found.Add($"rail: expected the {(compact ? "icon" : "full")} rail for a {window.ScaleRoot.ActualWidth:0} px layout");
                        if (window.PageHost.ActualWidth < 300)
                            found.Add($"page too narrow: the page gets {window.PageHost.ActualWidth:0} px");
                        if (destination == Destination.MyPc) found.AddRange(RailProblems(window));
                        found.AddRange(AudioLayoutChecks.Inspect(window.ScaleRoot, PageScroller(window)));
                        problems.AddRange(found.Select(p => combo + ": " + p));
                        if (renders is not null && (found.Count > 0 || renderAll))
                            AudioLayoutChecks.Save(window, width, Path.Combine(renders, combo.Replace(' ', '-').Replace("%", "pct", StringComparison.Ordinal) + ".png"));
                    }
                }
                // Search fits the window at its narrowest.
                RenderSmokeCapture.ConfigureWindow(window, AudioLayoutChecks.Widths[0], AudioLayoutChecks.WindowHeight);
                window.OpenSearchForCapture("micro");
                window.UpdateLayout();
                AudioLayoutChecks.Pump();
                Border card = AudioLayoutChecks.Descendants(window.SearchOverlay).OfType<Border>().First();
                Rect bounds = card.TransformToAncestor(window.ScaleRoot).TransformBounds(new Rect(card.RenderSize));
                if (bounds.Left < 0 || bounds.Right > window.ScaleRoot.ActualWidth + 1)
                    problems.Add($"{scale}% {AudioLayoutChecks.Widths[0]}px search: the search box spans {bounds.Left:0}..{bounds.Right:0} in {window.ScaleRoot.ActualWidth:0} px");
                window.SearchOverlay.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            AudioPreview.Enabled = preview;
            AudioPage.SonarInstalled = sonar;
            ThemeService.Apply(Application.Current, new AppSettings());
        }
        if (renders is not null)
        {
            Directory.CreateDirectory(renders);
            File.WriteAllLines(Path.Combine(renders, "problems.txt"), problems);
        }
        if (problems.Count > 0)
            throw new InvalidOperationException($"{problems.Count} layout problems in {layouts} layouts. First ones:\n      " + string.Join("\n      ", problems.Take(40)));
    }

    /// <summary>The page's own scroll host: the first scroll viewer inside the page area.</summary>
    private static ScrollViewer? PageScroller(ShellWindow window)
    {
        Queue<DependencyObject> queue = new([window.PageHost]);
        while (queue.Count > 0)
        {
            DependencyObject node = queue.Dequeue();
            if (node is ScrollViewer scroller && !ReferenceEquals(node, window.PageHost)) return scroller;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }

    /// <summary>On the icon rail every page keeps its name and a tooltip with its label, and search stays reachable.</summary>
    private static IEnumerable<string> RailProblems(ShellWindow window)
    {
        foreach (ListBox nav in new[] { window.PrimaryNav, window.FooterNav })
        {
            foreach (NavItem item in nav.Items.Cast<NavItem>())
            {
                // A short window scrolls the list; items out of view have no container yet and are checked once scrolled to.
                if (nav.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) continue;
                if (AutomationProperties.GetName(container) != item.Label) yield return "rail: " + item.Label + " lost its accessible name";
                if (container.ToolTip is not string tip || !tip.Contains(window.IsRailCompact ? item.Label : item.Description, StringComparison.Ordinal))
                    yield return "rail: " + item.Label + " has no tooltip naming it";
                if (!container.Focusable) yield return "rail: " + item.Label + " can't be reached with the keyboard";
            }
        }
        if (!window.SearchButton.IsVisible || AutomationProperties.GetName(window.SearchButton) != "Search settings and tools" ||
            window.SearchButton.ToolTip is not string searchTip || !searchTip.Contains("Ctrl+K", StringComparison.Ordinal))
            yield return "rail: search is not reachable as a named button with its Ctrl+K hint";
    }
}
