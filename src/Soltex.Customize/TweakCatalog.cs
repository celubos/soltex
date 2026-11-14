namespace Soltex.Customize;

/// <summary>
/// Everyday Windows options (taskbar, Start, File Explorer, appearance and desktop icons, multitasking, keyboard and
/// mouse, gaming). Every row is a per-user value the
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
    private const string Accessibility = @"Control Panel\Accessibility";
    /// <summary>Desktop icons: one DWORD per icon, named by the icon's CLSID; 0 shows it, 1 hides it.</summary>
    private const string DesktopIcons = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private const string GameBar = @"Software\Microsoft\GameBar";

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
        Switch("taskbar.alldisplays", SettingGroup.Taskbar, "Show taskbar on all displays", Advanced, "MMTaskbarEnabled", D(1), D(0), D(1), TakesEffect.Immediately),
        Choice("taskbar.mmmode", SettingGroup.Taskbar, "Show my taskbar apps on", Advanced, "MMTaskbarMode", D(0), TakesEffect.Immediately,
            [("All taskbars", D(0)), ("Main taskbar and where the window is open", D(1)), ("Taskbar where the window is open", D(2))]),
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
        Switch("explorer.statusbar", SettingGroup.FileExplorer, "Status bar", Advanced, "ShowStatusBar", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("explorer.recent", SettingGroup.FileExplorer, "Recent files in Home", Explorer, "ShowRecent", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("explorer.frequent", SettingGroup.FileExplorer, "Frequent folders in Home", Explorer, "ShowFrequent", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("explorer.home", SettingGroup.FileExplorer, "Home in the navigation pane", HomePin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart,
            minimumBuild: 22621),
        Switch("explorer.gallery", SettingGroup.FileExplorer, "Gallery in the navigation pane", GalleryPin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart,
            minimumBuild: 22621),
        Switch("explorer.onedrive", SettingGroup.FileExplorer, "OneDrive in the navigation pane", OneDrivePin, PinnedToTree, None, D(0), D(1), TakesEffect.ExplorerRestart),
        Switch("explorer.expandfolder", SettingGroup.FileExplorer, "Expand the navigation pane to the open folder", Advanced, "NavPaneExpandToCurrentFolder", D(1), D(0), D(0),
            TakesEffect.Immediately),
        Switch("explorer.allfolders", SettingGroup.FileExplorer, "Show all folders in the navigation pane", Advanced, "NavPaneShowAllFolders", D(1), D(0), D(0),
            TakesEffect.Immediately),
        Switch("explorer.separateprocess", SettingGroup.FileExplorer, "Open folder windows in a separate process", Advanced, "SeparateProcess", D(1), D(0), D(0),
            TakesEffect.ExplorerRestart),

        // ---- Appearance ----
        Choice("appearance.apps", SettingGroup.Appearance, "App mode", Personalize, "AppsUseLightTheme", D(1), TakesEffect.Immediately,
            [("Light", D(1)), ("Dark", D(0))]),
        Choice("appearance.windows", SettingGroup.Appearance, "Windows mode", Personalize, "SystemUsesLightTheme", D(1), TakesEffect.Immediately,
            [("Light", D(1)), ("Dark", D(0))]),
        Switch("appearance.transparency", SettingGroup.Appearance, "Transparency effects", Personalize, "EnableTransparency", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("appearance.accentstart", SettingGroup.Appearance, "Accent color on Start and taskbar", Personalize, "ColorPrevalence", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("appearance.accenttitle", SettingGroup.Appearance, "Accent color on title bars and borders", Dwm, "ColorPrevalence", D(1), D(0), D(0), TakesEffect.Immediately),
        Switch("appearance.scrollbars", SettingGroup.Appearance, "Always show scrollbars", Accessibility, "DynamicScrollbars", D(0), D(1), D(1), TakesEffect.AppRestart),

        // ---- Desktop icons (Appearance page) ----
        Switch("desktop.thispc", SettingGroup.DesktopIcons, "This PC", DesktopIcons, "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", D(0), D(1), D(1), TakesEffect.Immediately),
        Switch("desktop.recyclebin", SettingGroup.DesktopIcons, "Recycle Bin", DesktopIcons, "{645FF040-5081-101B-9F08-00AA002F954E}", D(0), D(1), D(0), TakesEffect.Immediately),
        Switch("desktop.userfiles", SettingGroup.DesktopIcons, "Your user folder", DesktopIcons, "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", D(0), D(1), D(1), TakesEffect.Immediately),
        Switch("desktop.network", SettingGroup.DesktopIcons, "Network", DesktopIcons, "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", D(0), D(1), D(1), TakesEffect.Immediately),
        Switch("desktop.controlpanel", SettingGroup.DesktopIcons, "Control Panel", DesktopIcons, "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", D(0), D(1), D(1), TakesEffect.Immediately),

        // ---- Multitasking ----
        Switch("multitasking.snap", SettingGroup.Multitasking, "Snap windows", Desktop, "WindowArrangementActive", S("1"), S("0"), S("1"), TakesEffect.SignOut),
        Switch("multitasking.snaplayouts", SettingGroup.Multitasking, "Snap layouts on the maximize button", Advanced, "EnableSnapAssistFlyout", D(1), D(0), D(1),
            TakesEffect.Immediately, minimumBuild: 22000),
        Switch("multitasking.snapbar", SettingGroup.Multitasking, "Snap layouts at the top of the screen", Advanced, "EnableSnapBar", D(1), D(0), D(1),
            TakesEffect.Immediately, minimumBuild: 22621),
        Switch("multitasking.snapassist", SettingGroup.Multitasking, "Suggest what to snap next", Advanced, "SnapAssist", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("multitasking.shake", SettingGroup.Multitasking, "Title bar window shake (minimize other windows)", Advanced, "DisallowShaking", D(0), D(1), D(1),
            TakesEffect.Immediately),
        Choice("multitasking.alttab", SettingGroup.Multitasking, "Alt+Tab shows browser tabs", Advanced, "MultiTaskingAltTabFilter", None, TakesEffect.Immediately,
            [("Windows default", None), ("Windows only", D(3)), ("3 most recent tabs", D(2)), ("5 most recent tabs", D(1)), ("20 most recent tabs", D(0))], profile: 1),

        // ---- Keyboard, mouse and clipboard (Multitasking page) ----
        Switch("input.clipboard", SettingGroup.Input, "Clipboard history (Win+V)", @"Software\Microsoft\Clipboard", "EnableClipboardHistory", D(1), D(0), D(0), TakesEffect.Immediately),
        // The id predates the Input group and stays, so saved setups and the change journal keep pointing at it.
        new("multitasking.pointerprecision", SettingGroup.Input, "Enhance pointer precision", "", "", TakesEffect.SignOut, false,
            [new(Mouse, "MouseSpeed", S("1"), S("1"), S("1")), new(Mouse, "MouseThreshold1", S("6"), S("6"), S("6")), new(Mouse, "MouseThreshold2", S("10"), S("10"), S("10"))],
            [], 0, "TW", [new SettingChoice("Off", [S("0"), S("0"), S("0")]), new SettingChoice("On", [S("1"), S("6"), S("10")])], IsSwitch: true),
        Switch("input.inactivescroll", SettingGroup.Input, "Scroll inactive windows when hovering over them", Desktop, "MouseWheelRouting", D(2), D(0), D(2), TakesEffect.SignOut),
        Switch("input.stickykeys", SettingGroup.Input, "Sticky Keys pop-up (press Shift five times)", Accessibility + @"\StickyKeys", "Flags", S("510"), S("506"), S("510"),
            TakesEffect.SignOut),
        Switch("input.filterkeys", SettingGroup.Input, "Filter Keys pop-up (hold right Shift)", Accessibility + @"\Keyboard Response", "Flags", S("126"), S("122"), S("126"),
            TakesEffect.SignOut),
        Switch("input.togglekeys", SettingGroup.Input, "Toggle Keys pop-up (hold Num Lock)", Accessibility + @"\ToggleKeys", "Flags", S("62"), S("58"), S("62"), TakesEffect.SignOut),

        // ---- Gaming (Multitasking page) ----
        Switch("gaming.gamebar", SettingGroup.Gaming, "Game Bar (Win+G)", GameBar, "UseNexusForGameBarEnabled", D(1), D(0), D(1), TakesEffect.Immediately),
        Switch("gaming.gamemode", SettingGroup.Gaming, "Game Mode", GameBar, "AutoGameModeEnabled", D(1), D(0), D(1), TakesEffect.Immediately),
        // Windows Settings writes both values together: the capture service's own switch and the per-user app capture switch.
        new("gaming.captures", SettingGroup.Gaming, "Game clips and screenshots (Win+Alt+R)", "", "", TakesEffect.Immediately, false,
            [new(@"System\GameConfigStore", "GameDVR_Enabled", D(1), D(1), D(1)), new(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", D(1), D(1), D(1))],
            [], 0, "TW", [new SettingChoice("Off", [D(0), D(0)]), new SettingChoice("On", [D(1), D(1)])], IsSwitch: true)
    ];
}
