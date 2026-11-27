using Microsoft.Win32;

namespace Soltex.App;

/// <summary>Whether Windows starts this app at sign-in, as Settings › Start when I sign in to Windows shows it.</summary>
internal enum SuiteStartupState
{
    /// <summary>No sign-in entry for this copy of the app.</summary>
    Off,
    /// <summary>Starts at sign-in.</summary>
    On,
    /// <summary>The entry is there, but it was turned off in Task Manager's Startup apps (or Settings › Apps › Startup).</summary>
    TurnedOffInTaskManager
}

/// <summary>
/// "Start when I sign in to Windows": the app's value in HKCU\...\Run, together with the on/off switch Task Manager keeps
/// for it in Explorer's StartupApproved\Run. Task Manager turns an app off there without removing its Run value, so both
/// are read, and turning the app on here also turns it back on there.
/// </summary>
/// <remarks>Tests pass an isolated <c>root</c> key (under HKCU\Software\Soltex) and their own command line.</remarks>
internal static class SuiteStartupRegistration
{
    internal const string ValueName = "Soltex";
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";
    private static readonly byte[] TurnedOn = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static bool IsEnabled(RegistryKey? root = null, string? command = null) => State(root, command) == SuiteStartupState.On;

    public static SuiteStartupState State(RegistryKey? root = null, string? command = null)
    {
        RegistryKey hive = root ?? Registry.CurrentUser;
        using (RegistryKey? run = hive.OpenSubKey(RunKey))
        {
            if (!string.Equals(run?.GetValue(ValueName) as string, command ?? Command, StringComparison.OrdinalIgnoreCase)) return SuiteStartupState.Off;
        }
        using RegistryKey? approved = hive.OpenSubKey(ApprovedKey);
        // Task Manager's format: the first byte's low bit set (0x03, 0x07…) means turned off; no value means on.
        return approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) != 0
            ? SuiteStartupState.TurnedOffInTaskManager
            : SuiteStartupState.On;
    }

    public static void SetEnabled(bool enabled, RegistryKey? root = null, string? command = null)
    {
        RegistryKey hive = root ?? Registry.CurrentUser;
        string line = command ?? Command;
        using RegistryKey run = hive.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            run.SetValue(ValueName, line, RegistryValueKind.String);
            // Turned off in Task Manager earlier: switch it back on there too (0x02, as Task Manager writes it), or the
            // Run value alone would still not start the app.
            using RegistryKey? approved = hive.OpenSubKey(ApprovedKey, writable: true);
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) != 0)
                approved.SetValue(ValueName, TurnedOn, RegistryValueKind.Binary);
        }
        else if (string.Equals(run.GetValue(ValueName) as string, line, StringComparison.OrdinalIgnoreCase))
        {
            run.DeleteValue(ValueName, false);
            // Task Manager's switch for the removed entry goes with it, so turning the app on later starts clean.
            using RegistryKey? approved = hive.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, false);
        }
    }
}
