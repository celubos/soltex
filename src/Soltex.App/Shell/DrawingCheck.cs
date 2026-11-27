using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Soltex.App.Shell;

/// <summary>
/// Some graphics drivers accept WPF's Direct3D drawing and never show it: every WPF window on the PC, this app's
/// included, is a blank rectangle while the app works normally underneath. Once the main window has drawn, its
/// pixels are read back the way Windows shows them; when the whole client area is one flat color the app switches
/// to software drawing, which those drivers show correctly. The switch is remembered until Windows restarts, so a
/// later start draws correctly from its first frame, and a driver that recovers gets the graphics card back.
/// </summary>
internal sealed class DrawingCheck
{
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(700);
    /// <summary>Boot times computed from the tick count drift by a few seconds; a real restart is far apart.</summary>
    private static readonly TimeSpan SameBootTolerance = TimeSpan.FromMinutes(2);

    private readonly string stateFile;
    private readonly Action<string> report;
    private DispatcherTimer? pending;
    /// <summary>Blank captures in a row; the switch needs two, so a frame Windows has not shown yet is never mistaken for one.</summary>
    private int blankInARow;
    private bool softwareOnly;

    public DrawingCheck(string stateFile, Action<string> report)
    {
        this.stateFile = stateFile;
        this.report = report;
    }

    /// <summary>True once this process draws in software, either from the start or after a blank window was seen.</summary>
    public static bool UsingSoftware => RenderOptions.ProcessRenderMode == RenderMode.SoftwareOnly;

    internal static DateTime CurrentBootUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Call before the first window is created: draws in software when this boot already showed a blank window.</summary>
    public void ApplyAtStartup()
    {
        if (BlankSeenThisBoot(stateFile, CurrentBootUtc())) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    /// <summary>Checks the window each time it is shown or restored, while it draws with the graphics card.</summary>
    public void Watch(Window window)
    {
        window.ContentRendered += (_, _) => Schedule(window);
        window.IsVisibleChanged += (_, _) => Schedule(window);
        window.StateChanged += (_, _) => Schedule(window);
    }

    private void Schedule(Window window)
    {
        if (softwareOnly || UsingSoftware || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
        pending?.Stop();
        pending = new DispatcherTimer(DispatcherPriority.ApplicationIdle, window.Dispatcher) { Interval = SettleTime };
        pending.Tick += (_, _) =>
        {
            pending?.Stop();
            pending = null;
            Check(window);
        };
        pending.Start();
    }

    private void Check(Window window)
    {
        if (softwareOnly || UsingSoftware || !window.IsVisible || window.WindowState == WindowState.Minimized) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || IsCloaked(handle)) return;
        // Hardware drawing is only in use at tier 1 and up; at tier 0 WPF already draws in software.
        if (RenderCapability.Tier >> 16 == 0) { softwareOnly = true; return; }
        if (CaptureClient(handle) is not { } capture) return;
        if (!IsBlank(capture.Pixels, capture.Width, capture.Height))
        {
            blankInARow = 0;
            return;
        }
        if (++blankInARow < 2)
        {
            Schedule(window);
            return;
        }
        UseSoftware();
        RememberBlank(stateFile, CurrentBootUtc());
        report("The graphics driver showed this window blank, so Soltex now draws it without the graphics card. It tries the graphics card again after Windows restarts.");
    }

    /// <summary>Switches every open window and every later one to software drawing.</summary>
    internal static void UseSoftware()
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        foreach (object source in (IEnumerable)PresentationSource.CurrentSources)
        {
            if (source is HwndSource { IsDisposed: false, CompositionTarget: { } target }) target.RenderMode = RenderMode.SoftwareOnly;
        }
    }

    /// <summary>
    /// True when every sampled pixel of a 32-bit capture is the same color. A drawn Soltex window never is: the rail
    /// alone has text and icons. Captures too small to judge are never called blank.
    /// </summary>
    internal static bool IsBlank(ReadOnlySpan<uint> pixels, int width, int height)
    {
        if (width < 64 || height < 64 || pixels.Length < width * height) return false;
        uint first = pixels[0];
        for (int y = 0; y < height; y += 2)
        {
            int row = y * width;
            for (int x = 0; x < width; x += 2)
            {
                if (pixels[row + x] != first) return false;
            }
        }
        return true;
    }

    internal static bool BlankSeenThisBoot(string stateFile, DateTime bootUtc)
    {
        try
        {
            string text = File.ReadAllText(stateFile).Trim();
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime seenBoot)) return false;
            return (seenBoot - bootUtc).Duration() <= SameBootTolerance;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static void RememberBlank(string stateFile, DateTime bootUtc)
    {
        try
        {
            if (Path.GetDirectoryName(stateFile) is { } directory) Directory.CreateDirectory(directory);
            File.WriteAllText(stateFile, bootUtc.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Not remembered: the next start checks again and switches again.
        }
    }

    private static bool IsCloaked(nint handle) =>
        DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private sealed record Capture(uint[] Pixels, int Width, int Height);

    /// <summary>The client area as Windows composes it (PW_RENDERFULLCONTENT reads the composed Direct3D content).</summary>
    private static Capture? CaptureClient(nint handle)
    {
        if (!GetWindowRect(handle, out Rect window) || !GetClientRect(handle, out Rect client)) return null;
        Point origin = new() { X = 0, Y = 0 };
        if (!ClientToScreen(handle, ref origin)) return null;
        int windowWidth = window.Right - window.Left, windowHeight = window.Bottom - window.Top;
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        int offsetX = origin.X - window.Left, offsetY = origin.Y - window.Top;
        if (windowWidth <= 0 || windowHeight <= 0 || width <= 0 || height <= 0) return null;
        if (offsetX < 0 || offsetY < 0 || offsetX + width > windowWidth || offsetY + height > windowHeight) return null;

        nint screen = GetDC(0);
        if (screen == 0) return null;
        nint memory = CreateCompatibleDC(screen);
        BitmapInfo info = new()
        {
            Size = Marshal.SizeOf<BitmapInfo>(),
            Width = windowWidth,
            Height = -windowHeight,
            Planes = 1,
            BitCount = 32
        };
        nint bitmap = CreateDIBSection(screen, ref info, 0, out nint bits, 0, 0);
        try
        {
            if (memory == 0 || bitmap == 0 || bits == 0) return null;
            nint previous = SelectObject(memory, bitmap);
            bool printed = PrintWindow(handle, memory, PwRenderFullContent);
            SelectObject(memory, previous);
            if (!printed) return null;
            uint[] pixels = new uint[width * height];
            int[] row = new int[width];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bits + (((y + offsetY) * windowWidth) + offsetX) * 4, row, 0, width);
                // Alpha is undefined in a GDI capture; only the color counts.
                for (int x = 0; x < width; x++) pixels[(y * width) + x] = (uint)row[x] & 0x00FF_FFFF;
            }
            return new Capture(pixels, width, height);
        }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            _ = ReleaseDC(0, screen);
        }
    }

    private const uint PwRenderFullContent = 2;
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        public int Colors;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
