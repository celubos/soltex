using Microsoft.Win32;

namespace Soltex.App.Shell;

/// <summary>A protection feature that a machine policy currently switches off. Read-only observation.</summary>
internal sealed record PolicyFinding(string Feature, string Effect);

/// <summary>
/// Reads (never writes) the documented policy values that turn off Windows protection features.
/// Used to explain states such as "cloud protection is off" in plain language.
/// </summary>
internal static class ProtectionPolicies
{
    public static IReadOnlyList<PolicyFinding> Read()
    {
        List<PolicyFinding> findings = [];
        int? spynet = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SpynetReporting");
        if (spynet == 0)
            findings.Add(new("Cloud-delivered protection", "Microsoft Defender cannot ask the cloud about new threats, so brand-new malware is caught later."));
        int? samples = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows Defender\Spynet", "SubmitSamplesConsent");
        if (samples == 2)
            findings.Add(new("Automatic sample submission", "Suspicious files are never sent for analysis."));
        int? smartScreen = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        if (smartScreen == 0)
            findings.Add(new("SmartScreen for apps and files", "Windows no longer warns before you run unrecognized downloads."));
        int? phishing = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows\WTDS\Components", "ServiceEnabled");
        if (phishing == 0)
            findings.Add(new("Enhanced phishing protection", "Windows no longer warns when you type your password into a suspicious site or app."));
        int? realtime = ReadDword(@"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection", "DisableRealtimeMonitoring");
        if (realtime == 1)
            findings.Add(new("Real-time protection", "Files are not scanned as they are opened or downloaded."));
        return findings;
    }

    private static int? ReadDword(string subKey, string name)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKey, writable: false);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return null;
        }
    }
}

/// <summary>Friendly Windows edition text. The registry ProductName still says "Windows 10" on Windows 11.</summary>
internal static class WindowsDescription
{
    public static string Describe()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            string edition = key?.GetValue("EditionID") as string ?? "";
            string display = key?.GetValue("DisplayVersion") as string ?? "";
            string build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
            object? ubr = key?.GetValue("UBR");
            if (!int.TryParse(build, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int buildNumber)) buildNumber = 0;
            string family = buildNumber >= 22000 ? "Windows 11" : "Windows 10";
            string editionName = edition switch
            {
                "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
                "Professional" => "Pro",
                "Enterprise" or "EnterpriseS" => "Enterprise",
                "Education" => "Education",
                "" => "",
                _ => edition
            };
            string version = display.Length > 0 ? " " + display : "";
            string fullBuild = ubr is int u ? $"{build}.{u}" : build;
            return $"{family} {editionName}{version} (build {fullBuild})".Replace("  ", " ", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return "Windows " + Environment.OSVersion.Version;
        }
    }
}
