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
/// The helper's own report, written after every pass: background rectangles found and restyled, elements matched by
/// style rules and styled, and whether it showed the resting look because the app that wrote the settings has ended.
/// </summary>
internal sealed record TaskbarHelperStatus(int Targets, int Applied, int Look, int Result, int Styled = 0, int StyleApplied = 0, int StyleResult = 0, int Request = 0,
    bool Resting = false);

/// <summary>
/// Each taskbar's look now, the look for a taskbar the helper cannot tell apart (<see cref="Global"/>: the look every
/// taskbar shows when they agree, otherwise the Desktop look), and the look of the strongest state on any taskbar
/// (<see cref="Strongest"/>), which every taskbar shows when the helper cannot tell them apart.
/// </summary>
internal readonly record struct TaskbarLooks(TaskbarLookState Global, TaskbarWindowLook[] PerTaskbar, TaskbarLookState Strongest)
{
    public void Deconstruct(out TaskbarLookState global, out TaskbarWindowLook[] perTaskbar)
    {
        global = Global;
        perTaskbar = PerTaskbar;
    }
}

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
/// The helper also follows the app process that wrote the settings (<see cref="Owner"/>): when it ends, even by a crash,
/// the helper shows the <see cref="Resting"/> look with no per-taskbar looks.
/// </summary>
internal sealed partial class TaskbarHelper(string key, string eventBase, string stagingDirectory, string? logPath, string? helperPath)
{
    public static readonly Guid HelperClassId = new("053df8ec-d4d0-4868-bc22-f1eefc050065");
    public const string ExplorerKey = @"Software\Soltex\Taskbar", ExplorerEvent = @"Local\SoltexTaskbar2";
    private const string FirstHelperEvent = @"Local\SoltexTaskbar"; // version 1 used one event name for every build
    private const int GetMessageHook = 3, NullMessage = 0;
    private string? build;
    private int request;

    /// <summary>Optional file for a one-time list of the visual tree (types and names) when the helper attaches.</summary>
    public string? DumpPath { get; init; }

    /// <summary>
    /// The app process the helper follows (this one unless a test names another). When it ends, quitting or crashing, the
    /// helper shows <see cref="Resting"/> with no per-taskbar looks until the settings name another process.
    /// </summary>
    public int Owner { get; init; } = Environment.ProcessId;

    /// <summary>The look the helper falls back to once <see cref="Owner"/> has ended (the Desktop look). Written with every change.</summary>
    public TaskbarLookState Resting { get; set; } = TaskbarLookState.Windows;

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
            Write(TaskbarLookState.Windows, null, []); // also clears any per-taskbar entries
            Set(name);
            _ = await ConfirmAsync(TaskbarLookState.Windows, anyReport: true, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Saves what the helper should draw and which style rules it should apply, the process it follows and the look to
    /// rest at without it, and clears its last report.
    /// </summary>
    public void Write(TaskbarLookState state, IReadOnlyList<TaskbarStyleRule>? rules = null)
    {
        string[] styles = [.. (rules ?? []).Select(r => r.Encode())];
        using RegistryKey settings = Registry.CurrentUser.CreateSubKey(key);
        settings.SetValue("Owner", Owner, RegistryValueKind.DWord);
        // The start time tells the helper whether the process number still belongs to this app (0: not checked).
        settings.SetValue("OwnerStart", ownerStart ??= StartTime(Owner), RegistryValueKind.QWord);
        settings.SetValue("RestLook", (int)Resting.Kind, RegistryValueKind.DWord);
        settings.SetValue("RestColor", unchecked((int)Resting.Color), RegistryValueKind.DWord);
        settings.SetValue("RestLine", Resting.Line ? 1 : 0, RegistryValueKind.DWord);
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

    private long? ownerStart;

    /// <summary>A process's creation time as a FILETIME (what the helper compares), or 0 when it cannot be read.</summary>
    internal static long StartTime(int process)
    {
        nint handle = StateNative.OpenProcess(0x1000, 0, (uint)process); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == 0) return 0;
        try { return StateNative.GetProcessTimes(handle, out long created, out _, out _, out _) ? created : 0; }
        finally { _ = StateNative.CloseHandle(handle); }
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
            Number(parts, "styled"), Number(parts, "styleApplied"), Code(parts, "styleHr"), Number(parts, "request"), Number(parts, "resting") == 1);
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
        string data = "key=" + key + "|event=" + eventBase + "-" + Build + (logPath is null ? "" : "|log=" + logPath) + (DumpPath is null ? "" : "|dump=" + DumpPath)
            + (TaskbarClasses is null ? "" : "|taskbarClasses=" + TaskbarClasses) + (MatchBySize ? "|match=size" : "");
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
/// The taskbar look (Taskbar page): a look per taskbar state (TranslucentTB-class) on every taskbar, the line above it,
/// and the details' style rules. Applies on start and after Explorer restarts. Windows are watched only while a state
/// that depends on them is enabled, and battery saver only while its state is: with only the Desktop look, nothing is
/// watched at all. On Windows 10, or without a helper for this PC, nothing is watched or loaded whatever is saved.
/// </summary>
public sealed class TaskbarLookService : IDisposable
{
    private const int PowerBroadcast = 0x0218, SettingChange = 0x001A, DisplayChange = 0x007E, ColorizationChanged = 0x0320, PowerSettingChange = 0x8013;
    private readonly AppSettingsStore settings;
    private readonly TaskbarHelper helper;
    private readonly string? bundled;
    private readonly bool usable; // Windows 11 (the XAML taskbar) and a helper for this PC
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new(); // cancelled on Dispose: no helper work after the app quits
    private readonly TaskbarScrollVolume scrollVolume = new();
    private readonly WindowEvents events;
    private readonly DispatcherTimer settle;
    private string applied;
    private Drawn? drawn;
    private HwndSource? source;
    private uint taskbarCreated, accent = TaskbarStateProbe.FallbackAccent;
    private nint powerNotification;
    private bool attached, disposed, accentKnown;
    // Whether the helper told the taskbars apart (it reports how many background rectangles showed their own look), and
    // for which taskbars and Explorer that was last checked.
    private bool apart = true;
    private string? apartFor;

    /// <summary>What the helper was last told to draw, so a window change writes only when a look actually changes.</summary>
    private sealed record Drawn(TaskbarLookState Global, TaskbarWindowLook[] PerTaskbar, string Rules, int Explorer)
    {
        public bool Same(Drawn? other) => other is not null && Global == other.Global && Rules == other.Rules && Explorer == other.Explorer
            && PerTaskbar.SequenceEqual(other.PerTaskbar);
    }

    public TaskbarLookService(AppSettingsStore settings, string dataRoot)
        : this(settings, dataRoot, TaskbarHelper.Bundled(), Environment.OSVersion.Version.Build >= 22000)
    {
    }

    /// <summary>
    /// <paramref name="helperPath"/> is the helper for this PC (null: none); <paramref name="windows11"/> says whether the
    /// taskbar is Windows 11's XAML one. Unless both hold, nothing is watched or loaded.
    /// </summary>
    internal TaskbarLookService(AppSettingsStore settings, string dataRoot, string? helperPath, bool windows11)
    {
        this.settings = settings;
        bundled = helperPath;
        usable = windows11 && helperPath is not null;
        string directory = Path.Combine(dataRoot, "Taskbar");
        helper = new TaskbarHelper(TaskbarHelper.ExplorerKey, TaskbarHelper.ExplorerEvent, directory, Path.Combine(directory, "helper.log"), bundled);
        applied = Key(settings.Current);
        // Window events come in bursts (a drag moves a window many times a second): the states are worked out at most
        // every 100 ms, and only while something is watched.
        settle = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Settle(), Dispatcher.CurrentDispatcher);
        settle.Stop();
        events = new WindowEvents(StateEvent, WindowEventKinds.None);
        settings.Changed += next =>
        {
            if (!attached || Key(next) == applied) return;
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

    /// <summary>The window events watched right now (none unless a state that needs them is enabled).</summary>
    internal WindowEventKinds Watching => events.Kinds;

    /// <summary>How many WinEvent hooks are installed right now.</summary>
    internal int WatchHooks => events.HookCount;

    /// <summary>True while battery saver changes are listened for.</summary>
    internal bool WatchingBatterySaver => powerNotification != 0;

    /// <summary>
    /// True when the details are the only thing the helper changes (Windows' background and line on the desktop, no
    /// other state enabled, at least one detail), so a <see cref="Problem"/> belongs next to the details rather than
    /// the look.
    /// </summary>
    public static bool OnlyDetails(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.LookFor(TaskbarState.Desktop) is { Kind: TaskbarLookKind.Default, Line: true }
            && Enum.GetValues<TaskbarState>().All(state => state == TaskbarState.Desktop || !s.LookFor(state).Enabled)
            && s.TaskbarTweaks.Count > 0;
    }

    /// <summary>True when anything differs from Windows' own taskbar: a look in an enabled state, or a detail.</summary>
    internal static bool Configured(AppSettings s) =>
        Enum.GetValues<TaskbarState>().Any(state => TaskbarStateRules.Enabled(s.TaskbarStates, state) && s.LookFor(state) is not { Kind: TaskbarLookKind.Default, Line: true })
        || TaskbarTweaks.RulesFor(s.TaskbarTweaks).Count > 0;

    /// <summary>Raised on the UI thread after each apply.</summary>
    public event Action? Changed;

    public void Attach(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        taskbarCreated = TaskbarNative.RegisterWindowMessageW("TaskbarCreated");
        source?.AddHook(Hook);
        attached = true;
        UpdateWatching();
        UpdateScrollVolume();
        // On Windows 10, or without a helper for this PC, saved or imported looks never load the helper.
        if (usable && (Configured(settings.Current) || TaskbarHelper.ExplorerProcessId() is int pid && pid != 0 && helper.IsLoaded(pid)))
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
            AppSettings s = settings.Current;
            applied = Key(s);
            UpdateWatching();
            UpdateScrollVolume();
            if (usable && !await DrawAsync(s, confirm, cancel).ConfigureAwait(true)) return; // nothing changed: nothing to report
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

    /// <summary>
    /// Tells the helper what to draw now and, for a confirmed pass, waits for its report. False when a window change left
    /// every look as it was (nothing written). Sets <see cref="Problem"/> only when the pass was confirmed, found a problem
    /// or just loaded the helper, so an event-driven pass never hides an earlier problem.
    /// </summary>
    private async Task<bool> DrawAsync(AppSettings s, bool confirm, CancellationToken cancel)
    {
        TaskbarLooks looks = Looks(s);
        IReadOnlyList<TaskbarStyleRule> rules = TaskbarTweaks.RulesFor(s.TaskbarTweaks);
        (int pid, int thread) = TaskbarHelper.ExplorerTaskbar();
        TaskbarWindowLook[] entries = Entries(looks.Global, looks.PerTaskbar);
        string taskbars = TaskbarsKey(looks.PerTaskbar, pid);
        TaskbarLookState global = GlobalFor(looks, entries, taskbars);
        Drawn next = new(global, looks.PerTaskbar, string.Join("\n", rules.Select(r => r.Encode())), pid);
        // A pass for a window change writes only when a taskbar's look actually changes.
        if (!confirm && next.Same(drawn)) return false;
        bool nothing = !Configured(s);
        bool loaded = pid != 0 && helper.IsLoaded(pid);
        if (pid != 0 && !loaded && !nothing) await helper.RetireOthersAsync(pid, cancel).ConfigureAwait(true);
        cancel.ThrowIfCancellationRequested();
        helper.Resting = DesktopLook(s);
        helper.Write(global, rules, entries);
        drawn = next;
        string? problem = null, details = null;
        bool justLoaded = false;
        if (pid == 0) problem = nothing ? null : "The taskbar isn't running.";
        else if (loaded) helper.Signal(pid);
        else if (!nothing)
        {
            problem = await helper.LoadAsync(pid, thread, cancel).ConfigureAwait(true);
            justLoaded = problem is null;
        }
        // Whether the helper told the taskbars apart is checked on every confirmed pass with per-taskbar looks, and on
        // any pass once the taskbars (or Explorer) change.
        bool check = entries.Length > 0 && (confirm || taskbars != apartFor);
        if (problem is null && (confirm || check) && pid != 0 && helper.IsLoaded(pid))
        {
            (problem, details) = await helper.ConfirmPartsAsync(Shown(global, looks.PerTaskbar), cancellationToken: cancel).ConfigureAwait(true);
            if (problem is null && check && TellsApart() is bool told)
            {
                apart = told;
                apartFor = taskbars;
                TaskbarLookState wanted = GlobalFor(looks, entries, taskbars);
                if (wanted != global)
                {
                    // Taskbars it cannot tell apart show the strongest state's look (as version 1.0 did), not the Desktop look.
                    cancel.ThrowIfCancellationRequested(); // the app may have quit (and rested the look) while this waited
                    global = wanted;
                    helper.Write(global, rules, entries);
                    drawn = next with { Global = global };
                    helper.Signal(pid);
                    if (confirm)
                        (problem, details) = await helper.ConfirmPartsAsync(Shown(global, looks.PerTaskbar), cancellationToken: cancel).ConfigureAwait(true);
                }
            }
        }
        if (confirm || problem is not null || justLoaded) Problem = problem;
        // An unconfirmed pass (a state changed) leaves the details' last report in place.
        if (confirm || problem is not null) DetailsProblem = details;
        return true;
    }

    private bool? TellsApart() => TellsApart(helper.ReadStatus(), helper.ReadMappedCount());

    /// <summary>
    /// True when a report (written for per-taskbar looks covering every taskbar) shows every background rectangle with its
    /// own taskbar's look, false when some could not be matched to their taskbar, null when the report does not say.
    /// </summary>
    internal static bool? TellsApart(TaskbarHelperStatus? status, int? mapped) =>
        status is { Targets: > 0 } && mapped is int count ? count >= status.Targets : null;

    private TaskbarLookState GlobalFor(TaskbarLooks looks, TaskbarWindowLook[] entries, string taskbars) =>
        GlobalToWrite(looks, entries, apart || apartFor != taskbars);

    /// <summary>
    /// The global look to write: normally <see cref="TaskbarLooks.Global"/>; when there are per-taskbar looks but the
    /// helper could not tell the taskbars apart, the strongest state's look (as version 1.0 showed it), so a taskbar it
    /// cannot match does not silently show the Desktop look. Pure.
    /// </summary>
    internal static TaskbarLookState GlobalToWrite(TaskbarLooks looks, TaskbarWindowLook[] entries, bool tellsApart) =>
        entries.Length > 0 && !tellsApart ? looks.Strongest : looks.Global;

    /// <summary>Which taskbars (and which Explorer) a check of the helper's matching was made for.</summary>
    private static string TaskbarsKey(TaskbarWindowLook[] perTaskbar, int explorer) =>
        explorer.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
        + string.Join(",", perTaskbar.Select(p => ((long)p.Window).ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

    /// <summary>The look of the Desktop state: what the taskbar rests at while no other state applies, or without the app.</summary>
    private TaskbarLookState DesktopLook(AppSettings s)
    {
        TaskbarStateLook look = s.LookFor(TaskbarState.Desktop);
        return TaskbarStateRules.Resolve(look, look.UseAccent ? Accent() : TaskbarStateProbe.FallbackAccent);
    }

    /// <summary>
    /// The look of each taskbar now, the look for any taskbar the helper cannot tell apart, and the strongest state's look.
    /// States other than Desktop count only while the app is running with its window open.
    /// </summary>
    private TaskbarLooks Looks(AppSettings s)
    {
        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks = s.TaskbarStates;
        bool live = attached && !disposed;
        WindowEventKinds kinds = live ? TaskbarStateRules.EventsFor(looks) : WindowEventKinds.None;
        DesktopSnapshot snapshot = TaskbarStateProbe.Capture(kinds.HasFlag(WindowEventKinds.Windows), kinds.HasFlag(WindowEventKinds.Foreground),
            live && TaskbarStateRules.Enabled(looks, TaskbarState.BatterySaver));
        return LooksFor(snapshot, looks, UsesAccent(s) ? Accent() : TaskbarStateProbe.FallbackAccent);
    }

    /// <summary>
    /// Each taskbar's look for <paramref name="snapshot"/>; the look for any taskbar the helper cannot tell apart (the one
    /// every taskbar shows when they all agree, such as battery saver or a single display, otherwise the Desktop look);
    /// and the look of the strongest state on any taskbar. Pure.
    /// </summary>
    internal static TaskbarLooks LooksFor(DesktopSnapshot snapshot, IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks, uint accentRgb)
    {
        TaskbarStateLook Look(TaskbarState state) => looks.TryGetValue(state, out TaskbarStateLook? look) && look is not null ? look : TaskbarStateLook.DefaultFor(state);
        IReadOnlyList<(nint Taskbar, TaskbarState State)> states = TaskbarStateRules.Evaluate(snapshot, looks);
        TaskbarWindowLook[] perTaskbar = [.. states.Select(e => new TaskbarWindowLook(e.Taskbar, TaskbarStateRules.Resolve(Look(e.State), accentRgb)))];
        TaskbarLookState desktop = TaskbarStateRules.Resolve(Look(TaskbarState.Desktop), accentRgb);
        TaskbarLookState global = perTaskbar.Length > 0 && perTaskbar.All(p => p.Look == perTaskbar[0].Look) ? perTaskbar[0].Look : desktop;
        TaskbarLookState strongest = states.Count > 0
            ? TaskbarStateRules.Resolve(Look(TaskbarStateRules.Winner(states.Select(e => e.State), looks)), accentRgb) : desktop;
        return new(global, perTaskbar, strongest);
    }

    /// <summary>
    /// The per-taskbar entries worth writing: none when every taskbar shows the global look (Desktop alone, a single
    /// look everywhere, or the look turned off), so no stale entries stay behind.
    /// </summary>
    internal static TaskbarWindowLook[] Entries(TaskbarLookState global, TaskbarWindowLook[] perTaskbar) =>
        perTaskbar.All(p => p.Look == global) ? [] : perTaskbar;

    /// <summary>The look the helper's report is checked against: one a taskbar actually shows.</summary>
    private static TaskbarLookState Shown(TaskbarLookState global, TaskbarWindowLook[] perTaskbar) =>
        perTaskbar.Length == 0 ? global : perTaskbar.Select(p => p.Look).FirstOrDefault(l => !l.IsWindowsDefault, TaskbarLookState.Windows);

    private static string Key(AppSettings s) =>
        string.Join(";", Enum.GetValues<TaskbarState>().Select(state => s.LookFor(state).ToString())) + "|"
        + string.Join(";", s.TaskbarTweaks.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key + "=" + t.Value)) + "|" + s.TaskbarScrollVolume;

    private static bool UsesAccent(AppSettings s) =>
        Enum.GetValues<TaskbarState>().Any(state => TaskbarStateRules.Enabled(s.TaskbarStates, state) && s.LookFor(state).UseAccent);

    private uint Accent()
    {
        if (!accentKnown)
        {
            accent = TaskbarStateProbe.AccentColor();
            accentKnown = true;
        }
        return accent;
    }

    private void UpdateScrollVolume()
    {
        bool wanted = settings.Current.TaskbarScrollVolume;
        ScrollVolumeProblem = scrollVolume.SetEnabled(wanted) ? null : "Windows did not allow the scroll-for-volume hook.";
    }

    /// <summary>
    /// Hooks exactly the window events the enabled states need (none for Desktop alone) and listens for battery saver
    /// only while its state is enabled (on the app window, so not before <see cref="Attach"/>). Nothing at all on Windows
    /// 10 or without a helper for this PC.
    /// </summary>
    internal void UpdateWatching()
    {
        IReadOnlyDictionary<TaskbarState, TaskbarStateLook> looks = settings.Current.TaskbarStates;
        bool watch = usable && !disposed;
        events.Watch(watch ? TaskbarStateRules.EventsFor(looks) : WindowEventKinds.None);
        bool battery = watch && TaskbarStateRules.Enabled(looks, TaskbarState.BatterySaver);
        if (battery && powerNotification == 0 && source is not null)
            powerNotification = StateNative.RegisterPowerSettingNotification(source.Handle, StateNative.PowerSavingStatus, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        else if (!battery && powerNotification != 0)
        {
            _ = StateNative.UnregisterPowerSettingNotification(powerNotification);
            powerNotification = 0;
        }
        if (events.Kinds == WindowEventKinds.None && powerNotification == 0) settle.Stop();
    }

    /// <summary>Something a state depends on may have changed: work the states out again soon.</summary>
    private void StateEvent()
    {
        if (!disposed && !settle.IsEnabled) settle.Start();
    }

    private void Settle()
    {
        settle.Stop();
        if (disposed || !attached || !usable) return;
        TaskbarLooks looks = Looks(settings.Current);
        if (drawn is { } last && last.PerTaskbar.SequenceEqual(looks.PerTaskbar)
            && last.Global == GlobalFor(looks, Entries(looks.Global, looks.PerTaskbar), TaskbarsKey(looks.PerTaskbar, last.Explorer)))
            return;
        _ = ApplyAsync(confirm: false);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Explorer restarted: the new taskbar needs the helper again.
        if (taskbarCreated != 0 && message == (int)taskbarCreated)
        {
            if (usable && Configured(settings.Current)) _ = ApplyAsync();
        }
        else if (message == PowerBroadcast && wParam == PowerSettingChange && powerNotification != 0) StateEvent();
        // Displays added or removed: taskbars come and go.
        else if (message == DisplayChange && events.Kinds != WindowEventKinds.None) StateEvent();
        else if (message == ColorizationChanged || (message == SettingChange && lParam != 0 && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet"))
        {
            accentKnown = false;
            if (attached && UsesAccent(settings.Current)) StateEvent();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel(); // an apply still waiting on the helper stops without loading or writing anything more
        settle.Stop();
        LeaveStates();
        events.Dispose();
        if (powerNotification != 0) _ = StateNative.UnregisterPowerSettingNotification(powerNotification);
        powerNotification = 0;
        source?.RemoveHook(Hook);
        scrollVolume.Dispose();
    }

    /// <summary>
    /// States other than Desktop last only while the app watches. If one is showing when the app quits, every taskbar
    /// goes back to the Desktop look (without waiting for the helper's report), so it does not stay in, say, the
    /// maximized look after the window is restored. The chosen looks and details themselves stay, like any other setting.
    /// The settings are written even while a load is still in flight, so a helper that starts after this reads the
    /// Desktop look; and should the app crash instead, the helper rests at the Desktop look on its own.
    /// </summary>
    private void LeaveStates()
    {
        if (!usable || drawn is not { } last) return;
        TaskbarLookState desktop = DesktopLook(settings.Current);
        if (last.Global == desktop && last.PerTaskbar.All(p => p.Look == desktop)) return;
        try
        {
            helper.Resting = desktop;
            helper.Write(desktop, TaskbarTweaks.RulesFor(settings.Current.TaskbarTweaks), []); // no per-taskbar entries left behind
            drawn = last with { Global = desktop, PerTaskbar = [] };
            int pid = TaskbarHelper.ExplorerProcessId();
            if (pid != 0 && helper.IsLoaded(pid)) helper.Signal(pid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Quitting: the next start puts the right look back.
        }
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
