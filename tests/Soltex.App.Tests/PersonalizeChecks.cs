using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Soltex.App.Pages;
using Soltex.App.Personalize;
using Soltex.App.Settings;
using Soltex.Customize;
using static Soltex.App.Tests.Program;

namespace Soltex.App.Tests;

/// <summary>Start folders (isolated registry), dark mode schedule, pointer schemes (read only) and live wallpaper settings.</summary>
internal static class PersonalizeChecks
{
    public static void StartFoldersKeepWindowsOrder()
    {
        string path = @"Software\Soltex\StartFoldersTest\" + Guid.NewGuid().ToString("N");
        using RegistryKey root = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            Guid settings = StartFolders.All.Single(f => f.Name == "Settings").Id, downloads = StartFolders.All.Single(f => f.Name == "Downloads").Id;
            True(StartFolders.Read(root).Count == 0, "A new profile shows no extra folders.");
            StartFolders.Set(downloads, true, root);
            StartFolders.Set(settings, true, root);
            True(StartFolders.Read(root) is [var first, var second] && first == settings && second == downloads, "Folders must follow Windows' order.");
            using (RegistryKey? start = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Start"))
                True(start?.GetValue("VisiblePlaces") is byte[] { Length: 32 } data && new Guid(data.AsSpan(0, 16)) == settings, "VisiblePlaces must hold raw GUIDs.");
            StartFolders.Set(settings, false, root);
            True(StartFolders.Read(root) is [var only] && only == downloads, "Hiding a folder did not remove it.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Soltex\StartFoldersTest", throwOnMissingSubKey: false); }
    }

    public static void ScheduleAndSchemes()
    {
        TimeSpan seven = TimeSpan.FromHours(7), nineteen = TimeSpan.FromHours(19);
        True(ThemeSchedule.LightAt(TimeSpan.FromHours(12), seven, nineteen) && !ThemeSchedule.LightAt(TimeSpan.FromHours(20), seven, nineteen)
            && !ThemeSchedule.LightAt(TimeSpan.FromHours(6.5), seven, nineteen) && ThemeSchedule.LightAt(seven, seven, nineteen), "Daytime light is wrong.");
        True(ThemeSchedule.LightAt(TimeSpan.FromHours(23), TimeSpan.FromHours(22), TimeSpan.FromHours(6)) && !ThemeSchedule.LightAt(TimeSpan.FromHours(12), TimeSpan.FromHours(22), TimeSpan.FromHours(6)),
            "A light range across midnight is wrong.");
        IReadOnlyList<string> schemes = PointerSchemes.Names();
        True(schemes.Contains("Windows Default") && schemes.Count > 1, "Windows' pointer schemes were not found: " + string.Join(", ", schemes));

        AppSettings saved = new AppSettings { LightFrom = TimeSpan.FromHours(30), DarkFrom = TimeSpan.FromHours(-1), LiveWallpaper = "  " }.Normalize();
        True(saved.LightFrom == seven && saved.DarkFrom == nineteen && saved.LiveWallpaper is null, "Schedule times or wallpaper were not normalized.");
    }

    /// <summary>The schedule switches through a stand-in, never the real Windows theme.</summary>
    public static void ScheduleSwitchesOnlyWhenThePeriodChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), "soltex-schedule-" + Guid.NewGuid().ToString("N") + ".json");
        AppSettingsStore store = new(path);
        DateTime now = DateTime.Today.AddHours(12);
        List<bool> applied = [];
        using ThemeSchedule schedule = new(store, () => now, applied.Add);
        List<bool> announced = [];
        schedule.Switched += announced.Add;
        try
        {
            store.Update(s => s with { DarkModeSchedule = true, LightFrom = TimeSpan.FromHours(7), DarkFrom = TimeSpan.FromHours(19) });
            DesktopChecks.Pump();
            True(applied.Count == 0, "Before it is attached (tests, render-only shells) the schedule must never switch Windows.");

            schedule.Attach();
            True(applied is [true], "Turning the schedule on at noon switches to light once.");
            schedule.Check();
            store.Update(s => s with { LightFrom = TimeSpan.FromHours(6) });
            DesktopChecks.Pump();
            True(applied.Count == 1, "Within the same period nothing is written again, so a manual App mode or Windows mode choice stays.");

            now = DateTime.Today.AddHours(20);
            schedule.Check();
            schedule.Check();
            True(applied is [true, false], "When the dark period starts, Windows switches to dark once.");

            store.Update(s => s with { DarkModeSchedule = false });
            DesktopChecks.Pump();
            schedule.Check();
            True(applied.Count == 2 && schedule.Period is null, "With the schedule off nothing is switched.");
            store.Update(s => s with { DarkModeSchedule = true });
            DesktopChecks.Pump();
            True(applied is [true, false, false], "Turning the schedule on again applies the current period.");
            True(announced.SequenceEqual(applied), "Each switch must be announced so pages showing the mode can refresh.");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Scheme lookup against an isolated registry key; nothing is applied.</summary>
    public static void PointerDefaultIsWindowsOwnScheme()
    {
        const string Root = @"Software\Soltex\PointerSchemesTest";
        using RegistryKey root = Registry.CurrentUser.CreateSubKey(Root + @"\" + Guid.NewGuid().ToString("N"));
        try
        {
            using RegistryKey user = root.CreateSubKey("User"), system = root.CreateSubKey("System"), defaults = root.CreateSubKey("Defaults");
            int hand = Array.IndexOf(PointerSchemes.Roles, "Hand"), beam = Array.IndexOf(PointerSchemes.Roles, "IBeam"), person = Array.IndexOf(PointerSchemes.Roles, "Person");
            system.SetValue("Windows Aero", string.Join(",", PointerSchemes.Roles.Select((role, i) => i == 0 ? @"C:\W\aero_arrow.cur" : i == person ? @"C:\W\aero_person.cur" : "")) + @",@C:\W\main.cpl,-1020");
            system.SetValue("Windows Black", @"C:\W\arrow_r.cur,C:\W\help_r.cur");
            user.SetValue("Mine", @"C:\Mine\arrow.cur");

            PointerSchemes.Scheme? aero = PointerSchemes.Resolve(PointerSchemes.WindowsDefault, user, system, defaults);
            True(aero is { Name: PointerSchemes.WindowsDefault, Source: 2 } && aero.Files.Count == PointerSchemes.Roles.Length
                && aero.Files[0] == @"C:\W\aero_arrow.cur" && aero.Files[person] == @"C:\W\aero_person.cur" && aero.Files[hand] == "",
                "Without a default profile, Windows' default must be the Aero scheme, marked as a Windows scheme.");
            defaults.SetValue("Arrow", @"C:\W\aero_arrow.cur");
            defaults.SetValue("Hand", @"C:\W\aero_link.cur");
            PointerSchemes.Scheme? fromDefaults = PointerSchemes.Resolve(PointerSchemes.WindowsDefault, user, system, defaults);
            True(fromDefaults is { Name: PointerSchemes.WindowsDefault, Source: 2 } && fromDefaults.Files[hand] == @"C:\W\aero_link.cur" && fromDefaults.Files[beam] == "",
                "Windows' default must be the pointers a new account gets, never empty paths.");
            True(PointerSchemes.Resolve("Windows Black", user, system, defaults) is { Source: 2 } black && black.Files[1] == @"C:\W\help_r.cur" && black.Files[2] == "",
                "A Windows scheme is read in role order.");
            True(PointerSchemes.Resolve("Mine", user, system, defaults) is { Source: 1 }, "A scheme the user saved is marked as theirs.");
            True(PointerSchemes.Resolve("Nope", user, system, defaults) is null, "An unknown scheme must not be applied.");

            True(PointerSchemes.Normalize(null) == PointerSchemes.WindowsDefault && PointerSchemes.Normalize("") == PointerSchemes.WindowsDefault
                && PointerSchemes.Normalize("@main.cpl,-1020") == PointerSchemes.WindowsDefault && PointerSchemes.Normalize("Windows Black") == "Windows Black",
                "Windows' ways of naming its default scheme are not recognized.");
            True(PointerSchemes.Label(PointerSchemes.WindowsDefault) == "Windows default" && PointerSchemes.Label("Windows Black") == "Windows Black", "The default scheme is not labelled in sentence case.");
            True(PointerSchemes.Names() is [PointerSchemes.WindowsDefault, ..], "Windows' default must be listed first.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false); }
    }

    /// <summary>
    /// A switch on a Windows settings page keeps its new position, stays enabled and keeps the keyboard while Windows is
    /// changed (the write is held in flight); a second switch pressed meanwhile is applied next; Undo hands the keyboard
    /// to the row's own switch.
    /// </summary>
    public static void CustomizeSwitchesKeepFocusWhileApplying()
    {
        PageTestRegistry registry = new();
        using PageShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        CustomizePage page = shell.Open<CustomizePage>(Destination.Customize);
        CheckBox[] switches = [.. DesktopChecks.Descendants(page).OfType<CheckBox>().Where(c => c.Tag is SettingDefinition && c.IsEnabled).Take(2)];
        CheckBox first = switches[0], second = switches[1];
        bool firstWas = first.IsChecked == true, secondWas = second.IsChecked == true;
        first.Focus();
        using (ManualResetEventSlim hold = new(false))
        {
            registry.Hold = hold;
            try
            {
                DesktopChecks.ToggleThroughAutomation(first);
                DesktopChecks.Pump();
                True(page.Busy.IsVisible, "Setup: the change should be in flight.");
                True(first.IsChecked == !firstWas, "The switch snapped back while Windows was being changed.");
                True(first.IsEnabled && second.IsEnabled, "Rows must stay enabled while a change runs (disabling them drops the keyboard).");
                True(DesktopChecks.Focused(shell.Window) == first, "The switch lost the keyboard while its change ran.");
                DesktopChecks.ToggleThroughAutomation(second);
                DesktopChecks.Pump();
                True(second.IsChecked == !secondWas, "A switch pressed during a change must keep its new position until it is applied.");
            }
            finally
            {
                registry.Hold = null;
                hold.Set();
            }
            DesktopChecks.WaitFor(() => !page.Busy.IsVisible, 5);
        }
        DesktopChecks.Pump();
        ChangeEngine engine = shell.Services.Calm;
        True(engine.Inspect((SettingDefinition)first.Tag).Choice == (firstWas ? 0 : 1) && engine.Inspect((SettingDefinition)second.Tag).Choice == (secondWas ? 0 : 1),
            "Both changes must be applied, the second after the first.");
        True(first.IsChecked == !firstWas && second.IsChecked == !secondWas && first.IsEnabled && DesktopChecks.Focused(shell.Window) == first,
            "After the change the page shows Windows' state once, with the keyboard still on the switch.");

        Button undo = DesktopChecks.Descendants(page).OfType<Button>().Single(b => ReferenceEquals(b.Tag, first.Tag));
        True(undo.IsVisible, "Setup: the applied row offers Undo.");
        undo.Focus();
        DesktopChecks.Invoke(undo);
        DesktopChecks.Pump();
        DesktopChecks.WaitFor(() => !undo.IsVisible && !page.Busy.IsVisible, 5);
        DesktopChecks.Pump();
        True(first.IsChecked == firstWas && DesktopChecks.Focused(shell.Window) == first, "Undo goes away once used; the keyboard must move to the row's own switch.");
    }

    /// <summary>
    /// The "Restart File Explorer" reminder is one for the whole app: every page shows it, and restarting from any place
    /// clears it everywhere (only when File Explorer came back). A refusal note clears once the value changes elsewhere.
    /// </summary>
    public static void RestartReminderAndRefusalNotesFollowWindows()
    {
        PageTestRegistry registry = new();
        registry.Denied.Add("TaskbarDa"); // Widgets, guarded by Windows on 24H2 and later
        using PageShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        SettingDefinition needsRestart = CalmCatalog.All.First(d => d.Group == SettingGroup.FileExplorer && d.TakesEffect == TakesEffect.ExplorerRestart
            && d.Options.Count == 2 && (d.IsSwitch || d.Choices is null));
        CustomizePage explorer = shell.Open<CustomizePage>(Destination.Explorer);
        DesktopChecks.ToggleThroughAutomation((CheckBox)explorer.ControlFor(needsRestart.Id)!);
        DesktopChecks.WaitFor(() => registry.Writes > 0 && !explorer.Busy.IsVisible, 5);
        DesktopChecks.Pump();
        True(explorer.RestartBar.IsVisible, "A change that shows after File Explorer restarts must say so.");
        CustomizePage start = shell.Open<CustomizePage>(Destination.Start);
        True(start.RestartBar.IsVisible, "The reminder belongs to the app, so every page shows it.");

        ExplorerRestartReminder reminder = ExplorerRestartReminder.For(shell.Services);
        Task<bool> failed = reminder.RestartAsync(() => Task.FromResult(false));
        DesktopChecks.WaitFor(() => failed.IsCompleted, 5);
        DesktopChecks.Pump();
        True(!failed.Result && reminder.Pending && start.RestartBar.IsVisible, "A restart that did not bring File Explorer back keeps the reminder.");
        Task<bool> restarted = reminder.RestartAsync(() => Task.FromResult(true));
        DesktopChecks.WaitFor(() => restarted.IsCompleted, 5);
        DesktopChecks.Pump();
        True(restarted.Result && !reminder.Pending && start.RestartBar.Visibility == Visibility.Collapsed && explorer.RestartBar.Visibility == Visibility.Collapsed,
            "Restarting File Explorer from one place must clear the reminder on every page.");

        CustomizePage taskbar = shell.Open<CustomizePage>(Destination.Taskbar);
        TextBlock note = taskbar.NoteFor("taskbar.widgets") ?? throw new InvalidOperationException("The Widgets row has no note.");
        DesktopChecks.ToggleThroughAutomation((CheckBox)taskbar.ControlFor("taskbar.widgets")!);
        DesktopChecks.WaitFor(() => note.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any() && !taskbar.Busy.IsVisible, 5);
        DesktopChecks.Pump();
        True(note.IsVisible && string.Concat(note.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).StartsWith("Windows doesn't let apps change this.", StringComparison.Ordinal),
            "Setup: the refused change says so on its row.");
        RegistryTarget widgets = CalmCatalog.All.Single(d => d.Id == "taskbar.widgets").Targets[0];
        registry.Put(widgets.Path, widgets.Name, RegistryValue.Dword(0)); // turned off in Windows Settings
        taskbar.OnShown();
        DesktopChecks.Pump();
        True(note.Visibility == Visibility.Collapsed && !note.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any(),
            "Once the value changes outside the app, the refusal no longer applies and its note must go.");
    }

    /// <summary>
    /// Lists on the Windows settings pages share one width and show their full choice as a tooltip; at large text in a
    /// narrow window a row's list moves below its title. Every page links to Windows Settings, Start's first rows have a
    /// heading, and the same folder has one name on Start and Appearance.
    /// </summary>
    public static void CustomizeListsLineUpAndRowsWrap()
    {
        using PageShell shell = new();
        foreach (Destination destination in new[] { Destination.Taskbar, Destination.Start, Destination.Explorer, Destination.Appearance, Destination.Multitasking, Destination.Customize })
        {
            CustomizePage page = shell.Open<CustomizePage>(destination);
            True(page.SettingsLinkText.IsVisible && CustomizePage.SettingsUriFor(destination) is { } uri && uri.StartsWith("ms-settings:", StringComparison.Ordinal),
                destination + ": every page must link to Windows Settings.");
            foreach (ComboBox list in DesktopChecks.Descendants(page).OfType<ComboBox>().Where(c => c.Tag is SettingDefinition || DesktopChecks.Ancestors(c).OfType<ExtrasPanel>().Any()))
                True(list.Width == CustomizePage.ChoiceWidth && (list.ItemTemplate is null || list.SelectedItem is null || list.ToolTip is string { Length: > 0 }),
                    $"{destination}: {System.Windows.Automation.AutomationProperties.GetName(list)} must be {CustomizePage.ChoiceWidth} wide, and a list that trims its choice must show it as a tooltip.");
        }
        CustomizePage start = shell.Open<CustomizePage>(Destination.Start);
        True(start.Groups.Children[0] is StackPanel { Children: [TextBlock { Text: "Start and search" }, ..] }, "Start's first rows must sit under their own heading, like the folders below them.");
        True(StartFolders.All.Any(f => f.Name == "Your user folder") && StartFolders.All.All(f => f.Name != "Personal folder"),
            "Start must call the user folder what Appearance › Desktop icons calls it.");

        CustomizePage taskbar = shell.Open<CustomizePage>(Destination.Taskbar);
        ComboBox choice = DesktopChecks.Descendants(taskbar).OfType<ComboBox>().First(c => c.Tag is SettingDefinition);
        Soltex.Audio.UI.ResponsiveRow row = DesktopChecks.Ancestors(choice).OfType<Soltex.Audio.UI.ResponsiveRow>().First();
        try
        {
            shell.Layout(760, 200);
            True(row.IsStacked, "At 200% in a narrow window a row's list must move below its title instead of squeezing it.");
            shell.Layout(1280, 100);
            True(!row.IsStacked, "With room a row's list sits beside its title.");
        }
        finally { shell.Layout(AudioLayoutChecks.Widths[0], 100); }
    }

    /// <summary>Uses a file that does not exist, so no video window is ever made and the desktop is never touched.</summary>
    public static void WallpaperReportsWhatHappened()
    {
        string path = Path.Combine(Path.GetTempPath(), "soltex-wallpaper-" + Guid.NewGuid().ToString("N") + ".json");
        AppSettingsStore store = new(path);
        using LiveWallpaper wallpaper = new(store);
        List<LiveWallpaperStatus> seen = [];
        wallpaper.StatusChanged += seen.Add;
        try
        {
            store.Update(s => s with { LiveWallpaper = Path.Combine(Path.GetTempPath(), "soltex-missing-" + Guid.NewGuid().ToString("N") + ".mp4") });
            DesktopChecks.Pump();
            True(wallpaper.Status == LiveWallpaperStatus.Off && seen.Count == 0 && !wallpaper.OnDesktop, "Before it is attached the wallpaper must do nothing.");

            wallpaper.Attach();
            True(wallpaper.Status == LiveWallpaperStatus.FileMissing && seen is [LiveWallpaperStatus.FileMissing] && !wallpaper.OnDesktop, "A missing video must be reported, not shown as playing.");
            store.Update(s => s with { DarkModeSchedule = !s.DarkModeSchedule });
            DesktopChecks.Pump();
            True(seen.Count == 1, "An unrelated setting must not look for the video again.");
            store.Update(s => s with { LiveWallpaper = null });
            DesktopChecks.Pump();
            True(wallpaper.Status == LiveWallpaperStatus.Off && seen is [LiveWallpaperStatus.FileMissing, LiveWallpaperStatus.Off], "Removing the video did not turn the wallpaper off.");
        }
        finally { File.Delete(path); }
    }
}
