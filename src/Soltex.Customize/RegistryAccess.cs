using Microsoft.Win32;

namespace Soltex.Customize;

public enum RegistryHive2 { CurrentUser, LocalMachine }
public enum ValueKind { DWord, Text }

/// <summary>A registry value, or its absence. Absence is a real state Windows treats as "default".</summary>
public readonly record struct RegistryValue(bool Exists, ValueKind Kind, string? Text)
{
    public static RegistryValue Absent { get; } = new(false, ValueKind.DWord, null);
    public static RegistryValue Dword(int value) => new(true, ValueKind.DWord, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static RegistryValue FromText(string value) => new(true, ValueKind.Text, value);
    public override string ToString() => Exists ? Text ?? "" : "(not set)";
}

/// <summary>Registry seam. Reads any hive; writes are limited to the current user by the real implementation.</summary>
public interface IRegistryStore
{
    RegistryValue Read(RegistryHive2 hive, string path, string name);
    void Write(string currentUserPath, string name, RegistryValue value);
}

/// <summary>Real registry. Writes only HKEY_CURRENT_USER, so no change here can need elevation or affect other users.</summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    public RegistryValue Read(RegistryHive2 hive, string path, string name)
    {
        using RegistryKey? key = (hive == RegistryHive2.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine).OpenSubKey(path, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            int dword => RegistryValue.Dword(dword),
            string text => RegistryValue.FromText(text),
            null => RegistryValue.Absent,
            object other => new RegistryValue(true, ValueKind.Text, other.ToString())
        };
    }

    public void Write(string currentUserPath, string name, RegistryValue value)
    {
        NeverTouch.Guard(RegistryHive2.CurrentUser, currentUserPath, name);
        if (!value.Exists)
        {
            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(currentUserPath, writable: true))
                existing?.DeleteValue(name, throwOnMissingValue: false);
            RemoveEmptyClassKeys(currentUserPath);
            return;
        }
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(currentUserPath, writable: true)
            ?? throw new UnauthorizedAccessException("Windows did not allow this setting to be changed.");
        if (value.Kind == ValueKind.DWord)
            key.SetValue(name, int.Parse(value.Text!, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.DWord);
        else
            key.SetValue(name, value.Text ?? "", RegistryValueKind.String);
    }

    /// <summary>
    /// Per-user COM overrides (Software\Classes\CLSID\{id}\...) take effect while the key exists, even empty, so removing
    /// the value is not enough to go back: empty keys this app's override created are removed up to the CLSID itself.
    /// </summary>
    private static void RemoveEmptyClassKeys(string path)
    {
        const string Root = @"Software\Classes\CLSID\";
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return;
        string current = path.TrimEnd('\\');
        while (current.Length > Root.Length && current.LastIndexOf('\\') >= Root.Length - 1)
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(current, writable: false))
            {
                if (key is null) { current = current[..current.LastIndexOf('\\')]; continue; }
                if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
            }
            Registry.CurrentUser.DeleteSubKey(current, throwOnMissingSubKey: false);
            current = current[..current.LastIndexOf('\\')];
        }
    }
}

/// <summary>
/// Hard guard against security, update and integrity settings. Applies to every write regardless of
/// catalogue content. The De-Windows profile can explain these, never change them.
/// </summary>
public static class NeverTouch
{
    private static readonly string[] DeniedPathFragments =
    [
        @"\Windows Defender", @"\Microsoft Defender", @"\Windows Advanced Threat Protection", @"\WindowsFirewall", @"\SharedAccess",
        // Fragments end at a key boundary so "\Windows\System\" does not also match "\Windows\SystemSettings".
        @"\WindowsUpdate", @"\Windows\CurrentVersion\Policies\System\", @"\Control\CI\", @"\DeviceGuard", @"\SecureBoot",
        @"\Windows Security Health", @"\WTDS", @"\Windows\System\", @"\AppHost", @"\Windows Error Reporting\Consent",
        @"\Lsa", @"\Explorer\Advanced\ShowSuperHidden"
    ];

    private static readonly string[] DeniedValueNames = ["EnableLUA", "ConsentPromptBehaviorAdmin", "EnableSmartScreen", "SmartScreenEnabled", "ShowSuperHidden"];

    public static bool IsDenied(RegistryHive2 hive, string path, string name)
    {
        _ = hive;
        string full = @"\" + path.Trim('\\') + @"\" + name;
        return DeniedPathFragments.Any(f => full.Contains(f, StringComparison.OrdinalIgnoreCase))
            || DeniedValueNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    public static void Guard(RegistryHive2 hive, string path, string name)
    {
        if (IsDenied(hive, path, name))
            throw new UnauthorizedAccessException("This setting protects your PC's security or updates, so this app will not change it.");
    }
}
