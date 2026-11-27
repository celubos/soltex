using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Soltex.Customize;

/// <summary>
/// The real Windows side of the system engine. Reads work for any user; writes need administrator rights and run
/// only inside the elevated De-Windows helper. Uses the registry for policies and service start types, sc.exe to
/// stop services, schtasks.exe for tasks, netsh for firewall block rules, the products' own uninstallers for
/// OneDrive and Edge, and the Windows package manager for Store apps (current user).
/// </summary>
public sealed class WindowsSystemHost : ISystemHost
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(4);
    private HashSet<string>? packages;

    public RegistryValue Read(RegistryHive2 hive, string path, string name)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(hive == RegistryHive2.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using RegistryKey? key = root.OpenSubKey(path, writable: false);
        return RegistryData.Read(key, name);
    }

    public void Write(RegistryHive2 hive, string path, string name, RegistryValue value)
    {
        using RegistryKey root = RegistryKey.OpenBaseKey(hive == RegistryHive2.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        if (!value.Exists)
        {
            using RegistryKey? existing = root.OpenSubKey(path, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }
        if (!RegistryData.TryEncode(value, out _, out _, out string reason)) throw new IOException(reason);
        using RegistryKey key = root.CreateSubKey(path, writable: true) ?? throw new UnauthorizedAccessException("Windows did not allow this change.");
        RegistryData.Write(key, name, value);
    }

    public int? ServiceStart(string service)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service, writable: false);
        return key?.GetValue("Start") is int start ? start : null;
    }

    public void SetServiceStart(string service, int startType)
    {
        string mode = startType switch { 2 => "auto", 3 => "demand", 4 => "disabled", _ => throw new ArgumentOutOfRangeException(nameof(startType)) };
        Run("sc.exe", ["config", service, "start=", mode], ToolTimeout, mustSucceed: true);
        if (startType == 4) Run("sc.exe", ["stop", service], ToolTimeout, mustSucceed: false);
    }

    public void StartService(string service)
    {
        (int code, string output) = Run("sc.exe", ["start", service], ToolTimeout, mustSucceed: false);
        // 1056: already running.
        if (code is not (0 or 1056))
            throw new InvalidOperationException("sc.exe failed (" + code.ToString(CultureInfo.InvariantCulture) + "): " + output.Trim());
    }

    public bool? TaskEnabled(string taskPath)
    {
        (int code, string output) = Run("schtasks.exe", ["/Query", "/TN", taskPath, "/XML", "ONE"], ToolTimeout, mustSucceed: false);
        if (code != 0) return null;
        // The task XML only carries <Enabled>false</Enabled> under Settings when the task is disabled.
        int settings = output.IndexOf("<Settings>", StringComparison.Ordinal);
        int end = settings < 0 ? -1 : output.IndexOf("</Settings>", settings, StringComparison.Ordinal);
        string block = settings < 0 || end < 0 ? output : output[settings..end];
        return !block.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
    }

    public void SetTaskEnabled(string taskPath, bool enabled) =>
        Run("schtasks.exe", ["/Change", "/TN", taskPath, enabled ? "/ENABLE" : "/DISABLE"], ToolTimeout, mustSucceed: true);

    public bool FirewallRuleExists(string ruleName) =>
        Run("netsh.exe", ["advfirewall", "firewall", "show", "rule", "name=" + ruleName], ToolTimeout, mustSucceed: false).Code == 0;

    public void AddFirewallBlock(FirewallAction rule)
    {
        List<string> args = ["advfirewall", "firewall", "add", "rule", "name=" + rule.RuleName, "dir=out", "action=block", "enable=yes", "profile=any"];
        if (rule.Service is { } service) args.Add("service=" + service);
        if (rule.Program is { } program) args.Add("program=" + Environment.ExpandEnvironmentVariables(program));
        Run("netsh.exe", args, ToolTimeout, mustSucceed: true);
    }

    public void RemoveFirewallRule(string ruleName) =>
        Run("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=" + ruleName], ToolTimeout, mustSucceed: true);

    public bool? AppInstalled(RemoveAppAction app) => app.Kind switch
    {
        AppKind.OneDrive => OneDriveExecutables().Any(File.Exists),
        AppKind.Edge => File.Exists(EdgeExecutable()),
        _ => StorePackages().Contains(app.PackageName ?? "") ? true : null
    };

    public void RemoveApp(RemoveAppAction app)
    {
        switch (app.Kind)
        {
            case AppKind.OneDrive:
                Run("taskkill.exe", ["/F", "/IM", "OneDrive.exe"], ToolTimeout, mustSucceed: false);
                string? setup = OneDriveUninstallers().FirstOrDefault(File.Exists)
                    ?? throw new InvalidOperationException("The OneDrive uninstaller was not found.");
                Run(setup, ["/uninstall"], UninstallTimeout, mustSucceed: false);
                break;
            case AppKind.Edge:
                string edgeDirectory = Path.GetDirectoryName(EdgeExecutable())!;
                string? installer = Directory.Exists(edgeDirectory)
                    ? Directory.GetDirectories(edgeDirectory).Select(d => Path.Combine(d, "Installer", "setup.exe")).Where(File.Exists).OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault()
                    : null;
                if (installer is null) throw new InvalidOperationException("The Edge uninstaller was not found.");
                Run("taskkill.exe", ["/F", "/IM", "msedge.exe"], ToolTimeout, mustSucceed: false);
                Run(installer, ["--uninstall", "--system-level", "--verbose-logging", "--force-uninstall"], UninstallTimeout, mustSucceed: false);
                break;
            default:
                try { RemoveStorePackages(app); }
                finally { packages = null; }
                break;
        }
    }

    /// <summary>Removes every package of a Store app for the current user. Package manager errors become one plain exception.</summary>
    private static void RemoveStorePackages(RemoveAppAction app)
    {
        try
        {
            Windows.Management.Deployment.PackageManager manager = new();
            foreach (Windows.ApplicationModel.Package package in manager.FindPackagesForUser(string.Empty)
                         .Where(p => string.Equals(p.Id.Name, app.PackageName, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var operation = manager.RemovePackageAsync(package.Id.FullName);
                if (!operation.AsTask().Wait(UninstallTimeout)) throw new TimeoutException("Removing " + app.Name + " did not finish.");
            }
        }
        catch (AggregateException error) when (error.InnerException is { } inner)
        {
            throw new InvalidOperationException("Windows could not remove " + app.Name + " (" + Describe(inner) + ").", inner);
        }
        catch (COMException error)
        {
            throw new InvalidOperationException("Windows could not remove " + app.Name + " (" + Describe(error) + ").", error);
        }
    }

    private static string Describe(Exception error) =>
        error is COMException com ? "0x" + com.HResult.ToString("X8", CultureInfo.InvariantCulture) : error.Message;

    public bool OtherBrowserInstalled()
    {
        foreach (RegistryKey root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using RegistryKey? clients = root.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet", writable: false);
            if (clients?.GetSubKeyNames().Any(n => !n.Contains("Edge", StringComparison.OrdinalIgnoreCase) && !n.Equals("IEXPLORE.EXE", StringComparison.OrdinalIgnoreCase)) == true)
                return true;
        }
        return false;
    }

    private HashSet<string> StorePackages()
    {
        if (packages is not null) return packages;
        try
        {
            packages = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty)
                .Select(p => p.Id.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        return packages;
    }

    private static string EdgeExecutable() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");

    private static IEnumerable<string> OneDriveExecutables()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive", "OneDrive.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive", "OneDrive.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive", "OneDrive.exe");
    }

    private static IEnumerable<string> OneDriveUninstallers()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "OneDrive");
        if (Directory.Exists(local))
            foreach (string version in Directory.GetDirectories(local).OrderByDescending(d => d, StringComparer.Ordinal))
                yield return Path.Combine(version, "OneDriveSetup.exe");
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string machine = Path.Combine(root, "Microsoft OneDrive");
            if (Directory.Exists(machine))
                foreach (string version in Directory.GetDirectories(machine).OrderByDescending(d => d, StringComparer.Ordinal))
                    yield return Path.Combine(version, "OneDriveSetup.exe");
        }
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        yield return Path.Combine(windows, "System32", "OneDriveSetup.exe");
        yield return Path.Combine(windows, "SysWOW64", "OneDriveSetup.exe");
    }

    private static (int Code, string Output) Run(string file, IEnumerable<string> arguments, TimeSpan timeout, bool mustSucceed)
    {
        string path = Path.IsPathRooted(file) ? file : Path.Combine(Environment.SystemDirectory, file);
        ProcessStartInfo start = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process? started;
        try { started = Process.Start(start); }
        catch (Win32Exception failure) { throw new InvalidOperationException(Path.GetFileName(file) + " could not start: " + failure.Message, failure); }
        using Process process = started ?? throw new InvalidOperationException(Path.GetFileName(file) + " did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException(Path.GetFileName(file) + " did not finish.");
        }
        string text = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (mustSucceed && process.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(file) + " failed (" + process.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + text.Trim());
        return (process.ExitCode, text);
    }
}
