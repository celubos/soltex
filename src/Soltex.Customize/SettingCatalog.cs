namespace Soltex.Customize;

public enum SettingGroup { StartAndSearch, Taskbar, TipsAndSuggestions, Privacy, FileExplorer, Appearance, Multitasking, DesktopIcons, Input, Gaming }

public enum TakesEffect { Immediately, NextStartOpen, ExplorerRestart, AppRestart, SignOut }

/// <summary>
/// One registry value a setting controls: the value the De-Windows profile wants, the value that restores Windows'
/// default, and (when known) what Windows assumes while the value does not exist.
/// </summary>
public sealed record RegistryTarget(string Path, string Name, RegistryValue Calm, RegistryValue WindowsDefault, RegistryValue? Implied = null);

/// <summary>One selectable option of a setting: a value for each target, in target order. Absent removes the value.</summary>
public sealed record SettingChoice(string Label, IReadOnlyList<RegistryValue> Values);

/// <summary>
/// A policy value that may also govern the setting. Windows Home mostly ignores policies and other editions apply them
/// over the setting, so a set policy is reported next to the setting, never changed.
/// </summary>
public sealed record PolicyHint(RegistryHive2 Hive, string Path, string Name, string Feature);

/// <summary>A user-facing Windows setting backed by per-user registry values the Windows Settings app reads.</summary>
public sealed record SettingDefinition(
    string Id,
    SettingGroup Group,
    string Title,
    string CalmEffect,
    string Tradeoff,
    TakesEffect TakesEffect,
    bool InCalmProfile,
    IReadOnlyList<RegistryTarget> Targets,
    IReadOnlyList<PolicyHint> Policies,
    int MinimumBuild = 0,
    string Source = "",
    IReadOnlyList<SettingChoice>? Choices = null,
    bool IsSwitch = false)
{
    /// <summary>
    /// The options shown to the user. Settings defined only by calm/default values get two implicit options.
    /// A switch is exactly [Off, On].
    /// </summary>
    public IReadOnlyList<SettingChoice> Options => Choices ??
    [
        new("Windows default", Targets.Select(t => t.WindowsDefault).ToArray()),
        new("On", Targets.Select(t => t.Calm).ToArray())
    ];

    public string When => TakesEffect switch
    {
        TakesEffect.Immediately => "Right away",
        TakesEffect.NextStartOpen => "Next time you open Start",
        TakesEffect.ExplorerRestart => "After File Explorer restarts",
        TakesEffect.AppRestart => "After the affected apps restart",
        _ => "After you sign out and back in"
    };
}

/// <summary>
/// Version 1 privacy and suggestions catalogue. Every row: per-user, no elevation, documented by Microsoft or written by
/// the Windows Settings app itself (reference ids in <see cref="SettingDefinition.Source"/>). Undocumented and
/// policy-only rows are deliberately absent.
/// </summary>
public static class CalmCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SearchSettings = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    /// <summary>
    /// One value the De-Windows profile sets to <paramref name="calm"/> (0 or 1). Its Windows default is "not set", which
    /// Windows reads as the opposite value: the feature is on.
    /// </summary>
    private static RegistryTarget Off(string path, string name, int calm) =>
        new(path, name, RegistryValue.Dword(calm), RegistryValue.Absent, RegistryValue.Dword(calm == 0 ? 1 : 0));

    /// <summary>
    /// A switch named after the Windows feature, so On always means the feature is shown or used, as on the rest of the
    /// page. On returns the values to Windows' default; Off is the De-Windows value.
    /// </summary>
    private static SettingDefinition Feature(string id, SettingGroup group, string title, string calmEffect, string tradeoff, TakesEffect effect,
        RegistryTarget[] targets, PolicyHint[]? policies = null, int minimumBuild = 0, string source = "") =>
        new(id, group, title, calmEffect, tradeoff, effect, true, targets, policies ?? [], minimumBuild, source,
            [new SettingChoice("Off", [.. targets.Select(t => t.Calm)]), new SettingChoice("On", [.. targets.Select(t => t.WindowsDefault)])], IsSwitch: true);

    private static IReadOnlyList<SettingDefinition> CalmRows { get; } =
    [
        // ---- Start and search ----
        Feature("start.recommendations", SettingGroup.StartAndSearch, "Tips, shortcuts and new apps in Start",
            "Start stops recommending apps and tips.", "You won't see newly installed apps highlighted in Start.",
            TakesEffect.NextStartOpen, [Off(Advanced, "Start_IrisRecommendations", 0)], minimumBuild: 22621, source: "DW-01"),
        Feature("start.recentfiles", SettingGroup.StartAndSearch, "Recent files in Start and jump lists",
            "Start, jump lists and File Explorer stop listing files you opened recently.", "Reopening a recent file takes a few more clicks.",
            TakesEffect.ExplorerRestart, [Off(Advanced, "Start_TrackDocs", 0)], source: "DW-04"),
        Feature("start.accountnotifications", SettingGroup.StartAndSearch, "Account reminders in Start",
            "Start stops showing Microsoft account prompts on your user picture.", "You may miss a reminder about your Microsoft account.",
            TakesEffect.NextStartOpen, [Off(Advanced, "Start_AccountNotifications", 0)],
            [new(RegistryHive2.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableAccountNotifications", "Account notifications policy")], 22621, "DW-06"),
        Feature("start.trackapps", SettingGroup.StartAndSearch, "Rank Start and search by the apps you open",
            "Windows stops ranking Start and search results by the apps you open.", "Your most-used apps are not listed first.",
            TakesEffect.ExplorerRestart, [Off(Advanced, "Start_TrackProgs", 0)], source: "DW-23"),
        Feature("search.highlights", SettingGroup.StartAndSearch, "Search highlights",
            "The search box stops showing daily trivia, holidays and trending searches.", "None for most people.",
            TakesEffect.AppRestart, [Off(SearchSettings, "IsDynamicSearchBoxEnabled", 0)],
            [new(RegistryHive2.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB", "Search highlights policy")], 22000, "DW-10"),
        Feature("search.cloudpersonal", SettingGroup.StartAndSearch, "Search my Microsoft account content",
            "Windows Search stops including OneDrive, Outlook and Bing results tied to your Microsoft account.", "Files that only exist in OneDrive won't appear in Windows Search.",
            TakesEffect.AppRestart, [Off(SearchSettings, "IsMSACloudSearchEnabled", 0)], source: "DW-11"),
        Feature("search.cloudwork", SettingGroup.StartAndSearch, "Search my work or school account content",
            "Windows Search stops including content from a work or school account.", "Work files in the cloud won't appear in Windows Search.",
            TakesEffect.AppRestart, [Off(SearchSettings, "IsAADCloudSearchEnabled", 0)], source: "DW-12"),
        Feature("search.history", SettingGroup.StartAndSearch, "Search history on this PC",
            "Searches you type are no longer saved to suggest them again.", "Previous searches are not suggested.",
            TakesEffect.AppRestart, [Off(SearchSettings, "IsDeviceSearchHistoryEnabled", 0)], source: "DW-13"),

        // ---- Tips and suggestions ----
        Feature("tips.lockscreen", SettingGroup.TipsAndSuggestions, "Fun facts and tips on the lock screen",
            "The lock screen stops showing tips, tricks and promotions.", "None for most people.",
            TakesEffect.Immediately, [Off(Cdm, "RotatingLockScreenOverlayEnabled", 0), Off(Cdm, "SubscribedContent-338387Enabled", 0)], source: "DW-36"),
        Feature("tips.windows", SettingGroup.TipsAndSuggestions, "Tips and suggestions while using Windows",
            "Windows stops popping up tips about features.", "You may discover fewer new features.",
            TakesEffect.Immediately, [Off(Cdm, "SubscribedContent-338389Enabled", 0)], source: "DW-41"),
        Feature("tips.welcome", SettingGroup.TipsAndSuggestions, "Welcome experience after updates",
            "Windows stops showing full-screen \"what's new\" pages after updates.", "You won't get the tour of new features.",
            TakesEffect.Immediately, [Off(Cdm, "SubscribedContent-310093Enabled", 0)], source: "DW-42"),
        Feature("tips.finishsetup", SettingGroup.TipsAndSuggestions, "Reminders to finish setting up Windows",
            "Windows stops suggesting extra setup steps such as Microsoft 365 or OneDrive.", "None for most people.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0)], source: "DW-43"),
        Feature("tips.settings", SettingGroup.TipsAndSuggestions, "Suggested content in Settings",
            "The Settings app stops showing suggestions and promotions.", "None for most people.",
            TakesEffect.AppRestart, [Off(Cdm, "SubscribedContent-338393Enabled", 0), Off(Cdm, "SubscribedContent-353694Enabled", 0), Off(Cdm, "SubscribedContent-353696Enabled", 0)],
            source: "DW-44"),
        Feature("tips.suggestednotifications", SettingGroup.TipsAndSuggestions, "Suggestion notifications",
            "Windows stops sending suggestion notifications such as trying new features.", "None for most people.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0)], source: "DW-46"),
        Feature("tips.accountsettings", SettingGroup.TipsAndSuggestions, "Account notifications in Settings",
            "Settings stops showing Microsoft account prompts on its home page.", "You may miss an account security reminder; check your account online instead.",
            TakesEffect.AppRestart, [Off(@"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications", 0)], minimumBuild: 22631, source: "DW-47"),

        // ---- Privacy ----
        Feature("privacy.tailored", SettingGroup.Privacy, "Tailored experiences from diagnostic data",
            "Microsoft stops using your diagnostic data for personalized tips and ads.", "None for most people.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0)],
            [new(RegistryHive2.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", "Tailored experiences policy")], source: "DW-45"),
        Feature("privacy.advertisingid", SettingGroup.Privacy, "Advertising ID",
            "Apps can no longer use your advertising ID to personalize ads.", "Ads in apps become less relevant, not fewer.",
            TakesEffect.AppRestart, [Off(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0)],
            [new(RegistryHive2.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", "Advertising ID policy")], source: "DW-53"),
        Feature("privacy.language", SettingGroup.Privacy, "Share my language list with websites",
            "Websites can no longer read your Windows language list.", "Some sites may not pick your language automatically.",
            TakesEffect.AppRestart, [Off(@"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1)], source: "DW-54"),
        Feature("privacy.inking", SettingGroup.Privacy, "Send inking and typing data to Microsoft",
            "Handwriting and typing samples are no longer sent to improve recognition.", "None for most people.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Input\TIPC", "Enabled", 0)], source: "DW-56"),
        Feature("privacy.typinginsights", SettingGroup.Privacy, "Typing insights",
            "Windows stops collecting statistics about your typing.", "No typing statistics in Settings.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Input\Settings", "InsightsEnabled", 0)], source: "DW-70"),
        Feature("privacy.suggestedactions", SettingGroup.Privacy, "Suggested actions when copying",
            "Copying a phone number or date no longer pops up app suggestions.", "You lose one-click actions on copied text.",
            TakesEffect.Immediately, [Off(@"Software\Microsoft\Windows\CurrentVersion\SmartActionPlatform\SmartClipboard", "Disabled", 1)], minimumBuild: 22621, source: "DW-50"),

        // ---- File Explorer ----
        Feature("explorer.syncads", SettingGroup.FileExplorer, "OneDrive and Microsoft 365 notices",
            "File Explorer stops showing sync-provider promotions.", "None for most people.",
            TakesEffect.ExplorerRestart, [Off(Advanced, "ShowSyncProviderNotifications", 0)], source: "DW-49"),
        Feature("explorer.cloudfiles", SettingGroup.FileExplorer, "Office.com files in Home",
            "File Explorer stops listing cloud files from your Microsoft account.", "Cloud-only Office files don't appear in Home.",
            TakesEffect.ExplorerRestart, [Off(Explorer, "ShowCloudFilesInQuickAccess", 0)], minimumBuild: 22621, source: "DW-78"),
    ];

    /// <summary>Every setting the app manages: the calm (privacy and ads) rows and the everyday Windows options.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [.. CalmRows, .. TweakCatalog.All];

    public static string GroupTitle(SettingGroup group) => group switch
    {
        SettingGroup.StartAndSearch => "Start and search",
        SettingGroup.Taskbar => "Taskbar",
        SettingGroup.TipsAndSuggestions => "Tips and suggestions",
        SettingGroup.Privacy => "Privacy",
        SettingGroup.Appearance => "Appearance",
        SettingGroup.Multitasking => "Multitasking",
        SettingGroup.DesktopIcons => "Desktop icons",
        SettingGroup.Input => "Keyboard, mouse and clipboard",
        SettingGroup.Gaming => "Gaming",
        _ => "File Explorer"
    };
}
