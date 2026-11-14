using System.Runtime.InteropServices;

namespace Soltex.Customize;

/// <summary>
/// Broadcasts WM_SETTINGCHANGE so Explorer, the taskbar and other running apps re-read per-user settings.
/// Bounded with SMTO_ABORTIFHUNG so a hung window can never block the caller for long.
/// </summary>
public sealed partial class WindowsSettingsNotifier : ISettingsChangeNotifier
{
    private const int WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr Broadcast = new(0xFFFF);

    public void Notify()
    {
        foreach (string area in new[] { "TraySettings", "Policy", "ImmersiveColorSet" })
        {
            _ = SendMessageTimeout(Broadcast, WmSettingChange, IntPtr.Zero, area, SmtoAbortIfHung, 1000, out _);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
}
