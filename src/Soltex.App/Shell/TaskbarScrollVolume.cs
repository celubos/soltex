using System.Runtime.InteropServices;

namespace Soltex.App.Shell;

/// <summary>
/// Scroll over the taskbar to change the volume (Windhawk "taskbar volume control"-class). A low-level mouse hook on its
/// own thread (never the UI thread, so the pointer never waits on the app) turns each wheel notch over a taskbar into a
/// volume key press; Windows changes the default device and shows its own volume flyout. Off unless the user turns it on.
/// </summary>
public sealed unsafe class TaskbarScrollVolume : IDisposable
{
    private const int MouseLowLevelHook = 14, MouseWheel = 0x020A, Quit = 0x0012, VolumeMessage = 0x8000 + 0x61, Notch = 120, MaxPresses = 10;
    private const ushort VolumeUp = 0xAF, VolumeDown = 0xAE;
    private const uint KeyUp = 0x0002, InputKeyboard = 1;
    private static volatile uint hookThread;
    private static int wheelRemainder; // hook thread only: wheel movement over the taskbar not yet turned into a key press
    private Thread? thread;
    private readonly ManualResetEventSlim ready = new();
    private volatile bool running;

    public bool Enabled => running;

    /// <summary>Starts or stops the hook. Returns false when Windows refused the hook.</summary>
    public bool SetEnabled(bool enabled)
    {
        if (enabled == running) return true;
        if (!enabled)
        {
            Stop();
            return true;
        }
        ready.Reset();
        thread = new Thread(Run) { IsBackground = true, Name = "Taskbar scroll volume" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return running;
    }

    /// <summary>
    /// Adds a wheel delta to <paramref name="remainder"/> and returns the volume key presses for the whole notches (120
    /// each) now in it, up to ten; the part of a notch left over stays in <paramref name="remainder"/>. High-resolution
    /// wheels and touchpads report one notch as several smaller deltas, which add up to one press instead of one each.
    /// Turning the wheel the other way starts over.
    /// </summary>
    public static (ushort Key, int Presses) Steps(ref int remainder, int delta)
    {
        ushort key = delta > 0 ? VolumeUp : VolumeDown;
        if (delta == 0) return (key, 0);
        if (remainder != 0 && (remainder > 0) != (delta > 0)) remainder = 0;
        remainder += delta;
        int presses = Math.Min(Math.Abs(remainder) / Notch, MaxPresses);
        remainder %= Notch; // movement past the cap is dropped, not saved for later
        return (key, presses);
    }

    private void Run()
    {
        wheelRemainder = 0;
        delegate* unmanaged<int, nint, nint, nint> callback = &OnMouse;
        nint hook = SetWindowsHookExW(MouseLowLevelHook, (nint)callback, GetModuleHandleW(null), 0);
        hookThread = GetCurrentThreadId();
        running = hook != 0;
        ready.Set();
        if (!running) return;
        try
        {
            while (GetMessageW(out Message message, 0, 0, 0) > 0)
            {
                if (message.Id == VolumeMessage) Press((ushort)message.WParam, (int)message.LParam);
            }
        }
        finally
        {
            _ = UnhookWindowsHookEx(hook);
            running = false;
            hookThread = 0;
        }
    }

    private void Stop()
    {
        uint id = hookThread;
        if (id != 0) _ = PostThreadMessageW(id, Quit, 0, 0);
        thread?.Join(TimeSpan.FromSeconds(5));
        thread = null;
    }

    [UnmanagedCallersOnly]
    private static nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message == MouseWheel)
        {
            MouseData* mouse = (MouseData*)data;
            if (OverTaskbar(mouse->X, mouse->Y))
            {
                (ushort key, int presses) = Steps(ref wheelRemainder, (short)(mouse->MouseInfo >> 16));
                if (presses == 0) return 1; // less than a notch so far: nothing to press yet, but the wheel is still ours
                // Press the keys after the hook returns: low-level hooks must answer quickly.
                if (PostThreadMessageW(hookThread, VolumeMessage, key, presses)) return 1; // handled: the wheel goes no further
            }
            else wheelRemainder = 0; // scrolling elsewhere: a partial notch from earlier does not carry over
        }
        return CallNextHookEx(0, code, message, data);
    }

    private static bool OverTaskbar(int x, int y)
    {
        nint window = GetAncestor(WindowFromPoint(new Point(x, y)), 2); // GA_ROOT
        if (window == 0) return false;
        char* name = stackalloc char[32];
        int length = GetClassNameW(window, name, 32);
        string className = new(name, 0, Math.Max(0, length));
        return className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static void Press(ushort key, int presses)
    {
        Span<KeyboardInput> inputs = stackalloc KeyboardInput[2];
        inputs[0] = new KeyboardInput { Type = InputKeyboard, Key = key };
        inputs[1] = new KeyboardInput { Type = InputKeyboard, Key = key, Flags = KeyUp };
        for (int i = 0; i < presses; i++)
            fixed (KeyboardInput* pointer = inputs) _ = SendInput(2, pointer, sizeof(KeyboardInput));
    }

    public void Dispose()
    {
        Stop();
        ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point(int x, int y) { public int X = x, Y = y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { public int X, Y; public uint MouseInfo, Flags, Time; public nint Extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public Point Point; }

    // INPUT with a KEYBDINPUT, padded to the size of the union's largest member (MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyboardInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint Extra;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int type, nint procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, char* name, int capacity);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyboardInput* inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
