using Microsoft.Win32;

namespace Soltex.App;

internal static class SuiteStartupRegistration
{
    private const string KeyName = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyName);
        return string.Equals(key?.GetValue("Soltex") as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyName, writable: true);
        if (enabled) key.SetValue("Soltex", Command, RegistryValueKind.String);
        else if (string.Equals(key.GetValue("Soltex") as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue("Soltex", false);
    }
}
