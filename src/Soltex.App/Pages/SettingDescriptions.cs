using Soltex.App.Settings;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// One line under each setting that says what it does, in the On sense of its switch (the catalog's own text says what
/// the De-Windows value changes, which reads backwards under a switch named after the feature). An empty line means
/// the title already says it all. A test keeps this list complete.
/// </summary>
internal static class SettingDescriptions
{
    private static readonly Dictionary<string, string> Lines = new(StringComparer.Ordinal)
    {
        // Taskbar
        ["taskbar.alignment"] = "Where the Start button and your apps sit on the taskbar.",
        ["taskbar.search"] = "How search appears on the taskbar: a box, a button with a label, an icon or nothing.",
        ["taskbar.taskview"] = "The button that shows all your open windows and desktops.",
        ["taskbar.widgets"] = "The weather and news button and its board.",
        ["taskbar.combine"] = "When windows of one app share a button, and whether buttons show their names.",
        ["taskbar.alldisplays"] = "A taskbar on every display, not only the main one.",
        ["taskbar.mmmode"] = "Which taskbars show the buttons of your open windows.",
        ["taskbar.combineother"] = "Combine buttons and hide labels, for the taskbars on your other displays.",
        ["taskbar.endtask"] = "Adds End task to an app button's right-click menu, to close an app that stopped responding.",
        ["taskbar.seconds"] = "The taskbar clock counts seconds. Uses a little more power.",
        ["taskbar.badges"] = "Small counts and alerts on app buttons, such as unread mail.",
        ["taskbar.flashing"] = "App buttons flash when an app needs your attention.",
        ["taskbar.desktopcorner"] = "Click the far end of the taskbar to show the desktop.",
        ["taskbar.animations"] = "Buttons slide and fade as apps open and close.",

        // Start and search
        ["start.layout"] = "How Start shares its space between your pins and recommendations.",
        ["start.recommendations"] = "Suggested apps, tips and newly installed apps in Start.",
        ["start.recentfiles"] = "Files you opened lately, in Start, jump lists and File Explorer.",
        ["start.accountnotifications"] = "Microsoft account prompts on your picture in Start.",
        ["start.trackapps"] = "Puts the apps you use most at the top of Start and search.",
        ["search.highlights"] = "Daily trivia, holidays and trending searches in the search box.",
        ["search.cloudpersonal"] = "Results from OneDrive, Outlook and Bing tied to your Microsoft account.",
        ["search.cloudwork"] = "Results from your work or school account.",
        ["search.history"] = "Remembers what you searched for on this PC to suggest it again.",

        // Tips and suggestions
        ["tips.lockscreen"] = "Tips, facts and promotions over the lock screen picture.",
        ["tips.windows"] = "Pop-up tips about Windows features while you work.",
        ["tips.welcome"] = "Full-screen “what's new” pages after some updates.",
        ["tips.finishsetup"] = "Prompts to set up Microsoft 365, OneDrive and other extras.",
        ["tips.settings"] = "Suggestions and promotions in the Settings app.",
        ["tips.suggestednotifications"] = "Notifications that suggest trying new features.",
        ["tips.accountsettings"] = "Microsoft account prompts on the Settings home page.",

        // Privacy
        ["privacy.tailored"] = "Microsoft uses your diagnostic data for personal tips and ads.",
        ["privacy.advertisingid"] = "An ID apps can use to show you personalized ads.",
        ["privacy.language"] = "Websites can read your language list to pick a language for you.",
        ["privacy.inking"] = "Sends samples of your handwriting and typing to improve Microsoft's recognition.",
        ["privacy.typinginsights"] = "Keeps statistics on your typing, such as how many words suggestions saved you.",
        ["privacy.suggestedactions"] = "Offers actions, such as calling or adding to your calendar, when you copy a phone number or date.",

        // File Explorer
        ["explorer.extensions"] = "Shows the end of file names, such as .pdf or .exe, so you can tell files apart.",
        ["explorer.hidden"] = "Shows files and folders that are marked hidden.",
        ["explorer.checkboxes"] = "A check box on every item, to pick several with the mouse.",
        ["explorer.compact"] = "Less space between items in lists.",
        ["explorer.fullpath"] = "The whole folder path in the window title.",
        ["explorer.statusbar"] = "The item count and selection size at the bottom of the window.",
        ["explorer.thispc"] = "The page a new File Explorer window opens on.",
        ["explorer.recent"] = "Files you opened lately, in Home.",
        ["explorer.frequent"] = "Folders you use often, in Home.",
        ["explorer.cloudfiles"] = "Files from Office.com and OneDrive, in Home.",
        ["explorer.syncads"] = "Messages about OneDrive and Microsoft 365 at the top of File Explorer.",
        ["explorer.home"] = "Home at the top of the navigation pane.",
        ["explorer.gallery"] = "Your photo gallery in the navigation pane.",
        ["explorer.onedrive"] = "Your OneDrive folder in the navigation pane.",
        ["explorer.expandfolder"] = "The navigation pane opens out to the folder you are in.",
        ["explorer.allfolders"] = "Every folder in the navigation pane, including Control Panel and Recycle Bin.",
        ["explorer.classicmenu"] = "Right-click shows the full menu at once, without “Show more options”.",
        ["explorer.separateprocess"] = "Folder windows run apart from the taskbar and desktop, so a stuck window can't freeze them. Uses a little more memory.",

        // Appearance
        ["appearance.apps"] = "Light or dark for apps that follow Windows.",
        ["appearance.windows"] = "Light or dark for the taskbar, Start and notifications.",
        ["appearance.transparency"] = "See-through backgrounds in Start, the taskbar and windows.",
        ["appearance.accentstart"] = "Your accent color on Start, the taskbar and notifications. Needs dark Windows mode.",
        ["appearance.accenttitle"] = "Your accent color on window title bars and edges.",
        ["appearance.scrollbars"] = "Scrollbars stay full size instead of shrinking while you don't use them.",
        ["desktop.thispc"] = "",
        ["desktop.recyclebin"] = "",
        ["desktop.userfiles"] = "",
        ["desktop.network"] = "",
        ["desktop.controlpanel"] = "",

        // Multitasking
        ["multitasking.snap"] = "Drag a window to an edge or corner of the screen to fill that part.",
        ["multitasking.snaplayouts"] = "Layouts to choose from when you point at a window's maximize button.",
        ["multitasking.snapbar"] = "Layouts that appear when you drag a window to the top of the screen.",
        ["multitasking.snapassist"] = "After you snap a window, offers your other windows to fill the rest.",
        ["multitasking.shake"] = "Shake a window by its title bar to minimize all the others.",
        ["multitasking.alttab"] = "How many Microsoft Edge tabs Alt+Tab shows next to your windows.",
        ["input.clipboard"] = "Keeps the last things you copied; Win+V pastes any of them.",
        ["input.inactivescroll"] = "The mouse wheel scrolls the window under the pointer, even when it isn't the active one.",
        ["multitasking.pointerprecision"] = "Small, slow mouse movements move the pointer less. Many players turn it off for steady aim.",
        ["input.stickykeys"] = "Pressing Shift five times turns on Sticky Keys. Off stops the prompt during games.",
        ["input.filterkeys"] = "Holding right Shift for eight seconds turns on Filter Keys.",
        ["input.togglekeys"] = "Holding Num Lock for five seconds turns on Toggle Keys.",
        ["gaming.gamebar"] = "The Xbox button on a controller opens Game Bar.",
        ["gaming.gamemode"] = "Gives the game you're playing priority and holds back Windows Update while you play.",
        ["gaming.captures"] = "Records clips and screenshots of your games in the background.",
    };

    /// <summary>The line for a setting, or null when it has none (the title says it all).</summary>
    public static string? For(string id) => Lines.TryGetValue(id, out string? line) && line.Length > 0 ? line : null;

    /// <summary>True when the list has an entry for the setting (even an empty one).</summary>
    public static bool Covers(string id) => Lines.ContainsKey(id);
}

/// <summary>
/// The sections of the long pages, in page order. Settings of the page that a layout does not list are added to its last
/// section, so nothing can go missing; a test keeps the layouts complete.
/// </summary>
internal static class PageLayouts
{
    /// <summary>Taskbar behavior: the custom auto-hide row is the first row of the first section.</summary>
    public static readonly (string Heading, string[] Ids)[] Taskbar =
    [
        ("Behavior", ["taskbar.alignment", "taskbar.search", "taskbar.taskview", "taskbar.widgets", "taskbar.endtask", "taskbar.desktopcorner", "taskbar.animations"]),
        ("App buttons and clock", ["taskbar.combine", "taskbar.badges", "taskbar.flashing", "taskbar.seconds"]),
        ("Multiple displays", ["taskbar.alldisplays", "taskbar.mmmode", "taskbar.combineother"]),
    ];

    public static readonly (string Heading, string[] Ids)[] Start =
    [
        ("Start menu", ["start.layout", "start.recommendations", "start.recentfiles", "start.accountnotifications", "start.trackapps"]),
        ("Search", ["search.highlights", "search.cloudpersonal", "search.cloudwork", "search.history"]),
    ];

    public static readonly (string Heading, string[] Ids)[] Explorer =
    [
        ("Files and folders", ["explorer.extensions", "explorer.hidden", "explorer.checkboxes", "explorer.compact", "explorer.fullpath", "explorer.statusbar"]),
        ("Home", ["explorer.thispc", "explorer.recent", "explorer.frequent", "explorer.cloudfiles", "explorer.syncads"]),
        ("Navigation pane", ["explorer.home", "explorer.gallery", "explorer.onedrive", "explorer.expandfolder", "explorer.allfolders"]),
        ("Menus and windows", ["explorer.classicmenu", "explorer.separateprocess"]),
    ];

    /// <summary>A page's layout, or null when its catalog groups are its sections.</summary>
    public static (string Heading, string[] Ids)[]? For(Destination destination) => destination switch
    {
        Destination.Taskbar => Taskbar,
        Destination.Start => Start,
        Destination.Explorer => Explorer,
        _ => null
    };

    /// <summary>
    /// A heading for a catalog group that would repeat the page title (Appearance on the Appearance page) when the page
    /// has other sections; null keeps the group's own title.
    /// </summary>
    public static string? FirstHeading(Destination destination) => destination switch
    {
        Destination.Appearance => "Mode and colors",
        Destination.Multitasking => "Snap and windows",
        Destination.Customize => "Data and ads",
        _ => null
    };

    /// <summary>Groups the page's settings by a layout: each heading with its settings in layout order, the leftovers at the end.</summary>
    public static IReadOnlyList<(string Heading, IReadOnlyList<SettingDefinition> Rows)> Arrange(
        (string Heading, string[] Ids)[] layout, IReadOnlyList<SettingDefinition> settings)
    {
        Dictionary<string, SettingDefinition> byId = settings.ToDictionary(s => s.Id, StringComparer.Ordinal);
        List<(string, IReadOnlyList<SettingDefinition>)> sections = [];
        HashSet<string> placed = new(StringComparer.Ordinal);
        foreach ((string heading, string[] ids) in layout)
        {
            List<SettingDefinition> rows = [];
            foreach (string id in ids)
                if (byId.TryGetValue(id, out SettingDefinition? d) && placed.Add(id)) rows.Add(d);
            sections.Add((heading, rows));
        }
        List<SettingDefinition> rest = [.. settings.Where(s => !placed.Contains(s.Id))];
        if (rest.Count > 0)
        {
            (string heading, IReadOnlyList<SettingDefinition> rows) = sections[^1];
            sections[^1] = (heading, [.. rows, .. rest]);
        }
        return sections;
    }
}
