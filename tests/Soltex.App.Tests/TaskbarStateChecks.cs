using System.IO;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Tests;

/// <summary>
/// Taskbar states (TranslucentTB-class): which state wins, what each display shows (from made-up facts, no real
/// windows), which window events are watched, and settings saved by version 1.0.
/// </summary>
internal static class TaskbarStateChecks
{
    private const nint LeftDisplay = 0x10, RightDisplay = 0x20, MainTaskbar = 0x100, SecondTaskbar = 0x200;
    private static readonly TaskbarWindowFacts[] Taskbars = [new(MainTaskbar, LeftDisplay), new(SecondTaskbar, RightDisplay)];

    public static void PrecedenceCountsOnlyEnabledStates()
    {
        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> all = Enabled(Enum.GetValues<TaskbarState>());
        TaskbarState[] order = [TaskbarState.TaskViewOpen, TaskbarState.StartOpen, TaskbarState.SearchOpen, TaskbarState.BatterySaver,
            TaskbarState.MaximizedWindow, TaskbarState.VisibleWindow, TaskbarState.Desktop];
        Program.True(TaskbarStateRules.Precedence.SequenceEqual(order), "The precedence order changed.");
        for (int i = 0; i < order.Length; i++)
            Program.True(TaskbarStateRules.Winner(order[i..], all) == order[i], $"{order[i]} must win over every state after it.");
        Program.True(TaskbarStateRules.Winner([TaskbarState.Desktop, TaskbarState.VisibleWindow, TaskbarState.MaximizedWindow], all) == TaskbarState.MaximizedWindow,
            "A maximized window must win over a visible one.");
        Program.True(TaskbarStateRules.Winner([TaskbarState.SearchOpen, TaskbarState.BatterySaver, TaskbarState.MaximizedWindow], all) == TaskbarState.SearchOpen,
            "Search must win over battery saver.");
        Program.True(TaskbarStateRules.Winner([TaskbarState.BatterySaver, TaskbarState.MaximizedWindow], all) == TaskbarState.BatterySaver,
            "Battery saver must win over a maximized window.");

        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> visibleOnly = Enabled([TaskbarState.VisibleWindow]);
        Program.True(TaskbarStateRules.Winner([TaskbarState.Desktop, TaskbarState.VisibleWindow, TaskbarState.MaximizedWindow, TaskbarState.StartOpen], visibleOnly)
            == TaskbarState.VisibleWindow, "Only enabled states count.");
        Program.True(TaskbarStateRules.Winner([TaskbarState.MaximizedWindow, TaskbarState.StartOpen], Enabled([])) == TaskbarState.Desktop,
            "Desktop applies when nothing else is enabled, even when it is not listed.");
        Program.True(new AppSettings().WithTaskbarState(TaskbarState.Desktop, l => l with { Enabled = false }).Normalize().LookFor(TaskbarState.Desktop).Enabled,
            "Desktop can never be switched off.");
    }

    public static void StatesFollowEachDisplay()
    {
        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> all = Enabled(Enum.GetValues<TaskbarState>());
        Dictionary<nint, TaskbarState> Evaluate(DesktopSnapshot snapshot, IReadOnlyDictionary<TaskbarState, TaskbarStateLook>? looks = null) =>
            TaskbarStateRules.Evaluate(snapshot, looks ?? all).ToDictionary(e => e.Taskbar, e => e.State);
        void Expect(Dictionary<nint, TaskbarState> found, TaskbarState left, TaskbarState right, string what) =>
            Program.True(found.Count == 2 && found[MainTaskbar] == left && found[SecondTaskbar] == right,
                $"{what}: expected {left} / {right}, got {string.Join(" / ", found.Values)}.");

        Expect(Evaluate(new(Taskbars, [], null, false)), TaskbarState.Desktop, TaskbarState.Desktop, "Empty desktop");
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true), Window(2, LeftDisplay)], null, false)),
            TaskbarState.VisibleWindow, TaskbarState.MaximizedWindow, "A maximized window on the right, a normal one on the left");

        WindowFacts[] ignored =
        [
            Window(3, LeftDisplay, maximized: true) with { Minimized = true },
            Window(4, LeftDisplay, maximized: true) with { Cloaked = true },
            Window(5, LeftDisplay) with { ToolWindow = true },
            Window(6, LeftDisplay) with { OnScreen = false },
            Window(7, LeftDisplay) with { Visible = false },
            Window(8, LeftDisplay, maximized: true) with { OwnedByThisApp = true },
            Window(9, LeftDisplay, className: "Progman"), Window(10, LeftDisplay, className: "WorkerW"),
            Window(11, LeftDisplay, className: "Shell_TrayWnd"), Window(12, RightDisplay, className: "Shell_SecondaryTrayWnd")
        ];
        foreach (WindowFacts window in ignored)
            Program.True(!TaskbarStateRules.Counts(window), $"Window {window.Window} ({window.ClassName}) must not count.");
        Expect(Evaluate(new(Taskbars, ignored, null, false)), TaskbarState.Desktop, TaskbarState.Desktop,
            "Minimized, cloaked, tool, off-screen, hidden, own and shell windows");

        ForegroundFacts start = new(20, RightDisplay, @"C:\Windows\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",
            "Windows.UI.Core.CoreWindow", "");
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true), Window(2, LeftDisplay, maximized: true)], start, false)),
            TaskbarState.MaximizedWindow, TaskbarState.StartOpen, "Start open on the right display");
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true)], start, true)),
            TaskbarState.BatterySaver, TaskbarState.StartOpen, "Battery saver on every display, Start over it on its own");
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true)], start, true), Enabled([TaskbarState.MaximizedWindow])),
            TaskbarState.Desktop, TaskbarState.MaximizedWindow, "Disabled states fall through to the next one");

        (ForegroundFacts Window, TaskbarState? State)[] foregrounds =
        [
            (start, TaskbarState.StartOpen),
            (new(21, LeftDisplay, @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", "Windows.UI.Core.CoreWindow", ""), TaskbarState.SearchOpen),
            (new(22, LeftDisplay, @"C:\Windows\SystemApps\Microsoft.Windows.Search_cw5n1h2txyewy\SearchApp.exe", "Windows.UI.Core.CoreWindow", ""), TaskbarState.SearchOpen),
            (new(23, LeftDisplay, @"C:\Windows\explorer.exe", "XamlExplorerHostIslandWindow", "Task View"), TaskbarState.TaskViewOpen),
            (new(24, LeftDisplay, @"C:\Windows\explorer.exe", "XamlExplorerHostIslandWindow_WASDK", "Task View"), TaskbarState.TaskViewOpen),
            (new(25, LeftDisplay, @"C:\Windows\explorer.exe", "MultitaskingViewFrame", "Task View"), TaskbarState.TaskViewOpen),
            (new(26, LeftDisplay, @"C:\Windows\explorer.exe", "XamlExplorerHostIslandWindow", "Task Switching"), null),
            (new(27, LeftDisplay, @"C:\Tools\other.exe", "XamlExplorerHostIslandWindow", "Task View"), null),
            (new(28, LeftDisplay, @"C:\Windows\explorer.exe", "CabinetWClass", "Task View"), null),
            (new(29, LeftDisplay, "", "Notepad", ""), null)
        ];
        foreach ((ForegroundFacts window, TaskbarState? state) in foregrounds)
            Program.True(TaskbarStateRules.Opened(window) == state, $"{Path.GetFileName(window.Process)} {window.ClassName} \"{window.Title}\" must be {state?.ToString() ?? "nothing"}.");

        // Closed but still the foreground window (Start and Search cloak themselves; Task View may hide its window): nothing is open.
        Program.True(TaskbarStateRules.Opened(start with { Shown = false }) is null, "A cloaked Start must not count as open.");
        Program.True(TaskbarStateRules.Opened(foregrounds[1].Window with { Shown = false }) is null, "A cloaked Search must not count as open.");
        Program.True(TaskbarStateRules.Opened(foregrounds[3].Window with { Shown = false }) is null, "A hidden Task View must not count as open.");
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true)], start with { Shown = false }, false)),
            TaskbarState.Desktop, TaskbarState.MaximizedWindow, "Start closed (cloaked) while still the foreground window");

        // Task View shows on every display, so every taskbar gets its look wherever its window is.
        ForegroundFacts taskView = foregrounds[3].Window;
        Expect(Evaluate(new(Taskbars, [Window(1, RightDisplay, maximized: true)], taskView, true)),
            TaskbarState.TaskViewOpen, TaskbarState.TaskViewOpen, "Task View open (its window on the left display)");

        // Other display languages translate the title: any title but Alt+Tab's counts, unless Alt is held (Alt+Tab).
        ForegroundFacts translated = new(30, LeftDisplay, @"C:\Windows\explorer.exe", "XamlExplorerHostIslandWindow", "Vista de tareas", EnglishTitles: false);
        Program.True(TaskbarStateRules.Opened(translated) == TaskbarState.TaskViewOpen, "A translated Task View title must still count on a non-English display.");
        Program.True(TaskbarStateRules.Opened(translated with { AltDown = true }) is null, "With Alt held it is Alt+Tab, not Task View.");
        Program.True(TaskbarStateRules.Opened(translated with { Title = "Task Switching" }) is null, "Alt+Tab's English title never counts.");
        Program.True(TaskbarStateRules.Opened(translated with { Shown = false }) is null, "A hidden switcher window never counts.");
        Program.True(TaskbarStateRules.Opened(translated with { EnglishTitles = true }) is null, "On an English display only the title \"Task View\" counts.");
        Program.True(TaskbarStateRules.Opened(translated with { Process = @"C:\Tools\other.exe" }) is null, "Only Explorer's window counts.");

        // Looks: each taskbar its own, accent colour with the look's opacity, and one shared look when they all agree.
        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks = new AppSettings()
            .WithTaskbarState(TaskbarState.Desktop, l => l with { Kind = TaskbarLookKind.Clear, Color = 0x00000000 })
            .WithTaskbarState(TaskbarState.MaximizedWindow, l => l with { Enabled = true, Kind = TaskbarLookKind.Opaque, Color = 0xFF202020 })
            .WithTaskbarState(TaskbarState.StartOpen, l => l with { Enabled = true, Kind = TaskbarLookKind.Acrylic, Color = 0x99000000, UseAccent = true })
            .Normalize().TaskbarStates;
        TaskbarLookState clear = new(TaskbarLookKind.Clear, 0, true), opaque = new(TaskbarLookKind.Opaque, 0xFF202020, true);
        (TaskbarLookState global, TaskbarWindowLook[] perTaskbar) = TaskbarLookService.LooksFor(new(Taskbars, [Window(1, RightDisplay, maximized: true)], null, false), looks, 0x0067C0);
        Program.True(perTaskbar.SequenceEqual([new TaskbarWindowLook(MainTaskbar, clear), new TaskbarWindowLook(SecondTaskbar, opaque)]) && global == clear,
            "Each taskbar must get its own look, and the Desktop look stays the global one when they differ.");
        (global, perTaskbar) = TaskbarLookService.LooksFor(new(Taskbars, [], start, false), looks, 0x0067C0);
        Program.True(perTaskbar[1].Look == new TaskbarLookState(TaskbarLookKind.Acrylic, 0x990067C0, true),
            "The accent colour must keep the look's opacity: " + perTaskbar[1].Look);
        Program.True(TaskbarLookService.Entries(global, perTaskbar).Length == 2, "Taskbars with different looks need their own entries.");

        // Taskbars the helper cannot tell apart show the strongest state's look, not the Desktop look (as version 1.0 did).
        TaskbarLooks maximizedRight = TaskbarLookService.LooksFor(new(Taskbars, [Window(1, RightDisplay, maximized: true)], null, false), looks, 0x0067C0);
        TaskbarWindowLook[] entries = TaskbarLookService.Entries(maximizedRight.Global, maximizedRight.PerTaskbar);
        Program.True(maximizedRight.Strongest == opaque && entries.Length == 2, "The strongest look on any taskbar is the maximized one: " + maximizedRight.Strongest);
        Program.True(TaskbarLookService.GlobalToWrite(maximizedRight, entries, tellsApart: true) == clear,
            "When the helper tells the taskbars apart, the Desktop look stays the global one.");
        Program.True(TaskbarLookService.GlobalToWrite(maximizedRight, entries, tellsApart: false) == opaque,
            "When it cannot, the strongest look becomes the global one.");
        Program.True(TaskbarLookService.GlobalToWrite(maximizedRight, [], tellsApart: false) == maximizedRight.Global,
            "Without per-taskbar looks there is nothing to tell apart.");
        TaskbarLooks startRight = TaskbarLookService.LooksFor(new(Taskbars, [Window(1, RightDisplay, maximized: true)], start, false), looks, 0x0067C0);
        Program.True(startRight.Strongest == new TaskbarLookState(TaskbarLookKind.Acrylic, 0x990067C0, true), "Start open wins over a maximized window: " + startRight.Strongest);
        Program.True(TaskbarLookService.TellsApart(new TaskbarHelperStatus(4, 4, 2, 0), 2) == false
            && TaskbarLookService.TellsApart(new TaskbarHelperStatus(4, 4, 2, 0), 4) == true
            && TaskbarLookService.TellsApart(new TaskbarHelperStatus(0, 0, 2, 0), 0) is null
            && TaskbarLookService.TellsApart(new TaskbarHelperStatus(4, 4, 2, 0), null) is null
            && TaskbarLookService.TellsApart(null, 4) is null, "A report with fewer mapped rectangles than targets means the helper could not tell the taskbars apart.");
        (global, perTaskbar) = TaskbarLookService.LooksFor(new(Taskbars, [Window(1, RightDisplay, maximized: true), Window(2, LeftDisplay, maximized: true)], null, false), looks, 0);
        Program.True(global == opaque, "When every taskbar shows the same look it is also the global look (for taskbars the helper cannot tell apart).");
        Program.True(TaskbarLookService.Entries(global, perTaskbar).Length == 0, "When every taskbar shows the global look no per-taskbar entries are written.");
        (global, perTaskbar) = TaskbarLookService.LooksFor(new(Taskbars, [], null, false), new AppSettings().Normalize().TaskbarStates, 0);
        Program.True(global == TaskbarLookState.Windows && TaskbarLookService.Entries(global, perTaskbar).Length == 0,
            "With the look off, Windows' look is written with no per-taskbar entries.");
        Program.True(TaskbarStateRules.Resolve(new(true, TaskbarLookKind.Default, 0x12345678, true, UseAccent: true), 0x0067C0) == TaskbarLookState.Windows,
            "Windows' own look ignores the colour.");
    }

    public static void HooksOnlyWhileAStateNeedsThem()
    {
        Program.True(TaskbarStateRules.EventsFor(Enabled([])) == WindowEventKinds.None
            && TaskbarStateRules.EventsFor(Enabled([TaskbarState.BatterySaver])) == WindowEventKinds.None, "Desktop and battery saver need no window events.");
        Program.True(TaskbarStateRules.EventsFor(Enabled([TaskbarState.StartOpen])) == WindowEventKinds.Foreground
            && TaskbarStateRules.EventsFor(Enabled([TaskbarState.TaskViewOpen, TaskbarState.SearchOpen])) == WindowEventKinds.Foreground,
            "Start, Search and Task View need only the foreground window.");
        Program.True(TaskbarStateRules.EventsFor(Enabled([TaskbarState.VisibleWindow])) == (WindowEventKinds.Windows | WindowEventKinds.Foreground),
            "Window states need every top-level window.");

        // A service that is never attached: it hooks and unhooks window events, and never reaches Explorer. The helper
        // path only has to exist as a name (nothing is loaded without an attached window).
        string root = Path.Combine(Path.GetTempPath(), "soltex-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppSettingsStore store = new(AppSettingsStore.DefaultPath(root));
        string helperName = Path.Combine(root, "SoltexTaskbar.dll");
        TaskbarLookService service = new(store, root, helperName, windows11: true);
        TaskbarLookService windows10 = new(store, root, helperName, windows11: false), noHelper = new(store, root, null, windows11: true);
        try
        {
            void Expect(WindowEventKinds kinds, int hooks, string what)
            {
                service.UpdateWatching();
                Program.True(service.Watching == kinds && service.WatchHooks == hooks, $"{what}: watching {service.Watching} with {service.WatchHooks} hook(s).");
            }
            Expect(WindowEventKinds.None, 0, "Default settings");
            store.Update(s => s.WithTaskbarState(TaskbarState.Desktop, l => l with { Kind = TaskbarLookKind.Acrylic, Color = 0x66000000 }));
            Expect(WindowEventKinds.None, 0, "Only a Desktop look");
            store.Update(s => s.WithTaskbarState(TaskbarState.BatterySaver, l => l with { Enabled = true }));
            Expect(WindowEventKinds.None, 0, "Battery saver (listened for on the app window, not with window hooks)");
            store.Update(s => s.WithTaskbarState(TaskbarState.StartOpen, l => l with { Enabled = true }));
            // The foreground window, cloaking, and hiding (Task View may hide its window rather than cloak it).
            Expect(WindowEventKinds.Foreground, 3, "Start open");

            // Windows 10, or no helper for this PC: imported or saved looks never hook window events or load the helper.
            foreach ((TaskbarLookService unusable, string what) in new[] { (windows10, "Windows 10"), (noHelper, "No helper for this PC") })
            {
                unusable.UpdateWatching();
                Program.True(unusable.Watching == WindowEventKinds.None && unusable.WatchHooks == 0 && !unusable.WatchingBatterySaver,
                    $"{what}: watching {unusable.Watching} with {unusable.WatchHooks} hook(s).");
                Task apply = unusable.ApplyAsync();
                Program.True(apply.IsCompletedSuccessfully && unusable.Problem is null && unusable.WatchHooks == 0,
                    $"{what}: applying must stop before the helper (problem: {unusable.Problem}).");
            }
            store.Update(s => s.WithTaskbarState(TaskbarState.MaximizedWindow, l => l with { Enabled = true }));
            Expect(WindowEventKinds.Foreground | WindowEventKinds.Windows, 5, "Start open and Window maximized");
            store.Update(s => s.WithTaskbarState(TaskbarState.StartOpen, l => l with { Enabled = false }).WithTaskbarState(TaskbarState.MaximizedWindow, l => l with { Enabled = false }));
            Expect(WindowEventKinds.None, 0, "States switched off again");
            store.Update(s => s.WithTaskbarState(TaskbarState.VisibleWindow, l => l with { Enabled = true }));
            Expect(WindowEventKinds.Foreground | WindowEventKinds.Windows, 5, "Window visible");
            Program.True(!service.WatchingBatterySaver, "Battery saver is listened for only on the attached app window.");
        }
        finally
        {
            service.Dispose();
            windows10.Dispose();
            noHelper.Dispose();
        }
        Program.True(service.WatchHooks == 0 && service.Watching == WindowEventKinds.None, "Disposing must remove every window hook.");
    }

    public static void VersionOneLookIsKept()
    {
        string root = Path.Combine(Path.GetTempPath(), "soltex-state-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "app-settings-v1.json");
        // As version 1.0.0 saved it: every property written, enums by name.
        File.WriteAllText(path, """
            {
              "SchemaVersion": 1,
              "Theme": "Dark",
              "TaskbarLook": "Clear",
              "TaskbarColor": 1073768384,
              "TaskbarLine": false,
              "TaskbarMaximizedLook": "Opaque",
              "TaskbarTweaks": { "clock": "time" },
              "TaskbarScrollVolume": true,
              "DesktopGroups": [],
              "ReportedDrift": null
            }
            """);
        AppSettingsStore store = new(path);
        AppSettings s = store.Current;
        Program.True(store.LoadProblem is null && s.Theme == ThemePreference.Dark && s.TaskbarTweaks["clock"] == "time" && s.TaskbarScrollVolume,
            "A version 1.0 file must load: " + store.LoadProblem);
        Program.True(s.LookFor(TaskbarState.Desktop) == new TaskbarStateLook(true, TaskbarLookKind.Clear, 0x400067C0, false),
            "The 1.0 look must become the Desktop look: " + s.LookFor(TaskbarState.Desktop));
        Program.True(s.LookFor(TaskbarState.MaximizedWindow) == new TaskbarStateLook(true, TaskbarLookKind.Opaque, 0x400067C0, false),
            "The 1.0 maximized look must become the enabled Window maximized look with the same colour and line: " + s.LookFor(TaskbarState.MaximizedWindow));
        Program.True(Enum.GetValues<TaskbarState>().Where(t => t is not (TaskbarState.Desktop or TaskbarState.MaximizedWindow)).All(t => s.LookFor(t) == TaskbarStateLook.DefaultFor(t)),
            "Every other state must be present and off.");

        store.Update(x => x with { ScalePercent = 125 });
        string saved = File.ReadAllText(path);
        Program.True(saved.Contains("\"TaskbarStates\"", StringComparison.Ordinal) && saved.Contains("\"MaximizedWindow\"", StringComparison.Ordinal)
            && !saved.Contains("\"TaskbarLook\"", StringComparison.Ordinal) && !saved.Contains("\"TaskbarColor\"", StringComparison.Ordinal)
            && !saved.Contains("\"TaskbarLine\"", StringComparison.Ordinal) && !saved.Contains("TaskbarMaximizedLook", StringComparison.Ordinal),
            "Saving must write the states by name and drop the 1.0 fields:\n" + saved);
        AppSettingsStore reloaded = new(path);
        Program.True(reloaded.LoadProblem is null && reloaded.Current.LookFor(TaskbarState.MaximizedWindow).Enabled
            && reloaded.Current.LookFor(TaskbarState.Desktop) == s.LookFor(TaskbarState.Desktop), "The converted look must survive a restart.");

        // Version 1.0 ignored the maximized look over Windows' own background: it stays saved, switched off.
        File.WriteAllText(path, """{ "TaskbarLook": "Default", "TaskbarColor": 0, "TaskbarLine": true, "TaskbarMaximizedLook": "Acrylic" }""");
        s = new AppSettingsStore(path).Current;
        Program.True(s.LookFor(TaskbarState.Desktop) == TaskbarStateLook.DefaultFor(TaskbarState.Desktop)
            && s.LookFor(TaskbarState.MaximizedWindow) is { Enabled: false, Kind: TaskbarLookKind.Acrylic }, "A maximized look without effect must stay off.");
        Program.True(!TaskbarLookService.Configured(s), "Windows' own taskbar must stay untouched after the conversion.");

        // A "Windows default" maximized look drew Windows' own taskbar, line included; an unknown look number is Windows default.
        File.WriteAllText(path, """{ "TaskbarLook": "Acrylic", "TaskbarColor": 1711276032, "TaskbarLine": false, "TaskbarMaximizedLook": "Default" }""");
        s = new AppSettingsStore(path).Current;
        Program.True(s.LookFor(TaskbarState.MaximizedWindow) == new TaskbarStateLook(true, TaskbarLookKind.Default, 0x66000000, true),
            "A Windows default maximized look must keep Windows' line: " + s.LookFor(TaskbarState.MaximizedWindow));
        File.WriteAllText(path, """{ "TaskbarLook": 9, "TaskbarMaximizedLook": null }""");
        Program.True(new AppSettingsStore(path).Current.LookFor(TaskbarState.Desktop) == TaskbarStateLook.DefaultFor(TaskbarState.Desktop),
            "An unknown 1.0 look must load as Windows default.");

        // A state a newer version adds is skipped; the rest load.
        File.WriteAllText(path, """
            { "TaskbarStates": {
                "Desktop": { "Enabled": false, "Kind": "Acrylic", "Color": 1711276032, "Line": true, "UseAccent": true },
                "Fullscreen": { "Enabled": true, "Kind": "Clear", "Color": 0, "Line": true },
                "startopen": { "Enabled": true, "Kind": "Blur", "Color": 0, "Line": false } } }
            """);
        AppSettingsStore newer = new(path);
        Program.True(newer.LoadProblem is null && newer.Current.LookFor(TaskbarState.Desktop) == new TaskbarStateLook(true, TaskbarLookKind.Acrylic, 0x66000000, true, true)
            && newer.Current.LookFor(TaskbarState.StartOpen) == new TaskbarStateLook(true, TaskbarLookKind.Blur, 0, false),
            "Unknown state names must be skipped and known ones loaded: " + newer.LoadProblem);

        // A look a newer version adds (or any look this version cannot read) skips only that state; the other states and
        // every other setting still load.
        File.WriteAllText(path, """
            { "Theme": "Dark", "TaskbarScrollVolume": true, "TaskbarStates": {
                "Desktop": { "Enabled": true, "Kind": "Frosted", "Color": 1711276032, "Line": true },
                "SearchOpen": 7,
                "StartOpen": { "Enabled": true, "Kind": "Blur", "Color": 0, "Line": false } } }
            """);
        AppSettingsStore frosted = new(path);
        Program.True(frosted.LoadProblem is null && frosted.Current.Theme == ThemePreference.Dark && frosted.Current.TaskbarScrollVolume
            && frosted.Current.LookFor(TaskbarState.Desktop) == TaskbarStateLook.DefaultFor(TaskbarState.Desktop)
            && frosted.Current.LookFor(TaskbarState.SearchOpen) == TaskbarStateLook.DefaultFor(TaskbarState.SearchOpen)
            && frosted.Current.LookFor(TaskbarState.StartOpen) == new TaskbarStateLook(true, TaskbarLookKind.Blur, 0, false),
            "A look from a newer version must skip only its state: " + frosted.LoadProblem + " " + frosted.Current.LookFor(TaskbarState.Desktop));
        File.WriteAllText(path, """{ "Theme": "Dark", "TaskbarStates": [ 1, 2 ] }""");
        AppSettingsStore listed = new(path);
        Program.True(listed.LoadProblem is null && listed.Current.Theme == ThemePreference.Dark
            && Enum.GetValues<TaskbarState>().All(t => listed.Current.LookFor(t) == TaskbarStateLook.DefaultFor(t)),
            "Unreadable taskbar states must leave the states at their starting looks and keep every other setting: " + listed.LoadProblem);

        // A setup exported by version 1.0 keeps its look on import.
        string profile = Path.Combine(root, "setup.json");
        File.WriteAllText(profile, """
            { "FormatVersion": 2, "App": { "TaskbarLook": "Acrylic", "TaskbarColor": 1711276032, "TaskbarLine": true, "TaskbarMaximizedLook": "Opaque" },
              "WindowsChoices": {}, "SystemItems": [] }
            """);
        AppSettings imported = SettingsProfile.Load(profile).App;
        Program.True(imported.LookFor(TaskbarState.Desktop) == new TaskbarStateLook(true, TaskbarLookKind.Acrylic, 0x66000000, true)
            && imported.LookFor(TaskbarState.MaximizedWindow) == new TaskbarStateLook(true, TaskbarLookKind.Opaque, 0x66000000, true),
            "A 1.0 setup must import with its look.");
    }

    private static WindowFacts Window(nint handle, nint monitor, bool maximized = false, string className = "Notepad") =>
        new(handle, monitor, className, Visible: true, Minimized: false, maximized, Cloaked: false, ToolWindow: false, OnScreen: true, OwnedByThisApp: false);

    private static IReadOnlyDictionary<TaskbarState, TaskbarStateLook> Enabled(IEnumerable<TaskbarState> states)
    {
        AppSettings settings = new();
        foreach (TaskbarState state in states) settings = settings.WithTaskbarState(state, l => l with { Enabled = true });
        return settings.Normalize().TaskbarStates;
    }
}
