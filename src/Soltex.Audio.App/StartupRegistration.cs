using System.IO;
using Microsoft.Win32;

namespace Soltex.Audio.App;

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SoltexAudio";
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "Soltex.Audio.Desktop.exe");
    private static string Command => "\"" + Executable + "\" --tray";
    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }
    public static void SetEnabled(bool enabled)
    {
        if (enabled == Enabled) return;
        if (enabled && !File.Exists(Executable)) throw new IOException("Publish an executable package before enabling launch at login.");
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (enabled) key.SetValue(Name, Command, RegistryValueKind.String);
        else if (string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
