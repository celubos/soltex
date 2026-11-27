using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Pages;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Tests;

/// <summary>
/// Lays out every Audio tab (Mixer, Microphone, Apps, Sonar connected and Sonar not running) inside the real shell
/// window with made-up audio state
/// (<see cref="AudioPreview"/>: nothing real is opened) at several window widths, text scales and both themes, and
/// checks what a person would call a jumbled layout: controls or text drawn over each other, text cut off or
/// trimmed without a tooltip, words broken in the middle, and anything pushed past the right edge (a horizontal
/// scroller is allowed only when its scroll bar shows).
/// </summary>
internal static class AudioLayoutChecks
{
    internal static readonly int[] Widths = [760, 900, 1100, 1280, 1600, 1920];
    internal static readonly int[] Scales = [100, 150, 200];
    /// <summary>The four tabs, then Sonar again as it looks when Sonar isn't running.</summary>
    internal static readonly string[] Tabs = ["playback", "microphone", "apps", "sonar", "sonar-offline"];
    internal const int WindowHeight = 860;
    private const double Tolerance = 1.0;

    public static void TabsFitEveryWidthScaleAndTheme()
    {
        List<string> problems = [];
        int layouts = 0;
        bool preview = AudioPreview.Enabled, sonar = AudioPage.SonarInstalled;
        AudioPreview.Enabled = true;
        AudioPage.SonarInstalled = true;
        // With SOLTEX_RENDER_DIR set, layouts with problems are saved as pictures (every layout with SOLTEX_RENDER_ALL=1).
        string? renders = Environment.GetEnvironmentVariable("SOLTEX_RENDER_DIR") is { Length: > 0 } directory ? Path.Combine(directory, "audio-layout") : null;
        bool renderAll = Environment.GetEnvironmentVariable("SOLTEX_RENDER_ALL") == "1";
        try
        {
            foreach (ThemePreference theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                ThemeService.Apply(Application.Current, new AppSettings { Theme = theme });
                using Shell shell = new(theme);
                AudioPage page = (AudioPage)shell.Window.Page(Destination.Audio);
                shell.Window.Navigate(Destination.Audio);
                Pump();
                foreach (int scale in Scales)
                {
                    ThemeService.ApplyScale(shell.Window.ScaleRoot, new AppSettings { ScalePercent = scale });
                    foreach (string tab in Tabs)
                    {
                        page.Reveal(tab == "sonar-offline" ? "sonar" : tab);
                        Pump();
                        ShowEverything(page);
                        foreach (Soltex.Audio.UI.MixerWorkspace workspace in Descendants(page).OfType<Soltex.Audio.UI.MixerWorkspace>().Where(s => s.IsVisible))
                        {
                            if (tab == "sonar-offline") workspace.ShowUnavailablePreview("No connection could be made because the target machine actively refused it.");
                            else workspace.ShowPreview(AudioPreview.Sonar(), AudioPreview.SonarChatMix(), AudioPreview.SonarPeaks());
                        }
                        Pump();
                        foreach (int width in Widths)
                        {
                            RenderSmokeCapture.ConfigureWindow(shell.Window, width, WindowHeight);
                            shell.Window.UpdateLayout();
                            Pump();
                            layouts++;
                            string combo = $"{theme.ToString().ToLowerInvariant()} {scale}% {width}px {tab}";
                            List<string> found = Inspect(page);
                            problems.AddRange(found.Select(p => combo + ": " + p));
                            if (renders is not null && (found.Count > 0 || renderAll)) Save(shell.Window, width, Path.Combine(renders, combo.Replace(' ', '-').Replace("%", "pct", StringComparison.Ordinal) + ".png"));
                        }
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
        if (renders is not null)
        {
            Directory.CreateDirectory(renders);
            File.WriteAllLines(Path.Combine(renders, "problems.txt"), problems);
        }
        if (problems.Count > 0)
            throw new InvalidOperationException($"{problems.Count} layout problems in {layouts} layouts. First ones:\n      " +
                string.Join("\n      ", problems.Take(40)));
    }

    /// <summary>
    /// Opens the parts that start hidden, so they are checked too: the microphone setup form and, on the Mixer, the
    /// bar that asks to apply a staged change (made by switching Aux on in the made-up state; nothing is applied).
    /// </summary>
    private static void ShowEverything(AudioPage page)
    {
        foreach (MicrophonePathView microphone in Descendants(page).OfType<MicrophonePathView>().Where(v => v.IsVisible && !v.IsSetupOpen)) microphone.OpenSetup();
        foreach (PlaybackView playback in Descendants(page).OfType<PlaybackView>().Where(v => v.IsVisible)) playback.PlaybackChannels[^1].Enabled = true;
        Pump();
    }

    /// <summary>
    /// Every layout problem under <paramref name="root"/>, described for a person. <paramref name="pageScroller"/> is the
    /// page's own scroll host (content below the fold still counts); by default the Audio page's PageScroll.
    /// </summary>
    internal static List<string> Inspect(FrameworkElement root, ScrollViewer? pageScroller = null)
    {
        List<string> problems = [];
        List<FrameworkElement> leaves = [];
        List<TextBlock> texts = [];
        Collect(root, insideLeaf: false, leaves, texts);
        pageScroller ??= root.FindName("PageScroll") as ScrollViewer;

        // Overlap: what is visible of each leaf (inner scroll viewports cut it, the page's own vertical scroll does not).
        var visible = leaves.Select(l => (Leaf: l, Rect: VisibleBounds(l, root, pageScroller))).Where(v => !v.Rect.IsEmpty && v.Rect.Width > Tolerance && v.Rect.Height > Tolerance).ToArray();
        for (int i = 0; i < visible.Length; i++)
        {
            for (int j = i + 1; j < visible.Length; j++)
            {
                Rect overlap = Rect.Intersect(visible[i].Rect, visible[j].Rect);
                if (overlap.IsEmpty || overlap.Width <= Tolerance || overlap.Height <= Tolerance) continue;
                if (IsPlaceholder(visible[i].Leaf, visible[j].Leaf) || IsPlaceholder(visible[j].Leaf, visible[i].Leaf)) continue;
                problems.Add($"overlap: {Describe(visible[i].Leaf)} and {Describe(visible[j].Leaf)} ({overlap.Width:0}x{overlap.Height:0})");
            }
        }

        foreach (FrameworkElement leaf in leaves)
        {
            if (CutOff(leaf, root) is { } cut) problems.Add(cut);
            switch (leaf)
            {
                case ComboBox combo when combo.ActualWidth < 96:
                    problems.Add($"too narrow to use: {Describe(combo)} is {combo.ActualWidth:0} wide");
                    break;
                case Slider { Orientation: Orientation.Horizontal } slider when slider.ActualWidth < 96:
                    problems.Add($"too narrow to use: {Describe(slider)} is {slider.ActualWidth:0} wide");
                    break;
            }
        }
        foreach (TextBlock text in texts)
        {
            if (!leaves.Contains(text) && CutOff(text, root) is { } cut) problems.Add(cut);
            if (TextProblem(text) is { } problem) problems.Add(problem);
        }

        // A horizontal scroller is fine only when it shows its scroll bar.
        foreach (ScrollViewer scroller in Descendants(root).OfType<ScrollViewer>().Where(s => s.IsVisible))
        {
            if (scroller.ScrollableWidth > Tolerance && scroller.ComputedHorizontalScrollBarVisibility != Visibility.Visible)
                problems.Add($"hidden horizontal scroll: {Describe(scroller)} hides {scroller.ScrollableWidth:0} px without a scroll bar");
        }
        return problems;
    }

    private static void Collect(DependencyObject node, bool insideLeaf, List<FrameworkElement> leaves, List<TextBlock> texts)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is not FrameworkElement element || !element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
            if (element is ScrollBar) continue;
            if (element is TextBlock text && !string.IsNullOrWhiteSpace(text.Text)) texts.Add(text);
            bool leaf = !insideLeaf && IsLeaf(element);
            if (leaf && (element is not TextBlock block || !string.IsNullOrWhiteSpace(block.Text))) leaves.Add(element);
            Collect(element, insideLeaf || leaf, leaves, texts);
        }
    }

    private static bool IsLeaf(FrameworkElement element) => element switch
    {
        TextBlock or ButtonBase or ComboBox or Slider or TextBoxBase or ProgressBar or ListBoxItem => true,
        Panel or Control or Decorator or ContentPresenter or ItemsPresenter or Viewbox => false,
        _ => element.GetType().Namespace?.StartsWith("Soltex", StringComparison.Ordinal) == true
    };

    /// <summary>A hint drawn on purpose over an empty list box (it does not take clicks).</summary>
    private static bool IsPlaceholder(FrameworkElement hint, FrameworkElement under) =>
        hint is TextBlock { IsHitTestVisible: false } && under is ComboBox { SelectedItem: null };

    /// <summary>The part of <paramref name="element"/> left visible by inner scroll viewports (the page's own scroll is ignored).</summary>
    private static Rect VisibleBounds(FrameworkElement element, FrameworkElement root, ScrollViewer? pageScroller)
    {
        Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        for (DependencyObject? node = VisualTreeHelper.GetParent(element); node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollContentPresenter presenter || (pageScroller is not null && ReferenceEquals(presenter.TemplatedParent, pageScroller))) continue;
            Rect viewport = presenter.TransformToAncestor(root).TransformBounds(new Rect(presenter.RenderSize));
            bounds.Intersect(viewport);
            if (bounds.IsEmpty) return Rect.Empty;
        }
        return bounds;
    }

    /// <summary>Says how an element is cut: by a layout clip on it or an ancestor, or past a viewport that cannot scroll that way.</summary>
    private static string? CutOff(FrameworkElement element, FrameworkElement root)
    {
        // Everything in the page's coordinates. Once the element is inside a viewport that scrolls one way, only the
        // part inside that viewport counts that way for the ancestors further out (the viewport itself must fit).
        Rect own = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not FrameworkElement ancestor) continue;
            Rect area = ancestor.TransformToAncestor(root).TransformBounds(new Rect(ancestor.RenderSize));
            if (ancestor is ScrollContentPresenter presenter)
            {
                ScrollViewer? scroller = presenter.TemplatedParent as ScrollViewer;
                bool horizontal = scroller?.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;
                bool vertical = scroller?.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;
                if (!horizontal && (own.Left < area.Left - Tolerance || own.Right > area.Right + Tolerance))
                    return $"past the edge: {Describe(element)} spans {own.Left - area.Left:0}..{own.Right - area.Left:0} in {Describe(scroller ?? (FrameworkElement)presenter)} {area.Width:0} wide";
                if (!vertical && (own.Top < area.Top - Tolerance || own.Bottom > area.Bottom + Tolerance))
                    return $"past the edge: {Describe(element)} spans {own.Top - area.Top:0}..{own.Bottom - area.Top:0} vertically in {Describe(scroller ?? (FrameworkElement)presenter)} {area.Height:0} high";
                if (horizontal) { own.X = Math.Max(own.Left, area.Left); own.Width = Math.Max(0, Math.Min(own.Right, area.Right) - own.X); }
                if (vertical) { own.Y = Math.Max(own.Top, area.Top); own.Height = Math.Max(0, Math.Min(own.Bottom, area.Bottom) - own.Y); }
                // Scrolled out of view, not cut: the person scrolls to it.
                if (own.Width <= 0 || own.Height <= 0) return null;
            }
            else if (LayoutInformation.GetLayoutClip(ancestor) is { } clip && !clip.Bounds.IsEmpty)
            {
                Rect allowed = ancestor.TransformToAncestor(root).TransformBounds(clip.Bounds);
                allowed.Inflate(Tolerance, Tolerance);
                if (!allowed.Contains(own))
                    return $"cut off: {Describe(element)} ({own.Width:0}x{own.Height:0}) clipped by {(ReferenceEquals(ancestor, element) ? "its own slot" : Describe(ancestor))} to {clip.Bounds.Width:0}x{clip.Bounds.Height:0}";
            }
            if (ReferenceEquals(node, root)) break;
        }
        return null;
    }

    /// <summary>Trimmed text without a tooltip, text cut off vertically, or a word broken in the middle.</summary>
    private static string? TextProblem(TextBlock text)
    {
        double available = text.ActualWidth - text.Padding.Left - text.Padding.Right;
        if (available <= 0) return $"no room: {Describe(text)} has {available:0} px";
        double natural = Measure(text, TextWrapping.NoWrap, double.PositiveInfinity).Width;
        if (text.TextWrapping == TextWrapping.NoWrap)
        {
            if (natural <= available + Tolerance) return null;
            return text.TextTrimming == TextTrimming.None
                ? $"cut off: {Describe(text)} needs {natural:0} px, has {available:0}"
                : HasTooltip(text) ? null : $"trimmed without a tooltip: {Describe(text)} needs {natural:0} px, has {available:0}";
        }
        foreach (string word in text.Text.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            double wordWidth = Measure(text, TextWrapping.NoWrap, double.PositiveInfinity, word).Width;
            if (wordWidth > available + Tolerance) return $"word broken: \"{word}\" in {Describe(text)} needs {wordWidth:0} px, has {available:0}";
        }
        // The page rounds layout to whole pixels, which can take up to a pixel off a measured width without the text
        // wrapping; the copy is measured with that pixel of slack.
        double needed = Measure(text, text.TextWrapping, available + Tolerance).Height;
        double room = text.ActualHeight - text.Padding.Top - text.Padding.Bottom;
        if (needed <= room + Tolerance) return null;
        return text.TextTrimming == TextTrimming.None
            ? $"cut off: {Describe(text)} needs {needed:0} px high, has {room:0}"
            : HasTooltip(text) ? null : $"trimmed without a tooltip: {Describe(text)} needs {needed:0} px high, has {room:0}";
    }

    /// <summary>Measures a detached copy of the text block, so the page's own layout is untouched.</summary>
    private static Size Measure(TextBlock text, TextWrapping wrapping, double width, string? content = null)
    {
        TextBlock copy = new()
        {
            Text = content ?? text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight,
            FontStyle = text.FontStyle, FontStretch = text.FontStretch, TextWrapping = wrapping, LineHeight = text.LineHeight,
            LineStackingStrategy = text.LineStackingStrategy, FlowDirection = text.FlowDirection
        };
        System.Windows.Documents.Typography.SetCapitals(copy, System.Windows.Documents.Typography.GetCapitals(text));
        TextOptions.SetTextFormattingMode(copy, TextOptions.GetTextFormattingMode(text));
        copy.Measure(new Size(width, double.PositiveInfinity));
        return copy.DesiredSize;
    }

    private static bool HasTooltip(FrameworkElement element)
    {
        int steps = 0;
        for (DependencyObject? node = element; node is not null && steps < 6; node = VisualTreeHelper.GetParent(node), steps++)
        {
            if (node is FrameworkElement { ToolTip: not null }) return true;
            if (node is Control && !ReferenceEquals(node, element)) break;
        }
        return false;
    }

    private static string Describe(FrameworkElement element)
    {
        string name = element.Name is { Length: > 0 } n ? "#" + n
            : AutomationProperties.GetName(element) is { Length: > 0 } a ? "'" + a + "'"
            : element is TextBlock { Text: var t } ? "\"" + (t.Length > 40 ? t[..40] + "..." : t) + "\""
            : element is ContentControl { Content: string c } ? "\"" + c + "\"" : "";
        FrameworkElement? owner = Ancestors(element).OfType<FrameworkElement>().FirstOrDefault(a => a.Name.Length > 0 || AutomationProperties.GetName(a).Length > 0);
        string within = owner is null ? "" : " in " + (owner.Name.Length > 0 ? "#" + owner.Name : "'" + AutomationProperties.GetName(owner) + "'");
        return element.GetType().Name + (name.Length > 0 ? " " + name : "") + within;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (DependencyObject below in Descendants(child)) yield return below;
        }
    }

    internal static void Save(Window window, int width, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RenderTargetBitmap bitmap = RenderSmokeCapture.Capture(window, width, WindowHeight);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    internal static void Pump()
    {
        for (int i = 0; i < 2; i++)
        {
            DispatcherFrame frame = new();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>The real shell window on a throw-away profile with an in-memory registry; nothing in Windows changes.</summary>
    internal sealed class Shell : IDisposable
    {
        private bool closed;

        public Shell(ThemePreference theme)
        {
            string root = Path.Combine(Path.GetTempPath(), "soltex-audio-layout", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppSettingsStore store = new(AppSettingsStore.DefaultPath(root));
            store.Update(s => s with { Theme = theme });
            Services = new ShellServices(root, store, Dispatcher.CurrentDispatcher,
                journal => new ChangeEngine(new MemoryRegistry(), new ChangeJournal(journal), 26300));
            Window = new ShellWindow(Services, renderSmoke: true);
            Window.Closed += (_, _) => closed = true;
            RenderSmokeCapture.ConfigureWindow(Window, Widths[0], WindowHeight);
            Window.ShowInTaskbar = false;
            Window.ShowActivated = false;
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = -32_000;
            Window.Top = -32_000;
            Window.Show();
            Pump();
        }

        public ShellServices Services { get; }
        public ShellWindow Window { get; }

        public void Dispose()
        {
            Window.RequestQuit();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!closed && DateTime.UtcNow < deadline) Pump();
            if (!closed) throw new InvalidOperationException("The shell did not finish shutting down.");
        }
    }

    /// <summary>Registry kept in memory, so building the shell can never write to Windows.</summary>
    private sealed class MemoryRegistry : IRegistryStore
    {
        private readonly Dictionary<string, RegistryValue> values = new(StringComparer.OrdinalIgnoreCase);
        public RegistryValue Read(RegistryHive2 hive, string path, string name) =>
            values.TryGetValue(hive + "|" + path + "|" + name, out RegistryValue value) ? value : RegistryValue.Absent;
        public void Write(string currentUserPath, string name, RegistryValue value)
        {
            string key = RegistryHive2.CurrentUser + "|" + currentUserPath + "|" + name;
            if (value.Exists) values[key] = value; else values.Remove(key);
        }
    }
}
