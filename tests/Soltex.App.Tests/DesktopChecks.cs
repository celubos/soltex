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
using Soltex.Customize;
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

    /// <summary>
    /// History and the De-Windows card keep the keyboard: after an undo it moves to the next Undo, then rests on the
    /// summary; after Apply it lands on the card's Undo. Full De-Windows offers Undo… on its card, beside Review….
    /// </summary>
    public static void HistoryAndDeWindowsKeepTheKeyboard()
    {
        using PageShell shell = new();
        ChangeEngine engine = shell.Services.Calm;
        SettingDefinition[] privacy = [.. CalmCatalog.All.Where(d => d.Group == SettingGroup.Privacy && d.Options.Count == 2 && (d.IsSwitch || d.Choices is null)).Take(2)];
        foreach (SettingDefinition d in privacy)
            True(engine.ApplyChoice(d, engine.Inspect(d).Choice == 1 ? 0 : 1).Outcome == ChangeOutcome.Applied, "Setup: " + d.Id + " was not applied.");

        HistoryPage history = shell.Open<HistoryPage>(Destination.History);
        True(history.Intro.Text.Contains("De‑Windows", StringComparison.Ordinal) && !history.Intro.Text.Contains("De-Windows", StringComparison.Ordinal),
            "History's subtitle must keep \"De-Windows\" on one line (non-breaking hyphen).");
        True(history.EmptyText.Text.Contains("aren't listed", StringComparison.Ordinal) && history.Intro.Text.Contains("aren't listed", StringComparison.Ordinal),
            "History must say which quick options it does not list, rather than promise to undo every change.");
        IReadOnlyList<Button> undo = history.UndoButtons();
        True(undo.Count == 2, $"Setup: expected two Undo buttons, found {undo.Count}.");
        undo[0].Focus();
        Invoke(undo[0]);
        WaitFor(() => history.UndoButtons().Count == 1, 5);
        Pump();
        Button left = history.UndoButtons().Single();
        True(Focused(shell.Window) == left, "After an undo the keyboard must move to the next Undo in the list, not drop to the window: " + Focused(shell.Window));
        Invoke(left);
        WaitFor(() => history.UndoButtons().Count == 0, 5);
        Pump();
        True(Focused(shell.Window) == history.Summary && history.Summary.IsVisible,
            "With nothing left to undo the keyboard must rest on the summary: " + Focused(shell.Window));

        MyPcPage home = shell.Open<MyPcPage>(Destination.MyPc);
        DeWindowsPanel card = home.DeWindows;
        Invoke(card.ReviewButton);
        Pump();
        True(card.PlanPanel.IsVisible && Focused(shell.Window) == card.ApplyPlanButton, "Review must open the list with Apply focused.");
        Invoke(card.ApplyPlanButton);
        Pump();
        WaitFor(() => card.Busy.Visibility == Visibility.Collapsed, 10);
        Pump();
        True(!card.PlanPanel.IsVisible && card.UndoAllButton.IsEnabled && Focused(shell.Window) == card.UndoAllButton,
            "After Apply the keyboard must land on the card's Undo (Review has nothing left to do): " + Focused(shell.Window));

        SystemPanel full = home.SystemPanel;
        True(full.UndoButton.IsVisible && !full.PlanPanel.IsAncestorOf(full.UndoButton) && full.UndoButton.Content as string == "Undo…"
            && AutomationProperties.GetName(full.UndoButton) == "Undo the full De-Windows changes" && full.ReviewButton.Content as string == "Review…",
            "Full De-Windows must offer Undo… on its card, beside Review…, as the De-Windows card does.");
    }

    /// <summary>Home's readings go two by two in a narrow window or large text, wrap rather than clip, and keep units together.</summary>
    public static void HomeReadingsReflowAndKeepUnitsTogether()
    {
        using PageShell shell = new();
        MyPcPage home = shell.Open<MyPcPage>(Destination.MyPc);
        True(home.ProtectionDetail.Visibility == Visibility.Collapsed, "An empty protection detail must be collapsed so the headline sits centred on its tile.");
        try
        {
            shell.Layout(760, 150);
            True(home.Metrics.Columns == 2, $"At 150% in a 760 px window the readings must sit two by two (grid {home.Metrics.ActualWidth:0} px wide).");
            shell.Layout(1920, 100);
            True(home.Metrics.Columns == 4, $"With room the readings sit in one row (grid {home.Metrics.ActualWidth:0} px wide).");
        }
        finally { shell.Layout(AudioLayoutChecks.Widths[0], 100); }

        string line = MyPcPage.NetworkLine(13_312, 2_048);
        True(line.Split(' ') is [_, "·", _], "The network line may break only around its dot: " + line.Replace(' ', '_').Replace("⁠", "", StringComparison.Ordinal));
        string rate = MyPcPage.Unbroken("13 KB/s");
        // WrapWithOverflow breaks only where line breaking allows (Wrap would also cut a word wider than the box).
        TextBlock plain = new() { Text = "13 KB/s", FontSize = 12, TextWrapping = TextWrapping.WrapWithOverflow },
            kept = new() { Text = rate, FontSize = 12, TextWrapping = TextWrapping.WrapWithOverflow };
        plain.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        kept.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        True(Math.Abs(plain.DesiredSize.Width - kept.DesiredSize.Width) < 2, $"Word joiners must draw nothing ({plain.DesiredSize.Width:0.0} vs {kept.DesiredSize.Width:0.0} px).");
        double oneLine = kept.DesiredSize.Height, narrow = kept.DesiredSize.Width - 3;
        plain.Measure(new Size(narrow, double.PositiveInfinity));
        kept.Measure(new Size(narrow, double.PositiveInfinity));
        True(plain.DesiredSize.Height > oneLine + 1, "Setup: the plain reading breaks when it does not fit.");
        True(Math.Abs(kept.DesiredSize.Height - oneLine) < 1, "A reading must not split between its number, unit and \"/s\".");
    }

    /// <summary>
    /// The Desktop page follows the window's padding resource, adds groups from a row like its other sections, asks for a
    /// video with "Choose video…", and the desktop service reports a save it could not make instead of throwing.
    /// </summary>
    public static void DesktopPageFollowsPaddingAndReportsFailedSaves()
    {
        using (PageShell shell = new())
        {
            DesktopPage page = shell.Open<DesktopPage>(Destination.Desktop);
            Grid frame = (Grid)((ScrollViewer)page.Content).Content;
            try
            {
                shell.Layout(760, 200);
                Thickness narrow = (Thickness)shell.Window.FindResource("Shell.PagePadding");
                True(frame.Margin == narrow && narrow.Left == 16, $"In a narrow page the Desktop page must take the window's slimmer padding ({frame.Margin} vs {narrow}).");
                shell.Layout(1280, 100);
                True(frame.Margin == (Thickness)shell.Window.FindResource("Shell.PagePadding") && frame.Margin != narrow, "With room the Desktop page must go back to the usual padding.");
            }
            finally { shell.Layout(AudioLayoutChecks.Widths[0], 100); }
            Button addFolder = Descendants(page).OfType<Button>().Single(b => b.Content as string == "Show a folder…");
            True(Ancestors(addFolder).OfType<Border>().Any(b => ReferenceEquals(b.Style, page.FindResource("Studio.Row"))), "Adding a group must sit in a row like the page's other sections.");
            True(Descendants(page).OfType<Button>().Any(b => b.Content as string == "Choose video…"), "Choose video opens a dialog, so it ends with \"…\".");
        }

        string folder = Path.Combine(Path.GetTempPath(), "soltex-desktop-save-" + Guid.NewGuid().ToString("N"));
        string settingsFolder = Path.Combine(folder, "Settings");
        AppSettingsStore store = new(Path.Combine(settingsFolder, "app.json"));
        store.Update(s => s with
        {
            DesktopGroups = [new DesktopGroup("list", "List", null, [], OffScreen, OffScreen, 300, 220)],
            DesktopWidgets = [new(DesktopWidgetKind.Clock, OffScreen, OffScreen)]
        });
        using DesktopService service = new(store);
        List<string> failures = [];
        service.SaveFailed += failures.Add;
        try
        {
            service.Attach(desktopLayer: false);
            Pump();
            // From now on the settings folder can't be made, so every save fails.
            Directory.Delete(settingsFolder, recursive: true);
            File.WriteAllText(settingsFolder, "");
            // Moved by the user (arranging): the new place can't be saved.
            System.Reflection.MethodInfo commit = typeof(DesktopSurfaceWindow).GetMethod("CommitBounds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (DesktopSurfaceWindow moved in new DesktopSurfaceWindow[] { service.WidgetWindows[DesktopWidgetKind.Clock], service.GroupWindows["list"] })
            {
                moved.Left = OffScreen + 40;
                commit.Invoke(moved, null);
            }
            True(failures.Count == 2 && failures.All(f => f.StartsWith("A change to a desktop group or widget could not be saved", StringComparison.Ordinal)),
                "A move that can't be saved must be reported, not thrown into the window's event: " + string.Join("; ", failures));
            True(store.Current.DesktopGroups.Count == 1 && store.Current.DesktopWidgets.Count == 1, "Nothing was saved, so the settings stay as they were.");
        }
        finally
        {
            service.Dispose();
            try { File.Delete(settingsFolder); Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }

    internal static void Invoke(Button button) =>
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    /// <summary>The element with focus in the window (logical focus: test windows are never activated).</summary>
    internal static object? Focused(Window window) => FocusManager.GetFocusedElement(window);

    internal static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(node); parent is not null; parent = VisualTreeHelper.GetParent(parent)) yield return parent;
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

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
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

/// <summary>
/// The real shell window, render-only and off screen, on a throw-away profile with an in-memory registry: pages can be
/// opened and used, and nothing in Windows changes (no tray, taskbar, desktop or input hooks are attached).
/// </summary>
internal sealed class PageShell : IDisposable
{
    private bool closed;

    public PageShell(Func<AppSettings, AppSettings>? configure = null, Func<string, ChangeEngine>? calmFactory = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "soltex-page-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        AppSettingsStore store = new(AppSettingsStore.DefaultPath(Root));
        if (configure is not null) store.Update(configure);
        Services = new ShellServices(Root, store, Dispatcher.CurrentDispatcher,
            calmFactory ?? (journal => new ChangeEngine(new PageTestRegistry(), new ChangeJournal(journal), 26300)));
        Window = new ShellWindow(Services, renderSmoke: true);
        Window.Closed += (_, _) => closed = true;
        RenderSmokeCapture.ConfigureWindow(Window);
        Window.ShowInTaskbar = false;
        Window.ShowActivated = false;
        Window.WindowStartupLocation = WindowStartupLocation.Manual;
        Window.Left = -32_000;
        Window.Top = -32_000;
        Window.Show();
        DesktopChecks.Pump();
    }

    public string Root { get; }
    public ShellServices Services { get; }
    public ShellWindow Window { get; }
    public ShellContext Context => new(Services, Window);

    /// <summary>Navigates to a page and returns it once it is laid out.</summary>
    public T Open<T>(Destination destination) where T : class
    {
        Window.Navigate(destination);
        DesktopChecks.Pump();
        return (T)(object)Window.Page(destination);
    }

    /// <summary>Lays the window out at a width (px) and text scale (percent).</summary>
    public void Layout(int width, int scale)
    {
        ThemeService.ApplyScale(Window.ScaleRoot, new AppSettings { ScalePercent = scale });
        RenderSmokeCapture.ConfigureWindow(Window, width, AudioLayoutChecks.WindowHeight);
        Window.UpdateLayout();
        DesktopChecks.Pump();
        DesktopChecks.Pump();
    }

    /// <summary>From now on every settings save fails, as when the settings folder can't be written (a file stands where it belongs).</summary>
    public void BreakSettingsFolder()
    {
        string folder = Path.GetDirectoryName(Services.Settings.Path)!;
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "");
    }

    public void Dispose()
    {
        Window.RequestQuit();
        DesktopChecks.WaitFor(() => closed, 10);
        True(closed, "The shell did not finish shutting down.");
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Registry kept in memory for page checks. <see cref="Hold"/> keeps writes waiting (a change in flight); names in
/// <see cref="Denied"/> are refused with access denied, as Windows does for values it guards from apps.
/// </summary>
internal sealed class PageTestRegistry : IRegistryStore
{
    private readonly Dictionary<string, RegistryValue> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private int writes;

    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ManualResetEventSlim? Hold { get; set; }
    public int Writes => Volatile.Read(ref writes);

    public RegistryValue Read(RegistryHive2 hive, string path, string name)
    {
        lock (gate) return values.TryGetValue(Key(hive, path, name), out RegistryValue value) ? value : RegistryValue.Absent;
    }

    public void Write(string currentUserPath, string name, RegistryValue value)
    {
        Hold?.Wait(TimeSpan.FromSeconds(10));
        if (Denied.Contains(name)) throw new UnauthorizedAccessException("Access to the registry key is denied.");
        NeverTouch.Guard(RegistryHive2.CurrentUser, currentUserPath, name);
        Put(currentUserPath, name, value);
        Interlocked.Increment(ref writes);
    }

    /// <summary>Changes a value the way Windows Settings or another app would: never held, never refused.</summary>
    public void Put(string currentUserPath, string name, RegistryValue value)
    {
        lock (gate)
        {
            string key = Key(RegistryHive2.CurrentUser, currentUserPath, name);
            if (value.Exists) values[key] = value; else values.Remove(key);
        }
    }

    private static string Key(RegistryHive2 hive, string path, string name) => hive + "|" + path + "|" + name;
}
