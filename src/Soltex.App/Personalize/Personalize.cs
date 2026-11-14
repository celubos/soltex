using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Settings;

namespace Soltex.App.Personalize;

/// <summary>
/// Folders next to Start's power button (Start11-class idea). Windows keeps the list as GUIDs in
/// HKCU\...\Start\VisiblePlaces; the IDs below come from Windows' own table in StartDocked.dll (build 26300).
/// </summary>
public static class StartFolders
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Start";

    public static IReadOnlyList<(string Name, Guid Id)> All { get; } =
    [
        ("Settings", new("52730886-51AA-4243-9F7B-2776584659D4")),
        ("File Explorer", new("148A24BC-D60C-4289-A080-6ED9BBA24882")),
        ("Documents", new("2D34D5CE-FA5A-4543-82F2-22E6EAF7773C")),
        ("Downloads", new("E367B32F-89DE-4355-BFCE-61F37B18A937")),
        ("Music", new("B00B0620-7F51-4C32-AA1E-34CC547F7315")),
        ("Pictures", new("383F07A0-E80A-4C80-B05A-86DB845DBC4D")),
        ("Videos", new("42B3A5C5-7D86-42F4-80A4-93FACA7A88B5")),
        ("Network", new("FE758144-080D-42AE-8BDA-34ED97B66394")),
        ("Personal folder", new("74BDB04A-F94A-4F68-8BD6-4398071DA8BC"))
    ];

    public static IReadOnlyList<Guid> Read(RegistryKey? root = null)
    {
        using RegistryKey? start = (root ?? Registry.CurrentUser).OpenSubKey(Key);
        if (start?.GetValue("VisiblePlaces") is not byte[] data) return [];
        return [.. Enumerable.Range(0, data.Length / 16).Select(i => new Guid(data.AsSpan(i * 16, 16)))];
    }

    /// <summary>Shows or hides one folder, keeping Windows' order for the rest.</summary>
    public static void Set(Guid folder, bool shown, RegistryKey? root = null)
    {
        HashSet<Guid> visible = [.. Read(root)];
        if (shown) visible.Add(folder); else visible.Remove(folder);
        byte[] data = [.. All.Where(f => visible.Contains(f.Id)).SelectMany(f => f.Id.ToByteArray())];
        using RegistryKey start = (root ?? Registry.CurrentUser).CreateSubKey(Key);
        start.SetValue("VisiblePlaces", data, RegistryValueKind.Binary);
    }
}

/// <summary>Pointer schemes (CursorFX-class idea, using Windows' own schemes).</summary>
public static class PointerSchemes
{
    /// <summary>The name Windows gives its own default scheme (the Aero pointers).</summary>
    public const string WindowsDefault = "Windows Default";
    private const string SystemSchemesPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";
    private const string UserSchemesPath = @"Control Panel\Cursors\Schemes";
    private const string DefaultCursorsPath = @".DEFAULT\Control Panel\Cursors";
    internal static readonly string[] Roles = ["Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person"];

    /// <summary>A scheme ready to apply: its name, where it comes from ("Scheme Source": 1 the user's own, 2 Windows') and one file per role.</summary>
    internal sealed record Scheme(string Name, int Source, IReadOnlyList<string> Files);

    /// <summary>Windows' default first, then the schemes Windows ships and the user's own, by name.</summary>
    public static IReadOnlyList<string> Names()
    {
        SortedSet<string> names = new(StringComparer.CurrentCultureIgnoreCase);
        foreach ((RegistryKey hive, string path) in new[] { (Registry.LocalMachine, SystemSchemesPath), (Registry.CurrentUser, UserSchemesPath) })
        {
            using RegistryKey? schemes = hive.OpenSubKey(path);
            foreach (string name in schemes?.GetValueNames() ?? []) if (name.Length > 0) names.Add(name);
        }
        names.Remove(WindowsDefault);
        return [WindowsDefault, .. names];
    }

    /// <summary>How a scheme is shown in this app.</summary>
    public static string Label(string name) => name == WindowsDefault ? "Windows default" : name;

    public static string Current()
    {
        using RegistryKey? cursors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Cursors");
        return Normalize(cursors?.GetValue("") as string);
    }

    /// <summary>
    /// Maps the saved scheme name to one <see cref="Names"/> lists. Windows writes its default as "Windows Default", as
    /// a resource reference ("@main.cpl,-1020") or leaves it empty.
    /// </summary>
    internal static string Normalize(string? saved) =>
        string.IsNullOrEmpty(saved) || saved.StartsWith('@') && saved.Contains("main.cpl", StringComparison.OrdinalIgnoreCase) ? WindowsDefault : saved;

    /// <summary>Applies a scheme now (Windows reloads its pointers). Returns false when the scheme is unknown.</summary>
    public static bool Apply(string name)
    {
        Scheme? scheme;
        using (RegistryKey? user = Registry.CurrentUser.OpenSubKey(UserSchemesPath))
        using (RegistryKey? system = Registry.LocalMachine.OpenSubKey(SystemSchemesPath))
        using (RegistryKey? defaults = Registry.Users.OpenSubKey(DefaultCursorsPath))
            scheme = Resolve(name, user, system, defaults);
        if (scheme is null) return false;
        using RegistryKey cursors = Registry.CurrentUser.CreateSubKey(@"Control Panel\Cursors");
        cursors.SetValue("", scheme.Name);
        cursors.SetValue("Scheme Source", scheme.Source, RegistryValueKind.DWord);
        for (int i = 0; i < Roles.Length; i++) cursors.SetValue(Roles[i], scheme.Files[i], RegistryValueKind.ExpandString);
        return SystemParametersInfoW(0x0057, 0, 0, 0x01 | 0x02); // SPI_SETCURSORS, update the profile and tell apps
    }

    /// <summary>
    /// Finds a scheme's pointers. Windows' default is the set a new user account gets (<paramref name="defaultCursors"/>,
    /// HKEY_USERS\.DEFAULT), or else the "Windows Aero" scheme it is made of. A user scheme wins over a Windows scheme of
    /// the same name. Null when the name is unknown.
    /// </summary>
    internal static Scheme? Resolve(string name, RegistryKey? userSchemes, RegistryKey? systemSchemes, RegistryKey? defaultCursors)
    {
        if (name == WindowsDefault)
        {
            if (defaultCursors?.GetValue("Arrow") is string { Length: > 0 })
                return new Scheme(WindowsDefault, 2, [.. Roles.Select(role => defaultCursors.GetValue(role) as string ?? "")]);
            if (systemSchemes?.GetValue("Windows Aero") is string aero) return new Scheme(WindowsDefault, 2, Split(aero));
            return new Scheme(WindowsDefault, 2, [.. Roles.Select(_ => "")]);
        }
        if (userSchemes?.GetValue(name) is string own) return new Scheme(name, 1, Split(own));
        if (systemSchemes?.GetValue(name) is string shipped) return new Scheme(name, 2, Split(shipped));
        return null;
    }

    /// <summary>A saved scheme is a comma-separated list of files in role order (extra fields, such as a display name, are ignored).</summary>
    private static string[] Split(string list)
    {
        string[] parts = list.Split(',');
        return [.. Roles.Select((_, i) => i < parts.Length ? parts[i].Trim() : "")];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfoW(uint action, uint parameter, nint value, uint flags);
}

/// <summary>
/// Automatic dark mode: light between <see cref="AppSettings.LightFrom"/> and <see cref="AppSettings.DarkFrom"/>.
/// Windows is switched only when the schedule is turned on and when the light or dark period begins, so a manual choice
/// in between (App mode, Windows mode, Windows Settings) stays until the next period. Does nothing until attached.
/// </summary>
public sealed class ThemeSchedule : IDisposable
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly AppSettingsStore settings;
    private readonly Func<DateTime> clock;
    private readonly Action<bool> apply;
    private readonly DispatcherTimer timer;
    private bool? period;
    private bool attached, disposed;

    public ThemeSchedule(AppSettingsStore settings) : this(settings, () => DateTime.Now, SetWindowsTheme) { }

    /// <summary>Tests pass a clock and a stand-in for switching the Windows theme.</summary>
    internal ThemeSchedule(AppSettingsStore settings, Func<DateTime> clock, Action<bool> apply)
    {
        this.settings = settings;
        this.clock = clock;
        this.apply = apply;
        timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        timer.Stop();
        settings.Changed += _ => { if (attached) Application.Current?.Dispatcher.InvokeAsync(Update); };
    }

    /// <summary>The period last applied: true light, false dark, null while the schedule is off.</summary>
    internal bool? Period => period;

    /// <summary>Raised on the UI thread after the schedule switched Windows (true light, false dark), so pages showing the mode can refresh.</summary>
    public event Action<bool>? Switched;

    public void Attach()
    {
        if (disposed) return;
        attached = true;
        Update();
    }

    /// <summary>True when light mode is wanted at <paramref name="now"/> (handles ranges across midnight).</summary>
    public static bool LightAt(TimeSpan now, TimeSpan lightFrom, TimeSpan darkFrom) =>
        lightFrom <= darkFrom ? now >= lightFrom && now < darkFrom : now >= lightFrom || now < darkFrom;

    private void Update()
    {
        if (!attached || disposed) return;
        if (!settings.Current.DarkModeSchedule)
        {
            timer.Stop();
            period = null; // turning the schedule on again applies the current period
            return;
        }
        if (!timer.IsEnabled) timer.Start();
        Check();
    }

    /// <summary>Switches Windows when the period differs from the one last applied.</summary>
    internal void Check()
    {
        AppSettings s = settings.Current;
        if (!attached || disposed || !s.DarkModeSchedule) return;
        bool light = LightAt(clock().TimeOfDay, s.LightFrom, s.DarkFrom);
        if (period == light) return;
        period = light;
        apply(light);
        Switched?.Invoke(light);
    }

    private static void SetWindowsTheme(bool light)
    {
        int value = light ? 1 : 0;
        try
        {
            using RegistryKey personalize = Registry.CurrentUser.CreateSubKey(Key);
            if (personalize.GetValue("AppsUseLightTheme") is int apps && apps == value && personalize.GetValue("SystemUsesLightTheme") is int system && system == value) return;
            personalize.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            personalize.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return; }
        _ = SendMessageTimeoutW(0xFFFF, 0x001A, 0, "ImmersiveColorSet", 0x0002, 1000, out _); // WM_SETTINGCHANGE to all windows
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}

/// <summary>What the live wallpaper is doing, for the Desktop page.</summary>
public enum LiveWallpaperStatus { Off, Starting, Playing, FileMissing, CannotPlay, NoDesktop }

/// <summary>
/// Live wallpaper (DeskScapes-class): a looping, muted video drawn behind the desktop icons (inside the desktop's WorkerW
/// window), paused while any window is maximized so it costs nothing when it cannot be seen. The video window is placed
/// only once the video has opened, so a file that cannot be played never blanks the desktop. It is placed again after
/// File Explorer restarts and resized when screens change.
/// </summary>
public sealed class LiveWallpaper : IDisposable
{
    private const int PlaceTries = 10;
    private readonly AppSettingsStore settings;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer settle, retry;
    private Window? window;
    private MediaElement? media;
    private Shell.MaximizedWatcher? watcher;
    private Desktop.DesktopEvents? events;
    private string? subject;
    private nint layer;
    private int tries;
    private bool attached, disposed;

    public LiveWallpaper(AppSettingsStore settings)
    {
        this.settings = settings;
        dispatcher = Dispatcher.CurrentDispatcher;
        settle = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => Pause(), dispatcher);
        settle.Stop();
        retry = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Retry(), dispatcher);
        retry.Stop();
        settings.Changed += _ => { if (attached) Application.Current?.Dispatcher.InvokeAsync(Update); };
    }

    /// <summary>Whether the video is drawn behind the desktop icons.</summary>
    public bool OnDesktop => Status == LiveWallpaperStatus.Playing;

    /// <summary>What happened to the chosen video. Stays <see cref="LiveWallpaperStatus.Off"/> until attached.</summary>
    public LiveWallpaperStatus Status { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes.</summary>
    public event Action<LiveWallpaperStatus>? StatusChanged;

    public void Attach()
    {
        if (disposed) return;
        attached = true;
        Update();
    }

    private void Update()
    {
        if (!attached || disposed) return;
        string? path = settings.Current.LiveWallpaper;
        if (path is not null && path == subject) return; // already playing, starting, or known not to work
        Stop();
        subject = path;
        tries = 0;
        if (path is null)
        {
            events?.Dispose();
            events = null;
            SetStatus(LiveWallpaperStatus.Off);
            return;
        }
        if (events is null)
        {
            events = new Desktop.DesktopEvents();
            events.ShellRestarted += ShellRestarted;
            events.DisplayChanged += Refit;
        }
        if (!File.Exists(path))
        {
            SetStatus(LiveWallpaperStatus.FileMissing);
            return;
        }
        Start(path);
    }

    private void Start(string path)
    {
        Uri source;
        try { source = new Uri(path); }
        catch (UriFormatException)
        {
            SetStatus(LiveWallpaperStatus.CannotPlay);
            return;
        }
        SetStatus(LiveWallpaperStatus.Starting);
        MediaElement video = new() { Source = source, LoadedBehavior = MediaState.Manual, IsMuted = true, Stretch = System.Windows.Media.Stretch.UniformToFill };
        video.MediaEnded += (_, _) => { video.Position = TimeSpan.Zero; video.Play(); };
        video.MediaOpened += (_, _) => { if (ReferenceEquals(video, media)) Place(); };
        video.MediaFailed += (_, _) =>
        {
            if (!ReferenceEquals(video, media)) return;
            Stop();
            SetStatus(LiveWallpaperStatus.CannotPlay);
        };
        media = video;
        // Off screen until the video has opened and is placed behind the icons.
        window = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Content = video, Background = System.Windows.Media.Brushes.Black, Left = -32000, Top = -32000, Width = 10, Height = 10 };
        window.Closed += Window_Closed;
        window.Show();
        video.Play();
    }

    /// <summary>Puts the video window inside the desktop's wallpaper layer, covering every screen.</summary>
    private void Place()
    {
        retry.Stop();
        if (window is null || media is null) return;
        nint handle = new WindowInteropHelper(window).Handle;
        layer = handle == 0 ? 0 : FindLayer();
        if (layer == 0)
        {
            media.Pause();
            // At sign-in the desktop may not be ready yet; try again for a while.
            if (tries++ < PlaceTries)
            {
                retry.Start();
                return;
            }
            Stop();
            SetStatus(LiveWallpaperStatus.NoDesktop);
            return;
        }
        tries = 0;
        _ = SetParent(handle, layer);
        Fit(handle);
        watcher ??= new Shell.MaximizedWatcher(() => { settle.Stop(); settle.Start(); });
        Pause();
        SetStatus(LiveWallpaperStatus.Playing);
    }

    private void Retry()
    {
        retry.Stop();
        if (!attached || disposed) return;
        if (window is not null && media is not null) Place();
        else Restart();
    }

    /// <summary>
    /// Explorer announced a new taskbar. While the video still sits in a live wallpaper layer Explorer did not restart
    /// (it also announces this after some display changes), so the video is only resized; otherwise it starts again.
    /// </summary>
    private void ShellRestarted()
    {
        if (window is not null && layer != 0) Refit();
        else Restart();
    }

    /// <summary>File Explorer restarted, or the video window went away with the old desktop: start again.</summary>
    private void Restart()
    {
        if (!attached || disposed) return;
        Stop();
        subject = null;
        Update();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        // Closed by Windows (its parent, the old desktop, was destroyed) rather than by Stop.
        if (!ReferenceEquals(sender, window) || disposed || dispatcher.HasShutdownStarted) return;
        window = null;
        Stop();
        retry.Start();
    }

    /// <summary>Screens changed: cover the new virtual screen, or start again if the wallpaper layer is gone.</summary>
    private void Refit()
    {
        if (window is null || layer == 0) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        if (!IsWindow(layer) || GetAncestor(handle, 1) != layer) Restart(); // GA_PARENT
        else Fit(handle);
    }

    private void Fit(nint handle)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79); // virtual screen
        _ = GetWindowRect(layer, out Rect origin);
        _ = SetWindowPos(handle, 0, x - origin.Left, y - origin.Top, w, h, 0x0010 | 0x0040);
    }

    private void Pause()
    {
        settle.Stop();
        if (media is null || layer == 0) return;
        if (Shell.MaximizedWatcher.AnyMaximized()) media.Pause(); else media.Play();
    }

    /// <summary>The desktop's wallpaper layer (a WorkerW window), or 0 if it was not found.</summary>
    private static nint FindLayer()
    {
        nint progman = FindWindowExW(0, 0, "Progman", null);
        if (progman == 0) return 0;
        _ = SendMessageTimeoutW(progman, 0x052C, 0xD, 0x1, 0, 1000, out _); // ask the desktop for its wallpaper layer
        nint layer = FindWindowExW(progman, 0, "WorkerW", null); // Windows 11 24H2 and later: inside Progman
        for (nint top = 0; layer == 0 && (top = FindWindowExW(0, top, null, null)) != 0;)
            if (FindWindowExW(top, 0, "SHELLDLL_DefView", null) != 0) layer = FindWindowExW(0, top, "WorkerW", null); // earlier builds
        return layer;
    }

    private void SetStatus(LiveWallpaperStatus status)
    {
        if (Status == status) return;
        Status = status;
        if (!disposed) StatusChanged?.Invoke(status);
    }

    private void Stop()
    {
        retry.Stop();
        settle.Stop();
        watcher?.Dispose();
        watcher = null;
        layer = 0;
        MediaElement? oldMedia = media;
        Window? oldWindow = window;
        media = null;
        window = null;
        oldMedia?.Stop();
        oldMedia?.Close();
        try { oldWindow?.Close(); }
        catch (InvalidOperationException) { } // already closed or closing
    }

    public void Dispose()
    {
        if (disposed) return;
        // The video window belongs to the UI thread; close it there even if shutdown runs elsewhere.
        if (!dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownFinished) dispatcher.Invoke(Dispose);
            return;
        }
        disposed = true;
        Stop();
        events?.Dispose();
        events = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? className, string? windowName);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
