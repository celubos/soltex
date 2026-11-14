using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Settings;

namespace Soltex.App.Desktop;

/// <summary>
/// A borderless, translucent window that lives on the desktop layer: owned by the desktop (Progman), kept at the bottom
/// of the z-order, out of Alt+Tab and the taskbar, so it stays put through Win+D like desktop icons do.
/// </summary>
public class DesktopSurfaceWindow : Window
{
    internal static readonly Brush Glass = Frozen(Color.FromArgb(0xB0, 0x14, 0x16, 0x1E));
    internal static readonly Brush GlassStroke = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush Ink = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush InkDim = Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
    internal static readonly Brush Highlight = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private readonly bool onDesktop;
    private bool arrange;

    public DesktopSurfaceWindow(bool onDesktop)
    {
        this.onDesktop = onDesktop;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) => AttachToDesktop();
        ContentRendered += (_, _) => KeepOnScreen();
    }

    /// <summary>Raised after the user moved or resized the window (arrange mode), or after it was pulled back on screen.</summary>
    public event Action<Rect>? BoundsCommitted;

    /// <summary>Set by the desktop service before it closes the window on purpose.</summary>
    internal bool Retired { get; set; }

    /// <summary>
    /// Pulls the surface back onto the screen when less than half of it lies on the nearest screen's work area (its
    /// screen was unplugged or the resolution dropped) and saves the new position. Only on the real desktop.
    /// </summary>
    internal void KeepOnScreen()
    {
        if (!onDesktop || !IsLoaded || Retired) return;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !GetWindowRect(handle, out NativeRect pixels)) return;
        nint monitor = MonitorFromRect(ref pixels, 2); // MONITOR_DEFAULTTONEAREST
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || MostlyVisible(pixels.ToRect(), info.Work.ToRect())) return;
        Point spot = Within(new Rect(Left, Top, ActualWidth, ActualHeight), SystemParameters.WorkArea);
        Left = spot.X;
        Top = spot.Y;
        CommitBounds();
    }

    /// <summary>True while the window is open and owned by <paramref name="desktop"/> (the current Progman window).</summary>
    internal bool OwnedBy(nint desktop)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        return desktop != 0 && handle != 0 && !Retired && IsVisible && GetWindowLongPtrW(handle, -8) == desktop; // GWLP_HWNDPARENT reads the owner
    }

    /// <summary>True when at least half of <paramref name="window"/> lies inside <paramref name="workArea"/>.</summary>
    internal static bool MostlyVisible(Rect window, Rect workArea)
    {
        if (window.Width <= 0 || window.Height <= 0) return true;
        Rect visible = Rect.Intersect(window, workArea);
        return !visible.IsEmpty && visible.Width * visible.Height * 2 >= window.Width * window.Height;
    }

    /// <summary>The nearest top-left corner that keeps <paramref name="window"/> inside <paramref name="workArea"/>.</summary>
    internal static Point Within(Rect window, Rect workArea) => new(
        Math.Clamp(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width)),
        Math.Clamp(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height)));

    /// <summary>Move and resize allowed (arrange mode); otherwise the surface stays where it is.</summary>
    public bool Arranging
    {
        get => arrange;
        set
        {
            arrange = value;
            OnArrangeChanged();
        }
    }

    protected virtual void OnArrangeChanged() { }

    /// <summary>Lets the user drag the surface while arranging; saves the new position.</summary>
    protected void DragWhenArranging(MouseButtonEventArgs e)
    {
        if (!arrange || e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        CommitBounds();
    }

    protected void CommitBounds() => BoundsCommitted?.Invoke(new Rect(Left, Top, ActualWidth, ActualHeight));

    private void AttachToDesktop()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        const int ExStyle = -20, ToolWindow = 0x80, AppWindow = 0x40000;
        nint style = GetWindowLongPtrW(handle, ExStyle);
        _ = SetWindowLongPtrW(handle, ExStyle, (style | ToolWindow) & ~(nint)AppWindow);
        if (!onDesktop) return;
        nint desktop = FindWindowW("Progman", null);
        if (desktop != 0) _ = SetWindowLongPtrW(handle, -8, desktop); // GWLP_HWNDPARENT: owned by the desktop
        HwndSource.FromHwnd(handle)?.AddHook(KeepAtBottom);
        _ = SetWindowPos(handle, 1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_BOTTOM, no size/move/activate
    }

    private static nint KeepAtBottom(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WindowPosChanging = 0x0046;
        if (message == WindowPosChanging)
        {
            WindowPos position = Marshal.PtrToStructure<WindowPos>(lParam);
            position.InsertAfter = 1; // HWND_BOTTOM (above its owner, the desktop)
            position.Flags &= ~0x0004u; // clear SWP_NOZORDER
            Marshal.StructureToPtr(position, lParam, false);
        }
        return 0;
    }

    internal static Brush Frozen(Color color)
    {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos { public nint Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Math.Max(0, Right - Left), Math.Max(0, Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}

/// <summary>
/// A desktop group: a header (click to roll up) and the items of a folder or of the user's own list. Double-click opens an
/// item; files dropped on a list group are added to it; the context menu opens the item's folder or removes it.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The folder watcher is disposed when the window closes.")]
public sealed class DesktopGroupWindow : DesktopSurfaceWindow
{
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300), ReloadAtMost = TimeSpan.FromSeconds(2);
    private readonly TextBlock title = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Ink, Margin = new Thickness(12, 8, 12, 8), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel items = new() { Margin = new Thickness(6, 0, 6, 8) };
    private readonly ScrollViewer scroller;
    private readonly Border frame;
    private readonly DispatcherTimer commitLater, reloadLater;
    private FileSystemWatcher? watcher;
    private DesktopGroup group;
    private DateTime? reloadPendingSince;
    private int reloadPosted, loadVersion, watchVersion;
    private bool watchPending, closed;

    public DesktopGroupWindow(DesktopGroup group, bool onDesktop) : base(onDesktop)
    {
        this.group = group;
        Title = group.Title;
        AutomationProperties.SetName(this, "Desktop group " + group.Title);
        Border header = new() { Background = Brushes.Transparent, Child = title, Cursor = Cursors.Hand };
        header.MouseLeftButtonDown += Header_MouseDown;
        scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = items };
        DockPanel layout = new();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(scroller);
        frame = new Border { Background = Glass, BorderBrush = GlassStroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = layout };
        Content = frame;
        AllowDrop = true;
        Drop += Group_Drop;
        commitLater = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { commitLater!.Stop(); CommitBounds(); }, Dispatcher);
        commitLater.Stop();
        reloadLater = new DispatcherTimer(ReloadDelay, DispatcherPriority.Background, (_, _) => { reloadLater!.Stop(); reloadPendingSince = null; Reload(); }, Dispatcher);
        reloadLater.Stop();
        SizeChanged += (_, _) => { if (Arranging && IsLoaded) { commitLater.Stop(); commitLater.Start(); } };
        Closed += (_, _) =>
        {
            closed = true;
            commitLater.Stop();
            reloadLater.Stop();
            watcher?.Dispose();
            watcher = null;
        };
        Apply(group, first: true);
    }

    /// <summary>Raised when files are dropped on a list group: (group id, paths).</summary>
    public event Action<string, IReadOnlyList<string>>? ItemsDropped;

    /// <summary>Raised when the user removes an item from a list group: (group id, path).</summary>
    public event Action<string, string>? ItemRemoved;

    /// <summary>Raised when the header is clicked to roll the group up or down: (group id, rolled up).</summary>
    public event Action<string, bool>? RolledUpChanged;

    public DesktopGroup Group => group;

    /// <summary>The paths currently shown (for tests and accessibility checks).</summary>
    public IReadOnlyList<string> ShownItems => [.. items.Children.OfType<FrameworkElement>().Select(e => (string)e.Tag)];

    public void Update(DesktopGroup next) => Apply(next, first: false);

    /// <summary>
    /// Applies the saved group. The folder or list is read again only when it changed: moving, resizing or rolling up
    /// never touches the disk.
    /// </summary>
    private void Apply(DesktopGroup next, bool first)
    {
        bool folderChanged = first || !string.Equals(next.Folder, group.Folder, StringComparison.OrdinalIgnoreCase);
        bool contentChanged = folderChanged || !next.Items.SequenceEqual(group.Items, StringComparer.OrdinalIgnoreCase);
        group = next;
        title.Text = next.Title;
        Title = next.Title;
        AutomationProperties.SetName(this, "Desktop group " + next.Title);
        Left = next.Left;
        Top = next.Top;
        Width = next.Width;
        Height = next.RolledUp ? 34 : next.Height;
        scroller.Visibility = next.RolledUp ? Visibility.Collapsed : Visibility.Visible;
        if (folderChanged) WatchFolder(reloadWhenReady: false);
        // A folder that was missing (an offline share, say) is looked for again on the next update, and read once found.
        else if (watcher is null && !watchPending && next.Folder is not null) WatchFolder(reloadWhenReady: true);
        if (contentChanged) Reload();
    }

    protected override void OnArrangeChanged()
    {
        ResizeMode = Arranging && !group.RolledUp ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        frame.BorderBrush = Arranging ? Highlight : GlassStroke;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Arranging && e.ClickCount == 1)
        {
            DragWhenArranging(e);
            return;
        }
        if (e.ClickCount == 1) RolledUpChanged?.Invoke(group.Id, !group.RolledUp);
    }

    /// <summary>How many times the entries were read and shown (tests check that bursts and moves do not add reads).</summary>
    internal int Loads { get; private set; }

    /// <summary>True once the folder is being watched (tests wait for it).</summary>
    internal bool Watching => watcher is not null;

    /// <summary>Starts watching the group's folder. The folder is opened on a worker thread, so a slow or offline share never blocks the desktop.</summary>
    private void WatchFolder(bool reloadWhenReady)
    {
        watcher?.Dispose();
        watcher = null;
        int version = ++watchVersion;
        if (group.Folder is not { } folder)
        {
            watchPending = false;
            return;
        }
        watchPending = true;
        _ = Task.Run(() => Watch(folder)).ContinueWith(task =>
        {
            FileSystemWatcher? created = task.IsCompletedSuccessfully ? task.Result : null;
            Dispatcher.InvokeAsync(() =>
            {
                if (closed || version != watchVersion)
                {
                    created?.Dispose();
                    return;
                }
                watchPending = false;
                watcher = created;
                if (created is not null && reloadWhenReady) Reload();
            });
        }, TaskScheduler.Default);
    }

    private FileSystemWatcher? Watch(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        FileSystemWatcher? created = null;
        try
        {
            created = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, InternalBufferSize = 32 * 1024 };
            created.Created += (_, _) => FolderChanged();
            created.Deleted += (_, _) => FolderChanged();
            created.Renamed += (_, _) => FolderChanged();
            // Too many changes at once (the buffer overflowed): read the folder again rather than show a stale view.
            created.Error += (_, _) => FolderChanged();
            created.EnableRaisingEvents = true;
            return created;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            created?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Called on the watcher's threads. A burst of changes (extracting an archive, say) becomes one reload: at most one
    /// request waits on the UI thread, and the reload runs once things are quiet for a moment (or every two seconds while
    /// changes keep coming).
    /// </summary>
    private void FolderChanged()
    {
        if (Interlocked.Exchange(ref reloadPosted, 1) != 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref reloadPosted, 0);
            if (closed) return;
            DateTime now = DateTime.UtcNow;
            reloadPendingSince ??= now;
            if (now - reloadPendingSince < ReloadAtMost) reloadLater.Stop();
            if (!reloadLater.IsEnabled) reloadLater.Start();
        }, DispatcherPriority.Background);
    }

    /// <summary>The folder's visible entries (folders first, then files, by name), or the list group's existing items.</summary>
    internal static IReadOnlyList<string> Entries(DesktopGroup group)
    {
        if (group.Folder is null) return [.. group.Items.Where(p => File.Exists(p) || Directory.Exists(p))];
        try
        {
            DirectoryInfo folder = new(group.Folder);
            return [.. folder.EnumerateFileSystemInfos()
                .Where(e => (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(e => e is FileInfo).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(DesktopGroup.MaximumItems).Select(e => e.FullName)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>Reads the entries on a worker thread (each check can wait on a network share) and shows them when ready.</summary>
    private void Reload()
    {
        int version = ++loadVersion;
        DesktopGroup snapshot = group;
        _ = Task.Run(() => Entries(snapshot)).ContinueWith(task =>
        {
            IReadOnlyList<string> entries = task.IsCompletedSuccessfully ? task.Result : [];
            Dispatcher.InvokeAsync(() =>
            {
                if (closed || version != loadVersion) return;
                Loads++;
                ShowEntries(entries);
            }, DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    private void ShowEntries(IReadOnlyList<string> entries)
    {
        if (entries.SequenceEqual(ShownItems, StringComparer.Ordinal)) return;
        items.Children.Clear();
        foreach (string path in entries) items.Children.Add(Item(path));
    }

    private Border Item(string path)
    {
        string label = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path.TrimEnd('\\'));
        StackPanel content = new() { Width = 76 };
        content.Children.Add(new Image { Source = ShellIcons.For(path), Width = 32, Height = 32, Margin = new Thickness(0, 4, 0, 2) });
        content.Children.Add(new TextBlock { Text = label, Foreground = Ink, FontSize = 11.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis });
        Border item = new() { Child = content, Padding = new Thickness(2, 2, 2, 4), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Tag = path, ToolTip = path };
        AutomationProperties.SetName(item, label);
        item.MouseEnter += (_, _) => item.Background = Highlight;
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Open(path); };
        ContextMenu menu = new();
        MenuItem open = new() { Header = "Open" };
        open.Click += (_, _) => Open(path);
        MenuItem location = new() { Header = "Open file location" };
        location.Click += (_, _) => Open("explorer.exe", "/select,\"" + path + "\"");
        menu.Items.Add(open);
        menu.Items.Add(location);
        if (group.Folder is null)
        {
            MenuItem remove = new() { Header = "Remove from group" };
            remove.Click += (_, _) => ItemRemoved?.Invoke(group.Id, path);
            menu.Items.Add(remove);
        }
        item.ContextMenu = menu;
        return item;
    }

    private void Group_Drop(object sender, DragEventArgs e)
    {
        if (group.Folder is not null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        ItemsDropped?.Invoke(group.Id, paths);
    }

    private static void Open(string target, string? arguments = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" })?.Dispose(); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>A desktop widget: clock, system (CPU and memory) or network (download and upload rates).</summary>
public sealed class DesktopWidgetWindow : DesktopSurfaceWindow
{
    private readonly TextBlock primary = new() { Foreground = Ink, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock secondary = new() { Foreground = InkDim, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Border cpuBar = Bar(), memoryBar = Bar();
    private readonly DispatcherTimer? clock;

    public DesktopWidgetWindow(DesktopWidget widget, bool onDesktop) : base(onDesktop)
    {
        Kind = widget.Kind;
        Title = widget.Kind + " widget";
        Left = widget.Left;
        Top = widget.Top;
        SizeToContent = SizeToContent.WidthAndHeight;
        StackPanel body = new() { Margin = new Thickness(16, 12, 16, 14), MinWidth = 180 };
        body.Children.Add(primary);
        body.Children.Add(secondary);
        if (widget.Kind == DesktopWidgetKind.System)
        {
            primary.FontSize = secondary.FontSize = 13;
            body.Children.Clear();
            body.Children.Add(Meter("CPU", primary, cpuBar));
            body.Children.Add(Meter("Memory", secondary, memoryBar));
        }
        else if (widget.Kind == DesktopWidgetKind.Network)
        {
            primary.FontSize = 15;
        }
        else
        {
            primary.FontSize = 44;
            primary.FontWeight = FontWeights.Light;
            clock = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
            Tick();
            // The clock or time zone was changed: show the new time now rather than at the next minute.
            SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TimeChanged);
        }
        Border frame = new() { Background = Glass, BorderBrush = GlassStroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = body };
        frame.MouseLeftButtonDown += (_, e) => DragWhenArranging(e);
        Content = frame;
        AutomationProperties.SetName(this, Title);
        Closed += (_, _) => clock?.Stop();
    }

    public DesktopWidgetKind Kind { get; }

    /// <summary>Text shown, for tests.</summary>
    public string Reading => primary.Text + " | " + secondary.Text;

    public void Move(DesktopWidget widget)
    {
        if (Left == widget.Left && Top == widget.Top) return;
        Left = widget.Left;
        Top = widget.Top;
    }

    /// <summary>Shows a new CPU/memory or network sample (ignored by the clock).</summary>
    public void Show(double? cpu, double? memory, long? down, long? up)
    {
        if (Kind == DesktopWidgetKind.System)
        {
            primary.Text = cpu is double c ? c.ToString("0", CultureInfo.CurrentCulture) + "%" : "–";
            secondary.Text = memory is double m ? m.ToString("0", CultureInfo.CurrentCulture) + "%" : "–";
            cpuBar.Width = 120 * Math.Clamp((cpu ?? 0) / 100, 0, 1);
            memoryBar.Width = 120 * Math.Clamp((memory ?? 0) / 100, 0, 1);
        }
        else if (Kind == DesktopWidgetKind.Network)
        {
            primary.Text = "↓ " + (down is long d ? Shell.Plain.Rate(d) : "–");
            secondary.Text = "↑ " + (up is long u ? Shell.Plain.Rate(u) : "–");
        }
    }

    private void Tick()
    {
        DateTime now = DateTime.Now;
        primary.Text = now.ToString("t", CultureInfo.CurrentCulture);
        secondary.Text = now.ToString("D", CultureInfo.CurrentCulture);
        // The clock shows hours and minutes only: wake just after the next minute starts.
        if (clock is not null) clock.Interval = UntilNextMinute(now);
    }

    /// <summary>Time from <paramref name="now"/> until just after the next whole minute.</summary>
    internal static TimeSpan UntilNextMinute(DateTime now) => TimeSpan.FromMilliseconds((60 - now.Second) * 1000 - now.Millisecond + 5);

    private nint TimeChanged(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int TimeChange = 0x001E; // WM_TIMECHANGE
        if (message == TimeChange)
        {
            TimeZoneInfo.ClearCachedData();
            Tick();
        }
        return 0;
    }

    private static Border Bar() => new() { Height = 4, Width = 0, CornerRadius = new CornerRadius(2), Background = Frozen(Color.FromArgb(0xFF, 0x8B, 0x7C, 0xFF)), HorizontalAlignment = HorizontalAlignment.Left };

    private static StackPanel Meter(string label, TextBlock value, Border bar)
    {
        Grid row = new() { Margin = new Thickness(0, 2, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new TextBlock { Text = label, Foreground = InkDim, FontSize = 13 });
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        StackPanel meter = new();
        meter.Children.Add(row);
        meter.Children.Add(new Border { Width = 120, Height = 4, CornerRadius = new CornerRadius(2), Background = Highlight, HorizontalAlignment = HorizontalAlignment.Left, Child = bar });
        return meter;
    }
}

/// <summary>Shell icons for files, folders and shortcuts (cached per path for the session).</summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string path)
    {
        if (Cache.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? image = null;
        FileInfo info = default;
        if (GetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100 /* SHGFI_ICON | large */) != 0 && info.Icon != 0)
        {
            try
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                image = bitmap;
            }
            finally { _ = DestroyIcon(info.Icon); }
        }
        if (Cache.Count > 2000) Cache.Clear();
        Cache[path] = image;
        return image;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern nint GetFileInfoW(string path, uint attributes, ref FileInfo info, uint size, uint flags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
