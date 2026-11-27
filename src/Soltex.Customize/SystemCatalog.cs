using System.Globalization;

namespace Soltex.Customize;

/// <summary>Areas of the full (administrator) De-Windows profile.</summary>
public enum SystemArea { Telemetry, Ads, SearchAndAi, Updates, Apps }

/// <summary>One system-level change. Only catalogue entries are ever executed; a request names entries by id.</summary>
public abstract record SystemAction;

/// <summary>Sets (or removes, with <see cref="RegistryValue.Absent"/>) a policy value.</summary>
public sealed record PolicyAction(RegistryHive2 Hive, string Path, string Name, RegistryValue Value) : SystemAction;

/// <summary>Sets a service's start type (2 automatic, 3 manual, 4 disabled) and stops it when disabling.</summary>
public sealed record ServiceAction(string Service, int StartType) : SystemAction;

/// <summary>Enables or disables a scheduled task.</summary>
public sealed record TaskAction(string TaskPath, bool Enabled) : SystemAction;

/// <summary>Adds an outbound block rule for a service or a program. Blocking rules only add protection.</summary>
public sealed record FirewallAction(string RuleName, string? Service, string? Program) : SystemAction;

public enum AppKind { OneDrive, Edge, Store }

/// <summary>Removes an app. Not undoable here; the item says how to get it back.</summary>
public sealed record RemoveAppAction(AppKind Kind, string Name, string? PackageName = null) : SystemAction;

public sealed record SystemItem(string Id, SystemArea Area, string Title, IReadOnlyList<SystemAction> Actions, bool Default = true)
{
    private readonly string? subject;

    /// <summary>False when the item removes an app: removals cannot be rolled back by this app (its other changes can).</summary>
    public bool Reversible => !RemovesApps;

    /// <summary>True when the item removes at least one app.</summary>
    public bool RemovesApps => Actions.Any(a => a is RemoveAppAction);

    /// <summary>Where a removed app can be downloaded again: a web address, or "Microsoft Store".</summary>
    public string? Reinstall { get; init; }

    /// <summary>A short name of what the item is about, for sentences such as "Windows turned back on: OneDrive".</summary>
    public string Subject { get => subject ?? Title; init => subject = value; }
}

/// <summary>
/// The full De-Windows profile (needs administrator). Telemetry, advertising and AI data collection off by policy,
/// telemetry services and tasks disabled and firewalled, Windows Update set to ask before downloading (it still checks),
/// OneDrive, Edge and chosen built-in apps removed. Defender, the firewall itself, UAC, Secure Boot and driver signing
/// are never weakened: firewall entries here only add blocks, and no write (including an undo) turns updates off.
/// </summary>
public static class SystemCatalog
{
    private const string Policies = @"SOFTWARE\Policies\Microsoft\Windows";
    private const string DataCollection = Policies + @"\DataCollection";
    private const string CloudContent = Policies + @"\CloudContent";
    private const string Search = Policies + @"\Windows Search";
    private const string WindowsUpdate = Policies + @"\WindowsUpdate";
    private const string AutoUpdate = WindowsUpdate + @"\AU";

    /// <summary>Prefix of every firewall rule this app adds.</summary>
    public const string RuleTag = "Soltex - Block ";

    /// <summary>Prefix used by earlier versions; their rules are still recognised and removed by undo.</summary>
    public const string LegacyRuleTag = "Lintel - Block ";

    private const string MicrosoftStore = "Microsoft Store";

    private static RegistryValue D(int value) => RegistryValue.Dword(value);
    private static PolicyAction Lm(string path, string name, int value) => new(RegistryHive2.LocalMachine, path, name, D(value));
    private static PolicyAction Cu(string path, string name, int value) => new(RegistryHive2.CurrentUser, path, name, D(value));
    private static TaskAction Off(string path) => new(path, false);
    private static RemoveAppAction Store(string name, string package) => new(AppKind.Store, name, package);

    public static IReadOnlyList<SystemItem> All { get; } =
    [
        // ---- Telemetry and diagnostics ----
        new("system.telemetry.policy", SystemArea.Telemetry, "Send the minimum diagnostic data",
        [
            Lm(DataCollection, "AllowTelemetry", 0), Lm(DataCollection, "MaxTelemetryAllowed", 0), Lm(DataCollection, "AllowDeviceNameInTelemetry", 0),
            Lm(DataCollection, "DoNotShowFeedbackNotifications", 1), Lm(DataCollection, "LimitDiagnosticLogCollection", 1),
            Lm(DataCollection, "LimitDumpCollection", 1), Lm(DataCollection, "DisableOneSettingsDownloads", 1),
            Lm(DataCollection, "DisableEnterpriseAuthProxy", 1)
        ]) { Subject = "Full diagnostic data" },
        new("system.telemetry.services", SystemArea.Telemetry, "Turn off the telemetry services",
            [new ServiceAction("DiagTrack", 4), new ServiceAction("dmwappushservice", 4)]) { Subject = "Telemetry services" },
        new("system.telemetry.tasks", SystemArea.Telemetry, "Turn off telemetry scheduled tasks",
        [
            Off(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
            Off(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser Exp"),
            Off(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
            Off(@"\Microsoft\Windows\Application Experience\StartupAppTask"),
            Off(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
            Off(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
            Off(@"\Microsoft\Windows\Autochk\Proxy"),
            Off(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
            Off(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
            Off(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"),
            Off(@"\Microsoft\Windows\Windows Error Reporting\QueueReporting"),
            Off(@"\Microsoft\Windows\Maps\MapsUpdateTask"),
            Off(@"\Microsoft\Windows\CloudExperienceHost\CreateObjectTask")
        ]) { Subject = "Telemetry tasks" },
        new("system.telemetry.firewall", SystemArea.Telemetry, "Block telemetry programs from the network",
        [
            new FirewallAction(RuleTag + "DiagTrack", "DiagTrack", null),
            new FirewallAction(RuleTag + "CompatTelRunner", null, @"%SystemRoot%\System32\CompatTelRunner.exe"),
            new FirewallAction(RuleTag + "DeviceCensus", null, @"%SystemRoot%\System32\DeviceCensus.exe")
        ]) { Subject = "Network access for telemetry programs" },
        new("system.telemetry.ceip", SystemArea.Telemetry, "Turn off the improvement program and app inventory",
        [
            Lm(@"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable", 0),
            Lm(Policies + @"\AppCompat", "AITEnable", 0), Lm(Policies + @"\AppCompat", "DisableInventory", 1),
            Lm(Policies + @"\AppCompat", "DisableUAR", 1)
        ]) { Subject = "Improvement program and app inventory" },
        new("system.telemetry.errorreporting", SystemArea.Telemetry, "Don't send error reports",
            [Lm(Policies + @"\Windows Error Reporting", "Disabled", 1), Lm(@"SOFTWARE\Policies\Microsoft\PCHealth\ErrorReporting", "DoReport", 0)])
            { Subject = "Error reports" },
        new("system.telemetry.activity", SystemArea.Telemetry, "Don't collect or upload activity history",
            [Lm(Policies + @"\System", "EnableActivityFeed", 0), Lm(Policies + @"\System", "PublishUserActivities", 0), Lm(Policies + @"\System", "UploadUserActivities", 0)])
            { Subject = "Activity history" },
        new("system.telemetry.input", SystemArea.Telemetry, "Don't send typing and inking data",
            [Lm(@"SOFTWARE\Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", 0), Lm(Policies + @"\TabletPC", "PreventHandwritingDataSharing", 1),
             Lm(Policies + @"\HandwritingErrorReports", "PreventHandwritingErrorReports", 1)]) { Subject = "Typing and inking data" },
        new("system.telemetry.location", SystemArea.Telemetry, "Turn off location for all apps",
            [Lm(Policies + @"\LocationAndSensors", "DisableLocation", 1)], Default: false) { Subject = "Location" },

        // ---- Ads and suggestions ----
        new("system.ads.consumer", SystemArea.Ads, "No suggested apps, games or promotions",
        [
            Lm(CloudContent, "DisableWindowsConsumerFeatures", 1), Lm(CloudContent, "DisableSoftLanding", 1),
            Lm(CloudContent, "DisableCloudOptimizedContent", 1), Lm(CloudContent, "DisableConsumerAccountStateContent", 1),
            Cu(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", 1),
            Cu(@"Software\Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", 1)
        ]) { Subject = "Suggested apps and promotions" },
        new("system.ads.advertisingid", SystemArea.Ads, "Turn off the advertising ID for everyone",
            [Lm(Policies + @"\AdvertisingInfo", "DisabledByGroupPolicy", 1)]) { Subject = "Advertising ID" },

        // ---- Search and AI ----
        new("system.search.web", SystemArea.SearchAndAi, "Search only this PC, not Bing",
        [
            Lm(Search, "AllowCortana", 0), Lm(Search, "DisableWebSearch", 1), Lm(Search, "ConnectedSearchUseWeb", 0), Lm(Search, "AllowCloudSearch", 0),
            Cu(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1)
        ]) { Subject = "Bing in search" },
        new("system.ai.copilot", SystemArea.SearchAndAi, "Turn off Copilot",
            [Lm(Policies + @"\WindowsCopilot", "TurnOffWindowsCopilot", 1), Cu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1)])
            { Subject = "Copilot" },
        new("system.ai.recall", SystemArea.SearchAndAi, "Turn off Recall snapshots",
            [Lm(Policies + @"\WindowsAI", "DisableAIDataAnalysis", 1), Cu(@"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1)])
            { Subject = "Recall snapshots" },

        // ---- Updates ----
        new("system.updates.notify", SystemArea.Updates, "Ask before downloading updates; never restart while signed in",
            [Lm(AutoUpdate, "NoAutoUpdate", 0), Lm(AutoUpdate, "AUOptions", 2), Lm(AutoUpdate, "NoAutoRebootWithLoggedOnUsers", 1)])
            { Subject = "Automatic update downloads" },
        new("system.updates.p2p", SystemArea.Updates, "Don't share updates with other PCs",
            [Lm(Policies + @"\DeliveryOptimization", "DODownloadMode", 0)]) { Subject = "Update sharing with other PCs" },
        new("system.updates.drivers", SystemArea.Updates, "Don't install drivers through Windows Update",
            [Lm(WindowsUpdate, "ExcludeWUDriversInQualityUpdate", 1)], Default: false) { Subject = "Driver updates" },

        // ---- Apps ----
        new("system.apps.onedrive", SystemArea.Apps, "Remove OneDrive",
            [new RemoveAppAction(AppKind.OneDrive, "OneDrive"), Lm(Policies + @"\OneDrive", "DisableFileSyncNGSC", 1)])
            { Reinstall = "https://www.microsoft.com/microsoft-365/onedrive/download", Subject = "OneDrive" },
        new("system.apps.edge", SystemArea.Apps, "Remove Microsoft Edge (keeps WebView2, which other apps need)",
            [new RemoveAppAction(AppKind.Edge, "Microsoft Edge"), Lm(@"SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium", 1)])
            { Reinstall = "https://www.microsoft.com/edge/download", Subject = "Microsoft Edge" },
        new("system.apps.copilot", SystemArea.Apps, "Remove the Copilot app", [Store("Copilot", "Microsoft.Copilot")]) { Reinstall = MicrosoftStore, Subject = "Copilot app" },
        new("system.apps.news", SystemArea.Apps, "Remove News and Weather", [Store("News", "Microsoft.BingNews"), Store("Weather", "Microsoft.BingWeather")])
            { Reinstall = MicrosoftStore, Subject = "News and Weather" },
        new("system.apps.tips", SystemArea.Apps, "Remove Tips and Get Help", [Store("Tips", "Microsoft.Getstarted"), Store("Get Help", "Microsoft.GetHelp")])
            { Reinstall = MicrosoftStore, Subject = "Tips and Get Help" },
        new("system.apps.office", SystemArea.Apps, "Remove the Microsoft 365 (Office) hub", [Store("Microsoft 365", "Microsoft.MicrosoftOfficeHub")])
            { Reinstall = MicrosoftStore, Subject = "Microsoft 365 hub" },
        new("system.apps.solitaire", SystemArea.Apps, "Remove Solitaire", [Store("Solitaire", "Microsoft.MicrosoftSolitaireCollection")])
            { Reinstall = MicrosoftStore, Subject = "Solitaire" },
        new("system.apps.clipchamp", SystemArea.Apps, "Remove Clipchamp", [Store("Clipchamp", "Clipchamp.Clipchamp")]) { Reinstall = MicrosoftStore, Subject = "Clipchamp" },
        new("system.apps.feedback", SystemArea.Apps, "Remove Feedback Hub", [Store("Feedback Hub", "Microsoft.WindowsFeedbackHub")])
            { Reinstall = MicrosoftStore, Subject = "Feedback Hub" },
        new("system.apps.todo", SystemArea.Apps, "Remove To Do and Power Automate", [Store("To Do", "Microsoft.Todos"), Store("Power Automate", "Microsoft.PowerAutomateDesktop")])
            { Reinstall = MicrosoftStore, Subject = "To Do and Power Automate" },
        new("system.apps.outlook", SystemArea.Apps, "Remove the new Outlook and Mail", [Store("Outlook", "Microsoft.OutlookForWindows"), Store("Mail and Calendar", "microsoft.windowscommunicationsapps")])
            { Reinstall = MicrosoftStore, Subject = "Outlook and Mail" },
        // Only the consumer client: MSTeams is the work and school client and is never removed here.
        new("system.apps.teams", SystemArea.Apps, "Remove Teams (personal)", [Store("Teams (personal)", "MicrosoftTeams")])
            { Reinstall = MicrosoftStore, Subject = "Teams (personal)" },
        new("system.apps.people", SystemArea.Apps, "Remove People and Maps", [Store("People", "Microsoft.People"), Store("Maps", "Microsoft.WindowsMaps")])
            { Reinstall = MicrosoftStore, Subject = "People and Maps" },
        new("system.apps.phone", SystemArea.Apps, "Remove Phone Link", [Store("Phone Link", "Microsoft.YourPhone")], Default: false)
            { Reinstall = MicrosoftStore, Subject = "Phone Link" },
        new("system.apps.xbox", SystemArea.Apps, "Remove the Xbox apps (Game Bar stays)",
            [Store("Xbox", "Microsoft.GamingApp"), Store("Xbox Console Companion", "Microsoft.XboxApp"), Store("Xbox Identity", "Microsoft.XboxIdentityProvider")], Default: false)
            { Reinstall = MicrosoftStore, Subject = "Xbox apps" }
    ];

    public static string AreaTitle(SystemArea area) => area switch
    {
        SystemArea.Telemetry => "Telemetry and diagnostics",
        SystemArea.Ads => "Ads and suggestions",
        SystemArea.SearchAndAi => "Search and AI",
        SystemArea.Updates => "Windows Update",
        _ => "Apps"
    };

    /// <summary>One plain sentence on how to get an item's removed apps back, or null when it removes nothing.</summary>
    public static string? ReinstallHint(SystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.RemovesApps) return null;
        return ReinstallLink(item) is { } link
            ? "To get " + item.Subject + " back, download it from " + link.Host + "."
            : "To get " + item.Subject + " back, use the Microsoft Store.";
    }

    /// <summary>The download page of a removed app, or null when it comes back through the Microsoft Store.</summary>
    public static Uri? ReinstallLink(SystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Reinstall is { } where && Uri.TryCreate(where, UriKind.Absolute, out Uri? link) && link.Scheme == Uri.UriSchemeHttps ? link : null;
    }

    /// <summary>Every name a firewall rule of this app may have: the current one first, then the one earlier versions used.</summary>
    internal static IReadOnlyList<string> RuleNames(FirewallAction rule) =>
        rule.RuleName.StartsWith(RuleTag, StringComparison.Ordinal)
            ? [rule.RuleName, LegacyRuleTag + rule.RuleName[RuleTag.Length..]]
            : [rule.RuleName];

    /// <summary>Only these locations may be written by the system engine (defence in depth against a tampered catalogue).</summary>
    internal static bool AllowedPolicy(RegistryHive2 hive, string path)
    {
        string p = path.TrimEnd('\\');
        return (hive == RegistryHive2.LocalMachine && (p.StartsWith(@"SOFTWARE\Policies\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
                                                       p.Equals(@"SOFTWARE\Microsoft\EdgeUpdate", StringComparison.OrdinalIgnoreCase)))
            || (hive == RegistryHive2.CurrentUser && p.StartsWith(@"Software\Policies\Microsoft\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Policies that weaken protection are refused even if a catalogue entry asked for them.</summary>
    internal static bool Forbidden(string path, string name)
    {
        string full = path + @"\" + name;
        string[] denied = [@"\Windows Defender", @"\Microsoft Defender", @"\WindowsFirewall", @"\SmartScreen", "EnableSmartScreen", @"\DeviceGuard", @"\System\EnableLUA"];
        return denied.Any(d => full.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when writing <paramref name="value"/> would turn automatic updates off: NoAutoUpdate other than 0, or an
    /// AUOptions value that is not one of Windows' documented update modes. Updates are never turned off; Windows keeps
    /// checking for them. Every documented mode stays allowed, including 2, "notify before download" (the mode
    /// system.updates.notify sets on purpose: Windows finds updates and asks before downloading them). Such a value is
    /// never written, not even to restore what was there before.
    /// </summary>
    internal static bool WeakensUpdates(string path, string name, RegistryValue value)
    {
        if (!value.Exists || !path.TrimEnd('\\').EndsWith(@"\WindowsUpdate\AU", StringComparison.OrdinalIgnoreCase)) return false;
        int parsed = IsDword(value, out int number) ? number : -1;
        if (name.Equals("NoAutoUpdate", StringComparison.OrdinalIgnoreCase)) return parsed != 0;
        if (name.Equals("AUOptions", StringComparison.OrdinalIgnoreCase)) return parsed is not (2 or 3 or 4 or 5 or 7);
        return false;
    }

    /// <summary>
    /// Whether undo may write <paramref name="value"/> back for <paramref name="action"/>. The journal is an ordinary file,
    /// so the elevated helper only restores values of the kind the action itself handles: a missing or DWORD policy value
    /// that does not turn updates off, a start type of automatic, manual or disabled, or an on/off state. Removed apps are
    /// never restored.
    /// </summary>
    internal static bool Restorable(SystemAction action, RegistryValue value) => action switch
    {
        PolicyAction p => (!value.Exists || IsDword(value, out _)) && !WeakensUpdates(p.Path, p.Name, value),
        ServiceAction => IsDword(value, out int start) && start is 2 or 3 or 4,
        TaskAction or FirewallAction => IsDword(value, out int state) && state is 0 or 1,
        _ => false
    };

    private static bool IsDword(RegistryValue value, out int number)
    {
        number = 0;
        return value.Exists && value.Kind == ValueKind.DWord && int.TryParse(value.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
    }
}
