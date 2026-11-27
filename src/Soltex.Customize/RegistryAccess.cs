using System.Globalization;
using Microsoft.Win32;

namespace Soltex.Customize;

public enum RegistryHive2 { CurrentUser, LocalMachine }

/// <summary>
/// The registry type of a value, so a value is put back exactly as it was. New kinds are added at the end: journals
/// store the names. <see cref="Untyped"/> is REG_NONE; <see cref="Unsupported"/> is any other type (it is read and
/// shown, never written, so a value of that type is never changed).
/// </summary>
public enum ValueKind { DWord, Text, QWord, ExpandText, MultiText, Binary, Untyped, Unsupported }

/// <summary>
/// A registry value, or its absence. Absence is a real state Windows treats as "default". <see cref="Text"/> holds the
/// data: a decimal number for DWORD and QWORD, the raw (unexpanded) string for text, every string followed by a NUL for
/// a multi-string, and base64 for binary data.
/// </summary>
public readonly record struct RegistryValue(bool Exists, ValueKind Kind, string? Text)
{
    public static RegistryValue Absent { get; } = new(false, ValueKind.DWord, null);
    public static RegistryValue Dword(int value) => new(true, ValueKind.DWord, value.ToString(CultureInfo.InvariantCulture));
    public static RegistryValue Qword(long value) => new(true, ValueKind.QWord, value.ToString(CultureInfo.InvariantCulture));
    public static RegistryValue FromText(string value) => new(true, ValueKind.Text, value);
    public static RegistryValue ExpandText(string value) => new(true, ValueKind.ExpandText, value);
    public static RegistryValue MultiText(IEnumerable<string> values) => new(true, ValueKind.MultiText, string.Concat(values.Select(v => v + "\0")));
    public static RegistryValue Binary(byte[] data) => new(true, ValueKind.Binary, Convert.ToBase64String(data));

    /// <summary>The strings of a multi-string value; null when this is not a well-formed one.</summary>
    public IReadOnlyList<string>? Strings()
    {
        if (!Exists || Kind != ValueKind.MultiText || Text is null) return null;
        if (Text.Length == 0) return [];
        return Text[^1] == '\0' ? Text[..^1].Split('\0') : null;
    }

    /// <summary>The bytes of a binary or REG_NONE value; null when this is not a well-formed one.</summary>
    public byte[]? Bytes()
    {
        if (!Exists || Kind is not (ValueKind.Binary or ValueKind.Untyped) || Text is null) return null;
        try { return Convert.FromBase64String(Text); }
        catch (FormatException) { return null; }
    }

    public override string ToString() => !Exists ? "(not set)" : Kind switch
    {
        ValueKind.MultiText => string.Join("; ", Strings() ?? []),
        ValueKind.Binary or ValueKind.Untyped or ValueKind.Unsupported => "binary data",
        _ => Text ?? ""
    };
}

/// <summary>Reads and writes <see cref="RegistryValue"/>s with their registry type, shared by the per-user and administrator sides.</summary>
internal static class RegistryData
{
    /// <summary>The value with its type, or <see cref="RegistryValue.Absent"/>.</summary>
    public static RegistryValue Read(RegistryKey? key, string name)
    {
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } data) return RegistryValue.Absent;
        RegistryValueKind kind;
        try { kind = key.GetValueKind(name); }
        catch (IOException) { return RegistryValue.Absent; } // removed between the two reads
        return From(kind, data);
    }

    internal static RegistryValue From(RegistryValueKind kind, object data) => (kind, data) switch
    {
        (RegistryValueKind.DWord, int number) => RegistryValue.Dword(number),
        (RegistryValueKind.QWord, long number) => RegistryValue.Qword(number),
        (RegistryValueKind.String, string text) => RegistryValue.FromText(text),
        (RegistryValueKind.ExpandString, string text) => RegistryValue.ExpandText(text),
        (RegistryValueKind.MultiString, string[] texts) => RegistryValue.MultiText(texts),
        (RegistryValueKind.Binary, byte[] bytes) => RegistryValue.Binary(bytes),
        (RegistryValueKind.None, byte[] bytes) => new(true, ValueKind.Untyped, Convert.ToBase64String(bytes)),
        (_, byte[] bytes) => new(true, ValueKind.Unsupported, Convert.ToBase64String(bytes)),
        _ => new(true, ValueKind.Unsupported, Convert.ToString(data, CultureInfo.InvariantCulture))
    };

    /// <summary>
    /// The data and registry type to write for a value. False for a value this app never writes (an unsupported type or
    /// malformed data, for example from an edited journal), with the reason.
    /// </summary>
    internal static bool TryEncode(RegistryValue value, out object data, out RegistryValueKind kind, out string reason)
    {
        data = ""; kind = RegistryValueKind.String; reason = "";
        switch (value.Kind)
        {
            case ValueKind.DWord when int.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int dword):
                (data, kind) = (dword, RegistryValueKind.DWord); return true;
            case ValueKind.QWord when long.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long qword):
                (data, kind) = (qword, RegistryValueKind.QWord); return true;
            case ValueKind.Text:
                (data, kind) = (value.Text ?? "", RegistryValueKind.String); return true;
            case ValueKind.ExpandText:
                (data, kind) = (value.Text ?? "", RegistryValueKind.ExpandString); return true;
            case ValueKind.MultiText when value.Strings() is { } strings:
                (data, kind) = (strings.ToArray(), RegistryValueKind.MultiString); return true;
            case ValueKind.Binary when value.Bytes() is { } bytes:
                (data, kind) = (bytes, RegistryValueKind.Binary); return true;
            case ValueKind.Untyped when value.Bytes() is { } bytes:
                (data, kind) = (bytes, RegistryValueKind.None); return true;
            case ValueKind.Unsupported:
                reason = "This value has a registry type this app does not change."; return false;
            default:
                reason = "The value to write is malformed (" + value.Kind + ")."; return false;
        }
    }

    /// <summary>Writes a value with its own registry type. Throws <see cref="IOException"/> for a value this app never writes.</summary>
    public static void Write(RegistryKey key, string name, RegistryValue value)
    {
        if (!TryEncode(value, out object data, out RegistryValueKind kind, out string reason)) throw new IOException(reason);
        key.SetValue(name, data, kind);
    }
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
        return RegistryData.Read(key, name);
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
        // Refused before the key is created, so a value this app never writes leaves no empty key behind.
        if (!RegistryData.TryEncode(value, out _, out _, out string reason)) throw new IOException(reason);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(currentUserPath, writable: true)
            ?? throw new UnauthorizedAccessException("Windows did not allow this setting to be changed.");
        RegistryData.Write(key, name, value);
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
