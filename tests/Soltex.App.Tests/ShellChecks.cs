using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Pages;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Tests;

/// <summary>Checks for the shell. Pages that would start the audio helper (Audio) are not opened here.</summary>
internal static class ShellChecks
{
    private static readonly Destination[] InertPages = [Destination.MyPc, Destination.Taskbar, Destination.Start, Destination.Explorer, Destination.Appearance, Destination.Multitasking, Destination.Desktop, Destination.Customize, Destination.Tools, Destination.History, Destination.Settings];

    public static void ThemeResourcesAndPalettes()
    {
        Application app = Application.Current;
        foreach (string key in new[] { "Shell.Card", "Shell.NavItem", "Shell.PageTitle", "Shell.InfoBar", "Shell.Glyph" })
            Program.True(app.Resources.Contains(key) || app.TryFindResource(key) is not null, "Shell style missing: " + key);
        ThemeService.Apply(app, new AppSettings { Theme = ThemePreference.Light });
        double light = Luminance(app.TryFindResource("SolidBackgroundFillColorBaseBrush"));
        Program.True(app.TryFindResource("CardBackgroundFillColorDefaultBrush") is Brush, "Fluent card brush is unavailable in light mode.");
        ThemeService.Apply(app, new AppSettings { Theme = ThemePreference.Dark });
        double dark = Luminance(app.TryFindResource("SolidBackgroundFillColorBaseBrush"));
        Program.True(light > 0.8 && dark < 0.3, $"Light/dark palettes did not switch (light {light:F2}, dark {dark:F2}).");
        ThemeService.Apply(app, new AppSettings { Density = DensityPreference.Compact });
        Program.True(app.Resources["Shell.CardPadding"] is Thickness { Left: < 20 }, "Compact density did not tighten card padding.");
        ThemeService.Apply(app, new AppSettings());
    }

    public static void SettingsStoreResilience()
    {
        string root = NewRoot();
        string path = Path.Combine(root, "Settings", "app-settings-v1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, "{ not json");
        AppSettingsStore corrupt = new(path);
        Program.True(corrupt.LoadProblem is not null && corrupt.Current.Theme == ThemePreference.System, "Corrupt settings must fall back to defaults with an explanation.");
        Program.True(File.ReadAllText(path) == "{ not json", "Corrupt settings must be preserved until the user changes something.");

        File.WriteAllText(path, "{\"SchemaVersion\":99,\"Theme\":\"Dark\"}");
        AppSettingsStore future = new(path);
        Program.True(future.LoadProblem is not null && future.Current.Theme == ThemePreference.System, "A newer settings version must not be half-applied.");

        File.Delete(path);
        AppSettingsStore store = new(path);
        int changes = 0;
        store.Changed += _ => changes++;
        store.Update(s => s with { Theme = ThemePreference.Dark, ScalePercent = 999, HiddenDestinations = [Destination.Settings, Destination.Tools, Destination.Tools], StartPage = Destination.Settings });
        Program.True(store.Current.ScalePercent == AppSettings.MaximumScalePercent, "Scale must be clamped to 200%.");
        Program.True(store.Current.HiddenDestinations.SequenceEqual([Destination.Tools]), "Settings can never be hidden and duplicates are removed.");
        Program.True(store.Current.StartPage == Destination.MyPc, "Settings cannot be the start page.");
        store.Update(s => s with { });
        Program.True(changes == 1, "An unchanged update must not save or notify again.");
        AppSettingsStore reloaded = new(path);
        Program.True(reloaded.LoadProblem is null && reloaded.Current.Theme == ThemePreference.Dark && reloaded.Current.HiddenDestinations.SequenceEqual([Destination.Tools]),
            "Saved settings did not round-trip.");
        Program.True(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "Atomic save left a temporary file behind.");
    }

    public static void SearchRanking()
    {
        CommandIndex index = new();
        index.Replace(InertPages.Concat([Destination.Audio]).SelectMany(StaticSearch.For));
        // Windows' own dark mode (Appearance) is the common intent; this app's theme must still be offered.
        Program.True(First(index.Find("dark mode"))?.Destination == Destination.Appearance && index.Find("dark mode").Any(e => e.Anchor == "theme"),
            "\"dark mode\" must find Windows dark mode first and still offer this app's theme.");
        Program.True(First(index.Find("mic"))?.Anchor == "microphone", "\"mic\" must find microphone setup first.");
        Program.True(index.Find("virus").Any(e => e.Destination == Destination.MyPc || e.Anchor == "scan"), "\"virus\" must reach protection or scanning.");
        Program.True(index.Find("dark zzzz").Count == 0, "Every typed word must match something.");
        Program.True(index.Find("   ").Count == 0, "Blank search must return nothing.");

        using TestShell shell = new(s => s with { HiddenDestinations = [Destination.Tools] });
        shell.Window.RebuildSearchIndex();
        Program.True(!shell.Search("rustdesk").Any(), "Hidden pages must not appear in search.");
    }

    public static void NavigationIsNamedAndSelectable()
    {
        using TestShell shell = new();
        ListBox nav = shell.Window.PrimaryNav;
        Program.True(nav.Items.Count == 10, "Primary navigation must list Home, the five Windows areas, Desktop, Privacy, Audio and Tools.");
        // UI Automation reaches list items through the list's peer (item peers), exactly as a screen reader does.
        AutomationPeer listPeer = UIElementAutomationPeer.CreatePeerForElement(nav);
        List<AutomationPeer> itemPeers = listPeer.GetChildren() ?? [];
        Program.True(itemPeers.Count == nav.Items.Count, "Every navigation item must be present in the automation tree.");
        foreach (AutomationPeer peer in itemPeers)
        {
            Program.True(!string.IsNullOrWhiteSpace(peer.GetName()), "A navigation item has no accessible name.");
            Program.True(peer.GetPattern(PatternInterface.SelectionItem) is not null, "Navigation items must expose selection to assistive technology.");
        }
        Program.True(listPeer.GetName() == "Main navigation", "The navigation list must be named.");
        shell.Window.Navigate(Destination.Tools);
        Program.True(nav.SelectedItem is NavItem { Destination: Destination.Tools } && shell.Window.Title.StartsWith("Tools", StringComparison.Ordinal),
            "Navigating did not update selection and the window title.");
        shell.Window.Navigate(Destination.Settings);
        Program.True(nav.SelectedItem is null && shell.Window.FooterNav.SelectedItem is NavItem { Destination: Destination.Settings },
            "Settings selection must move to the footer item.");
        shell.Window.Navigate(Destination.History);
        Program.True(shell.Window.FooterNav.SelectedItem is NavItem { Destination: Destination.History }, "History must be reachable from the footer.");

        shell.Services.Settings.Update(s => s with { HiddenDestinations = [Destination.Audio] });
        Pump();
        Program.True(nav.Items.Cast<NavItem>().All(i => i.Destination != Destination.Audio), "A hidden page stayed in navigation.");
    }

    public static void EveryControlIsNamed()
    {
        using TestShell shell = new();
        List<string> unnamed = [];
        foreach (Destination destination in InertPages)
        {
            shell.Window.Navigate(destination);
            Pump();
            foreach (FrameworkElement control in Descendants(shell.Window).OfType<FrameworkElement>())
            {
                if (!control.IsVisible || control.TemplatedParent is not null) continue; // template parts belong to their control
                if (control is not (ButtonBase or ItemsControl or Slider or TextBox or ListBoxItem or Expander)) continue;
                string? name = UIElementAutomationPeer.CreatePeerForElement(control)?.GetName();
                if (string.IsNullOrWhiteSpace(name)) unnamed.Add($"{destination}: {control.GetType().Name} {control.Name}");
            }
        }
        Program.True(unnamed.Count == 0, "Unnamed interactive controls: " + string.Join("; ", unnamed));
    }

    public static void MyPcSamplesOnlyWhileShown()
    {
        using TestShell shell = new();
        MyPcPage page = (MyPcPage)shell.Window.Page(Destination.MyPc);
        page.OnHidden();
        Program.True(Field(page, "sampling") is null, "Home kept a sampler while hidden.");
        page.OnShown();
        Program.True(Field(page, "sampling") is CancellationTokenSource { IsCancellationRequested: false }, "Home did not sample while shown.");
        page.OnHidden();
        Program.True(Field(page, "sampling") is null, "Home did not stop sampling when hidden.");
        shell.Window.WindowState = WindowState.Minimized;
        Pump();
        Program.True(Field(page, "sampling") is null, "A minimized window must not sample.");
    }

    public static void RevealFocusesTarget()
    {
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Settings, "theme");
        Pump();
        SettingsPage settings = (SettingsPage)shell.Window.Page(Destination.Settings);
        Program.True(FocusManager.GetFocusedElement(shell.Window) == settings.ThemeChoice || settings.ThemeChoice.IsKeyboardFocusWithin,
            "Revealing a setting must focus its control.");
    }

    public static void CustomizeToggleAppliesThroughAutomation()
    {
        // Regression: UI Automation's Toggle (screen readers, voice control) must apply the setting, not just flip the box.
        MemoryRegistry registry = new();
        using TestShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        shell.Window.Navigate(Destination.Customize);
        Pump();
        CustomizePage page = (CustomizePage)shell.Window.Page(Destination.Customize);
        CheckBox toggle = Descendants(page).OfType<CheckBox>().First(c => c.Tag is SettingDefinition { Id: "privacy.typinginsights" });
        IToggleProvider provider = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle);
        provider.Toggle();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        RegistryTarget target = CalmCatalog.All.Single(d => d.Id == "privacy.typinginsights").Targets[0];
        while (!registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name).Exists && DateTime.UtcNow < deadline) Pump();
        Program.True(registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name) == RegistryValue.Dword(0), "Toggling through UI Automation did not apply the setting.");
        Program.True(shell.Services.Calm.Inspect(CalmCatalog.All.Single(d => d.Id == "privacy.typinginsights")).CanUndo, "The applied change must be undoable.");
    }

    public static void CustomizeSectionsLinksAndAutoHide()
    {
        // Render-only shell with the in-memory registry; the auto-hide switch is read from Windows but never toggled here.
        using TestShell shell = new();
        (Destination Page, string[] Headings)[] expected =
        [
            (Destination.Taskbar, ["Look", "Details", "Behavior"]),
            (Destination.Start, []),
            (Destination.Explorer, []),
            (Destination.Appearance, ["Desktop icons"]),
            (Destination.Multitasking, ["Keyboard, mouse and clipboard", "Gaming"]),
            (Destination.Customize, ["Tips and suggestions"]),
        ];
        foreach ((Destination destination, string[] headings) in expected)
        {
            shell.Window.Navigate(destination);
            Pump();
            CustomizePage page = (CustomizePage)shell.Window.Page(destination);
            UIElement[] children = [.. page.Groups.Children.OfType<UIElement>()];
            List<(int Index, StackPanel Panel, string Text)> found = [];
            for (int i = 0; i < children.Length; i++)
                if (children[i] is StackPanel { Orientation: Orientation.Horizontal, Children: [TextBlock { Text: { } text } title, ..] } heading
                    && ReferenceEquals(title.Style, page.FindResource("Shell.CardTitle")))
                    found.Add((i, heading, text));
            Program.True(found.Select(h => h.Text).SequenceEqual(headings),
                $"{destination}: headings are [{string.Join(", ", found.Select(h => h.Text))}], expected [{string.Join(", ", headings)}].");
            Program.True(found.All(h => h.Text != page.Title && h.Panel.IsVisible && h.Panel.ActualHeight > 0), $"{destination}: a heading repeats the page title or is not shown.");
            Program.True(found.All(h => h.Panel.Margin.Top == (h.Index == 0 ? 0 : 18)), $"{destination}: only a heading at the very top has no top margin.");

            // Each group's rows sit under that group's heading.
            foreach ((int index, _, string text) in found)
            {
                SettingGroup? group = Enum.GetValues<SettingGroup>().Cast<SettingGroup?>().FirstOrDefault(g => CalmCatalog.GroupTitle(g!.Value) == text);
                if (group is null) continue;
                int next = found.FirstOrDefault(h => h.Index > index).Index is int n and > 0 ? n : children.Length;
                foreach (SettingDefinition d in CalmCatalog.All.Where(d => d.Group == group))
                {
                    int row = Array.FindIndex(children, c => Descendants(c).OfType<Control>().Any(x => ReferenceEquals(x.Tag, d)));
                    Program.True(row > index && row < next, $"{destination}: {d.Id} is not under \"{text}\".");
                }
            }

            string? uri = CustomizePage.SettingsUriFor(destination);
            Program.True(destination == Destination.Explorer ? uri is null && page.SettingsLinkText.Visibility == Visibility.Collapsed
                    : uri is not null && uri.StartsWith("ms-settings:", StringComparison.Ordinal) && page.SettingsLinkText.IsVisible,
                $"{destination}: the Windows Settings link is wrong ({uri}).");
            string linkName = System.Windows.Automation.AutomationProperties.GetName(page.SettingsLink);
            string linkText = string.Concat(page.SettingsLink.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            Program.True(linkName == "More in Windows Settings" && linkText == linkName,
                $"{destination}: the Windows Settings link needs its name (name \"{linkName}\", text \"{linkText}\").");
        }
        Program.True(CustomizePage.SettingsUriFor(Destination.Taskbar) == "ms-settings:taskbar" && CustomizePage.SettingsUriFor(Destination.Start) == "ms-settings:personalization-start"
            && CustomizePage.SettingsUriFor(Destination.Appearance) == "ms-settings:personalization-colors" && CustomizePage.SettingsUriFor(Destination.Multitasking) == "ms-settings:multitasking"
            && CustomizePage.SettingsUriFor(Destination.Customize) == "ms-settings:privacy-general", "A page links to the wrong Windows Settings page.");

        // Automatically hide the taskbar: first row of Behavior, named, searchable.
        shell.Window.Navigate(Destination.Taskbar);
        Pump();
        CustomizePage taskbar = (CustomizePage)shell.Window.Page(Destination.Taskbar);
        CheckBox autoHide = taskbar.AutoHideSwitch ?? throw new InvalidOperationException("The Taskbar page has no auto-hide switch.");
        UIElement[] rows = [.. taskbar.Groups.Children.OfType<UIElement>()];
        int behavior = Array.FindIndex(rows, c => c is StackPanel { Children: [TextBlock { Text: "Behavior" }, ..] });
        Program.True(behavior >= 0 && behavior + 1 < rows.Length && Descendants(rows[behavior + 1]).Contains(autoHide), "Auto-hide must be the first row under Behavior.");
        Program.True(System.Windows.Automation.AutomationProperties.GetName(autoHide) == "Automatically hide the taskbar", "The auto-hide switch needs its name.");
        Program.True(First([.. shell.Search("auto-hide")]) is { Destination: Destination.Taskbar, Anchor: "autohide" }
            && First([.. shell.Search("automatically hide")]) is { Anchor: "autohide" }, "Search does not find auto-hide.");
        Program.True(First([.. shell.Search("desktop icons recycle")]) is { Destination: Destination.Appearance, Anchor: "desktop.recyclebin" }
            && First([.. shell.Search("game mode")]) is { Destination: Destination.Multitasking, Anchor: "gaming.gamemode" }, "Search does not find the new rows by their heading.");
    }

    public static void CustomizeRowSaysWhenWindowsRefuses()
    {
        // TaskbarDa (Widgets) is guarded by Windows' UCPD driver on 24H2 and later: the write fails with access denied.
        DenyingRegistry registry = new("TaskbarDa");
        using TestShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        shell.Window.Navigate(Destination.Taskbar);
        Pump();
        CustomizePage page = (CustomizePage)shell.Window.Page(Destination.Taskbar);
        CheckBox widgets = Descendants(page).OfType<CheckBox>().First(c => c.Tag is SettingDefinition { Id: "taskbar.widgets" });
        TextBlock note = page.NoteFor("taskbar.widgets") ?? throw new InvalidOperationException("The Widgets row has no note.");
        Program.True(widgets.IsChecked == true && note.Visibility == Visibility.Collapsed, "Setup: Widgets starts on, with no note.");
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(widgets).GetPattern(PatternInterface.Toggle)).Toggle();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while ((registry.Denied == 0 || page.Busy.Visibility == Visibility.Visible || !note.Inlines.OfType<System.Windows.Documents.Hyperlink>().Any()) && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(5);
        }
        string text = string.Concat(note.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
        Program.True(registry.Denied == 1 && note.IsVisible && text.StartsWith("Windows doesn't let apps change this.", StringComparison.Ordinal)
            && !text.Contains("registry", StringComparison.OrdinalIgnoreCase), "The row must say plainly that Windows refused: " + text);
        Program.True(note.Inlines.OfType<System.Windows.Documents.Hyperlink>().SingleOrDefault() is { } link
            && System.Windows.Automation.AutomationProperties.GetName(link) == "Open Windows Settings", "The row must link to Windows Settings.");
        Program.True(widgets.IsChecked == true && !shell.Services.Calm.Inspect(CalmCatalog.All.Single(d => d.Id == "taskbar.widgets")).CanUndo,
            "The switch must show what Windows kept, with nothing to undo.");
    }

    public static void TaskbarLookSavesChoicesAndShowsRelevantRows()
    {
        // The shell is created render-only, so the taskbar service is never attached: nothing reaches Explorer.
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Taskbar);
        Pump();
        TaskbarLookPanel panel = Descendants(shell.Window.Page(Destination.Taskbar).View).OfType<TaskbarLookPanel>().Single();
        AppSettings Saved() => shell.Services.Settings.Current;
        Program.True(panel.ColorRow.Visibility == Visibility.Collapsed && panel.MaximizedRow.Visibility == Visibility.Collapsed,
            "Windows default shows only the background and line rows.");

        panel.LookList.SelectedIndex = 1; // Clear
        Pump();
        Program.True(Saved().TaskbarLook == TaskbarLookKind.Clear, "Choosing Clear was not saved.");
        Program.True(panel.ColorRow.Visibility == Visibility.Visible && panel.OpacityRow.Visibility == Visibility.Visible && panel.MaximizedRow.Visibility == Visibility.Visible,
            "Clear shows color, tint and the maximized look.");

        Button blue = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Taskbar color: Blue");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(blue).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        Program.True(Saved().TaskbarColor == 0x000067C0, "Choosing a swatch through UI Automation did not save the color.");
        Program.True(blue.BorderBrush != Brushes.Transparent, "The chosen swatch is not marked.");

        panel.LookList.SelectedIndex = 2; // Acrylic starts with a visible tint
        Pump();
        Program.True(Saved().TaskbarLook == TaskbarLookKind.Acrylic && Saved().TaskbarColor == 0x660067C0, "Acrylic did not start with a visible tint.");
        Program.True(panel.OpacityText.Text == "40%", "The tint percentage is wrong.");

        panel.MaximizedList.SelectedIndex = 2; // Solid
        panel.LineSwitch.IsChecked = false;
        Pump();
        Program.True(Saved().TaskbarMaximizedLook == TaskbarLookKind.Opaque && !Saved().TaskbarLine, "The maximized look or line choice was not saved.");

        panel.LookList.SelectedIndex = 3; // Blur has no color or tint
        Pump();
        Program.True(panel.ColorRow.Visibility == Visibility.Collapsed && panel.OpacityRow.Visibility == Visibility.Collapsed, "Blur shows no color or tint rows.");
        Program.True(First([.. shell.Search("translucent")]) is { Destination: Destination.Taskbar, Anchor: "look" }, "Search does not find the taskbar look.");
    }

    public static void TaskbarDetailsSaveChoices()
    {
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Taskbar);
        Pump();
        TaskbarDetailsPanel panel = Descendants(shell.Window.Page(Destination.Taskbar).View).OfType<TaskbarDetailsPanel>().Single();
        AppSettings Saved() => shell.Services.Settings.Current;
        Program.True(Saved().TaskbarTweaks.Count == 0 && panel.ControlFor("indicator") is CheckBox { IsChecked: true }, "Details start at Windows' look.");

        // The indicator switch through UI Automation Toggle (screen readers, voice control).
        ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(panel.ControlFor("indicator")).GetPattern(PatternInterface.Toggle)).Toggle();
        ((ComboBox)panel.ControlFor("clock")).SelectedIndex = 1;    // Time only
        ((ComboBox)panel.ControlFor("iconsize")).SelectedIndex = 2; // Large
        Pump();
        Program.True(Saved().TaskbarTweaks.TryGetValue("indicator", out string? indicator) && indicator == "hidden"
            && Saved().TaskbarTweaks["clock"] == "time" && Saved().TaskbarTweaks["iconsize"] == "large", "Details choices were not saved.");
        Program.True(TaskbarTweaks.RulesFor(Saved().TaskbarTweaks).Count == 4, "Hidden indicator, time only and large icons are four rules.");

        ((ComboBox)panel.ControlFor("clock")).SelectedIndex = 0;   // back to Windows' look removes the choice
        panel.ScrollVolumeSwitch.IsChecked = true;
        Pump();
        Program.True(!Saved().TaskbarTweaks.ContainsKey("clock") && Saved().TaskbarScrollVolume, "Clock reset or scroll-for-volume was not saved.");
        Program.True(First([.. shell.Search("windhawk clock")]) is { Destination: Destination.Taskbar, Anchor: "tweak.clock" }, "Search does not find the clock option.");
        Program.True(First([.. shell.Search("scroll volume")]) is { Anchor: "scrollvolume" }, "Search does not find scroll for volume.");
    }

    public static void DesktopPageSavesGroupsAndWidgets()
    {
        // Render-only shell: the desktop service is never attached, so no window appears on the desktop.
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Desktop);
        Pump();
        DesktopPage page = (DesktopPage)shell.Window.Page(Destination.Desktop);
        AppSettings Saved() => shell.Services.Settings.Current;
        DesktopChecks.ToggleThroughAutomation(page.WidgetSwitch(DesktopWidgetKind.Clock));
        page.AddGroup(null, "Projects");
        Pump();
        Program.True(Saved().DesktopWidgets is [{ Kind: DesktopWidgetKind.Clock }], "Turning the clock on did not save it.");
        Program.True(Saved().DesktopGroups is [{ Title: "Projects", Folder: null }], "Adding a group did not save it.");
        Program.True(Descendants(page).OfType<Button>().Any(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Remove group Projects"), "The new group has no row.");
        DesktopChecks.ToggleThroughAutomation(page.ArrangeSwitch);
        Pump();
        Program.True(Saved().DesktopArrange, "Arrange was not saved.");
        Program.True(First([.. shell.Search("fences")]) is { Destination: Destination.Desktop, Anchor: "groups" }, "Search does not find desktop groups.");
    }

    public static void PowerToolsSaveChoices()
    {
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Tools);
        Pump();
        PowerToolsPanel panel = Descendants(shell.Window.Page(Destination.Tools).View).OfType<PowerToolsPanel>().Single();
        AppSettings Saved() => shell.Services.Settings.Current;
        Program.True(Saved().TextExtractorShortcut && panel.TextExtractorSwitch.IsChecked == true, "The text extractor shortcut starts on.");
        DesktopChecks.ToggleThroughAutomation(panel.TextExtractorSwitch);
        panel.ZonesList.SelectedIndex = 2; // Three columns
        Button add = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Add a key remap");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(add).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        Program.True(!Saved().TextExtractorShortcut && Saved().WindowZones == WindowZonesLayout.ThreeColumns, "Text extractor or zones were not saved.");
        Program.True(Saved().KeyRemaps is [{ From: 0x14, To: 0x14 }], "Adding a remap should start inactive until both keys are chosen.");
        Program.True(Descendants(panel).OfType<ComboBox>().Any(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Key to remap"), "The remap row is missing.");
        Program.True(First([.. shell.Search("fancyzones")]) is { Destination: Destination.Tools, Anchor: "zones" }, "Search does not find window zones.");
    }

    public static void PersonalizeExtrasShowAndSave()
    {
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Appearance);
        Pump();
        Personalize.ExtrasPanel appearance = Descendants(shell.Window.Page(Destination.Appearance).View).OfType<Personalize.ExtrasPanel>().Single();
        CheckBox schedule = Descendants(appearance).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Switch between light and dark by time of day");
        DesktopChecks.ToggleThroughAutomation(schedule);
        Pump();
        Program.True(shell.Services.Settings.Current.DarkModeSchedule, "Turning the schedule on did not save it (render-only shell: nothing is switched).");
        Program.True(Descendants(appearance).OfType<ComboBox>().Any(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Pointer scheme" && c.Items.Count > 1), "Pointer schemes are not listed.");
        shell.Window.Navigate(Destination.Start);
        Pump();
        Personalize.ExtrasPanel start = Descendants(shell.Window.Page(Destination.Start).View).OfType<Personalize.ExtrasPanel>().Single();
        Program.True(Descendants(start).OfType<CheckBox>().Count() == 9, "Start should offer nine folders.");
        Program.True(First([.. shell.Search("start11 downloads")]) is { Destination: Destination.Start, Anchor: "startfolders" }, "Search does not find Start folders.");
    }

    public static void OldProfilesKeepTheirMeaning()
    {
        string root = NewRoot();
        string file = Path.Combine(root, "format1.json");
        // Format 1 stored [Windows default, De-Windows] for this row; format 2 shows the feature as [Off, On].
        File.WriteAllText(file, """
            { "FormatVersion": 1, "App": {}, "WindowsChoices": { "start.recommendations": 1, "taskbar.widgets": 0 }, "SystemItems": [] }
            """);
        SettingsProfile loaded = SettingsProfile.Load(file);
        SettingDefinition recommendations = CalmCatalog.All.Single(d => d.Id == "start.recommendations");
        Program.True(recommendations.Options[loaded.WindowsChoices["start.recommendations"]].Label == "Off",
            "A format 1 'De-Windows' choice must load as the feature turned Off.");
        Program.True(loaded.WindowsChoices["taskbar.widgets"] == 0, "Rows whose options did not move must load unchanged.");
        File.WriteAllText(file, """
            { "FormatVersion": 2, "App": {}, "WindowsChoices": { "start.recommendations": 1 }, "SystemItems": [] }
            """);
        Program.True(SettingsProfile.Load(file).WindowsChoices["start.recommendations"] == 1, "Format 2 choices load as saved.");
    }

    public static void ProfileExportsAndImports()
    {
        string root = NewRoot();
        MemoryRegistry source = new(), target = new();
        ChangeEngine sourceEngine = new(source, new ChangeJournal(Path.Combine(root, "a.jsonl")), 26300);
        ChangeEngine targetEngine = new(target, new ChangeJournal(Path.Combine(root, "b.jsonl")), 26300);
        SettingDefinition widgets = CalmCatalog.All.Single(d => d.Id == "taskbar.widgets"), search = CalmCatalog.All.Single(d => d.Id == "taskbar.search");
        Program.True(sourceEngine.ApplyChoice(widgets, 0).Outcome == ChangeOutcome.Applied && sourceEngine.ApplyChoice(search, 2).Outcome == ChangeOutcome.Applied, "Setup failed.");
        SystemEngine system = new(new EmptySystemHost(), new ChangeJournal(Path.Combine(root, "s.jsonl")));
        AppSettings app = new() { TaskbarLook = TaskbarLookKind.Clear, DesktopWidgets = [new(DesktopWidgetKind.Clock, 1, 2)] };
        SettingsProfile profile = SettingsProfile.Capture(app, sourceEngine, system);
        Program.True(profile.WindowsChoices.Count == 2 && profile.WindowsChoices["taskbar.search"] == 2, "Capture missed the Windows options.");
        string file = Path.Combine(root, "setup.json");
        profile.Save(file);
        SettingsProfile loaded = SettingsProfile.Load(file);
        Program.True(loaded.App.TaskbarLook == TaskbarLookKind.Clear && loaded.App.DesktopWidgets.Count == 1, "App preferences did not survive export.");
        Program.True(loaded.ApplyWindowsChoices(targetEngine).All(r => r.Outcome == ChangeOutcome.Applied) && targetEngine.Inspect(search).Choice == 2 && targetEngine.Inspect(search).CanUndo,
            "Importing did not set the same Windows options (undoable).");
        File.WriteAllText(file, "{\"FormatVersion\":1,\"App\":{},\"WindowsChoices\":{\"nope\":1,\"taskbar.search\":99},\"SystemItems\":[\"nope\"]}");
        loaded = SettingsProfile.Load(file);
        Program.True(loaded.WindowsChoices.Count == 0 && loaded.SystemItems.Count == 0, "Unknown or out-of-range entries must be dropped on import.");
    }

    /// <summary>A PC with nothing to report (profile capture reads only).</summary>
    private sealed class EmptySystemHost : ISystemHost
    {
        public RegistryValue Read(RegistryHive2 hive, string path, string name) => RegistryValue.Absent;
        public void Write(RegistryHive2 hive, string path, string name, RegistryValue value) => throw new InvalidOperationException("Read only.");
        public int? ServiceStart(string service) => null;
        public void SetServiceStart(string service, int startType) => throw new InvalidOperationException("Read only.");
        public bool? TaskEnabled(string taskPath) => null;
        public void SetTaskEnabled(string taskPath, bool enabled) => throw new InvalidOperationException("Read only.");
        public bool FirewallRuleExists(string ruleName) => false;
        public void AddFirewallBlock(FirewallAction rule) => throw new InvalidOperationException("Read only.");
        public void RemoveFirewallRule(string ruleName) => throw new InvalidOperationException("Read only.");
        public bool? AppInstalled(RemoveAppAction app) => null;
        public void RemoveApp(RemoveAppAction app) => throw new InvalidOperationException("Read only.");
        public bool OtherBrowserInstalled() => true;
    }

    public static void CustomizeToggleSurvivesMissingSyncContext()
    {
        // Regression for a crash report from a real launch: a switch's change handler started without a UI
        // synchronization context, so the code after the background apply ran on a thread-pool thread, touched
        // the page and terminated the process. The page must return to its dispatcher explicitly.
        MemoryRegistry registry = new();
        using TestShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        shell.Window.Navigate(Destination.Customize);
        Pump();
        CustomizePage page = (CustomizePage)shell.Window.Page(Destination.Customize);
        CheckBox toggle = Descendants(page).OfType<CheckBox>().First(c => c.Tag is SettingDefinition { Id: "privacy.typinginsights" });
        System.Threading.SynchronizationContext? saved = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(null);
        try { toggle.IsChecked = !toggle.IsChecked; }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(saved); }
        RegistryTarget target = CalmCatalog.All.Single(d => d.Id == "privacy.typinginsights").Targets[0];
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while ((page.Busy.Visibility == Visibility.Visible || !registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name).Exists) && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(5);
        }
        Program.True(registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name) == RegistryValue.Dword(0), "The change did not apply.");
        Program.True(page.Busy.Visibility == Visibility.Collapsed, "The page did not finish the change on its own thread.");
    }

    public static void ElevatedHelperIsBounded()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(local, "Soltex", "test-system-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string request = Path.Combine(folder, SystemRunner.RequestName);
            Program.True(SystemRunner.TrustedDirectory(request) == folder, "The helper must accept its own request location.");
            bool Refused(string path) { try { SystemRunner.TrustedDirectory(path); return false; } catch (Exception e) when (e is UnauthorizedAccessException or InvalidDataException) { return true; } }
            Program.True(Refused(Path.Combine(Path.GetTempPath(), SystemRunner.RequestName)), "A request outside this user's app data must be refused.");
            Program.True(Refused(Path.Combine(folder, "other.json")), "Any other file name must be refused.");
            File.WriteAllText(request, "{\"Action\":\"apply\",\"Items\":[\"system.not.real\"]}");
            Program.True(SystemRunner.Run(request) == 1, "An unknown change must fail.");
            string result = File.ReadAllText(Path.Combine(folder, SystemRunner.ResultName));
            Program.True(result.Contains("Unknown change", StringComparison.Ordinal) && !File.Exists(Path.Combine(folder, SystemRunner.JournalName)),
                "An unknown change must be refused before anything is journalled.");
        }
        finally { Directory.Delete(folder, true); }
    }

    public static void PagesRenderInThemesAndScales()
    {
        foreach ((ThemePreference theme, int scale) in new[] { (ThemePreference.Light, 100), (ThemePreference.Dark, 100), (ThemePreference.Light, 200) })
        {
            ThemeService.Apply(Application.Current, new AppSettings { Theme = theme });
            using TestShell shell = new(s => s with { Theme = theme, ScalePercent = scale });
            Program.True(Math.Abs(shell.Window.ScaleRoot.LayoutTransform.Value.M11 - scale / 100d) < 0.001, "Scale was not applied to the shell.");
            foreach (Destination destination in InertPages)
            {
                shell.Window.Navigate(destination);
                Pump();
                RenderTargetBitmap bitmap = RenderSmokeCapture.Capture(shell.Window);
                byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                int i = ((bitmap.PixelHeight - 6) * bitmap.PixelWidth + bitmap.PixelWidth - 40) * 4; // bottom-right page background
                double luminance = (0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i]) / 255;
                Program.True(theme == ThemePreference.Light ? luminance > 0.75 : luminance < 0.3,
                    $"{destination} {theme} {scale}%: page background luminance {luminance:F2} does not match the theme.");
                Program.True(Program.CountVisiblePixels(pixels) > 100_000, $"{destination} {theme} {scale}%: render was empty.");
            }
        }
        ThemeService.Apply(Application.Current, new AppSettings());
    }

    public static void NormalStartupWiringIsInert()
    {
        // Regression for a crash on every normal start (a timer built with a missing callback in the drift check).
        // Render captures and the other shell checks never run the session attach path, so it is exercised here
        // with default settings, where it must not hook input, write to Windows or open windows. The tray icon,
        // hotkeys and taskbar look are left out on purpose: they reach the notification area and Explorer.
        string root = NewRoot();
        AppSettingsStore store = new(AppSettingsStore.DefaultPath(root));
        ShellServices services = new(root, store, Dispatcher.CurrentDispatcher,
            journal => new ChangeEngine(new MemoryRegistry(), new ChangeJournal(journal), 26300));
        Program.True(services.Zones is null && services.Theme is null,
            "Zones and the dark mode schedule must not exist before the session attaches (they follow settings changes).");
        try
        {
            services.AttachInputTools();
            services.Desktop.Attach(desktopLayer: false);
            Pump();
            Program.True(services.Drift.IsAttached, "The drift check was not scheduled.");
            Program.True(services.Zones is not null && Field(services.Zones, "hook") is nint zoneHook && zoneHook == 0,
                "Window zones hooked window events while turned off.");
            Program.True(services.Theme is not null && Field(services.Theme, "timer") is DispatcherTimer { IsEnabled: false },
                "The dark mode schedule runs while turned off.");
            Program.True(!Tools.KeyRemapper.Running, "Key remapping hooked the keyboard without a remap.");
            Program.True(Field(services.Wallpaper, "window") is null, "A wallpaper window opened without a video.");
            Program.True(services.Desktop.GroupWindows.Count == 0 && services.Desktop.WidgetWindows.Count == 0, "Desktop windows opened without groups or widgets.");
            Program.True(services.SystemChanges.UnavailableReason is not null,
                "Full De-Windows must not offer the administrator helper with a separate profile folder.");
        }
        finally
        {
            // Disposed on this (UI) thread, as the shell does when it quits.
            ValueTask closing = services.DisposeAsync();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!closing.IsCompleted && DateTime.UtcNow < deadline) Pump();
            Program.True(closing.IsCompleted, "Shutting the services down did not finish.");
            closing.GetAwaiter().GetResult();
        }
        Program.True(!services.Drift.IsAttached, "The drift check kept running after shutdown.");
        Program.True(Field(services.Zones!, "hook") is nint after && after == 0 && !Tools.KeyRemapper.Running, "A hook survived shutdown.");
    }

    public static void DriftIsReportedOnceAndNeutrally()
    {
        SettingDefinition apps = CalmCatalog.All.Single(d => d.Id == "appearance.apps");
        SettingDefinition widgets = CalmCatalog.All.Single(d => d.Id == "taskbar.widgets");
        SystemItem item = SystemCatalog.All[0];
        DriftReport report = new([apps, widgets], [new SystemItemStatus(item, SystemItemState.NotApplied, true)]);
        Program.True(report.Summary.StartsWith($"3 settings you changed with {ProductIdentity.DisplayName} are different now:", StringComparison.Ordinal)
            && !report.Summary.Contains("Windows turned", StringComparison.Ordinal), "The drift summary must be neutral: " + report.Summary);
        Program.True(new DriftReport([widgets], []).Summary.EndsWith(": " + widgets.Title + ".", StringComparison.Ordinal), "A single difference must be named.");

        DriftReport scheduled = DriftWatcher.Filter(report, new AppSettings { DarkModeSchedule = true });
        Program.True(scheduled.Settings is [{ Id: "taskbar.widgets" }] && scheduled.SystemItems.Count == 1,
            "The dark mode schedule's own light and dark switches must not be reported while it is on.");
        Program.True(DriftWatcher.Filter(report, new AppSettings()).Count == 3, "Without the schedule every difference is reported.");

        string? key = AppSettings.NormalizeIdList(string.Join("|", report.Ids.Reverse()));
        Program.True(key == AppSettings.NormalizeIdList(" " + string.Join(" | ", report.Ids) + " |"), "The remembered report must not depend on order or spacing.");
        Program.True(!DriftWatcher.IsNew(report.Ids, key), "The same report must not be shown again.");
        Program.True(!DriftWatcher.IsNew(["taskbar.widgets"], key), "Fewer differences than before is nothing new.");
        Program.True(DriftWatcher.IsNew(report.Ids, "taskbar.widgets"), "A new difference must be reported.");

        string root = NewRoot();
        string path = AppSettingsStore.DefaultPath(root);
        new AppSettingsStore(path).Update(s => s with { ReportedDrift = string.Join("|", report.Ids.Reverse()) });
        Program.True(new AppSettingsStore(path).Current.ReportedDrift == key, "The remembered report must survive a restart.");

        DriftPutBack done = new([new ChangeResult(widgets, ChangeOutcome.Applied, "Put back.")], [], null);
        Program.True(done.Succeeded && done.Message == "Put back 1 setting.", "Put back must confirm success: " + done.Message);
        DriftPutBack cancelled = new([new ChangeResult(widgets, ChangeOutcome.Applied, "Put back.")], [item.Id],
            new SystemResponse([], "Cancelled at the administrator prompt."));
        Program.True(!cancelled.Succeeded && cancelled.StillDifferent.SequenceEqual([item.Id])
            && cancelled.Message.Contains(item.Subject, StringComparison.Ordinal) && cancelled.Message.Contains("Cancelled", StringComparison.Ordinal),
            "A declined administrator prompt must be reported with what was not put back: " + cancelled.Message);
        DriftPutBack partial = new([new ChangeResult(widgets, ChangeOutcome.Failed, "Access denied.")], [item.Id],
            new SystemResponse([new SystemResult(item.Id, true, "")], null));
        Program.True(partial.Message.StartsWith("Put back 1 of 2.", StringComparison.Ordinal) && partial.Message.Contains(widgets.Title, StringComparison.Ordinal)
            && partial.StillDifferent.SequenceEqual([widgets.Id]), "A partial put back must name what failed: " + partial.Message);
    }

    public static void SettingsStoreKeepsLargeDesktopGroups()
    {
        string root = NewRoot();
        string path = AppSettingsStore.DefaultPath(root);
        AppSettingsStore store = new(path);
        // About 900 items with long and non-Latin names: far over the old 64 KB read limit.
        DesktopGroup[] groups = [.. Enumerable.Range(0, 5).Select(g => new DesktopGroup("g" + g, "Группа " + g, null,
            [.. Enumerable.Range(0, 180).Select(i => $@"D:\Проекты\Документы\длинное имя папки {g}\отчёт номер {i:D4} — итоговая версия.docx")],
            24, 24, 280, 220))];
        store.Update(s => s with { DesktopGroups = groups });
        Program.True(new FileInfo(path).Length > 64 * 1024, "The fixture must exceed the old read limit.");
        Program.True(!File.ReadAllText(path).Contains("\\u04", StringComparison.Ordinal), "Non-Latin names must be saved as they are, not escaped.");
        AppSettingsStore reloaded = new(path);
        Program.True(reloaded.LoadProblem is null && reloaded.Current.DesktopGroups.Sum(g => g.Items.Count) == 900,
            "A settings file this app saved must load again: " + reloaded.LoadProblem);

        string tooLong = @"D:\" + new string('a', AppSettings.MaximumPathLength + 1);
        store.Update(s => s with
        {
            LiveWallpaper = tooLong,
            DesktopGroups = [new DesktopGroup("x", new string('t', 500), tooLong, [tooLong, @"D:\ok.txt"], 0, 0, 280, 220)]
        });
        Program.True(store.Current.LiveWallpaper is null && store.Current.DesktopGroups is [{ Folder: null, Items: [@"D:\ok.txt"] }]
            && store.Current.DesktopGroups[0].Title.Length == AppSettings.MaximumTitleLength, "Over-long paths and titles must be limited.");

        string? copy = store.SaveCopy(ProfilePanel.BeforeImportSuffix);
        Program.True(copy is not null && File.Exists(copy) && copy.EndsWith("app-settings-v1.before-import.json", StringComparison.Ordinal),
            "An import must be able to keep a copy of the current settings.");
    }

    public static void ImportFindsNetworkLocations()
    {
        string share = @"\\host.example\share";
        AppSettings app = new()
        {
            LiveWallpaper = share + @"\loop.mp4",
            DesktopGroups = [new DesktopGroup("g", "Shared", share, [@"C:\Data\a.txt", @"\\?\UNC\host.example\share\b.txt", "relative.txt", @"\\?\C:\Data\c.txt"], 0, 0, 280, 220)]
        };
        SettingsProfile profile = new(SettingsProfile.CurrentFormat, app, new Dictionary<string, int>(), []);
        IReadOnlyList<string> network = profile.NonLocalPaths();
        Program.True(network.Count == 4 && network.Contains(share) && network.Contains(share + @"\loop.mp4") && network.Contains("relative.txt"),
            "Network shares, UNC device paths and relative paths must be listed before import: " + string.Join("; ", network));
        SettingsProfile local = profile.WithoutNonLocalPaths();
        Program.True(local.App.LiveWallpaper is null && local.App.DesktopGroups is [{ Folder: null, Items: [@"C:\Data\a.txt", @"\\?\C:\Data\c.txt"] }]
            && local.NonLocalPaths().Count == 0, "Removing network locations must keep the local ones.");
    }

    // ---------- helpers ----------
    private sealed class TestShell : IDisposable
    {
        public TestShell(Func<AppSettings, AppSettings>? configure = null, Func<string, ChangeEngine>? calmFactory = null)
        {
            string root = NewRoot();
            AppSettingsStore store = new(AppSettingsStore.DefaultPath(root));
            if (configure is not null) store.Update(configure);
            // Tests never use the real registry engine: a fake is the default.
            Services = new ShellServices(root, store, Dispatcher.CurrentDispatcher,
                calmFactory ?? (journal => new ChangeEngine(new MemoryRegistry(), new ChangeJournal(journal), 26300)));
            Window = new ShellWindow(Services, renderSmoke: true);
            Window.Closed += (_, _) => closed = true;
            RenderSmokeCapture.ConfigureWindow(Window);
            Window.ShowInTaskbar = false;
            Window.ShowActivated = false;
            Window.WindowStartupLocation = WindowStartupLocation.Manual;
            Window.Left = -32_000;
            Window.Top = -32_000;
            Window.Show();
            Pump();
        }

        private bool closed;
        public ShellServices Services { get; }
        public ShellWindow Window { get; }

        public IEnumerable<SearchEntry> Search(string query)
        {
            CommandIndex index = (CommandIndex)typeof(ShellWindow).GetField("search", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Window)!;
            return index.Find(query);
        }

        public void Dispose()
        {
            Window.RequestQuit();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!closed && DateTime.UtcNow < deadline) Pump();
            Program.True(closed, "The shell did not finish shutting down.");
        }
    }

    private static SearchEntry? First(IReadOnlyList<SearchEntry> found) => found.Count > 0 ? found[0] : null;

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "soltex-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>In-memory registry so shell tests never change this PC's settings.</summary>
    private sealed class MemoryRegistry : IRegistryStore
    {
        private readonly Dictionary<string, RegistryValue> values = new(StringComparer.OrdinalIgnoreCase);
        public RegistryValue Read(RegistryHive2 hive, string path, string name) =>
            values.TryGetValue(hive + "|" + path + "|" + name, out RegistryValue v) ? v : RegistryValue.Absent;
        public void Write(string currentUserPath, string name, RegistryValue value)
        {
            NeverTouch.Guard(RegistryHive2.CurrentUser, currentUserPath, name);
            string key = RegistryHive2.CurrentUser + "|" + currentUserPath + "|" + name;
            if (value.Exists) values[key] = value; else values.Remove(key);
        }
    }

    /// <summary>In-memory registry that refuses one value with access denied, as Windows does for values it guards from apps.</summary>
    private sealed class DenyingRegistry(string deniedName) : IRegistryStore
    {
        private readonly MemoryRegistry inner = new();
        private int denied;
        public int Denied => Volatile.Read(ref denied);
        public RegistryValue Read(RegistryHive2 hive, string path, string name) => inner.Read(hive, path, name);
        public void Write(string currentUserPath, string name, RegistryValue value)
        {
            if (string.Equals(name, deniedName, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref denied);
                throw new UnauthorizedAccessException("Access to the registry key is denied.");
            }
            inner.Write(currentUserPath, name, value);
        }
    }

    private static double Luminance(object? brush) => brush is SolidColorBrush { Color: var c }
        ? (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255
        : -1;
}
