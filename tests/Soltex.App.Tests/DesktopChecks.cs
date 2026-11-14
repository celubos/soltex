using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Soltex.App.Desktop;
using Soltex.App.Pages;
using Soltex.App.Personalize;
using Soltex.App.Settings;
using Soltex.App.Shell;
using static Soltex.App.Tests.Program;

namespace Soltex.App.Tests;

/// <summary>Desktop groups and widgets: settings, the windows that show them (off screen), and the Desktop page.</summary>
internal static class DesktopChecks
{
    private const double OffScreen = -32000;

    public static void SettingsAreNormalized()
    {
        AppSettings saved = new AppSettings
        {
            DesktopGroups =
            [
                new DesktopGroup("a", "  Work  ", null, ["C:\\x", "", "C:\\y"], double.NaN, 10, 20, 5000),
                new DesktopGroup("a", "Duplicate", null, [], 0, 0, 300, 300),
                new DesktopGroup("", "No id", null, [], 0, 0, 300, 300)
            ],
            DesktopWidgets = [new(DesktopWidgetKind.Clock, 1, 2), new(DesktopWidgetKind.Clock, 3, 4), new((DesktopWidgetKind)99, 0, 0)]
        }.Normalize();
        True(saved.DesktopGroups is [{ Id: "a", Title: "Work", Left: 24, Width: DesktopGroup.MinimumWidth, Height: 2000 } g] && g.Items.Count == 2,
            "Groups were not normalized: " + string.Join("; ", saved.DesktopGroups));
        True(saved.DesktopWidgets is [{ Kind: DesktopWidgetKind.Clock, Left: 1 }], "Widgets were not normalized.");

        // Round trip through the settings file.
        string path = Path.Combine(Path.GetTempPath(), "soltex-desktop-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AppSettingsStore store = new(path);
            store.Update(_ => saved with { DesktopArrange = true });
            AppSettingsStore reloaded = new(path);
            True(reloaded.LoadProblem is null, "Saved desktop settings could not be read back: " + reloaded.LoadProblem);
            True(reloaded.Current.DesktopGroups is [{ Id: "a", Title: "Work" } r] && r.Items.Count == 2 && reloaded.Current.DesktopWidgets.Count == 1 && reloaded.Current.DesktopArrange,
                "Desktop settings did not survive a save and reload.");
        }
        finally { File.Delete(path); }
    }

    public static void ServiceShowsGroupsAndWidgets()
    {
        string folder = Path.Combine(Path.GetTempPath(), "soltex-desktop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Sub"));
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b");
        string hidden = Path.Combine(folder, "hidden.txt");
        File.WriteAllText(hidden, "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        string listed = Path.Combine(folder, "b.txt");
        AppSettingsStore store = new(folder + "-settings.json");
        store.Update(s => s with
        {
            DesktopGroups =
            [
                new DesktopGroup("folder", "Folder", folder, [], OffScreen, OffScreen, 300, 220),
                new DesktopGroup("list", "List", null, [listed, Path.Combine(folder, "missing.lnk")], OffScreen, OffScreen, 300, 220)
            ],
            DesktopWidgets = [new(DesktopWidgetKind.Clock, OffScreen, OffScreen), new(DesktopWidgetKind.System, OffScreen, OffScreen)]
        });
        using DesktopService service = new(store);
        try
        {
            service.Attach(desktopLayer: false);
            Pump();
            True(service.GroupWindows.Count == 2 && service.WidgetWindows.Count == 2, "Expected two group and two widget windows.");
            // Entries are read on a worker thread; wait for both groups to show them and for the folder to be watched.
            WaitFor(() => service.GroupWindows["folder"].ShownItems.Count == 2 && service.GroupWindows["list"].ShownItems.Count == 1 && service.GroupWindows["folder"].Watching, 5);
            True(service.GroupWindows["folder"].Left == OffScreen, "Outside the real desktop, surfaces are never moved on screen.");
            IReadOnlyList<string> shown = service.GroupWindows["folder"].ShownItems;
            True(shown.Count == 2 && shown[0].EndsWith("Sub", StringComparison.Ordinal) && shown[1] == listed,
                "A folder group shows folders first, then files, and never hidden files: " + string.Join(", ", shown));
            True(service.GroupWindows["list"].ShownItems is [string only] && only == listed, "A list group shows only items that exist.");
            True(service.WidgetWindows[DesktopWidgetKind.Clock].Reading.Contains(DateTime.Now.Year.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
                || service.WidgetWindows[DesktopWidgetKind.Clock].Reading.Length > 5, "The clock shows nothing.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!service.WidgetWindows[DesktopWidgetKind.System].Reading.Contains('%', StringComparison.Ordinal) && DateTime.UtcNow < deadline) Pump(100);
            True(service.WidgetWindows[DesktopWidgetKind.System].Reading.Contains('%', StringComparison.Ordinal), "The CPU and memory widget never showed a reading.");

            // New file in the folder appears; roll-up and removal follow the settings.
            File.WriteAllText(Path.Combine(folder, "c.txt"), "c");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.GroupWindows["folder"].ShownItems.Count < 3 && DateTime.UtcNow < deadline) Pump(100);
            True(service.GroupWindows["folder"].ShownItems.Count == 3, "A new file in the folder did not appear.");
            store.Update(s => s with
            {
                DesktopGroups = [s.DesktopGroups[0] with { RolledUp = true }],
                DesktopWidgets = [s.DesktopWidgets[0]]
            });
            Pump();
            True(service.GroupWindows.Count == 1 && service.GroupWindows["folder"].Height < 40, "Removing a group or rolling one up did not apply.");
            True(service.WidgetWindows.Count == 1 && service.WidgetWindows.ContainsKey(DesktopWidgetKind.Clock), "Removing a widget did not close it.");
        }
        finally
        {
            service.Dispose();
            try { Directory.Delete(folder, recursive: true); File.Delete(folder + "-settings.json"); }
            catch (IOException) { }
        }
    }

    public static void FolderGroupsReadOnlyWhenNeeded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "soltex-desktop-burst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        AppSettingsStore store = new(folder + "-settings.json");
        store.Update(s => s with { DesktopGroups = [new DesktopGroup("folder", "Folder", folder, [], OffScreen, OffScreen, 300, 220)] });
        using DesktopService service = new(store);
        try
        {
            service.Attach(desktopLayer: false);
            DesktopGroupWindow window = service.GroupWindows["folder"];
            WaitFor(() => window.Loads >= 1 && window.Watching, 5);
            True(window.Loads == 1 && window.Watching, "The folder group did not read and watch its folder.");

            // Moving, resizing and rolling up never read the folder again.
            store.Update(s => s with { DesktopGroups = [s.DesktopGroups[0] with { Left = OffScreen + 10, Width = 320 }] });
            Pump();
            store.Update(s => s with { DesktopGroups = [s.DesktopGroups[0] with { RolledUp = true }] });
            Pump(400);
            True(window.Loads == 1, $"Moving or rolling up a group read its folder again ({window.Loads} reads).");

            // A burst of new files becomes a handful of reads, not one per file.
            for (int i = 0; i < 300; i++) File.WriteAllText(Path.Combine(folder, $"f{i:000}.txt"), "x");
            WaitFor(() => window.ShownItems.Count == DesktopGroup.MaximumItems, 10);
            Pump(800);
            True(window.ShownItems.Count == DesktopGroup.MaximumItems, "The group did not show the new files.");
            True(window.Loads - 1 <= 6, $"300 new files caused {window.Loads - 1} reads of the folder.");
        }
        finally
        {
            service.Dispose();
            try { Directory.Delete(folder, recursive: true); File.Delete(folder + "-settings.json"); }
            catch (IOException) { }
        }
    }

    public static void SurfacesComeBackAfterExplorerRestarts()
    {
        string path = Path.Combine(Path.GetTempPath(), "soltex-desktop-restart-" + Guid.NewGuid().ToString("N") + ".json");
        AppSettingsStore store = new(path);
        store.Update(s => s with
        {
            DesktopGroups = [new DesktopGroup("list", "List", null, [], OffScreen, OffScreen, 300, 220)],
            DesktopWidgets = [new(DesktopWidgetKind.Clock, OffScreen, OffScreen)]
        });
        using DesktopService service = new(store);
        try
        {
            service.Attach(desktopLayer: false);
            Pump();
            DesktopGroupWindow group = service.GroupWindows["list"];
            DesktopWidgetWindow clock = service.WidgetWindows[DesktopWidgetKind.Clock];

            // What Explorer's "TaskbarCreated" broadcast leads to: every surface is opened again for the new desktop.
            service.Rebuild();
            Pump();
            True(service.GroupWindows["list"] != group && service.WidgetWindows[DesktopWidgetKind.Clock] != clock && !group.IsVisible && !clock.IsVisible
                && service.GroupWindows["list"].IsVisible && service.WidgetWindows[DesktopWidgetKind.Clock].IsVisible,
                "After Explorer restarts, groups and widgets must be closed and opened again.");

            // A surface closed by Windows (it went down with the old desktop) comes back on its own.
            DesktopGroupWindow lost = service.GroupWindows["list"];
            lost.Close();
            WaitFor(() => service.GroupWindows.TryGetValue("list", out DesktopGroupWindow? back) && back != lost && back.IsVisible, 5);
            True(service.GroupWindows.TryGetValue("list", out DesktopGroupWindow? reopened) && reopened != lost && reopened.IsVisible, "A group closed by Windows was not opened again.");

            // Removing everything stops listening; nothing is reopened.
            store.Update(s => s with { DesktopGroups = [], DesktopWidgets = [] });
            Pump(1300);
            True(service.GroupWindows.Count == 0 && service.WidgetWindows.Count == 0, "Removed surfaces came back.");
        }
        finally
        {
            service.Dispose();
            File.Delete(path);
        }
    }

    public static void DesktopEventsSettleBursts()
    {
        using DesktopEvents events = new(TimeSpan.FromMilliseconds(100));
        int restarts = 0, displays = 0;
        events.ShellRestarted += () => restarts++;
        events.DisplayChanged += () => displays++;
        True(DesktopEvents.ShellRestartMessage != 0, "The TaskbarCreated message was not registered.");
        // Posted only to this test's own hidden window.
        for (int i = 0; i < 5; i++)
        {
            _ = PostMessageW(events.Handle, DesktopEvents.ShellRestartMessage, 0, 0);
            _ = PostMessageW(events.Handle, 0x007E, 32, 0); // WM_DISPLAYCHANGE
        }
        WaitFor(() => restarts > 0 && displays > 0, 3);
        Pump(300);
        True(restarts == 1 && displays == 1, $"A burst must settle into one event each (restarts {restarts}, display changes {displays}).");
    }

    public static void SurfacesStayOnScreen()
    {
        Rect work = new(0, 0, 1920, 1040);
        True(DesktopSurfaceWindow.MostlyVisible(new Rect(100, 100, 300, 200), work), "A group on the screen is visible.");
        True(!DesktopSurfaceWindow.MostlyVisible(new Rect(2300, 100, 280, 220), work), "A group saved on an unplugged screen is off screen.");
        True(!DesktopSurfaceWindow.MostlyVisible(new Rect(1800, 100, 280, 220), work), "Less than half on the screen counts as off screen.");
        True(DesktopSurfaceWindow.MostlyVisible(new Rect(1700, 100, 280, 220), work), "More than half on the screen is left alone.");
        True(DesktopSurfaceWindow.Within(new Rect(2300, 100, 280, 220), work) == new Point(1640, 100), "An off-screen group comes back to the nearest edge.");
        True(DesktopSurfaceWindow.Within(new Rect(-500, -50, 280, 220), work) == new Point(0, 0), "A group above and left of the screen comes back to the corner.");
        True(DesktopSurfaceWindow.Within(new Rect(100, 100, 3000, 220), work) == new Point(0, 100), "A group wider than the screen lines up with its left edge.");

        // The clock shows hours and minutes, so it wakes once a minute, just after the minute starts.
        True(DesktopWidgetWindow.UntilNextMinute(new DateTime(2026, 1, 1, 10, 0, 59, 500)) == TimeSpan.FromMilliseconds(505)
            && DesktopWidgetWindow.UntilNextMinute(new DateTime(2026, 1, 1, 10, 0, 0)) == TimeSpan.FromMilliseconds(60005), "The clock does not wake on the minute.");
    }

    public static void PageExplainsGroupsAndKeepsRows()
    {
        string root = Path.Combine(Path.GetTempPath(), "soltex-desktop-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppSettingsStore store = new(AppSettingsStore.DefaultPath(root));
        store.Update(s => s with { StartPage = Destination.Desktop });
        // Render-only shell: nothing is attached to the desktop, and the Windows settings engine must not be needed.
        ShellServices services = new(root, store, Dispatcher.CurrentDispatcher, _ => throw new InvalidOperationException("The Desktop page must not use the Windows settings engine."));
        ShellWindow window = new(services, renderSmoke: true);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        RenderSmokeCapture.ConfigureWindow(window);
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = OffScreen;
        window.Top = OffScreen;
        window.Show();
        Pump();
        try
        {
            window.Navigate(Destination.Desktop);
            Pump();
            DesktopPage page = (DesktopPage)window.Page(Destination.Desktop);
            TextBlock empty = Descendants(page).OfType<TextBlock>().Single(t => t.Text.StartsWith("Show a folder's files", StringComparison.Ordinal));
            True(empty.IsVisible, "With no groups, the page says what a group is.");
            True(Descendants(page).OfType<Button>().Any(b => b.Content as string == "Show a folder…") && Descendants(page).OfType<Button>().Any(b => b.Content as string == "New empty group"),
                "The two ways to add a group are not named plainly.");
            StackPanel heading = Descendants(page).OfType<StackPanel>().First(p => p.Children.OfType<TextBlock>().FirstOrDefault()?.Text == "Groups");
            True(heading.Margin.Top == 0, "The first heading must sit right under the page title, as on the other pages.");

            page.AddGroup(null, "Projects");
            Pump();
            True(!empty.IsVisible, "The explanation stays after a group was added.");
            Button remove = Descendants(page).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Remove group Projects");
            TextBox name = Descendants(page).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Group name: Projects");
            name.Text = "Work";
            name.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(name)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
            True(store.Current.DesktopGroups is [{ Title: "Work" }], "Enter did not save the group name.");
            True(Descendants(page).OfType<Button>().Contains(remove) && AutomationProperties.GetName(remove) == "Remove group Work",
                "Renaming rebuilt the rows, so a click on Remove that ends a rename would be lost.");
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(remove).GetPattern(PatternInterface.Invoke)).Invoke();
            Pump();
            Pump();
            True(store.Current.DesktopGroups.Count == 0 && empty.IsVisible, "Remove did not remove the group.");

            True(DesktopPage.WallpaperNote(null, LiveWallpaperStatus.Off) == "Off"
                && DesktopPage.WallpaperNote(@"C:\Videos\clip.mov", LiveWallpaperStatus.CannotPlay) == "clip.mov · this video can't be played"
                && DesktopPage.WallpaperNote(@"C:\Videos\clip.mov", LiveWallpaperStatus.FileMissing) == "clip.mov · file not found"
                && DesktopPage.WallpaperNote(@"C:\Videos\clip.mov", LiveWallpaperStatus.Playing) == "clip.mov · muted, paused while a window is maximized",
                "The video note does not follow what happened to the video.");
        }
        finally
        {
            window.RequestQuit();
            WaitFor(() => closed, 10);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void Pump(int milliseconds = 0)
    {
        if (milliseconds > 0) Thread.Sleep(milliseconds);
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    internal static void WaitFor(Func<bool> condition, double seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) Pump(50);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    internal static void ToggleThroughAutomation(System.Windows.Controls.CheckBox toggle) =>
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)).Toggle();
}
