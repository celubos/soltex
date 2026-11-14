using System.IO;
using Microsoft.Win32;
using Soltex.App.Personalize;
using Soltex.App.Settings;
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
