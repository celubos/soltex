using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

/// <summary>
/// Small always-available utilities (PowerToys-class): keep the PC awake, pin the active window on top
/// (Win+Ctrl+T) and copy the colour under the pointer (Win+Shift+C). Hotkeys are registered on the shell's window,
/// so they work while the window is hidden in the tray; nothing polls.
/// </summary>
public sealed partial class UtilityService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int AlwaysOnTopId = 0x5101, ColorPickerId = 0x5102, TextExtractorId = 0x5103;
    private const uint ModShift = 0x0004, ModControl = 0x0002, ModWin = 0x0008, ModNoRepeat = 0x4000;
    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    private readonly AppSettingsStore settings;
    private HwndSource? source;
    private (bool AlwaysOnTop, bool ColorPicker, bool TextExtractor)? registered;
    private bool keepAwake, disposed;

    public UtilityService(AppSettingsStore settings)
    {
        this.settings = settings;
        settings.Changed += next =>
        {
            // Most settings changes do not touch the shortcuts; re-registering them each time is needless churn.
            if (registered != (next.AlwaysOnTopShortcut, next.ColorPickerShortcut, next.TextExtractorShortcut)) ApplyShortcuts();
        };
    }

    /// <summary>Raised after any utility state changes (shortcut registration, keep awake).</summary>
    public event Action? Changed;

    /// <summary>Short confirmation for the user, e.g. "#1E90FF copied".</summary>
    public event Action<string>? Feedback;

    public string? AlwaysOnTopProblem { get; private set; }
    public string? ColorPickerProblem { get; private set; }
    public string? TextExtractorProblem { get; private set; }

    public void Attach(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        source?.AddHook(Hook);
        ApplyShortcuts();
    }

    /// <summary>Keeps the PC and display from sleeping while on. Not saved: it ends when the app quits.</summary>
    public bool KeepAwake
    {
        get => keepAwake;
        set
        {
            keepAwake = value;
            _ = SetThreadExecutionState(value ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
            Changed?.Invoke();
        }
    }

    private void ApplyShortcuts()
    {
        if (source is null || disposed) return;
        registered = (settings.Current.AlwaysOnTopShortcut, settings.Current.ColorPickerShortcut, settings.Current.TextExtractorShortcut);
        AlwaysOnTopProblem = Register(AlwaysOnTopId, settings.Current.AlwaysOnTopShortcut, ModWin | ModControl, 'T');
        ColorPickerProblem = Register(ColorPickerId, settings.Current.ColorPickerShortcut, ModWin | ModShift, 'C');
        TextExtractorProblem = Register(TextExtractorId, settings.Current.TextExtractorShortcut, ModWin | ModShift, 'T');
        Changed?.Invoke();
    }

    private string? Register(int id, bool wanted, uint modifiers, char key)
    {
        IntPtr window = source!.Handle;
        _ = UnregisterHotKey(window, id);
        if (!wanted) return null;
        return RegisterHotKey(window, id, modifiers | ModNoRepeat, key) ? null : "This shortcut is already used by another app.";
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case AlwaysOnTopId:
                handled = true;
                if (ToggleTopmost(GetForegroundWindow()) is { } pinned) Feedback?.Invoke(pinned ? "Always on top" : "No longer on top");
                break;
            case TextExtractorId:
                handled = true;
                _ = ExtractTextAsync();
                break;
            case ColorPickerId:
                handled = true;
                if (ColorUnderPointer() is { } hex)
                {
                    try { Clipboard.SetText(hex); Feedback?.Invoke(hex + " copied"); }
                    catch (COMException) { Feedback?.Invoke(hex); }
                }
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Runs the text extractor; a failure is told to the user instead of ending as an unobserved task.</summary>
    private async Task ExtractTextAsync()
    {
        try { await Tools.TextExtractor.RunAsync(message => Feedback?.Invoke(message)).ConfigureAwait(true); }
        catch (Exception error) when (error is ArgumentException or ExternalException or InvalidOperationException
            or System.IO.IOException or UnauthorizedAccessException)
        {
            Feedback?.Invoke("Text could not be read from that area.");
        }
    }

    /// <summary>Pins or unpins a top-level window. Returns the new state, or null for the desktop and taskbar.</summary>
    internal static bool? ToggleTopmost(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        char[] buffer = new char[64];
        int length = GetClassName(window, buffer, buffer.Length);
        string className = new(buffer, 0, Math.Max(0, length));
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        bool topmost = (GetWindowLong(window, -20) & 0x00000008) != 0;
        IntPtr insertAfter = topmost ? new IntPtr(-2) : new IntPtr(-1);
        return SetWindowPos(window, insertAfter, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010) ? !topmost : null;
    }

    /// <summary>The colour of the screen pixel under the pointer as #RRGGBB.</summary>
    internal static string? ColorUnderPointer()
    {
        if (!GetCursorPos(out Point point)) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            uint color = GetPixel(screen, point.X, point.Y);
            if (color == 0xFFFFFFFF) return null;
            return string.Create(CultureInfo.InvariantCulture, $"#{color & 0xFF:X2}{(color >> 8) & 0xFF:X2}{(color >> 16) & 0xFF:X2}");
        }
        finally { _ = ReleaseDC(IntPtr.Zero, screen); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (source is not null)
        {
            _ = UnregisterHotKey(source.Handle, AlwaysOnTopId);
            _ = UnregisterHotKey(source.Handle, ColorPickerId);
            _ = UnregisterHotKey(source.Handle, TextExtractorId);
            source.RemoveHook(Hook);
        }
        if (keepAwake) _ = SetThreadExecutionState(EsContinuous);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);
    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr window, [Out] char[] name, int max);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr window, int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);
    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);
    [LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(IntPtr dc, int x, int y);
}
