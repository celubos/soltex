using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Soltex.App.Settings;

namespace Soltex.App.Tools;

/// <summary>Zone rectangles for a monitor's work area, in physical pixels.</summary>
public static class ZoneLayouts
{
    public static IReadOnlyList<Int32Rect> For(WindowZonesLayout layout, Int32Rect area)
    {
        int x = area.X, y = area.Y, w = area.Width, h = area.Height;
        Int32Rect Part(double left, double top, double width, double height) =>
            new(x + (int)Math.Round(w * left), y + (int)Math.Round(h * top), (int)Math.Round(w * width), (int)Math.Round(h * height));
        return layout switch
        {
            WindowZonesLayout.TwoColumns => [Part(0, 0, 0.5, 1), Part(0.5, 0, 0.5, 1)],
            WindowZonesLayout.ThreeColumns => [Part(0, 0, 1 / 3.0, 1), Part(1 / 3.0, 0, 1 / 3.0, 1), Part(2 / 3.0, 0, 1 / 3.0, 1)],
            WindowZonesLayout.Grid => [Part(0, 0, 0.5, 0.5), Part(0.5, 0, 0.5, 0.5), Part(0, 0.5, 0.5, 0.5), Part(0.5, 0.5, 0.5, 0.5)],
            WindowZonesLayout.Focus => [Part(0, 0, 0.25, 1), Part(0.25, 0, 0.5, 1), Part(0.75, 0, 0.25, 1)],
            _ => []
        };
    }

    public static int ZoneAt(IReadOnlyList<Int32Rect> zones, int x, int y)
    {
        for (int i = 0; i < zones.Count; i++)
        {
            Int32Rect z = zones[i];
            if (x >= z.X && x < z.X + z.Width && y >= z.Y && y < z.Y + z.Height) return i;
        }
        return -1;
    }
}

/// <summary>
/// Window zones (FancyZones-class): while a window is being dragged with Shift held, the zones of the monitor under the
/// pointer are shown; releasing over a zone fits the window to it. Listens for move/size start and end only while a
/// layout is chosen; no polling otherwise.
/// </summary>
public sealed unsafe class WindowZonesService : IDisposable
{
    private static WindowZonesService? current;
    private readonly AppSettingsStore settings;
    private readonly DispatcherTimer tracking;
    private nint hook, moving;
    private ZoneOverlay? overlay;
    private IReadOnlyList<Int32Rect> zones = [];
    private int zone = -1;
    private bool attached, disposed;

    public WindowZonesService(AppSettingsStore settings)
    {
        this.settings = settings;
        tracking = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Input, (_, _) => Track(), Dispatcher.CurrentDispatcher);
        tracking.Stop();
        settings.Changed += OnSettingsChanged;
    }

    /// <summary>Starts following the setting. Before this (and after Dispose) settings changes never install the hook.</summary>
    public void Attach()
    {
        if (disposed) return;
        attached = true;
        Update();
    }

    private void OnSettingsChanged(AppSettings _) => Application.Current?.Dispatcher.InvokeAsync(Update);

    private void Update()
    {
        bool wanted = attached && !disposed && settings.Current.WindowZones != WindowZonesLayout.Off;
        if (wanted && hook == 0)
        {
            current = this;
            delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback = &OnEvent;
            hook = SetWinEventHook(0x000A, 0x000B, 0, (nint)callback, 0, 0, 0); // EVENT_SYSTEM_MOVESIZESTART..END, out of context
        }
        else if (!wanted && hook != 0)
        {
            _ = UnhookWinEvent(hook);
            hook = 0;
            End(apply: false);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (current is null || objectId != 0) return;
        if (eventType == 0x000A) current.Begin(window);
        else current.End(apply: true);
    }

    private void Begin(nint window)
    {
        moving = window;
        zone = -1;
        tracking.Start();
    }

    private void Track()
    {
        if (moving == 0) { tracking.Stop(); return; }
        // The physical state: while another app runs its move loop, this thread gets no keyboard input, so
        // GetKeyState would keep reporting Shift as it was when this app lost focus.
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (!shift || !GetCursorPos(out Point point))
        {
            overlay?.Hide();
            zone = -1;
            return;
        }
        Int32Rect area = WorkAreaAt(point);
        zones = ZoneLayouts.For(settings.Current.WindowZones, area);
        zone = ZoneLayouts.ZoneAt(zones, point.X, point.Y);
        overlay ??= new ZoneOverlay();
        overlay.ShowZones(area, zones, zone);
    }

    private void End(bool apply)
    {
        tracking.Stop();
        nint window = moving;
        moving = 0;
        overlay?.Hide();
        if (apply && window != 0 && zone >= 0 && zone < zones.Count) FitToZone(window, zones[zone]);
        zone = -1;
    }

    /// <summary>Restores the window if maximized and fits its visible frame to the zone.</summary>
    internal static void FitToZone(nint window, Int32Rect target)
    {
        if (IsZoomed(window)) _ = ShowWindow(window, 9); // SW_RESTORE
        _ = GetWindowRect(window, out Rect outer);
        Rect frame = outer;
        if (DwmGetWindowAttribute(window, 9, &frame, sizeof(Rect)) != 0) frame = outer; // DWMWA_EXTENDED_FRAME_BOUNDS
        int left = frame.Left - outer.Left, top = frame.Top - outer.Top, right = outer.Right - frame.Right, bottom = outer.Bottom - frame.Bottom;
        _ = SetWindowPos(window, 0, target.X - left, target.Y - top, target.Width + left + right, target.Height + top + bottom, 0x0004 | 0x0010);
    }

    private static Int32Rect WorkAreaAt(Point point)
    {
        nint monitor = MonitorFromPoint(point, 2); // nearest
        MonitorInfo info = new() { Size = sizeof(MonitorInfo) };
        if (!GetMonitorInfoW(monitor, ref info)) return new Int32Rect(0, 0, 1920, 1080);
        return new Int32Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    public void Dispose()
    {
        disposed = true;
        settings.Changed -= OnSettingsChanged;
        if (hook != 0) _ = UnhookWinEvent(hook);
        hook = 0;
        tracking.Stop();
        overlay?.Close();
        if (current == this) current = null;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, nint callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
}

/// <summary>A click-through, topmost window over one monitor's work area that draws the zones.</summary>
internal sealed class ZoneOverlay : Window
{
    private readonly Canvas canvas = new();
    private static readonly Brush Fill = DesktopSurfaceBrushes.Make(0x40, 0x8B, 0x7C, 0xFF), Active = DesktopSurfaceBrushes.Make(0x90, 0x8B, 0x7C, 0xFF),
        Edge = DesktopSurfaceBrushes.Make(0xE0, 0xFF, 0xFF, 0xFF);

    public ZoneOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = canvas;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint style = GetWindowLongPtrW(handle, -20);
            _ = SetWindowLongPtrW(handle, -20, style | 0x20 | 0x80 | 0x08000000); // transparent, tool window, no activate
        };
    }

    public void ShowZones(Int32Rect area, IReadOnlyList<Int32Rect> zones, int active)
    {
        if (!IsVisible) Show();
        nint handle = new WindowInteropHelper(this).Handle;
        _ = SetWindowPos(handle, -1, area.X, area.Y, area.Width, area.Height, 0x0010); // HWND_TOPMOST, no activate
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        canvas.Children.Clear();
        for (int i = 0; i < zones.Count; i++)
        {
            Int32Rect z = zones[i];
            Rectangle shape = new()
            {
                Width = Math.Max(0, z.Width / scale - 12), Height = Math.Max(0, z.Height / scale - 12),
                Fill = i == active ? Active : Fill, Stroke = Edge, StrokeThickness = 2, RadiusX = 8, RadiusY = 8
            };
            Canvas.SetLeft(shape, (z.X - area.X) / scale + 6);
            Canvas.SetTop(shape, (z.Y - area.Y) / scale + 6);
            canvas.Children.Add(shape);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

internal static class DesktopSurfaceBrushes
{
    public static Brush Make(byte a, byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
