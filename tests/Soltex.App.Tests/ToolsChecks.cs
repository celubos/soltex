using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Soltex.App.Settings;
using Soltex.App.Tools;
using static Soltex.App.Tests.Program;
using Drawing = System.Drawing;

namespace Soltex.App.Tests;

/// <summary>PowerToys-class tools: zones, batch rename, OCR and key remapping.</summary>
internal static class ToolsChecks
{
    public static void ZonesSplitTheWorkAreaAndFitWindows()
    {
        Int32Rect area = new(100, 50, 1000, 800);
        IReadOnlyList<Int32Rect> two = ZoneLayouts.For(WindowZonesLayout.TwoColumns, area);
        True(two is [{ X: 100, Width: 500, Height: 800 }, { X: 600, Width: 500 }], "Two columns are wrong.");
        True(ZoneLayouts.For(WindowZonesLayout.Grid, area).Count == 4 && ZoneLayouts.For(WindowZonesLayout.Off, area).Count == 0, "Grid or off is wrong.");
        IReadOnlyList<Int32Rect> focus = ZoneLayouts.For(WindowZonesLayout.Focus, area);
        True(focus[1] is { X: 350, Width: 500 } && ZoneLayouts.ZoneAt(focus, 360, 60) == 1 && ZoneLayouts.ZoneAt(focus, 99, 60) == -1, "Focus zones or hit testing are wrong.");

        // A real window (off screen) fits its visible frame exactly into a zone.
        Window window = new() { Title = "Zone test", Width = 300, Height = 200, Left = -32000, Top = -32000, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            Int32Rect zone = new(-30000, -30000, 640, 480);
            WindowZonesService.FitToZone(handle, zone);
            Pump();
            Rect frame = default;
            True(DwmGetWindowAttribute(handle, 9, out frame, Marshal.SizeOf<Rect>()) == 0, "The window frame could not be read.");
            True(frame.Left == zone.X && frame.Top == zone.Y && frame.Right - frame.Left == zone.Width && frame.Bottom - frame.Top == zone.Height,
                $"The visible frame is {frame.Left},{frame.Top} {frame.Right - frame.Left}x{frame.Bottom - frame.Top}, not the zone.");
        }
        finally { window.Close(); }
    }

    public static void BatchRenamePlansAppliesAndUndoes()
    {
        string folder = Path.Combine(Path.GetTempPath(), "soltex-rename-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string[] files = [.. SampleNames.Select(n => Path.Combine(folder, n))];
            foreach (string file in files) File.WriteAllText(file, "x");
            IReadOnlyList<RenameItem> plain = BatchRename.Plan(files, "IMG_", "Trip ", regex: false, matchCase: true, includeExtension: false);
            True(plain.Count(p => p.Changes) == 2 && plain[0].NewName == "Trip 001.jpg" && !plain[3].Changes, "Plain, case-sensitive replace is wrong.");
            IReadOnlyList<RenameItem> regex = BatchRename.Plan(files, @"^img_(\d+)$", "Photo $1", regex: true, matchCase: false, includeExtension: false);
            True(regex.Count(p => p.Changes) == 3 && regex[3].NewName == "Photo 003.JPG", "Regex replace without the extension is wrong.");
            True(BatchRename.Plan(files, "IMG_00", "", false, true, false)[0] is { NewName: "1.jpg", Problem: null }, "Removing text is wrong.");
            True(BatchRename.Plan(files, "IMG_00[12]", "same", true, true, false).Count(p => p.Problem == "Two items would get the same name.") == 1, "A name clash inside the batch must be refused.");
            True(BatchRename.Plan(files, "notes", "IMG_001", false, true, true) is var clash && clash[2].Problem is null, "Different extension is no clash.");
            True(BatchRename.Plan(files, "notes", "a:b", false, true, false)[2].Problem is not null, "Invalid characters must be refused.");
            True(BatchRename.Plan(files, "(", "x", true, true, false).All(p => p.Problem is not null), "An invalid pattern must be refused.");
            True(BatchRename.Plan([files[0]], "jpg", "png", false, true, false, isDirectory: _ => true)[0].NewName == "IMG_001.png"
                && BatchRename.Plan([files[0]], "jpg", "png", false, true, false, isDirectory: _ => false)[0].NewName == "IMG_001.jpg",
                "A folder keeps its whole name in the search; a file leaves its extension out.");

            IReadOnlyList<(string From, string To)> done = BatchRename.Apply(regex, out IReadOnlyList<string> failures);
            True(done.Count == 3 && failures.Count == 0 && File.Exists(Path.Combine(folder, "Photo 001.jpg")) && File.Exists(Path.Combine(folder, "notes.txt")), "Rename did not apply.");
            True(BatchRename.Undo(done, out failures).Count == 0 && failures.Count == 0 && files.All(File.Exists), "Undo did not put every name back.");

            // A file that is in use cannot be put back; it stays in the undo list so it can be tried again.
            IReadOnlyList<(string From, string To)> again = BatchRename.Apply(regex, out _);
            IReadOnlyList<(string From, string To)> left;
            using (FileStream inUse = new(again[0].To, FileMode.Open, FileAccess.Read, FileShare.Read))
                left = BatchRename.Undo(again, out failures);
            True(left is [var stuck] && stuck == again[0] && failures.Count == 1 && File.Exists(again[0].To) && File.Exists(files[1]) && File.Exists(files[3]),
                "A partly failed undo must return only the item that was not put back: " + string.Join("; ", failures));
            True(BatchRename.Undo(left, out failures).Count == 0 && failures.Count == 0 && files.All(File.Exists), "Trying the undo again did not put the last name back.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    public static void TextExtractorReadsText()
    {
        using Drawing.Bitmap bitmap = new(640, 120);
        using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Drawing.Color.White);
            graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using Drawing.Font font = new("Segoe UI", 36);
            graphics.DrawString("Hello Soltex 2026", font, Drawing.Brushes.Black, 10, 20);
        }
        Task<string?> recognize = TextExtractor.RecognizeAsync(bitmap);
        DispatcherFrame frame = new();
        recognize.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        string? text = recognize.GetAwaiter().GetResult();
        if (Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null)
        {
            // Machines without an OCR language (such as Windows Server build agents) can only check that the extractor
            // reports the missing language instead of returning text. Recognition itself was not checked.
            True(text is null, "With no OCR language installed, the extractor must report it rather than return text: " + text);
            throw new SkipException("No Windows OCR language is installed");
        }
        True(text is not null, "Windows has an OCR language for this user, but the extractor did not use it.");
        True(text!.Contains("Hello", StringComparison.OrdinalIgnoreCase) && text.Contains("2026", StringComparison.Ordinal), "OCR read: " + text);
    }

    /// <summary>Remapped keys carry the key's real scan code and extended flag, like a physical press.</summary>
    public static void KeyRemapperSendsScanCodes()
    {
        (int Key, ushort Scan, bool Extended, string Name)[] expected =
        [
            (0x14, 0x3A, false, "Caps Lock"), (0x1B, 0x01, false, "Esc"), (0xA2, 0x1D, false, "Left Ctrl"), (0xA3, 0x1D, true, "Right Ctrl"),
            (0x5B, 0x5B, true, "Left Windows"), (0x2E, 0x53, true, "Delete"), (0x13, 0x45, false, "Pause"), (0x70, 0x3B, false, "F1")
        ];
        foreach ((int key, ushort scan, bool extended, string name) in expected)
        {
            (ushort actualScan, bool actualExtended) = KeyRemapper.ScanCode(key);
            True(actualScan == scan && actualExtended == extended, $"{name} is sent as scan code 0x{actualScan:X2}{(actualExtended ? " (extended)" : "")}, not 0x{scan:X2}{(extended ? " (extended)" : "")}.");
        }
        True(KeyRemapper.Keys.Where(k => KeyRemapper.IsModifier(k.Key)).Select(k => k.Name).SequenceEqual(ShortcutKeys),
            "Ctrl, Alt and Windows keys must be recognised as shortcut keys.");
    }

    public static void KeyRemapperStartsMapsAndStops()
    {
        using KeyRemapper keys = new();
        True(keys.Set([new KeyRemap(0x13, 0x91)]) && KeyRemapper.Running, "The keyboard hook did not start."); // Pause → Scroll Lock (rarely pressed)
        True(KeyRemapper.Target(0x13) == 0x91 && KeyRemapper.Target(0x41) == 0x41, "Remapped and plain keys are wrong.");
        True(keys.Set([]) && !KeyRemapper.Running, "Clearing the remaps did not stop the hook.");
        True(KeyRemapper.Target(0x13) == 0x13, "A cleared remap still applies.");
    }

    public static void StartupAppsListAndSwitch()
    {
        string id = Guid.NewGuid().ToString("N");
        string userPath = @"Software\Soltex\StartupTest\" + id + @"\user", machinePath = @"Software\Soltex\StartupTest\" + id + @"\machine";
        string folder = Path.Combine(Path.GetTempPath(), "soltex-startup-" + id);
        Directory.CreateDirectory(folder);
        try
        {
            using Microsoft.Win32.RegistryKey user = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(userPath), machine = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(machinePath);
            using (Microsoft.Win32.RegistryKey run = user.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue("Chat", "\"C:\\Chat\\chat.exe\" --tray");
            using (Microsoft.Win32.RegistryKey run = machine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue("Driver panel", "C:\\Driver\\panel.exe");
            File.WriteAllText(Path.Combine(folder, "Notes.lnk"), "x");
            StartupApps apps = new(user, machine, folder, Path.Combine(folder, "none"));
            IReadOnlyList<StartupApp> list = apps.List();
            True(list.Count == 3 && list.All(a => a.Enabled), "Expected three enabled startup apps: " + string.Join(", ", list));
            StartupApp chat = list.Single(a => a.Name == "Chat"), notes = list.Single(a => a.Name == "Notes"), driver = list.Single(a => a.Name == "Driver panel");
            True(chat.CanChange && notes.CanChange && !driver.CanChange, "Only the user's own entries can be switched.");
            apps.SetEnabled(chat, false);
            apps.SetEnabled(notes, false);
            list = apps.List();
            True(!list.Single(a => a.Name == "Chat").Enabled && !list.Single(a => a.Name == "Notes").Enabled && list.Single(a => a.Name == "Driver panel").Enabled, "Turning apps off did not stick.");
            using (Microsoft.Win32.RegistryKey? approved = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))
                True(approved?.GetValue("Notes.lnk") is byte[] { Length: 12 } data && data[0] == 0x03, "The Startup folder entry must use Task Manager's format.");
            apps.SetEnabled(chat, true);
            True(apps.List().Single(a => a.Name == "Chat").Enabled, "Turning the app back on did not stick.");
            using (Microsoft.Win32.RegistryKey? run = user.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                True(run?.GetValue("Chat") is string, "Switching must never delete the app's own entry.");
            bool refused = false;
            try { apps.SetEnabled(driver, false); }
            catch (UnauthorizedAccessException) { refused = true; }
            True(refused, "Machine-wide entries must be refused.");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Soltex\StartupTest", throwOnMissingSubKey: false);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>32-bit installers' machine-wide entries (WOW6432Node) are listed, with their state from StartupApproved\Run32.</summary>
    public static void StartupAppsInclude32BitMachineEntries()
    {
        string id = Guid.NewGuid().ToString("N");
        string root = @"Software\Soltex\StartupTest\" + id, missingFolder = Path.Combine(Path.GetTempPath(), "soltex-startup-none-" + id);
        const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        try
        {
            using Microsoft.Win32.RegistryKey user = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\user"),
                machine = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\machine"),
                machine32 = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root + @"\machine32");
            using (Microsoft.Win32.RegistryKey run = machine.CreateSubKey(Run)) run.SetValue("Driver panel", "C:\\Driver\\panel.exe");
            using (Microsoft.Win32.RegistryKey run = machine32.CreateSubKey(Run))
            {
                run.SetValue("Updater", "\"C:\\Updater\\update.exe\" /background");
                run.SetValue("Driver panel", "C:\\Driver\\panel.exe"); // the same entry in both views is listed once
            }
            using (Microsoft.Win32.RegistryKey approved = machine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"))
                approved.SetValue("Updater", TurnedOff, Microsoft.Win32.RegistryValueKind.Binary);
            IReadOnlyList<StartupApp> list = new StartupApps(user, machine, missingFolder, missingFolder, machine32).List();
            True(list.Count == 2, "Expected the 64-bit and the 32-bit entry, each once: " + string.Join(", ", list));
            True(list.Single(a => a.Name == "Updater") is { Source: StartupSource.MachineRun, Enabled: false, CanChange: false }
                && list.Single(a => a.Name == "Driver panel").Enabled, "The 32-bit entry's state must come from StartupApproved\\Run32.");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Soltex\StartupTest", throwOnMissingSubKey: false); }
    }

    /// <summary>
    /// "Start when I sign in to Windows" in an isolated key: Task Manager's switch (StartupApproved\Run) is read with the
    /// Run value, and turning the app on here turns it back on there.
    /// </summary>
    public static void SuiteStartupFollowsTaskManager()
    {
        string path = @"Software\Soltex\SuiteStartupTest\" + Guid.NewGuid().ToString("N");
        const string Command = "\"C:\\Test\\Soltex.exe\" --tray";
        try
        {
            using Microsoft.Win32.RegistryKey root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path);
            byte[]? Approved()
            {
                using Microsoft.Win32.RegistryKey? key = root.OpenSubKey(SuiteStartupRegistration.ApprovedKey);
                return key?.GetValue(SuiteStartupRegistration.ValueName) as byte[];
            }
            True(SuiteStartupRegistration.State(root, Command) == SuiteStartupState.Off, "A new profile does not start at sign-in.");
            SuiteStartupRegistration.SetEnabled(true, root, Command);
            True(SuiteStartupRegistration.IsEnabled(root, Command) && Approved() is null, "Turning it on writes the Run value only.");

            // Task Manager turns it off without removing the Run value.
            using (Microsoft.Win32.RegistryKey approved = root.CreateSubKey(SuiteStartupRegistration.ApprovedKey))
                approved.SetValue(SuiteStartupRegistration.ValueName, TurnedOff, Microsoft.Win32.RegistryValueKind.Binary);
            True(SuiteStartupRegistration.State(root, Command) == SuiteStartupState.TurnedOffInTaskManager && !SuiteStartupRegistration.IsEnabled(root, Command),
                "Turned off in Task Manager must show as off.");
            SuiteStartupRegistration.SetEnabled(true, root, Command);
            True(SuiteStartupRegistration.IsEnabled(root, Command) && Approved() is [0x02, ..] data && data.Length == 12,
                "Turning it on again must also turn it back on in Task Manager (0x02).");

            SuiteStartupRegistration.SetEnabled(false, root, Command);
            using (Microsoft.Win32.RegistryKey? run = root.OpenSubKey(SuiteStartupRegistration.RunKey))
                True(run?.GetValue(SuiteStartupRegistration.ValueName) is null && Approved() is null, "Turning it off removes the Run value and Task Manager's switch for it.");

            // Another copy's entry (a different path) is neither shown as this app's nor removed.
            using (Microsoft.Win32.RegistryKey run = root.CreateSubKey(SuiteStartupRegistration.RunKey))
                run.SetValue(SuiteStartupRegistration.ValueName, "\"D:\\Other\\Soltex.exe\" --tray");
            SuiteStartupRegistration.SetEnabled(false, root, Command);
            using (Microsoft.Win32.RegistryKey? run = root.OpenSubKey(SuiteStartupRegistration.RunKey))
                True(SuiteStartupRegistration.State(root, Command) == SuiteStartupState.Off && run?.GetValue(SuiteStartupRegistration.ValueName) is string,
                    "Another copy's sign-in entry must be left alone.");
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Soltex\SuiteStartupTest", throwOnMissingSubKey: false); }
    }

    /// <summary>Search finds the sign-in switch as the hint words it, setup export and import, and lands on the Tools rows.</summary>
    public static void SearchFindsSignInSetupAndToolRows()
    {
        Shell.CommandIndex index = new();
        index.Replace(Enum.GetValues<Destination>().SelectMany(Pages.StaticSearch.For));
        foreach (string query in new[] { "start at sign-in", "start at sign in", "Start when I sign in" })
            True(index.Find(query) is [{ Destination: Destination.Settings, Anchor: "startup" }, ..],
                $"\"{query}\" must find Start when I sign in to Windows first, not " + string.Join(", ", index.Find(query).Select(e => e.Title)));
        foreach (string query in new[] { "export", "import setup", "backup" })
            True(index.Find(query) is [{ Destination: Destination.Settings, Anchor: "setup" }, ..], $"\"{query}\" must find setup export and import.");
        True(index.Find("startup apps") is [{ Destination: Destination.Tools, Anchor: "startup" }, ..], "Startup apps must still be found.");

        using PageShell shell = new();
        Pages.ToolsPage tools = shell.Open<Pages.ToolsPage>(Destination.Tools);
        Pages.PowerToolsPanel more = DesktopChecks.Descendants(tools).OfType<Pages.PowerToolsPanel>().Single();
        shell.Window.Navigate(Destination.Tools, "rename");
        Pump();
        Button open = DesktopChecks.Descendants(more.RenameRow).OfType<Button>().Single();
        True(DesktopChecks.Focused(shell.Window) == open && open.Content as string == "Open…", "Batch rename must reveal its own row and focus Open….");
        shell.Window.Navigate(Destination.Tools, "keys");
        Pump();
        Button add = DesktopChecks.Descendants(more.RemapRow).OfType<Button>().Single();
        True(DesktopChecks.Focused(shell.Window) == add, "Key remapping must reveal its own row and focus Add a remap.");
        shell.Window.Navigate(Destination.Tools, "quarantine");
        Pump();
        True(!tools.QuarantineList.IsVisible && DesktopChecks.Focused(shell.Window) == tools.OpenImports,
            "With nothing in quarantine, search must land on the section's button rather than a hidden list.");
    }

    /// <summary>Key remaps and startup apps are updated in place, so a list or switch being used keeps focus.</summary>
    public static void RemapsAndStartupAppsUpdateInPlace()
    {
        using PageShell shell = new();
        Pages.ToolsPage tools = shell.Open<Pages.ToolsPage>(Destination.Tools);
        Pages.PowerToolsPanel more = DesktopChecks.Descendants(tools).OfType<Pages.PowerToolsPanel>().Single();
        Button add = DesktopChecks.Descendants(more.RemapRow).OfType<Button>().Single();
        DesktopChecks.Invoke(add);
        Pump();
        ComboBox first = more.RemapFromLists.Single();
        first.Focus();
        first.SelectedIndex = KeyRemapper.Keys.ToList().FindIndex(k => k.Key == 0x13); // Pause
        Pump();
        True(shell.Services.Settings.Current.KeyRemaps is [{ From: 0x13 }], "Choosing the key was not saved.");
        True(more.RemapFromLists.Single() == first && DesktopChecks.Focused(shell.Window) == first, "Saving a remap must not rebuild its row.");
        DesktopChecks.Invoke(add);
        Pump();
        True(more.RemapFromLists.Count == 2 && more.RemapFromLists[0] == first, "Adding a remap must keep the rows already there.");
        Button[] remove = [.. DesktopChecks.Descendants(more).OfType<Button>().Where(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Remove key remap")];
        remove[1].Focus();
        DesktopChecks.Invoke(remove[1]);
        Pump();
        True(more.RemapFromLists.Single() == first && DesktopChecks.Focused(shell.Window) == remove[0],
            "Removing the last remap must move the keyboard to the row above: " + DesktopChecks.Focused(shell.Window));

        string id = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "soltex-startup-panel-" + id);
        Directory.CreateDirectory(folder);
        try
        {
            using Microsoft.Win32.RegistryKey user = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Soltex\StartupTest\" + id + @"\user"),
                machine = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Soltex\StartupTest\" + id + @"\machine");
            using (Microsoft.Win32.RegistryKey run = user.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue("Chat", "C:\\Chat\\chat.exe");
            using (Microsoft.Win32.RegistryKey run = machine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue("Driver panel", "C:\\Driver\\panel.exe");
            StartupApps apps = new(user, machine, folder, Path.Combine(folder, "none"));
            Pages.StartupAppsPanel panel = new(shell.Context, apps);
            CheckBox chat = panel.SwitchFor("Chat") ?? throw new InvalidOperationException("Chat is not listed.");
            True(chat.IsChecked == true && LogicalTexts(panel).Contains(Pages.StartupAppsPanel.OnlyYou) && LogicalTexts(panel).Contains(Pages.StartupAppsPanel.EveryUser),
                "Rows must say plainly who an app starts for: " + string.Join(" | ", LogicalTexts(panel)));
            apps.SetEnabled(apps.List().Single(a => a.Name == "Chat"), false); // as Task Manager would
            panel.Refresh();
            True(panel.SwitchFor("Chat") == chat && chat.IsChecked == false, "A changed app must be shown in place, keeping its switch.");
            using (Microsoft.Win32.RegistryKey run = user.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) run.SetValue("Editor", "C:\\Editor\\editor.exe");
            panel.Refresh();
            True(panel.SwitchFor("Editor") is not null && panel.SwitchFor("Chat") is { IsChecked: false }, "A new app must appear when the list is read again.");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Soltex\StartupTest", throwOnMissingSubKey: false);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A settings file that can't be written: Tools, Desktop and Appearance say so and show the saved state, without crashing.</summary>
    public static void FailedSettingsSavesShowTheSavedState()
    {
        using PageShell shell = new();
        Pages.ToolsPage tools = shell.Open<Pages.ToolsPage>(Destination.Tools);
        Pages.PowerToolsPanel more = DesktopChecks.Descendants(tools).OfType<Pages.PowerToolsPanel>().Single();
        Pages.DesktopPage desktop = shell.Open<Pages.DesktopPage>(Destination.Desktop);
        Pages.CustomizePage appearance = shell.Open<Pages.CustomizePage>(Destination.Appearance);
        Personalize.ExtrasPanel extras = DesktopChecks.Descendants(appearance).OfType<Personalize.ExtrasPanel>().Single();
        CheckBox schedule = DesktopChecks.Descendants(extras).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Switch between light and dark by time of day");
        shell.BreakSettingsFolder();
        int notices = shell.Services.Notices.Count(n => n.Message.StartsWith("Your change could not be saved", StringComparison.Ordinal));

        shell.Open<Pages.ToolsPage>(Destination.Tools);
        bool onTop = tools.AlwaysOnTopSwitch.IsChecked == true;
        DesktopChecks.ToggleThroughAutomation(tools.AlwaysOnTopSwitch);
        int zones = more.ZonesList.SelectedIndex;
        more.ZonesList.SelectedIndex = (zones + 1) % more.ZonesList.Items.Count;
        Pump();
        True(tools.AlwaysOnTopSwitch.IsChecked == onTop && more.ZonesList.SelectedIndex == zones, "Tools must show the saved choices again when saving fails.");

        shell.Open<Pages.DesktopPage>(Destination.Desktop);
        DesktopChecks.ToggleThroughAutomation(desktop.ArrangeSwitch);
        Pump();
        True(desktop.ArrangeSwitch.IsChecked == false && !shell.Services.Settings.Current.DesktopArrange, "The Desktop page must show the saved state again when saving fails.");

        shell.Open<Pages.CustomizePage>(Destination.Appearance);
        DesktopChecks.ToggleThroughAutomation(schedule);
        Pump();
        True(schedule.IsChecked == false && !shell.Services.Settings.Current.DarkModeSchedule, "Automatic dark mode must show the saved state again when saving fails.");
        int now = shell.Services.Notices.Count(n => n.Message.StartsWith("Your change could not be saved", StringComparison.Ordinal));
        True(now - notices >= 4, $"Each failed save must say so ({now - notices} notices for four failures).");
    }

    private static List<string> LogicalTexts(DependencyObject root)
    {
        List<string> found = [];
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is TextBlock { Text: { } text }) found.Add(text);
            if (child is DependencyObject node) found.AddRange(LogicalTexts(node));
        }
        return found;
    }

    private static readonly string[] SampleNames = ["IMG_001.jpg", "IMG_002.jpg", "notes.txt", "img_003.JPG"];
    private static readonly string[] ShortcutKeys = ["Left Ctrl", "Right Ctrl", "Left Alt", "Right Alt", "Left Windows", "Right Windows"];
    private static readonly byte[] TurnedOff = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]; // StartupApproved: low bit of the first byte set

    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect value, int size);
}
