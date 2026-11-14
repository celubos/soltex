namespace Soltex.Customize;

/// <summary>
/// Everyday Windows options (taskbar, Start, File Explorer, appearance, multitasking). Every row is a per-user value the
/// Windows Settings app or File Explorer itself writes, so each one is reversible and needs no elevation. Rows marked
/// InCalmProfile are part of the one-click De-Windows profile, which selects <see cref="RegistryTarget.Calm"/>.
/// </summary>
public static class TweakCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string TaskbarDeveloper = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string Cabinet = @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private const string Desktop = @"Control Panel\Desktop";
    private const string Mouse = @"Control Panel\Mouse";
    private const string ClassicMenu = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";
    private const string GalleryPin = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
    private const string HomePin = @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
    private const string OneDrivePin = @"Software\Classes\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
    private const string PinnedToTree = "System.IsPinnedToNameSpaceTree";

    private static RegistryValue D(int value) => RegistryValue.Dword(value);
    private static RegistryValue S(string value) => RegistryValue.FromText(value);
    private static readonly RegistryValue None = RegistryValue.Absent;

    /// <summary>On/off switch over one value. <paramref name="profileOn"/> null keeps it out of the De-Windows profile.</summary>
    private static SettingDefinition Switch(string id, SettingGroup group, string title, string path, string name,
        RegistryValue on, RegistryValue off, RegistryValue implied, TakesEffect effect, bool? profileOn = null, int minimumBuild = 0, string subtitle = "") =>
        new(id, group, title, subtitle, "", effect, profileOn is not null,
            [new RegistryTarget(path, name, profileOn == false ? off : on, implied, implied)], [], minimumBuild, "TW",
            [new SettingChoice("Off", [off]), new SettingChoice("On", [on])], IsSwitch: true);

    /// <summary>Several named options over one value. <paramref name="profile"/> is the option the De-Windows profile picks.</summary>
    private static SettingDefinition Choice(string id, SettingGroup group, string title, string path, string name, RegistryValue implied,
        TakesEffect effect, (string Label, RegistryValue Value)[] options, int? profile = null, int minimumBuild = 0, string subtitle = "") =>
        new(id, group, title, subtitle, "", effect, profile is not null,
            [new RegistryTarget(path, name, profile is int p ? options[p].Value : implied, implied, implied)], [], minimumBuild, "TW",
            options.Select(o => new SettingChoice(o.Label, [o.Value])).ToArray());

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        // ---- Taskbar ----
        Choice("taskbar.alignment", SettingGroup.Taskbar, "Alignment", Advanced, "TaskbarAl", D(1), TakesEffect.Immediately,
            [("Left", D(0)), ("Center", D(1))], minimumBuild: 22000),
        Choice("taskbar.search", SettingGroup.Taskbar, "Search", Search, "SearchboxTaskbarMode", None, TakesEffect.Immediately,
            [("Windows default", None), ("Hide", D(0)), ("Icon only", D(1)), ("Search box", D(2)), ("Icon and label", D(3))], profile: 2),
        Switch("taskbar.taskview", SettingGroup.Taskbar, "Task view", Advanced, "ShowTaskViewButton", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("taskbar.widgets", SettingGroup.Taskbar, "Widgets", Advanced, "TaskbarDa", D(1), D(0), D(1), TakesEffect.Immediately, profileOn: false, minimumBuild: 22000),
        Choice("taskbar.combine", SettingGroup.Taskbar, "Combine buttons and hide labels", Advanced, "TaskbarGlomLevel", D(0), TakesEffect.Immediately,
            [("Always", D(0)), ("When taskbar is full", D(1)), ("Never", D(2))]),
        Choice("taskbar.combineother", SettingGroup.Taskbar, "Combine on other displays", Advanced, "MMTaskbarGlomLevel", D(0), TakesEffect.Immediately,
            [("Always", D(0)), ("When taskbar is full", D(1)), ("Never", D(2))]),
        Switch("taskbar.endtask", SettingGroup.Taskbar, "End task on right-click", TaskbarDeveloper, "TaskbarEndTask", D(1), D(0), D(0), TakesEffect.Immediately,
            profileOn: true, minimumBuild: 22631),
        Switch("taskbar.seconds", SettingGroup.Taskbar, "Seconds in the clock", Advanced, "ShowSecondsInSystemClock", D(1), D(0), D(0), TakesEffect.Immediately,
            minimumBuild: 22621),
        Switch("taskbar.badges", SettingGroup.Taskbar, "Badges on app icons", Advanced, "TaskbarBadges", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("taskbar.flashing", SettingGroup.Taskbar, "Flashing app icons", Advanced, "TaskbarFlashing", D(1), D(0), D(1), TakesEffect.Immediately,
            minimumBuild: 22621),
        Switch("taskbar.desktopcorner", SettingGroup.Taskbar, "Show desktop in the far corner", Advanced, "TaskbarSd", D(1), D(0), D(1), TakesEffect.Immediately,
            minimumBuild: 22621),
        Switch("taskbar.animations", SettingGroup.Taskbar, "Taskbar animations", Advanced, "TaskbarAnimations", D(1), D(0), D(1), TakesEffect.ExplorerRestart),

        // ---- Start ----
        Choice("start.layout", SettingGroup.StartAndSearch, "Layout", Advanced, "Start_Layout", D(0), TakesEffect.NextStartOpen,
            [("Default", D(0)), ("More pins", D(1)), ("More recommendations", D(2))], profile: 1, minimumBuild: 22621),

        // ---- File Explorer ----
        Switch("explorer.extensions", SettingGroup.FileExplorer, "File name extensions", Advanced, "HideFileExt", D(0), D(1), D(1), TakesEffect.Immediately, profileOn: true),
        Switch("explorer.hidden", SettingGroup.FileExplorer, "Hidden files and folders", Advanced, "Hidden", D(1), D(2), D(2), TakesEffect.Immediately),
        Choice("explorer.thispc", SettingGroup.FileExplorer, "Open File Explorer to", Advanced, "LaunchTo", D(2), TakesEffect.Immediately,
            [("Home", D(2)), ("This PC", D(1))]),
        Switch("explorer.classicmenu", SettingGroup.FileExplorer, "Classic right-click menu", ClassicMenu, "", S(""), None, None, TakesEffect.ExplorerRestart,
            profileOn: true, minimumBuild: 22000),
        Switch("explorer.checkboxes", SettingGroup.FileExplorer, "Item check boxes", Advanced, "AutoCheckSelect", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("explorer.compact", SettingGroup.FileExplorer, "Compact view", Advanced, "UseCompactMode", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("explorer.fullpath", SettingGroup.FileExplorer, "Full path in the title bar", Cabinet, "FullPath", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("explorer.recent", SettingGroup.FileExplorer, "Recent files in Home", Explorer, "ShowRecent", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("explorer.frequent", SettingGroup.FileExplorer, "Frequent folders in Home", Explorer, "ShowFrequent", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("explorer.home", SettingGroup.FileExplorer, "Home in the navigation pane", HomePin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart,
            minimumBuild: 22621),
        Switch("explorer.gallery", SettingGroup.FileExplorer, "Gallery in the navigation pane", GalleryPin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart,
            minimumBuild: 22621),
        Switch("explorer.onedrive", SettingGroup.FileExplorer, "OneDrive in the navigation pane", OneDrivePin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart),

        // ---- Appearance ----
        Choice("appearance.apps", SettingGroup.Appearance, "App mode", Personalize, "AppsUseLightTheme", D(1), TakesEffect.Immediately,
            [("Light", D(1)), ("Dark", D(0))]),
        Choice("appearance.windows", SettingGroup.Appearance, "Windows mode", Personalize, "SystemUsesLightTheme", D(1), TakesEffect.Immediately,
            [("Light", D(1)), ("Dark", D(0))]),
        Switch("appearance.transparency", SettingGroup.Appearance, "Transparency effects", Personalize, "EnableTransparency", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("appearance.accentstart", SettingGroup.Appearance, "Accent color on Start and taskbar", Personalize, "ColorPrevalence", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("appearance.accenttitle", SettingGroup.Appearance, "Accent color on title bars and borders", Dwm, "ColorPrevalence", D(1), D(0), D(0), TakesEffect.Immediately),

        // ---- Multitasking ----
        Switch("multitasking.snap", SettingGroup.Multitasking, "Snap windows", Desktop, "WindowArrangementActive", S("1"), S("0"), S("1"), TakesEffect.SignOut),
        Switch("multitasking.snaplayouts", SettingGroup.Multitasking, "Snap layouts on the maximize button", Advanced, "EnableSnapAssistFlyout", D(1), D(0), D(1),
            TakesEffect.Immediately, minimumBuild: 22000),
        Switch("multitasking.snapbar", SettingGroup.Multitasking, "Snap layouts at the top of the screen", Advanced, "EnableSnapBar", D(1), D(0), D(1),
            TakesEffect.Immediately, minimumBuild: 22621),
        Switch("multitasking.snapassist", SettingGroup.Multitasking, "Suggest what to snap next", Advanced, "SnapAssist", D(1), D(0), D(1), TakesEffect.Immediately),
        Choice("multitasking.alttab", SettingGroup.Multitasking, "Alt+Tab shows browser tabs", Advanced, "MultiTaskingAltTabFilter", None, TakesEffect.Immediately,
            [("Windows default", None), ("Windows only", D(3)), ("3 most recent tabs", D(2)), ("5 most recent tabs", D(1)), ("20 most recent tabs", D(0))], profile: 1),
        new("multitasking.pointerprecision", SettingGroup.Multitasking, "Enhance pointer precision", "", "", TakesEffect.SignOut, false,
            [new(Mouse, "MouseSpeed", S("1"), S("1"), S("1")), new(Mouse, "MouseThreshold1", S("6"), S("6"), S("6")), new(Mouse, "MouseThreshold2", S("10"), S("10"), S("10"))],
            [], 0, "TW", [new SettingChoice("Off", [S("0"), S("0"), S("0")]), new SettingChoice("On", [S("1"), S("6"), S("10")])], IsSwitch: true)
    ];
}
