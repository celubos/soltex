using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    /// <summary>
    /// Short windows (900x700 at 100 and 150 percent, the smallest window at 200 percent): the rail still fits. Items get
    /// shorter instead of scrolling out of view; History and Settings stay under their rule; when even the shortest items
    /// do not fit, the page list scrolls with the selected page in view and no highlight under the scroll bar.
    /// </summary>
    public static void RailFitsShortWindows()
    {
        List<string> problems = [];
        bool preview = AudioPreview.Enabled, sonar = AudioPage.SonarInstalled;
        AudioPreview.Enabled = true;
        AudioPage.SonarInstalled = true;
        string? renders = Environment.GetEnvironmentVariable("SOLTEX_RENDER_DIR") is { Length: > 0 } directory ? Path.Combine(directory, "rail-fit") : null;
        try
        {
            ThemeService.Apply(Application.Current, new AppSettings { Theme = ThemePreference.Dark });
            using AudioLayoutChecks.Shell shell = new(ThemePreference.Dark);
            ShellWindow window = shell.Window;
            (int Width, int Height, int Scale, bool Short)[] cases =
            [
                (1280, 820, 100, false), (1180, 780, 100, false), (900, 700, 100, true), (900, 700, 150, true),
                (1920, 1080, 150, false), (760, 540, 100, true), (760, 540, 200, true), (1920, 1080, 200, true)
            ];
            foreach ((int width, int height, int scale, bool expectShort) in cases)
            {
                ThemeService.ApplyScale(window.ScaleRoot, new AppSettings { ScalePercent = scale });
                foreach (Destination destination in new[] { Destination.Tools, Destination.MyPc, Destination.Settings })
                {
                    window.Navigate(destination);
                    RenderSmokeCapture.ConfigureWindow(window, width, height);
                    window.UpdateLayout();
                    AudioLayoutChecks.Pump();
                    AudioLayoutChecks.Pump();
                    string combo = $"{width}x{height} {scale}% {destination}";
                    List<string> found = [.. RailFit(window)];
                    if (window.IsRailShort != expectShort) found.Add($"expected the {(expectShort ? "short" : "full-height")} rail");
                    problems.AddRange(found.Select(p => combo + ": " + p));
                    if (renders is not null && (found.Count > 0 || destination == Destination.Tools))
                    {
                        Directory.CreateDirectory(renders);
                        RenderTargetBitmap bitmap = RenderSmokeCapture.Capture(window, width, height);
                        PngBitmapEncoder encoder = new();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using FileStream stream = File.Create(Path.Combine(renders, combo.Replace(' ', '-').Replace("%", "pct", StringComparison.Ordinal) + ".png"));
                        encoder.Save(stream);
                    }
                }
            }
        }
        finally
        {
            AudioPreview.Enabled = preview;
            AudioPage.SonarInstalled = sonar;
            ThemeService.Apply(Application.Current, new AppSettings());
        }
        Program.True(problems.Count == 0, $"{problems.Count} rail problems. First ones:\n      " + string.Join("\n      ", problems.Take(30)));
    }

    private static IEnumerable<string> RailFit(ShellWindow window)
    {
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window.ScaleRoot).TransformBounds(new Rect(element.RenderSize));
        double standard = Application.Current.TryFindResource("Shell.NavItemHeight") is double height ? height : 40;
        Rect primary = Bounds(window.PrimaryNav), footer = Bounds(window.FooterNav), rule = Bounds(window.FooterRule);
        if (footer.Bottom > window.ScaleRoot.ActualHeight + 1) yield return $"History and Settings end at {footer.Bottom:0}, below the window ({window.ScaleRoot.ActualHeight:0})";
        if (!window.FooterRule.IsVisible || rule.Top < primary.Bottom - 0.5 || rule.Bottom > footer.Top + 0.5)
            yield return "History and Settings lost the rule that separates them from the pages";
        ScrollBar? bar = AudioLayoutChecks.Descendants(window.PrimaryNav).OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        if (window.IsPageListScrolling != bar is not null) yield return $"page list scrolling is {window.IsPageListScrolling} but its scroll bar is {(bar is null ? "hidden" : "shown")}";
        Rect? lane = bar is null ? null : Bounds(bar);
        foreach (ListBox nav in new[] { window.PrimaryNav, window.FooterNav })
        {
            Rect view = Bounds(nav);
            foreach (NavItem item in nav.Items.Cast<NavItem>())
            {
                if (nav.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem { IsVisible: true } row)
                {
                    if (!window.IsPageListScrolling || ReferenceEquals(nav, window.FooterNav)) yield return item.Label + " has no row although its list does not scroll";
                    if (ReferenceEquals(item, nav.SelectedItem)) yield return "the selected page " + item.Label + " is scrolled out of view";
                    continue;
                }
                if (row.ActualHeight < ShellWindow.MinimumRailItemHeight - 0.5) yield return $"{item.Label} is {row.ActualHeight:0} px high";
                if (!window.IsRailShort && Math.Abs(row.ActualHeight - standard) > 0.5) yield return $"{item.Label} is {row.ActualHeight:0} px high in a window with room for {standard:0}";
                Border fill = (Border)row.Template.FindName("Fill", row);
                Rect box = Bounds(fill);
                bool inView = box.Top >= view.Top - 0.5 && box.Bottom <= view.Bottom + 0.5;
                if (lane is { } l && box.Right > l.Left + 0.5 && box.Bottom > l.Top && box.Top < l.Bottom)
                    yield return $"{item.Label}'s highlight runs under the scroll bar ({box.Right:0} > {l.Left:0})";
                if (!inView && (!window.IsPageListScrolling || ReferenceEquals(nav, window.FooterNav))) yield return item.Label + " is cut off";
                if (!inView && ReferenceEquals(item, nav.SelectedItem)) yield return "the selected page " + item.Label + " is not fully in view";
            }
        }
    }

    /// <summary>
    /// Every page fills the page area with its scroll host (scroll bar at the window edge) and puts its content in the same
    /// column: same left edge, same width, capped at Shell.PageMaxWidth (1280x820 and 1920x1080, light and dark).
    /// </summary>
    public static void PagesShareOneContentColumn()
    {
        List<string> problems = [];
        bool preview = AudioPreview.Enabled, sonar = AudioPage.SonarInstalled;
        AudioPreview.Enabled = true;
        AudioPage.SonarInstalled = true;
        try
        {
            ThemeService.Apply(Application.Current, new AppSettings { Theme = ThemePreference.Dark });
            using AudioLayoutChecks.Shell shell = new(ThemePreference.Dark);
            ShellWindow window = shell.Window;
            ThemeService.ApplyScale(window.ScaleRoot, new AppSettings());
            double cap = Application.Current.TryFindResource("Shell.PageMaxWidth") is double max ? max : 900;
            foreach ((int width, int height) in new[] { (1280, 820), (1920, 1080) })
            {
                RenderSmokeCapture.ConfigureWindow(window, width, height);
                (double Left, double Width)? reference = null;
                foreach (Destination destination in Enum.GetValues<Destination>())
                {
                    window.Navigate(destination);
                    window.UpdateLayout();
                    AudioLayoutChecks.Pump();
                    string combo = $"{width}x{height} {destination}";
                    Rect Bounds(FrameworkElement element) => element.TransformToAncestor(window.ScaleRoot).TransformBounds(new Rect(element.RenderSize));
                    ScrollViewer? scroller = PageScroller(window);
                    if (scroller is null) { problems.Add(combo + ": no page scroll host"); continue; }
                    Rect area = Bounds(window.PageViewport), host = Bounds(scroller);
                    if (Math.Abs(host.Right - area.Right) > 1 || Math.Abs(host.Left - area.Left) > 1)
                        problems.Add($"{combo}: the page spans {host.Left:0}..{host.Right:0}, not the page area {area.Left:0}..{area.Right:0} (scroll bar away from the window edge)");
                    if (scroller.Content is not Grid { ColumnDefinitions.Count: > 0 } frame) { problems.Add(combo + ": the page has no column frame"); continue; }
                    (double Left, double Width) column = (Bounds(frame).Left, frame.ColumnDefinitions[0].ActualWidth);
                    // Layout rounding may add a pixel.
                    if (column.Width > cap + 1.5) problems.Add($"{combo}: the content column is {column.Width:0} px, over the {cap:0} px cap");
                    reference ??= column;
                    if (Math.Abs(column.Left - reference.Value.Left) > 1 || Math.Abs(column.Width - reference.Value.Width) > 1)
                        problems.Add($"{combo}: the content column is at {column.Left:0}, {column.Width:0} px wide; Home's is at {reference.Value.Left:0}, {reference.Value.Width:0} px");
                }
            }
        }
        finally
        {
            AudioPreview.Enabled = preview;
            AudioPage.SonarInstalled = sonar;
            ThemeService.Apply(Application.Current, new AppSettings());
        }
        Program.True(problems.Count == 0, $"{problems.Count} column problems:\n      " + string.Join("\n      ", problems.Take(30)));
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
