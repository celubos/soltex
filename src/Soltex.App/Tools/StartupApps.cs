using System.IO;
using Microsoft.Win32;

namespace Soltex.App.Tools;

public enum StartupSource { UserRun, MachineRun, UserFolder, MachineFolder }

/// <summary>One program Windows starts at sign-in.</summary>
public sealed record StartupApp(string Name, string Command, StartupSource Source, bool Enabled)
{
    /// <summary>Entries for every user need administrator rights to change (Task Manager can).</summary>
    public bool CanChange => Source is StartupSource.UserRun or StartupSource.UserFolder;
}

/// <summary>
/// Startup apps: the Run keys and Startup folders, and the same on/off switch Task Manager uses (Explorer's
/// StartupApproved values), so a turned-off app is never deleted and can be turned on again.
/// </summary>
/// <remarks>
/// <paramref name="machine32Root"/> is where 32-bit installers' machine-wide Run entries live (the 32-bit registry view,
/// WOW6432Node), with their state in StartupApproved\Run32. By default it is read from the real registry only when the
/// real machine registry is used and this is a 64-bit process.
/// </remarks>
public sealed class StartupApps(RegistryKey? userRoot = null, RegistryKey? machineRoot = null, string? userFolder = null, string? machineFolder = null,
    RegistryKey? machine32Root = null)
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private readonly RegistryKey user = userRoot ?? Registry.CurrentUser, machine = machineRoot ?? Registry.LocalMachine;
    private readonly RegistryKey? machine32 = machine32Root;
    private readonly bool realMachine = machineRoot is null;
    private readonly string userStartup = userFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        machineStartup = machineFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    public IReadOnlyList<StartupApp> List()
    {
        List<StartupApp> apps = [];
        AddRun(apps, user, user, "Run", StartupSource.UserRun);
        AddRun(apps, machine, machine, "Run", StartupSource.MachineRun);
        if (machine32 is not null) AddRun(apps, machine32, machine, "Run32", StartupSource.MachineRun);
        else if (realMachine && Environment.Is64BitProcess)
        {
            using RegistryKey view32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            AddRun(apps, view32, machine, "Run32", StartupSource.MachineRun);
        }
        AddFolder(apps, userStartup, user, StartupSource.UserFolder);
        AddFolder(apps, machineStartup, machine, StartupSource.MachineFolder);
        return [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Turns an app on or off at sign-in (only the user's own entries).</summary>
    public void SetEnabled(StartupApp app, bool enabled)
    {
        if (!app.CanChange) throw new UnauthorizedAccessException("Apps that start for every user need administrator rights; use Task Manager.");
        string valueName = app.Source == StartupSource.UserFolder ? app.Name + Path.GetExtension(app.Command) : app.Name;
        using RegistryKey approved = user.CreateSubKey(Approved + (app.Source == StartupSource.UserFolder ? "StartupFolder" : "Run"));
        byte[] data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        approved.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    /// <summary>Adds a Run key's entries; their on/off state is read from <paramref name="approvedRoot"/>'s StartupApproved\<paramref name="approvedList"/>.</summary>
    private static void AddRun(List<StartupApp> apps, RegistryKey runRoot, RegistryKey approvedRoot, string approvedList, StartupSource source)
    {
        using RegistryKey? run = runRoot.OpenSubKey(Run);
        if (run is null) return;
        foreach (string name in run.GetValueNames())
            if (name.Length > 0 && run.GetValue(name) is string command
                && !apps.Any(a => a.Source == source && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && a.Command.Equals(command, StringComparison.OrdinalIgnoreCase)))
                apps.Add(new StartupApp(name, command, source, IsEnabled(approvedRoot, approvedList, name)));
    }

    private static void AddFolder(List<StartupApp> apps, string folder, RegistryKey root, StartupSource source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.EnumerateFiles(folder).Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            apps.Add(new StartupApp(Path.GetFileNameWithoutExtension(file), file, source, IsEnabled(root, "StartupFolder", Path.GetFileName(file))));
    }

    /// <summary>StartupApproved: the first byte's low bit set means turned off (Task Manager's format).</summary>
    private static bool IsEnabled(RegistryKey root, string list, string valueName)
    {
        using RegistryKey? approved = root.OpenSubKey(Approved + list);
        return approved?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }
}
