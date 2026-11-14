using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

/// <summary>What the taskbar helper should draw right now.</summary>
internal readonly record struct TaskbarLookState(TaskbarLookKind Kind, uint Color, bool Line)
{
    public static TaskbarLookState Windows => new(TaskbarLookKind.Default, 0, true);
    public bool IsWindowsDefault => Kind == TaskbarLookKind.Default && Line;
}

/// <summary>
/// The helper's own report, written after every pass: background rectangles found and restyled, and elements matched by
/// style rules and styled.
/// </summary>
internal sealed record TaskbarHelperStatus(int Targets, int Applied, int Look, int Result, int Styled = 0, int StyleApplied = 0, int StyleResult = 0, int Request = 0);

/// <summary>
/// One style rule for the helper (Windhawk Taskbar Styler-class): every element of <paramref name="Type"/> and/or named
/// <paramref name="Name"/>, inside an element of type <paramref name="Under"/> when given, gets <paramref name="Property"/>
/// ("Owner.Property", e.g. "FrameworkElement.Margin") set to <paramref name="Value"/> (XAML text). The helper keeps the
/// previous value and puts it back when the rule goes away.
/// </summary>
public sealed record TaskbarStyleRule(string Under, string Type, string Name, string Property, string Value)
{
    internal string Encode()
    {
        string[] fields = [Under, Type, Name, Property, Value];
        if (fields.Any(f => f.Contains('|', StringComparison.Ordinal) || f.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException("A taskbar style rule cannot contain '|'.");
        if (Type.Length == 0 && Name.Length == 0) throw new ArgumentException("A taskbar style rule needs a type or a name.");
        return string.Join('|', fields);
    }
}

/// <summary>
/// Loads the native taskbar helper (SoltexTaskbar.dll, built from native/Soltex.TaskbarStyle) into a XAML process
/// (Explorer, or the test host) through Windows' XAML diagnostics API, and passes it settings through
/// HKCU\{key} and the named event {eventBase}-{build}.{pid}, where build identifies the helper's exact bytes.
/// A loaded helper stays in Explorer until Explorer restarts, so each build has its own event: before loading a new
/// build, older builds still loaded are set back to Windows' look with no rules (<see cref="RetireOthersAsync"/>).
/// </summary>
internal sealed class TaskbarHelper(string key, string eventBase, string stagingDirectory, string? logPath, string? helperPath)
{
    public static readonly Guid HelperClassId = new("053df8ec-d4d0-4868-bc22-f1eefc050065");
    public const string ExplorerKey = @"Software\Soltex\Taskbar", ExplorerEvent = @"Local\SoltexTaskbar2";
    private const string FirstHelperEvent = @"Local\SoltexTaskbar"; // version 1 used one event name for every build
    private const int GetMessageHook = 3, NullMessage = 0;
    private string? build;
    private int request;

    /// <summary>Optional file for a one-time list of the visual tree (types and names) when the helper attaches.</summary>
    public string? DumpPath { get; init; }

    /// <summary>The first 16 hex digits of the helper's SHA-256 (its staged file name and event suffix).</summary>
    private string Build => build ??= helperPath is null ? "none" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helperPath)))[..16].ToLowerInvariant();

    /// <summary>
    /// True when this app can load a helper that also runs inside Explorer. The app loads the helper into itself to find
    /// its hook, so the helper must match both the app and Explorer: an x64 app emulated on an ARM64 PC can load neither
    /// the ARM64 helper Explorer needs nor run the x64 one inside ARM64 Explorer.
    /// </summary>
    public static bool ArchitectureSupported =>
        RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64;

    /// <summary>The helper shipped next to the app for this PC's architecture, or null if missing or unusable here.</summary>
    public static string? Bundled()
    {
        if (!ArchitectureSupported) return null;
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string path = Path.Combine(AppContext.BaseDirectory, "TaskbarHelper", arch, "SoltexTaskbar.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Explorer's process and the thread that owns the taskbar (its XAML thread), or zeros.</summary>
    public static (int Process, int Thread) ExplorerTaskbar()
    {
        nint tray = TaskbarNative.FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return (0, 0);
        uint thread = TaskbarNative.GetWindowThreadProcessId(tray, out uint pid);
        return ((int)pid, (int)thread);
    }

    public static int ExplorerProcessId() => ExplorerTaskbar().Process;

    public bool IsLoaded(int pid) => Exists(EventName(pid));

    public void Signal(int pid) => Set(EventName(pid));

    private static bool Exists(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? handle)) return false;
        handle.Dispose();
        return true;
    }

    private static void Set(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out EventWaitHandle? handle)) return;
        using (handle) handle.Set();
    }

    /// <summary>
    /// Sets any other helper build still loaded in <paramref name="pid"/> back to Windows' look with no style rules and
    /// waits for its report, so it never fights the build about to load. Leaves the settings at Windows default.
    /// </summary>
    public async Task RetireOthersAsync(int pid, CancellationToken cancellationToken = default)
    {
        string suffix = "." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        List<string> events = [FirstHelperEvent + suffix];
        if (Directory.Exists(stagingDirectory))
            events.AddRange(Directory.GetFiles(stagingDirectory, "SoltexTaskbar-*.dll")
                .Select(f => Path.GetFileNameWithoutExtension(f)["SoltexTaskbar-".Length..])
                .Where(b => b != Build).Select(b => eventBase + "-" + b + suffix));
        foreach (string name in events.Where(Exists))
        {
            Write(TaskbarLookState.Windows);
            Set(name);
            _ = await ConfirmAsync(TaskbarLookState.Windows, anyReport: true, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Saves what the helper should draw and which style rules it should apply, and clears its last report.</summary>
    public void Write(TaskbarLookState state, IReadOnlyList<TaskbarStyleRule>? rules = null)
    {
        string[] styles = [.. (rules ?? []).Select(r => r.Encode())];
        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(key);
        settings.SetValue("Look", (int)state.Kind, RegistryValueKind.DWord);
        settings.SetValue("Color", unchecked((int)state.Color), RegistryValueKind.DWord);
        settings.SetValue("Line", state.Line ? 1 : 0, RegistryValueKind.DWord);
        if (styles.Length > 0) settings.SetValue("Styles", styles, RegistryValueKind.MultiString);
        else settings.DeleteValue("Styles", throwOnMissingValue: false);
        // A new number per change; the helper echoes it in the report that covers this change.
        request = Math.Max(request + 1, (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond % int.MaxValue));
        settings.SetValue("Request", request, RegistryValueKind.DWord);
        settings.DeleteValue("Status", throwOnMissingValue: false);
    }

    public TaskbarHelperStatus? ReadStatus()
    {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(key);
        if (settings?.GetValue("Status") is not string text || text.Length == 0) return null;
        Dictionary<string, string> parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        static int Number(Dictionary<string, string> parts, string name) =>
            parts.TryGetValue(name, out string? value) && int.TryParse(value, out int number) ? number : 0;
        static int Code(Dictionary<string, string> parts, string name) =>
            parts.TryGetValue(name, out string? hr) && hr.StartsWith("0x", StringComparison.Ordinal) ? unchecked((int)Convert.ToUInt32(hr[2..], 16)) : 0;
        return new TaskbarHelperStatus(Number(parts, "targets"), Number(parts, "applied"), Number(parts, "look"), Code(parts, "hr"),
            Number(parts, "styled"), Number(parts, "styleApplied"), Code(parts, "styleHr"), Number(parts, "request"));
    }

    /// <summary>
    /// Loads the helper into <paramref name="pid"/> and waits until it is ready. Returns a problem or null.
    /// A message hook on the target's XAML thread (<paramref name="thread"/>) brings the DLL in for a moment; on that
    /// thread the DLL asks the process's own XAML to attach it (the way Windhawk's taskbar styler attaches), then the
    /// hook is removed. The DLL keeps itself loaded.
    /// </summary>
    public async Task<string?> LoadAsync(int pid, int thread, CancellationToken cancellationToken = default)
    {
        if (IsLoaded(pid)) return null;
        if (helperPath is null) return "The taskbar helper is missing from this copy of the app.";
        string staged = Stage(helperPath);
        string data = "key=" + key + "|event=" + eventBase + "-" + Build + (logPath is null ? "" : "|log=" + logPath) + (DumpPath is null ? "" : "|dump=" + DumpPath);
        byte[] payload = System.Text.Encoding.Unicode.GetBytes(data + "\0");
        using System.IO.MemoryMappedFiles.MemoryMappedFile map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(
            @"Local\SoltexTaskbarInit." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), payload.Length);
        using (System.IO.MemoryMappedFiles.MemoryMappedViewAccessor view = map.CreateViewAccessor()) view.WriteArray(0, payload, 0, payload.Length);

        nint module = NativeLibrary.Load(staged);
        try
        {
            nint procedure = NativeLibrary.GetExport(module, "SoltexTaskbarHook");
            string? problem = "The taskbar helper did not start.";
            // Right after Explorer starts its XAML is not ready yet; the DLL reports that and the next attempt retries.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                ClearStatus();
                nint hook = TaskbarNative.SetWindowsHookExW(GetMessageHook, procedure, module, (uint)thread);
                if (hook == 0) return $"Windows did not let the taskbar helper start (error {Marshal.GetLastPInvokeError()}).";
                try
                {
                    _ = TaskbarNative.PostThreadMessageW((uint)thread, NullMessage, 0, 0);
                    for (int wait = 0; wait < 20; wait++)
                    {
                        if (IsLoaded(pid)) return null;
                        if (ReadStatus() is { Result: < 0 } failed) { problem = $"Windows did not let the taskbar helper start (0x{failed.Result:X8})."; break; }
                        await Task.Delay(100, cancellationToken).ConfigureAwait(true);
                    }
                }
                finally { _ = TaskbarNative.UnhookWindowsHookEx(hook); }
                await Task.Delay(500, cancellationToken).ConfigureAwait(true);
            }
            return problem;
        }
        finally { NativeLibrary.Free(module); } // this app's copy only; the target keeps its own
    }

    private void ClearStatus()
    {
        using RegistryKey? settings = Registry.CurrentUser.OpenSubKey(key, writable: true);
        settings?.DeleteValue("Status", throwOnMissingValue: false);
    }

    /// <summary>
    /// Waits for the helper's report on the last settings written. Returns a problem or null. A helper from before
    /// request numbers (version 1) is accepted on any report (<paramref name="anyReport"/>).
    /// </summary>
    public async Task<string?> ConfirmAsync(TaskbarLookState state, bool anyReport = false, CancellationToken cancellationToken = default)
    {
        (string? look, string? styles) = await ConfirmPartsAsync(state, anyReport, cancellationToken).ConfigureAwait(true);
        return look ?? styles;
    }

    /// <summary>
    /// <see cref="ConfirmAsync"/> with the problems kept apart: <c>Look</c> for the background and the helper itself
    /// (including no answer), <c>Styles</c> for style rules that could not be applied.
    /// </summary>
    public async Task<(string? Look, string? Styles)> ConfirmPartsAsync(TaskbarLookState state, bool anyReport = false, CancellationToken cancellationToken = default)
    {
        for (int wait = 0; wait < 40; wait++)
        {
            if (ReadStatus() is { } status && (anyReport || status.Request == request))
            {
                string? look = status.Result < 0 ? $"The taskbar could not be changed (0x{status.Result:X8})."
                    : status.Targets == 0 && !state.IsWindowsDefault ? "This version of the Windows taskbar was not recognised." : null;
                string? styles = status.StyleResult < 0 ? $"Some taskbar options could not be applied on this version of Windows (0x{status.StyleResult:X8})." : null;
                return (look, styles);
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);
        }
        return ("The taskbar helper did not answer.", null);
    }

    private string EventName(int pid) => eventBase + "-" + Build + "." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Copies the helper to a content-named file in the app's data folder. Explorer keeps the file it loaded until it
    /// restarts, so updates never have to overwrite a file in use.
    /// </summary>
    private string Stage(string source)
    {
        byte[] bytes = File.ReadAllBytes(source);
        string name = "SoltexTaskbar-" + Build + ".dll";
        Directory.CreateDirectory(stagingDirectory);
        string target = Path.Combine(stagingDirectory, name);
        if (!File.Exists(target) || !SHA256.HashData(File.ReadAllBytes(target)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
        {
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        foreach (string old in Directory.GetFiles(stagingDirectory, "SoltexTaskbar-*.dll"))
        {
            if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(old); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // still loaded by Explorer
        }
        return target;
    }
}

/// <summary>
/// The taskbar look (Taskbar page): clear, solid, acrylic or blurred backgrounds, an optional different look while a
/// window is maximized, and the top line. Applies on start and after Explorer restarts; watches window states only
/// when the maximized look differs.
/// </summary>
public sealed class TaskbarLookService : IDisposable
{
    private readonly AppSettingsStore settings;
    private readonly TaskbarHelper helper;
    private readonly string? bundled = TaskbarHelper.Bundled();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new(); // cancelled on Dispose: no helper work after the app quits
    private readonly TaskbarScrollVolume scrollVolume = new();
    private (TaskbarLookKind, uint, bool, TaskbarLookKind?, string, bool) applied;
    private HwndSource? source;
    private uint taskbarCreated;
    private MaximizedWatcher? watcher;
    private DispatcherTimer? settle;
    private bool maximized, attached, disposed;

    public TaskbarLookService(AppSettingsStore settings, string dataRoot)
    {
        this.settings = settings;
        string directory = Path.Combine(dataRoot, "Taskbar");
        helper = new TaskbarHelper(TaskbarHelper.ExplorerKey, TaskbarHelper.ExplorerEvent, directory, Path.Combine(directory, "helper.log"), bundled);
        applied = Snapshot(settings.Current);
        settings.Changed += next =>
        {
            if (!attached || Snapshot(next) == applied) return;
            Application.Current?.Dispatcher.InvokeAsync(() => _ = ApplyAsync());
        };
    }

    /// <summary>Plain-language reason the last change to the look (or the helper as a whole) did not take effect, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Plain-language reason some taskbar details (style rules) could not be applied, or null.</summary>
    public string? DetailsProblem { get; private set; }

    public bool Available => bundled is not null;

    /// <summary>Why the look and details cannot be used in this copy of the app on this PC, or null when they can.</summary>
    public string? Unavailable => bundled is not null ? null
        : TaskbarHelper.ArchitectureSupported ? "Not included in this copy of the app." : "Not available on this PC.";

    /// <summary>Set when Windows refused the scroll-for-volume hook.</summary>
    public string? ScrollVolumeProblem { get; private set; }

    /// <summary>
    /// True when the details are the only thing the helper changes (Windows' background and line, at least one detail),
    /// so a <see cref="Problem"/> belongs next to the details rather than the background.
    /// </summary>
    public static bool OnlyDetails(AppSettings s) => s.TaskbarLook == TaskbarLookKind.Default && s.TaskbarLine && s.TaskbarTweaks.Count > 0;

    /// <summary>Raised on the UI thread after each apply.</summary>
    public event Action? Changed;

    public void Attach(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        taskbarCreated = TaskbarNative.RegisterWindowMessageW("TaskbarCreated");
        source?.AddHook(Hook);
        attached = true;
        UpdateScrollVolume();
        if (!Nothing() || TaskbarHelper.ExplorerProcessId() is int pid && pid != 0 && helper.IsLoaded(pid))
            _ = ApplyAsync();
        else if (ScrollVolumeProblem is not null)
            Changed?.Invoke(); // no apply follows to report it
    }

    public async Task ApplyAsync(bool confirm = true)
    {
        if (disposed) return;
        await gate.WaitAsync().ConfigureAwait(true);
        CancellationToken cancel = lifetime.Token;
        try
        {
            if (disposed) return;
            applied = Snapshot(settings.Current);
            UpdateWatcher();
            UpdateScrollVolume();
            TaskbarLookState state = Effective();
            IReadOnlyList<TaskbarStyleRule> rules = TaskbarTweaks.RulesFor(settings.Current.TaskbarTweaks);
            bool nothing = state.IsWindowsDefault && rules.Count == 0;
            (int pid, int thread) = TaskbarHelper.ExplorerTaskbar();
            bool loaded = pid != 0 && helper.IsLoaded(pid);
            if (pid != 0 && !loaded && !nothing) await helper.RetireOthersAsync(pid, cancel).ConfigureAwait(true);
            cancel.ThrowIfCancellationRequested();
            helper.Write(state, rules);
            string? problem = null, details = null;
            if (pid == 0) problem = nothing ? null : "The taskbar isn't running.";
            else if (loaded) helper.Signal(pid);
            else if (!nothing) problem = await helper.LoadAsync(pid, thread, cancel).ConfigureAwait(true);
            if (problem is null && confirm && pid != 0 && helper.IsLoaded(pid))
                (problem, details) = await helper.ConfirmPartsAsync(state, cancellationToken: cancel).ConfigureAwait(true);
            Problem = problem;
            // An unconfirmed pass (a window was maximized or restored) leaves the details' last report in place.
            if (confirm || problem is not null) DetailsProblem = details;
        }
        catch (OperationCanceledException) when (disposed)
        {
            return; // the app is quitting
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException)
        {
            Problem = "The taskbar could not be changed: " + error.Message;
            DetailsProblem = null;
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke();
    }

    private static (TaskbarLookKind, uint, bool, TaskbarLookKind?, string, bool) Snapshot(AppSettings s) =>
        (s.TaskbarLook, s.TaskbarColor, s.TaskbarLine, s.TaskbarMaximizedLook,
         string.Join(";", s.TaskbarTweaks.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value)), s.TaskbarScrollVolume);

    /// <summary>True when Windows' own taskbar is wanted: no look and no details.</summary>
    private bool Nothing() => TaskbarLookState.Windows.Equals(Effective()) && TaskbarTweaks.RulesFor(settings.Current.TaskbarTweaks).Count == 0;

    private void UpdateScrollVolume()
    {
        bool wanted = settings.Current.TaskbarScrollVolume;
        ScrollVolumeProblem = scrollVolume.SetEnabled(wanted) ? null : "Windows did not allow the scroll-for-volume hook.";
    }

    /// <summary>
    /// The look for maximized windows, or null when there is none. It only applies on top of a chosen background: with
    /// Windows default the page hides that choice, so it stays saved (for when a look is picked again) but has no effect.
    /// </summary>
    internal static TaskbarLookKind? MaximizedLook(AppSettings s) => s.TaskbarLook == TaskbarLookKind.Default ? null : s.TaskbarMaximizedLook;

    private TaskbarLookState Effective()
    {
        AppSettings s = settings.Current;
        if (maximized && MaximizedLook(s) is { } look)
            return look == TaskbarLookKind.Default ? TaskbarLookState.Windows : new(look, s.TaskbarColor, s.TaskbarLine);
        return new(s.TaskbarLook, s.TaskbarColor, s.TaskbarLine);
    }

    private void UpdateWatcher()
    {
        AppSettings s = settings.Current;
        bool wanted = MaximizedLook(s) is { } look && look != s.TaskbarLook;
        if (wanted && watcher is null)
        {
            settle = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Settle(), Dispatcher.CurrentDispatcher);
            watcher = new MaximizedWatcher(() => { settle?.Stop(); settle?.Start(); });
            maximized = MaximizedWatcher.AnyMaximized();
        }
        else if (!wanted && watcher is not null)
        {
            watcher.Dispose();
            watcher = null;
            settle?.Stop();
            maximized = false;
        }
    }

    private void Settle()
    {
        settle?.Stop();
        bool now = MaximizedWatcher.AnyMaximized();
        if (now == maximized) return;
        maximized = now;
        _ = ApplyAsync(confirm: false);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer restarted: the new taskbar needs the helper again.
        if (taskbarCreated != 0 && message == (int)taskbarCreated && !Nothing())
            _ = ApplyAsync();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); // an apply still waiting on the helper stops without loading or writing anything more
        settle?.Stop();
        LeaveMaximizedLook();
        source?.RemoveHook(Hook);
        watcher?.Dispose();
        scrollVolume.Dispose();
    }

    /// <summary>
    /// The maximized look lasts only while the app watches windows. If it is showing when the app quits, the chosen look
    /// goes back (without waiting for the helper's report), so the taskbar does not stay in it after the window is
    /// restored. The chosen look and details themselves stay, like any other saved setting.
    /// </summary>
    private void LeaveMaximizedLook()
    {
        if (watcher is null || !maximized) return;
        maximized = false;
        try
        {
            int pid = TaskbarHelper.ExplorerProcessId();
            if (pid == 0 || !helper.IsLoaded(pid)) return;
            helper.Write(Effective(), TaskbarTweaks.RulesFor(settings.Current.TaskbarTweaks));
            helper.Signal(pid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Quitting: the next start puts the right look back.
        }
    }
}

/// <summary>Tells the taskbar service when windows are maximized, restored, moved, shown, hidden or switched (no polling).</summary>
internal sealed unsafe class MaximizedWatcher : IDisposable
{
    private const uint OutOfContext = 0;
    private static readonly (uint Min, uint Max)[] Ranges =
    [
        (0x0003, 0x0003), // EVENT_SYSTEM_FOREGROUND
        (0x0016, 0x0017), // EVENT_SYSTEM_MINIMIZESTART..MINIMIZEEND
        (0x8001, 0x8003), // EVENT_OBJECT_DESTROY..HIDE
        (0x800B, 0x800B), // EVENT_OBJECT_LOCATIONCHANGE
        (0x8017, 0x8018)  // EVENT_OBJECT_CLOAKED..UNCLOAKED
    ];
    private static readonly List<MaximizedWatcher> watchers = []; // UI thread only
    private static bool found;
    private readonly List<nint> hooks = [];
    private readonly Action changed;

    public MaximizedWatcher(Action changed)
    {
        this.changed = changed;
        watchers.Add(this);
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &OnEvent;
        foreach ((uint min, uint max) in Ranges)
        {
            nint hook = TaskbarNative.SetWinEventHook(min, max, 0, (nint)callback, 0, 0, OutOfContext);
            if (hook != 0) hooks.Add(hook);
        }
    }

    /// <summary>True when any visible, uncloaked, ordinary window is maximized.</summary>
    public static bool AnyMaximized()
    {
        found = false;
        delegate* unmanaged<nint, nint, int> callback = &OnWindow;
        _ = TaskbarNative.EnumWindows((nint)callback, 0);
        return found;
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (window == 0 || objectId != 0 || childId != 0) return; // whole windows only (OBJID_WINDOW, CHILDID_SELF)
        foreach (MaximizedWatcher watcher in watchers.ToArray())
        {
            try { watcher.changed(); }
            catch (InvalidOperationException) { }
        }
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint window, nint parameter)
    {
        const int ToolWindow = 0x00000080, ExStyle = -20, Cloaked = 14;
        if (!TaskbarNative.IsWindowVisible(window) || !TaskbarNative.IsZoomed(window) || TaskbarNative.IsIconic(window)) return 1;
        if ((TaskbarNative.GetWindowLongPtrW(window, ExStyle) & ToolWindow) != 0) return 1;
        int cloaked = 0;
        if (TaskbarNative.DwmGetWindowAttribute(window, Cloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0) return 1;
        found = true;
        return 0;
    }

    public void Dispose()
    {
        foreach (nint hook in hooks) _ = TaskbarNative.UnhookWinEvent(hook);
        hooks.Clear();
        watchers.Remove(this);
    }
}

internal static unsafe partial class TaskbarNative
{
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int hookType, nint procedure, nint module, uint thread);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module, nint callback, uint process, uint thread, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(nint callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
}
