using System.Runtime.InteropServices;
using Soltex.App.Settings;

namespace Soltex.App.Tools;

/// <summary>
/// Key remapping (PowerToys Keyboard Manager-class): a low-level keyboard hook on its own thread turns a remapped key's
/// presses into the target key (or nothing). Keys this app sends itself are ignored, so a remap can never loop. Only runs
/// while at least one remap exists.
/// </summary>
/// <remarks>
/// Every target key the hook presses is remembered until it is released, so a target is never left held down: it is
/// released when its source key comes up (even if the remap changed in between), when the remap changes or stops, and
/// when focus comes back from an administrator window (Windows does not show this hook the keys typed there, so the
/// source key's release can be missed).
/// </remarks>
public sealed unsafe class KeyRemapper : IDisposable
{
    /// <summary>Keys offered for remapping (virtual-key code, name).</summary>
    public static readonly IReadOnlyList<(int Key, string Name)> Keys =
    [
        (0x14, "Caps Lock"), (0x1B, "Esc"), (0xA2, "Left Ctrl"), (0xA3, "Right Ctrl"), (0xA4, "Left Alt"), (0xA5, "Right Alt"),
        (0x5B, "Left Windows"), (0x5C, "Right Windows"), (0x5D, "Menu"), (0x2D, "Insert"), (0x91, "Scroll Lock"), (0x13, "Pause"),
        (0x90, "Num Lock"), (0x09, "Tab"), (0x08, "Backspace"), (0x2E, "Delete"), (0x24, "Home"), (0x23, "End"),
        (0x70, "F1"), (0x71, "F2"), (0x72, "F3"), (0x73, "F4"), (0x74, "F5"), (0x75, "F6"), (0x76, "F7"), (0x77, "F8"),
        (0x78, "F9"), (0x79, "F10"), (0x7A, "F11"), (0x7B, "F12"), (0xB3, "Play/Pause"), (0xAD, "Mute"), (0xAE, "Volume down"),
        (0xAF, "Volume up")
    ];

    /// <summary>Ctrl, Alt and Windows keys: remapping one of these by accident breaks shortcuts everywhere.</summary>
    public static bool IsModifier(int key) => key is 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private const int KeyboardLowLevelHook = 13, Quit = 0x0012, ReleaseChanged = 0x8001; // WM_QUIT, WM_APP + 1
    private const uint Injected = 0x10, KeyUpFlag = 0x0002, ExtendedFlag = 0x0001;
    private const int MediumIntegrity = 0x2000;
    private static volatile Dictionary<int, int> map = [];
    private static volatile uint hookThread;

    // Target keys the hook has pressed and not yet released (source key → target key), and whether focus is on a window
    // whose keys the hook cannot see. Only used on the hook thread.
    private static readonly Dictionary<int, int> held = [];
    private static bool blind;
    private static int ownIntegrity = MediumIntegrity;
    private Thread? thread;

    public static bool Running => hookThread != 0;

    /// <summary>Applies the remaps (none stops the hook). Returns false when Windows refused the hook.</summary>
    public bool Set(IReadOnlyList<KeyRemap> remaps)
    {
        map = remaps.Where(r => r.From != r.To).GroupBy(r => r.From).ToDictionary(g => g.Key, g => g.First().To);
        if (map.Count == 0) { Stop(); return true; }
        if (Running)
        {
            // Release, on the hook thread, any target that is held down for a remap that just changed.
            _ = PostThreadMessageW(hookThread, ReleaseChanged, 0, 0);
            return true;
        }
        using ManualResetEventSlim ready = new();
        thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Key remapping" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return Running;
    }

    /// <summary>The key a press of <paramref name="key"/> becomes: itself when not remapped, 0 when turned off.</summary>
    public static int Target(int key) => map.TryGetValue(key, out int to) ? to : key;

    /// <summary>
    /// The hardware scan code and extended-key flag sent with <paramref name="key"/>, so programs that read scan codes
    /// (games, remote and virtual machine consoles, web pages) recognise the remapped key.
    /// </summary>
    internal static (ushort Scan, bool Extended) ScanCode(int key)
    {
        uint code = MapVirtualKeyW((uint)key, 4); // MAPVK_VK_TO_VSC_EX: 0xE0nn for extended keys, 0xE11D for Pause
        ushort scan = (code >> 8) == 0xE1 ? (ushort)0x45 : (ushort)(code & 0xFF); // Pause arrives as 0x45, not extended
        return (scan, Extended(key) || (code >> 8) == 0xE0);
    }

    private static void Run(ManualResetEventSlim ready)
    {
        ownIntegrity = IntegrityOf(GetCurrentProcessId()) ?? MediumIntegrity;
        delegate* unmanaged<int, nint, nint, nint> callback = &OnKey;
        nint hook = SetWindowsHookExW(KeyboardLowLevelHook, (nint)callback, GetModuleHandleW(null), 0);
        if (hook != 0) hookThread = GetCurrentThreadId();
        ready.Set();
        if (hook == 0) return;
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> onForeground = &OnForeground;
        nint foreground = SetWinEventHook(0x0003, 0x0003, 0, (nint)onForeground, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
        try
        {
            while (GetMessageW(out Message message, 0, 0, 0) > 0)
                if (message.Window == 0 && message.Id == ReleaseChanged) ReleaseChangedTargets();
        }
        finally
        {
            if (foreground != 0) _ = UnhookWinEvent(foreground);
            _ = UnhookWindowsHookEx(hook);
            ReleaseAll(); // after unhooking: this thread no longer pumps, so the hook must not be asked about these keys
            hookThread = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnKey(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            KeyData* key = (KeyData*)data;
            if ((key->Flags & Injected) == 0)
            {
                int source = (int)key->Key;
                bool up = message is 0x0101 or 0x0105; // WM_KEYUP, WM_SYSKEYUP
                if (up && held.Remove(source, out int pressed))
                {
                    // Release what was actually pressed, even if the remap changed while the key was down.
                    if (!held.ContainsValue(pressed)) Send(pressed, up: true);
                    if (held.Count == 0) blind = false;
                    return 1;
                }
                if (map.TryGetValue(source, out int to))
                {
                    if (!up && to != 0)
                    {
                        if (held.TryGetValue(source, out int previous) && previous != to && !HeldByOther(source, previous)) Send(previous, up: true);
                        held[source] = to;
                        Send(to, up: false);
                    }
                    return 1; // the original key goes no further
                }
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    [UnmanagedCallersOnly]
    private static void OnForeground(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (held.Count == 0) { blind = false; return; }
        if (!CanSee(window)) blind = true;
        else if (blind) ReleaseAll(); // the source key may have been released where the hook could not see it
    }

    private static bool HeldByOther(int source, int target)
    {
        foreach ((int other, int pressed) in held)
            if (other != source && pressed == target) return true;
        return false;
    }

    private static void ReleaseChangedTargets()
    {
        foreach ((int source, int target) in held.ToArray())
        {
            if (map.TryGetValue(source, out int to) && to == target) continue;
            held.Remove(source);
            if (!held.ContainsValue(target)) Send(target, up: true);
        }
        if (held.Count == 0) blind = false;
    }

    private static void ReleaseAll()
    {
        foreach (int target in held.Values.Distinct().ToArray()) Send(target, up: true);
        held.Clear();
        blind = false;
    }

    private static void Send(int key, bool up)
    {
        (ushort scan, bool extended) = ScanCode(key);
        KeyboardInput input = new() { Type = 1, Key = (ushort)key, Scan = scan, Flags = (up ? KeyUpFlag : 0) | (extended ? ExtendedFlag : 0) };
        _ = SendInput(1, &input, sizeof(KeyboardInput));
    }

    private static bool Extended(int key) => key is 0xA3 or 0xA5 or 0x5B or 0x5C or 0x5D or 0x2D or 0x2E or 0x24 or 0x23 or 0x90 or 0xB3 or 0xAD or 0xAE or 0xAF;

    /// <summary>
    /// Whether the hook sees keys typed into <paramref name="window"/>: not when its app runs at a higher integrity level
    /// than this one (an administrator window), or when that cannot be told.
    /// </summary>
    private static bool CanSee(nint window)
    {
        if (window == 0 || GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0) return true;
        return IntegrityOf(processId) is int level && level <= ownIntegrity;
    }

    /// <summary>A process's mandatory integrity level (0x2000 medium, 0x3000 high), or null when it cannot be read.</summary>
    private static int? IntegrityOf(uint processId)
    {
        nint process = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, 0x0008, out nint token)) return null; // TOKEN_QUERY
            try
            {
                byte* label = stackalloc byte[128];
                if (!GetTokenInformation(token, 25, label, 128, out _)) return null; // TokenIntegrityLevel → TOKEN_MANDATORY_LABEL
                nint sid = *(nint*)label;
                byte count = *GetSidSubAuthorityCount(sid);
                return count == 0 ? null : (int)*GetSidSubAuthority(sid, (uint)(count - 1));
            }
            finally { _ = CloseHandle(token); }
        }
        finally { _ = CloseHandle(process); }
    }

    private void Stop()
    {
        uint id = hookThread;
        if (id != 0) _ = PostThreadMessageW(id, Quit, 0, 0);
        thread?.Join(TimeSpan.FromSeconds(5));
        thread = null;
    }

    public void Dispose()
    {
        map = [];
        Stop();
    }

    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, Scan, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public int X, Y; }
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
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, nint callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyboardInput* inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(nint token, int infoClass, void* info, int length, out int returned);
    [DllImport("advapi32.dll")] private static extern byte* GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern uint* GetSidSubAuthority(nint sid, uint index);
}
