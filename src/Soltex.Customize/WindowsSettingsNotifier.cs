using System.Runtime.InteropServices;

namespace Soltex.Customize;

/// <summary>
/// Broadcasts WM_SETTINGCHANGE so Explorer, the taskbar and other running apps re-read per-user settings, then tells
/// the shell that file associations changed so the desktop and open File Explorer windows redraw (desktop icons and
/// Explorer view options show at once). Bounded with SMTO_ABORTIFHUNG so a hung window can never block the caller for
/// long; the shell notification is posted, not waited on.
/// </summary>
public sealed partial class WindowsSettingsNotifier : ISettingsChangeNotifier
{
    private const int WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
    private static readonly IntPtr Broadcast = new(0xFFFF);

    public void Notify()
    {
        foreach (string area in new[] { "TraySettings", "Policy", "ImmersiveColorSet" })
        {
            _ = SendMessageTimeout(Broadcast, WmSettingChange, IntPtr.Zero, area, SmtoAbortIfHung, 1000, out _);
        }
        // Without SHCNF_FLUSH the notification is queued for the shell and this returns at once.
        try { SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or SEHException) { }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, int message, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
