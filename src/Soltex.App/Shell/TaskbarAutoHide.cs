using System.Runtime.InteropServices;

namespace Soltex.App.Shell;

/// <summary>
/// Windows' own "Automatically hide the taskbar". Windows keeps it inside a binary blob (StuckRects3), not a value of
/// its own, so it is read and set live through the taskbar's app bar state, exactly as Windows Settings does; Windows
/// saves the new state itself. Only the auto-hide bit changes; the always-on-top bit stays as Windows had it.
/// </summary>
internal static partial class TaskbarAutoHide
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbmSetState = 0x0000000A;
    private const int AbsAutoHide = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public int Left, Top, Right, Bottom;
        public nint Param;
    }

    /// <summary>Whether the taskbar hides itself, or null when there is no taskbar to ask (Explorer is not running).</summary>
    public static bool? IsOn()
    {
        if (Tray() is not { } tray) return null;
        AppBarData data = New(tray);
        return ((int)SHAppBarMessage(AbmGetState, ref data) & AbsAutoHide) != 0;
    }

    /// <summary>Turns auto-hide on or off. False when there is no taskbar to change.</summary>
    public static bool Set(bool on)
    {
        if (Tray() is not { } tray) return false;
        AppBarData data = New(tray);
        int state = (int)SHAppBarMessage(AbmGetState, ref data);
        data = New(tray);
        data.Param = on ? state | AbsAutoHide : state & ~AbsAutoHide;
        _ = SHAppBarMessage(AbmSetState, ref data);
        return true;
    }

    private static nint? Tray()
    {
        nint tray = FindWindow("Shell_TrayWnd", null);
        return tray == 0 ? null : tray;
    }

    private static AppBarData New(nint tray) => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = tray };

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string? className, string? windowName);
}
