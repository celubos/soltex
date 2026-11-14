using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Soltex.App.Desktop;

/// <summary>
/// Tells desktop features when File Explorer has started again (the desktop window and its wallpaper layer are new
/// windows) and when screens were added, removed or resized. A hidden top-level window receives Explorer's
/// "TaskbarCreated" broadcast, WM_DISPLAYCHANGE and work-area changes; each kind is settled briefly so a burst becomes
/// one event. Create, use and dispose on the UI thread.
/// </summary>
internal sealed class DesktopEvents : IDisposable
{
    private const int DisplayChange = 0x007E, SettingChange = 0x001A, SetWorkArea = 0x002F;
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly HwndSource source;
    private readonly DispatcherTimer shellSettle, displaySettle;
    private bool disposed;

    public DesktopEvents() : this(TimeSpan.FromSeconds(1)) { }

    internal DesktopEvents(TimeSpan settle)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        shellSettle = new DispatcherTimer(settle, DispatcherPriority.Background, (_, _) => Fire(shellSettle!, ShellRestarted), dispatcher);
        shellSettle.Stop();
        displaySettle = new DispatcherTimer(settle, DispatcherPriority.Background, (_, _) => Fire(displaySettle!, DisplayChanged), dispatcher);
        displaySettle.Stop();
        source = new HwndSource(new HwndSourceParameters("Soltex desktop events")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP and never shown
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            PositionX = -32000,
            PositionY = -32000,
            Width = 1,
            Height = 1
        });
        source.AddHook(Hook);
        // Explorer runs at medium integrity; let its broadcast through if this app was started elevated.
        if (TaskbarCreated != 0) _ = ChangeWindowMessageFilterEx(source.Handle, TaskbarCreated, 1, 0);
    }

    /// <summary>Explorer started again: windows owned by or placed inside the old desktop must be attached again.</summary>
    public event Action? ShellRestarted;

    /// <summary>A screen was added, removed or resized, or the work area changed.</summary>
    public event Action? DisplayChanged;

    /// <summary>The hidden window's handle (tests post messages to it).</summary>
    internal nint Handle => source.Handle;

    internal static uint ShellRestartMessage => TaskbarCreated;

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (TaskbarCreated != 0 && message == (int)TaskbarCreated) Restart(shellSettle);
        else if (message == DisplayChange || message == SettingChange && wParam == SetWorkArea) Restart(displaySettle);
        return 0;
    }

    private void Restart(DispatcherTimer timer)
    {
        if (disposed) return;
        timer.Stop();
        timer.Start();
    }

    private void Fire(DispatcherTimer timer, Action? handler)
    {
        timer.Stop();
        if (!disposed) handler?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        shellSettle.Stop();
        displaySettle.Stop();
        source.RemoveHook(Hook);
        source.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint filterInfo);
}
