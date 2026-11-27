using System.Collections.Frozen;
using System.IO;
using System.Runtime.InteropServices;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

/// <summary>A taskbar window (the main one or a secondary one) and the display it is on.</summary>
internal readonly record struct TaskbarWindowFacts(nint Window, nint Monitor);

/// <summary>
/// What is known about one top-level window: its nearest display, class and state. <see cref="OnScreen"/> is false for
/// an empty window or one that touches no display; <see cref="OwnedByThisApp"/> marks this app's own windows that are not
/// in the taskbar (desktop groups, widgets, the wallpaper, overlays).
/// </summary>
internal readonly record struct WindowFacts(nint Window, nint Monitor, string ClassName, bool Visible, bool Minimized, bool Maximized,
    bool Cloaked, bool ToolWindow, bool OnScreen, bool OwnedByThisApp);

/// <summary>The foreground window: its display, its process's file name, its class and (only for Explorer's switcher windows) its title.</summary>
internal readonly record struct ForegroundFacts(nint Window, nint Monitor, string Process, string ClassName, string Title);

/// <summary>Everything the taskbar states depend on at one moment. Facts that no enabled state needs are left empty.</summary>
internal sealed record DesktopSnapshot(IReadOnlyList<TaskbarWindowFacts> Taskbars, IReadOnlyList<WindowFacts> Windows, ForegroundFacts? Foreground, bool BatterySaver);

/// <summary>Which window events a watcher needs: the foreground window only, or every top-level window too.</summary>
[Flags]
internal enum WindowEventKinds { None = 0, Foreground = 1, Windows = 2 }

/// <summary>
/// Which taskbar state applies where. Pure: everything here works on a <see cref="DesktopSnapshot"/>, so it can be checked
/// without real windows.
/// </summary>
internal static class TaskbarStateRules
{
    /// <summary>The order in which states win when several apply; only enabled states count, and Desktop always applies.</summary>
    public static readonly IReadOnlyList<TaskbarState> Precedence =
    [
        TaskbarState.TaskViewOpen, TaskbarState.StartOpen, TaskbarState.SearchOpen, TaskbarState.BatterySaver,
        TaskbarState.MaximizedWindow, TaskbarState.VisibleWindow, TaskbarState.Desktop
    ];

    /// <summary>Windows that belong to the desktop and taskbar themselves, never counted as an open window.</summary>
    private static readonly FrozenSet<string> ShellClasses =
        new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Titles of Explorer's Task View window. Its window class is shared with Alt+Tab and Snap Assist, so the title tells
    /// them apart. Windows translates the title: on other display languages Task View may not be recognised, and the
    /// taskbar then simply keeps the look of the next state that applies.
    /// </summary>
    private static readonly FrozenSet<string> TaskViewTitles = new[] { "Task View" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The state that wins among <paramref name="active"/>: the first enabled one in <see cref="Precedence"/>.</summary>
    public static TaskbarState Winner(IEnumerable<TaskbarState> active, IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks)
    {
        HashSet<TaskbarState> applying = [.. active];
        foreach (TaskbarState state in Precedence)
            if (state == TaskbarState.Desktop || (applying.Contains(state) && Enabled(looks, state))) return state;
        return TaskbarState.Desktop;
    }

    /// <summary>
    /// True for a window that makes its display count as having a window open: visible, not minimized, not cloaked
    /// (another virtual desktop, a suspended app), not a tool window, on a display, and neither the desktop, a taskbar nor
    /// one of this app's own desktop windows.
    /// </summary>
    public static bool Counts(in WindowFacts window) =>
        window.Visible && !window.Minimized && !window.Cloaked && !window.ToolWindow && window.OnScreen && !window.OwnedByThisApp
        && !ShellClasses.Contains(window.ClassName);

    /// <summary>
    /// The state the foreground window stands for, or null. Start and Search are recognised by their process
    /// (StartMenuExperienceHost.exe; SearchHost.exe, or SearchApp.exe before Windows 11). Task View is recognised by
    /// its window first (Explorer's XamlExplorerHostIslandWindow, or MultitaskingViewFrame on older builds), then by its
    /// title, because Alt+Tab and Snap Assist use the same window class.
    /// </summary>
    public static TaskbarState? Opened(in ForegroundFacts foreground)
    {
        string process = Path.GetFileName(foreground.Process);
        if (process.Equals("StartMenuExperienceHost.exe", StringComparison.OrdinalIgnoreCase)) return TaskbarState.StartOpen;
        if (process.Equals("SearchHost.exe", StringComparison.OrdinalIgnoreCase) || process.Equals("SearchApp.exe", StringComparison.OrdinalIgnoreCase))
            return TaskbarState.SearchOpen;
        if (process.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && IsSwitcherClass(foreground.ClassName) && TaskViewTitles.Contains(foreground.Title))
            return TaskbarState.TaskViewOpen;
        return null;
    }

    /// <summary>Explorer's window classes that may be Task View (the title is needed to be sure).</summary>
    public static bool IsSwitcherClass(string className) =>
        className.StartsWith("XamlExplorerHostIslandWindow", StringComparison.Ordinal) || className == "MultitaskingViewFrame";

    /// <summary>
    /// The winning state for each taskbar. Each taskbar has the states of its own display: a window visible or maximized
    /// there, Start, Search or Task View open there; battery saver applies to every display.
    /// </summary>
    public static IReadOnlyList<(nint Taskbar, TaskbarState State)> Evaluate(DesktopSnapshot snapshot, IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TaskbarState? opened = snapshot.Foreground is { } foreground ? Opened(foreground) : null;
        nint openedOn = snapshot.Foreground?.Monitor ?? 0;
        List<(nint, TaskbarState)> result = new(snapshot.Taskbars.Count);
        foreach (TaskbarWindowFacts taskbar in snapshot.Taskbars)
        {
            List<TaskbarState> active = [TaskbarState.Desktop];
            if (snapshot.BatterySaver) active.Add(TaskbarState.BatterySaver);
            if (opened is { } open && openedOn == taskbar.Monitor) active.Add(open);
            foreach (WindowFacts window in snapshot.Windows)
            {
                if (window.Monitor != taskbar.Monitor || !Counts(window)) continue;
                active.Add(TaskbarState.VisibleWindow);
                if (window.Maximized)
                {
                    active.Add(TaskbarState.MaximizedWindow);
                    break; // nothing more to learn on this display
                }
            }
            result.Add((taskbar.Window, Winner(active, looks)));
        }
        return result;
    }

    /// <summary>
    /// The window events the enabled states need: none for Desktop and battery saver alone, the foreground window for
    /// Start, Search and Task View, every top-level window for Window visible and Window maximized.
    /// </summary>
    public static WindowEventKinds EventsFor(IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks)
    {
        WindowEventKinds kinds = WindowEventKinds.None;
        if (Enabled(looks, TaskbarState.StartOpen) || Enabled(looks, TaskbarState.SearchOpen) || Enabled(looks, TaskbarState.TaskViewOpen))
            kinds |= WindowEventKinds.Foreground;
        if (Enabled(looks, TaskbarState.VisibleWindow) || Enabled(looks, TaskbarState.MaximizedWindow))
            kinds |= WindowEventKinds.Windows | WindowEventKinds.Foreground;
        return kinds;
    }

    /// <summary>
    /// What the helper draws for a state's look. Windows' own background ignores the colour; with the accent colour the
    /// look's alpha is kept and the colour comes from <paramref name="accentRgb"/> (0xRRGGBB).
    /// </summary>
    public static TaskbarLookState Resolve(TaskbarStateLook look, uint accentRgb)
    {
        ArgumentNullException.ThrowIfNull(look);
        if (look.Kind == TaskbarLookKind.Default) return new(TaskbarLookKind.Default, 0, look.Line);
        uint color = look.UseAccent ? (look.Color & 0xFF000000) | (accentRgb & 0xFFFFFF) : look.Color;
        return new(look.Kind, color, look.Line);
    }

    public static bool Enabled(IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks, TaskbarState state) =>
        state == TaskbarState.Desktop || (looks.TryGetValue(state, out TaskbarStateLook? look) && look is { Enabled: true });
}

/// <summary>Reads the facts for <see cref="TaskbarStateRules"/> from Windows. Read-only and quick; called on the UI thread.</summary>
internal static unsafe partial class TaskbarStateProbe
{
    private const uint MonitorDefaultToNull = 0, MonitorDefaultToNearest = 2, Owner = 4, QueryLimitedInformation = 0x1000;
    private const int ExStyle = -20, ToolWindowStyle = 0x80, Cloaked = 14;
    /// <summary>Windows' default blue, used when the accent colour cannot be read.</summary>
    public const uint FallbackAccent = 0x0078D4;

    [ThreadStatic] private static List<nint>? found;

    /// <summary>The main taskbar and every secondary one, each with its display.</summary>
    public static IReadOnlyList<TaskbarWindowFacts> Taskbars()
    {
        List<TaskbarWindowFacts> taskbars = [];
        nint main = TaskbarNative.FindWindowW("Shell_TrayWnd", null);
        if (main != 0) taskbars.Add(new(main, StateNative.MonitorFromWindow(main, MonitorDefaultToNearest)));
        for (nint secondary = 0; (secondary = StateNative.FindWindowExW(0, secondary, "Shell_SecondaryTrayWnd", null)) != 0;)
            taskbars.Add(new(secondary, StateNative.MonitorFromWindow(secondary, MonitorDefaultToNearest)));
        return taskbars;
    }

    /// <summary>The facts the enabled states need; the others stay empty so an unused state costs nothing.</summary>
    public static DesktopSnapshot Capture(bool windows, bool foreground, bool battery) =>
        new(Taskbars(), windows ? Windows() : [], foreground ? Foreground() : null, battery && BatterySaverOn());

    /// <summary>Every visible top-level window, in Z order.</summary>
    public static IReadOnlyList<WindowFacts> Windows()
    {
        List<nint> handles = found = [];
        try
        {
            delegate* unmanaged<nint, nint, int> callback = &OnWindow;
            _ = TaskbarNative.EnumWindows((nint)callback, 0);
        }
        finally { found = null; }
        uint self = (uint)Environment.ProcessId;
        List<WindowFacts> windows = new(handles.Count);
        foreach (nint window in handles)
        {
            int cloaked = 0;
            bool isCloaked = TaskbarNative.DwmGetWindowAttribute(window, Cloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0;
            bool tool = (TaskbarNative.GetWindowLongPtrW(window, ExStyle) & ToolWindowStyle) != 0;
            bool onScreen = StateNative.GetWindowRect(window, out StateNative.WindowRect rect) && rect.Right > rect.Left && rect.Bottom > rect.Top
                && StateNative.MonitorFromWindow(window, MonitorDefaultToNull) != 0;
            _ = TaskbarNative.GetWindowThreadProcessId(window, out uint process);
            bool own = process == self && StateNative.GetWindow(window, Owner) != 0;
            windows.Add(new WindowFacts(window, StateNative.MonitorFromWindow(window, MonitorDefaultToNearest), ClassName(window), Visible: true,
                TaskbarNative.IsIconic(window), TaskbarNative.IsZoomed(window), isCloaked, tool, onScreen, own));
        }
        return windows;
    }

    /// <summary>The foreground window, or null when there is none (for a moment while windows switch).</summary>
    public static ForegroundFacts? Foreground()
    {
        nint window = StateNative.GetForegroundWindow();
        if (window == 0) return null;
        string className = ClassName(window);
        _ = TaskbarNative.GetWindowThreadProcessId(window, out uint process);
        string title = TaskbarStateRules.IsSwitcherClass(className) ? Title(window) : "";
        return new ForegroundFacts(window, StateNative.MonitorFromWindow(window, MonitorDefaultToNearest), ProcessFile(process), className, title);
    }

    /// <summary>True while Windows' battery saver (energy saver) is on.</summary>
    public static bool BatterySaverOn() => StateNative.GetSystemPowerStatus(out StateNative.PowerStatus status) && status.SystemStatusFlag == 1;

    /// <summary>True when any window that would count for a display is maximized (the live wallpaper pauses then).</summary>
    public static bool AnyMaximized() => Windows().Any(w => w.Maximized && TaskbarStateRules.Counts(w));

    /// <summary>Windows' accent colour as 0xRRGGBB, or <see cref="FallbackAccent"/> when it cannot be read.</summary>
    public static uint AccentColor()
    {
        try
        {
            global::Windows.UI.Color accent = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            return (uint)accent.R << 16 | (uint)accent.G << 8 | accent.B;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or TypeLoadException or PlatformNotSupportedException)
        {
            return FallbackAccent;
        }
    }

    private static string ClassName(nint window)
    {
        char* buffer = stackalloc char[256];
        int length = StateNative.GetClassNameW(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    private static string Title(nint window)
    {
        char* buffer = stackalloc char[256];
        int length = StateNative.GetWindowTextW(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>The full path of a process's program, or "" when it cannot be read.</summary>
    private static string ProcessFile(uint process)
    {
        nint handle = StateNative.OpenProcess(QueryLimitedInformation, 0, process);
        if (handle == 0) return "";
        try
        {
            char* buffer = stackalloc char[1024];
            int length = 1024;
            return StateNative.QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? new string(buffer, 0, length) : "";
        }
        finally { _ = StateNative.CloseHandle(handle); }
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint parameter)
    {
        if (TaskbarNative.IsWindowVisible(window)) found?.Add(window);
        return 1;
    }
}

/// <summary>
/// Tells its owner when top-level windows are switched, shown, hidden, minimized, restored, moved or cloaked (no
/// polling). Out-of-context WinEvent hooks, delivered on the thread that created this, and only for the kinds asked
/// for: none at all while <see cref="WindowEventKinds.None"/>.
/// </summary>
internal sealed unsafe class WindowEvents : IDisposable
{
    private const uint OutOfContext = 0, LocationChange = 0x800B, Root = 2;
    private static readonly (uint Min, uint Max)[] ForegroundRanges =
    [
        (0x0003, 0x0003), // EVENT_SYSTEM_FOREGROUND
        (0x8017, 0x8018)  // EVENT_OBJECT_CLOAKED..UNCLOAKED (Start and Search hide by cloaking)
    ];
    private static readonly (uint Min, uint Max)[] WindowRanges =
    [
        (0x0016, 0x0017), // EVENT_SYSTEM_MINIMIZESTART..MINIMIZEEND
        (0x8002, 0x8003), // EVENT_OBJECT_SHOW..HIDE
        (LocationChange, LocationChange)
    ];
    private static readonly List<WindowEvents> watchers = []; // UI thread only
    private readonly List<nint> hooks = [];
    private readonly Action changed;

    public WindowEvents(Action changed, WindowEventKinds kinds = WindowEventKinds.Windows | WindowEventKinds.Foreground)
    {
        this.changed = changed;
        Watch(kinds);
    }

    public WindowEventKinds Kinds { get; private set; }

    /// <summary>How many WinEvent hooks are installed right now.</summary>
    public int HookCount => hooks.Count;

    /// <summary>Installs the hooks <paramref name="kinds"/> needs and removes the rest.</summary>
    public void Watch(WindowEventKinds kinds)
    {
        if (kinds == Kinds && (kinds == WindowEventKinds.None) == (hooks.Count == 0)) return;
        Unhook();
        Kinds = kinds;
        if (kinds == WindowEventKinds.None) return;
        watchers.Add(this);
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &OnEvent;
        IEnumerable<(uint, uint)> ranges = (kinds.HasFlag(WindowEventKinds.Foreground) ? ForegroundRanges : [])
            .Concat(kinds.HasFlag(WindowEventKinds.Windows) ? WindowRanges : []);
        foreach ((uint min, uint max) in ranges)
        {
            nint hook = TaskbarNative.SetWinEventHook(min, max, 0, (nint)callback, 0, 0, OutOfContext);
            if (hook != 0) hooks.Add(hook);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (window == 0 || objectId != 0 || childId != 0) return; // whole windows only (OBJID_WINDOW, CHILDID_SELF)
        // Top-level windows only. A window already gone (an out-of-context event arrives later) still counts: it was
        // probably closed.
        nint root = StateNative.GetAncestor(window, Root);
        if (root != 0 && root != window) return;
        foreach (WindowEvents watcher in watchers.ToArray())
        {
            if (!watcher.hooks.Contains(hook)) continue;
            try { watcher.changed(); }
            catch (InvalidOperationException) { }
        }
    }

    private void Unhook()
    {
        foreach (nint hook in hooks) _ = TaskbarNative.UnhookWinEvent(hook);
        hooks.Clear();
        watchers.Remove(this);
    }

    public void Dispose()
    {
        Unhook();
        Kinds = WindowEventKinds.None;
    }
}

internal static unsafe partial class StateNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    /// <summary>GUID_POWER_SAVING_STATUS: battery saver turned on or off.</summary>
    public static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowExW(nint parent, nint after, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint window, out WindowRect rect);

    [LibraryImport("user32.dll")]
    internal static partial int GetClassNameW(nint window, char* name, int length);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextW(nint window, char* text, int length);

    [LibraryImport("kernel32.dll")]
    internal static partial nint OpenProcess(uint access, int inherit, uint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref int length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("user32.dll")]
    internal static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterPowerSettingNotification(nint notification);
}
