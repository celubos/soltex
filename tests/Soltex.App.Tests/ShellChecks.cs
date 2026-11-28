using System.IO;
using System.Windows;
using System.Windows.Automation;
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

    public static void BlankWindowSwitchesToSoftwareUntilRestart()
    {
        const int width = 200, height = 120;
        uint[] flat = Enumerable.Repeat(0x00FF_FFFFu, width * height).ToArray();
        Program.True(DrawingCheck.IsBlank(flat, width, height), "A window that came out as one flat color is blank.");
        uint[] drawn = (uint[])flat.Clone();
        for (int y = 40; y < 43; y++)
            for (int x = 30; x < 33; x++) drawn[(y * width) + x] = 0x0020_2020;
        Program.True(!DrawingCheck.IsBlank(drawn, width, height), "A window with even one small glyph drawn is not blank.");
        Program.True(!DrawingCheck.IsBlank(new uint[40 * 40], 40, 40), "A capture too small to judge is never called blank.");
        Program.True(!DrawingCheck.IsBlank(flat, width, height + 10), "A capture shorter than its size is never called blank.");

        string file = Path.Combine(NewRoot(), "Settings", "software-drawing.txt");
        DateTime boot = new(2026, 11, 25, 18, 58, 40, DateTimeKind.Utc);
        Program.True(!DrawingCheck.BlankSeenThisBoot(file, boot), "With nothing remembered the graphics card is used.");
        DrawingCheck.RememberBlank(file, boot);
        Program.True(DrawingCheck.BlankSeenThisBoot(file, boot.AddSeconds(5)), "A blank window this boot means software drawing from the first frame.");
        Program.True(!DrawingCheck.BlankSeenThisBoot(file, boot.AddHours(6)), "After Windows restarts the graphics card is tried again.");
        File.WriteAllText(file, "not a time");
        Program.True(!DrawingCheck.BlankSeenThisBoot(file, boot), "An unreadable record is ignored.");
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
            (Destination.Taskbar, ["Look", "Details", "Behavior", "App buttons and clock", "Multiple displays"]),
            // Long pages are split by topic; a first group whose title would repeat the page's gets a topic of its own.
            (Destination.Start, ["Start menu", "Search"]),
            (Destination.Explorer, ["Files and folders", "Home", "Navigation pane", "Menus and windows"]),
            (Destination.Appearance, ["Mode and colors", "Desktop icons"]),
            (Destination.Multitasking, ["Snap and windows", "Keyboard, mouse and clipboard", "Gaming"]),
            (Destination.Customize, ["Data and ads", "Tips and suggestions"]),
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

            // Every page links to Windows Settings (File Explorer to System › Advanced, where its options live).
            string? uri = CustomizePage.SettingsUriFor(destination);
            Program.True(uri is not null && uri.StartsWith("ms-settings:", StringComparison.Ordinal) && page.SettingsLinkText.IsVisible,
                $"{destination}: the Windows Settings link is wrong ({uri}).");
            string linkName = System.Windows.Automation.AutomationProperties.GetName(page.SettingsLink);
            string linkText = string.Concat(page.SettingsLink.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
            Program.True(linkName == "More in Windows Settings" && linkText == linkName,
                $"{destination}: the Windows Settings link needs its name (name \"{linkName}\", text \"{linkText}\").");
        }
        Program.True(CustomizePage.SettingsUriFor(Destination.Taskbar) == "ms-settings:taskbar" && CustomizePage.SettingsUriFor(Destination.Start) == "ms-settings:personalization-start"
            && CustomizePage.SettingsUriFor(Destination.Appearance) == "ms-settings:personalization-colors" && CustomizePage.SettingsUriFor(Destination.Multitasking) == "ms-settings:multitasking"
            && CustomizePage.SettingsUriFor(Destination.Customize) == "ms-settings:privacy-general" && CustomizePage.SettingsUriFor(Destination.Explorer) == "ms-settings:developers",
            "A page links to the wrong Windows Settings page.");

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
        TaskbarStateLook Desktop() => shell.Services.Settings.Current.LookFor(TaskbarState.Desktop);
        TaskbarState[] order = [TaskbarState.Desktop, TaskbarState.VisibleWindow, TaskbarState.MaximizedWindow, TaskbarState.StartOpen,
            TaskbarState.SearchOpen, TaskbarState.TaskViewOpen, TaskbarState.BatterySaver];
        Program.True(TaskbarLookPanel.States.Select(s => s.State).SequenceEqual(order), "The state rows are out of order.");
        Program.True(panel.OpenState == TaskbarState.Desktop && panel.HeaderFor(TaskbarState.Desktop).IsChecked == true && panel.SwitchFor(TaskbarState.Desktop) is null
            && order.Skip(1).All(s => panel.SwitchFor(s) is { IsChecked: false } && panel.HeaderFor(s).IsChecked == false),
            "The Desktop editor starts open, with no switch; every other state starts off and closed.");
        Program.True(panel.ColorRow.Visibility == Visibility.Collapsed && panel.OpacityRow.Visibility == Visibility.Collapsed && panel.LineSwitch.IsVisible,
            "Windows default shows only the style and the line.");
        Program.True(panel.SummaryFor(TaskbarState.Desktop).Text == "Windows default" && panel.SummaryFor(TaskbarState.StartOpen).Text == "Off: the Desktop look is used",
            "Rows summarize their look.");

        panel.StyleList.SelectedIndex = 2; // Clear
        Pump();
        Program.True(Desktop().Kind == TaskbarLookKind.Clear, "Choosing Clear was not saved.");
        Program.True(panel.ColorRow.Visibility == Visibility.Visible && panel.OpacityRow.Visibility == Visibility.Visible, "Clear shows color and opacity.");

        Button blue = Descendants(panel).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Desktop color: Blue");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(blue).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
        Program.True(Desktop().Color == 0x000067C0 && !Desktop().UseAccent, "Choosing a swatch through UI Automation did not save the color.");
        Program.True(blue.BorderBrush != Brushes.Transparent, "The chosen swatch is not marked.");

        panel.StyleList.SelectedIndex = 4; // Acrylic starts with a visible tint
        Pump();
        Program.True(Desktop().Kind == TaskbarLookKind.Acrylic && Desktop().Color == 0x660067C0, "Acrylic did not start with a visible tint.");
        Program.True(panel.OpacityText.Text == "40%" && panel.SummaryFor(TaskbarState.Desktop).Text == "Acrylic, 40%", "The opacity or summary is wrong.");

        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(panel.AccentSwatch).GetPattern(PatternInterface.Invoke)).Invoke();
        panel.LineSwitch.IsChecked = false;
        Pump();
        Program.True(Desktop() is { UseAccent: true, Line: false, Color: 0x660067C0 } && panel.SummaryFor(TaskbarState.Desktop).Text == "Acrylic, accent color, 40%, no line",
            "The accent color or line choice was not saved: " + panel.SummaryFor(TaskbarState.Desktop).Text);
        Program.True(panel.AccentSwatch.BorderBrush != Brushes.Transparent && blue.BorderBrush == Brushes.Transparent, "The accent color is not the marked choice.");

        panel.StyleList.SelectedIndex = 1; // Opaque has a color but no opacity
        Pump();
        Program.True(panel.ColorRow.Visibility == Visibility.Visible && panel.OpacityRow.Visibility == Visibility.Collapsed, "Opaque shows color without opacity.");
        panel.StyleList.SelectedIndex = 3; // Blur has no color or opacity
        Pump();
        Program.True(panel.ColorRow.Visibility == Visibility.Collapsed && panel.OpacityRow.Visibility == Visibility.Collapsed, "Blur shows no color or opacity rows.");

        // Editing a look that is off turns it on, so the change shows on the taskbar.
        TaskbarStateLook Maximized() => shell.Services.Settings.Current.LookFor(TaskbarState.MaximizedWindow);
        Program.True(!Maximized().Enabled && panel.SummaryFor(TaskbarState.MaximizedWindow).Text == "Off: the Desktop look is used", "Window maximized starts off and says so.");
        panel.HeaderFor(TaskbarState.MaximizedWindow).IsChecked = true;
        Pump();
        panel.StyleList.SelectedIndex = 2; // Clear
        Pump();
        Program.True(Maximized() is { Enabled: true, Kind: TaskbarLookKind.Clear } && panel.SwitchFor(TaskbarState.MaximizedWindow)!.IsChecked == true
            && panel.SummaryFor(TaskbarState.MaximizedWindow).Text.StartsWith("Clear", StringComparison.Ordinal),
            "Changing the style of a look that is off must turn it on: " + Maximized());
        shell.Services.TryUpdateSettings(s => s.WithTaskbarState(TaskbarState.MaximizedWindow, _ => TaskbarStateLook.DefaultFor(TaskbarState.MaximizedWindow)));
        panel.HeaderFor(TaskbarState.Desktop).IsChecked = true;
        Pump();
        Program.True(shell.Services.Settings.Current.TaskbarStates.Where(p => p.Key != TaskbarState.Desktop).All(p => p.Value == TaskbarStateLook.DefaultFor(p.Key)),
            "Editing the Desktop look changed another state.");
        Program.True(First([.. shell.Search("translucent")]) is { Destination: Destination.Taskbar, Anchor: "look" }, "Search does not find the taskbar look.");
    }

    public static void TaskbarStatesSaveThroughAutomation()
    {
        using TestShell shell = new();
        shell.Window.Navigate(Destination.Taskbar);
        Pump();
        TaskbarLookPanel panel = Descendants(shell.Window.Page(Destination.Taskbar).View).OfType<TaskbarLookPanel>().Single();
        TaskbarStateLook Start() => shell.Services.Settings.Current.LookFor(TaskbarState.StartOpen);
        static AutomationPeer Peer(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element);

        // Turning a state on (UI Automation Toggle) saves it and opens its editor; only one editor is open.
        CheckBox start = panel.SwitchFor(TaskbarState.StartOpen)!;
        Program.True(System.Windows.Automation.AutomationProperties.GetName(start) == "Change the look when Start is open", "The Start open switch needs its name.");
        ((IToggleProvider)Peer(start).GetPattern(PatternInterface.Toggle)).Toggle();
        Pump();
        Program.True(Start().Enabled && panel.OpenState == TaskbarState.StartOpen, "Turning Start open on did not save it or open its editor.");
        Program.True(panel.HeaderFor(TaskbarState.StartOpen).IsChecked == true && panel.HeaderFor(TaskbarState.Desktop).IsChecked == false
            && Descendants(panel).OfType<ComboBox>().Count(c => c.IsVisible) == 1, "Only one editor may be open.");
        Program.True(System.Windows.Automation.AutomationProperties.GetName(panel.StyleList) == "Start open style", "The editor's controls must name the state they change.");

        // Style through UI Automation: open the list and select Acrylic.
        panel.StyleList.SelectedIndex = 0; // Windows default first, so choosing Acrylic is a change
        Pump();
        IExpandCollapseProvider list = (IExpandCollapseProvider)Peer(panel.StyleList).GetPattern(PatternInterface.ExpandCollapse);
        list.Expand();
        Pump();
        AutomationPeer acrylic = Peer(panel.StyleList).GetChildren().Single(c => c.GetName() == "Acrylic");
        ((ISelectionItemProvider)acrylic.GetPattern(PatternInterface.SelectionItem)).Select();
        list.Collapse();
        Pump();
        Program.True(Start().Kind == TaskbarLookKind.Acrylic, "Selecting Acrylic through UI Automation was not saved: " + Start());

        // Opacity through UI Automation RangeValue; it saves once the slider settles.
        ((IRangeValueProvider)Peer(panel.OpacitySlider).GetPattern(PatternInterface.RangeValue)).SetValue(60);
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (Start().Color >> 24 != 153 && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }
        Program.True(Start().Color >> 24 == 153 && panel.SummaryFor(TaskbarState.StartOpen).Text == "Acrylic, 60%",
            "Setting the opacity through UI Automation was not saved: " + panel.SummaryFor(TaskbarState.StartOpen).Text);

        // Row headers expand and collapse (UI Automation ExpandCollapse).
        IExpandCollapseProvider header = (IExpandCollapseProvider)Peer(panel.HeaderFor(TaskbarState.StartOpen)).GetPattern(PatternInterface.ExpandCollapse);
        header.Collapse();
        Pump();
        Program.True(panel.OpenState is null && header.ExpandCollapseState == ExpandCollapseState.Collapsed && !panel.StyleList.IsVisible, "Collapsing did not close the editor.");
        ((IExpandCollapseProvider)Peer(panel.HeaderFor(TaskbarState.BatterySaver)).GetPattern(PatternInterface.ExpandCollapse)).Expand();
        Pump();
        Program.True(panel.OpenState == TaskbarState.BatterySaver && System.Windows.Automation.AutomationProperties.GetName(panel.OpacitySlider) == "Battery saver opacity",
            "Expanding another row must move the editor there.");
        Program.True(!shell.Services.Settings.Current.LookFor(TaskbarState.BatterySaver).Enabled, "Opening a row must not turn its state on.");
        Program.True(shell.Services.Settings.Current.LookFor(TaskbarState.Desktop) == TaskbarStateLook.DefaultFor(TaskbarState.Desktop), "The Desktop look changed.");

        // Every state can be found by search, and revealing it focuses its row.
        Program.True(First([.. shell.Search("maximized")]) is { Destination: Destination.Taskbar, Anchor: "look.MaximizedWindow" }, "Search does not find the maximized state.");
        Program.True(First([.. shell.Search("battery saver")]) is { Anchor: "look.BatterySaver" }, "Search does not find the battery saver state.");
        Program.True(shell.Search("start menu").Any(e => e.Anchor == "look.StartOpen"), "Search does not find the Start open state.");
        foreach (TaskbarLookPanel.StateInfo state in TaskbarLookPanel.States)
            Program.True(shell.Search(state.SearchTitle).FirstOrDefault()?.Anchor == TaskbarLookPanel.AnchorFor(state.State), "Search does not find " + state.SearchTitle + ".");
        shell.Window.Navigate(Destination.Taskbar, "look.TaskViewOpen");
        Pump();
        ToggleButton taskView = panel.HeaderFor(TaskbarState.TaskViewOpen);
        Program.True(FocusManager.GetFocusedElement(shell.Window) == taskView || taskView.IsKeyboardFocusWithin, "Revealing a state must focus its row.");
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
        AppSettings app = new AppSettings { DesktopWidgets = [new(DesktopWidgetKind.Clock, 1, 2)] }
            .WithTaskbarState(TaskbarState.Desktop, l => l with { Kind = TaskbarLookKind.Clear })
            .WithTaskbarState(TaskbarState.StartOpen, l => l with { Enabled = true, UseAccent = true });
        SettingsProfile profile = SettingsProfile.Capture(app, sourceEngine, system);
        Program.True(profile.WindowsChoices.Count == 2 && profile.WindowsChoices["taskbar.search"] == 2, "Capture missed the Windows options.");
        string file = Path.Combine(root, "setup.json");
        profile.Save(file);
        SettingsProfile loaded = SettingsProfile.Load(file);
        Program.True(loaded.App.LookFor(TaskbarState.Desktop).Kind == TaskbarLookKind.Clear && loaded.App.LookFor(TaskbarState.StartOpen) is { Enabled: true, UseAccent: true }
            && loaded.App.DesktopWidgets.Count == 1, "App preferences did not survive export.");
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

    // ---------- customization reliability ----------

    /// <summary>Settings that failed to load are copied before any automatic save, shown as a caution and never treated as a first run.</summary>
    public static void UnreadableSettingsAreKeptAndShown()
    {
        string settingsPath = "";
        string CopyOf(string path) => Path.ChangeExtension(path, null) + "." + ShellServices.UnreadableCopySuffix + ".json";
        void Corrupt(string root)
        {
            settingsPath = AppSettingsStore.DefaultPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, "{ not json");
        }

        using (TestShell shell = new(prepareRoot: Corrupt))
        {
            ShellWindow window = shell.Window;
            string copy = CopyOf(settingsPath);
            Program.True(shell.Services.SettingsProblem is { CopyPath: { } kept } && kept == copy && File.ReadAllText(copy) == "{ not json",
                "The settings file that failed to load must be copied before anything can save: " + shell.Services.SettingsProblem);
            Program.True(window.NoticeBar.IsVisible && window.NoticeBar.Tag as string == nameof(NoticeSeverity.Caution)
                && window.NoticeText.Text.Contains(copy, StringComparison.Ordinal) && window.NoticeAction.IsVisible,
                "The problem must be shown as a caution that names the copy: " + window.NoticeText.Text);
            Program.True(shell.Services.Notices.Count(n => n.Area == "Settings" && n.Message.Contains(copy, StringComparison.Ordinal)) == 1,
                "The problem must be recorded once in Recent events.");
            window.ShowWelcomeOnFirstRun();
            Program.True(window.NoticeText.Text.Contains(copy, StringComparison.Ordinal) && !shell.Services.Settings.Current.WelcomeShown
                && File.ReadAllText(settingsPath) == "{ not json", "Settings that failed to load must not get the first-run welcome, nor be saved over by it.");
            Program.True(shell.Services.TryUpdateSettingsAutomatically(s => s with { CloseToTrayNoticeShown = true }) && File.ReadAllText(copy) == "{ not json",
                "With the copy kept, automatic saves may replace the file; the copy stays as it was.");
        }

        // The copy cannot be written (a folder stands in its place): nothing is saved automatically until the user changes something.
        using TestShell blocked = new(prepareRoot: root =>
        {
            Corrupt(root);
            Directory.CreateDirectory(CopyOf(settingsPath));
        });
        Program.True(blocked.Services.SettingsProblem is { CopyPath: null } && blocked.Window.NoticeText.Text.Contains("could not be kept", StringComparison.Ordinal),
            "A copy that could not be kept must be said: " + blocked.Window.NoticeText.Text);
        Program.True(!blocked.Services.TryUpdateSettingsAutomatically(s => s with { ReportedDrift = "taskbar.widgets" }) && File.ReadAllText(settingsPath) == "{ not json",
            "Without a copy, automatic saves must not replace settings that failed to load.");
        blocked.Services.Settings.Update(s => s with { ConfirmQuitWhileAudioRuns = false });
        Program.True(blocked.Services.TryUpdateSettingsAutomatically(s => s with { CloseToTrayNoticeShown = true })
            && blocked.Services.Settings.Current is { ConfirmQuitWhileAudioRuns: false, CloseToTrayNoticeShown: true },
            "After the user's own change the file is theirs, and automatic saves work again.");
    }

    /// <summary>Notices and search status are live regions: Narrator reads them when they appear, once each.</summary>
    public static void NoticesAndStatusAreAnnounced()
    {
        List<UIElement> announced = [];
        void Record(UIElement element) => announced.Add(element);
        Soltex.App.Controls.LiveRegion.Announced += Record;
        try
        {
            using TestShell shell = new();
            ShellWindow window = shell.Window;
            int Count(UIElement element) => announced.Count(e => ReferenceEquals(e, element));
            string SpokenName(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();
            Pump();
            announced.Clear();

            window.ShowNotice(NoticeSeverity.Information, "Saved.", null, null);
            Pump();
            Program.True(Count(window.NoticeText) == 1, $"A notice must be read once (read {Count(window.NoticeText)} times).");
            Program.True(AutomationProperties.GetLiveSetting(window.NoticeText) == AutomationLiveSetting.Polite && SpokenName(window.NoticeText) == "Saved.",
                "A notice is a polite live region named by its text.");
            window.ShowNotice(NoticeSeverity.Information, "Saved.", null, null);
            Pump();
            Program.True(Count(window.NoticeText) == 2, "The same notice shown again must be read again.");
            window.ShowNotice(NoticeSeverity.Caution, "Not saved.", null, null);
            Pump();
            Program.True(SpokenName(window.NoticeText) == "Warning: Not saved.", "A caution must say it is a warning: " + SpokenName(window.NoticeText));
            window.ShowNotice(NoticeSeverity.Critical, "Audio stopped.", null, null);
            Pump();
            Program.True(AutomationProperties.GetLiveSetting(window.NoticeText) == AutomationLiveSetting.Assertive && SpokenName(window.NoticeText) == "Problem: Audio stopped.",
                "A critical notice must be read at once and say it is a problem.");

            // Nothing is read from a hidden notice bar.
            Click(window.NoticeClose);
            int before = Count(window.NoticeText);
            Soltex.App.Controls.LiveRegion.Notify(window.NoticeText);
            Pump();
            Program.True(Count(window.NoticeText) == before, "A hidden notice must not be read.");

            // Search status: "No matches" is read; typing on with still nothing found is not read again.
            window.OpenSearchForCapture("zzzz");
            Pump();
            Program.True(Count(window.SearchHint) == 1 && window.SearchHint.Text.StartsWith("No matches", StringComparison.Ordinal), "No matches must be read.");
            window.SearchBox.Text = "zzzzz";
            Pump();
            Program.True(Count(window.SearchHint) == 1, "A status that did not change must not be read again.");

            // Turning the helper off stops it and removes the live setting.
            Soltex.App.Controls.LiveRegion.SetIsLive(window.SearchHint, false);
            window.SearchBox.Text = "";
            Pump();
            Program.True(Count(window.SearchHint) == 1 && AutomationProperties.GetLiveSetting(window.SearchHint) == AutomationLiveSetting.Off,
                "A text that is no longer live must not be read.");
            window.SearchOverlay.Visibility = Visibility.Collapsed;
        }
        finally { Soltex.App.Controls.LiveRegion.Announced -= Record; }
    }

    /// <summary>In high contrast the selected rail item draws its label, icon and pill in HighlightText on the Highlight fill.</summary>
    public static void HighContrastSelectionUsesHighlightText()
    {
        using TestShell shell = new();
        ShellWindow window = shell.Window;
        window.Navigate(Destination.Tools);
        Pump();
        List<string> problems = [];
        void Inspect(string mode, Color fillColor, Color textColor, Color pillColor)
        {
            foreach (ListBox nav in new[] { window.PrimaryNav, window.FooterNav })
            {
                if (nav.SelectedItem is null) continue;
                ListBoxItem item = (ListBoxItem)nav.ItemContainerGenerator.ContainerFromItem(nav.SelectedItem);
                Border fill = (Border)item.Template.FindName("Fill", item), pill = (Border)item.Template.FindName("Pill", item);
                TextBlock[] texts = [.. Descendants(item).OfType<TextBlock>()];
                if (ColorOf(fill.Background) != fillColor) problems.Add($"{mode}: the selection fill is {ColorOf(fill.Background)}, expected {fillColor}");
                if (texts.Length == 0 || texts.Any(t => ColorOf(t.Foreground) != textColor))
                    problems.Add($"{mode}: label or icon is {string.Join(", ", texts.Select(t => ColorOf(t.Foreground).ToString(System.Globalization.CultureInfo.InvariantCulture)))}, expected {textColor}");
                if (ColorOf(pill.Background) != pillColor) problems.Add($"{mode}: the pill is {ColorOf(pill.Background)}, expected {pillColor}");
            }
        }
        Inspect("palette", ColorOf(window.FindResource("Studio.SelectedBrush")), ColorOf(window.FindResource("Studio.SelectedTextBrush")),
            ColorOf(window.FindResource("Studio.SelectedPillBrush")));

        // The window takes the high-contrast palette the way ThemeService applies it when Windows is in high contrast.
        ResourceDictionary contrast = new() { Source = new Uri("/Soltex;component/Themes/Studio.HighContrast.xaml", UriKind.Relative) };
        window.Resources.MergedDictionaries.Add(contrast);
        try
        {
            Pump();
            Inspect("high contrast", SystemColors.HighlightColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor);
            // The icon rail (narrow window, large text) follows the same rule.
            ThemeService.ApplyScale(window.ScaleRoot, new AppSettings { ScalePercent = 200 });
            RenderSmokeCapture.ConfigureWindow(window, 760, 860);
            window.UpdateLayout();
            Pump();
            Program.True(window.IsRailCompact, "Setup: the icon rail did not appear.");
            Inspect("high contrast icon rail", SystemColors.HighlightColor, SystemColors.HighlightTextColor, SystemColors.HighlightTextColor);
        }
        finally { window.Resources.MergedDictionaries.Remove(contrast); }
        Program.True(problems.Count == 0, "Selected rail items: " + string.Join("; ", problems));
    }

    /// <summary>Search keeps Tab inside, highlights like the rail with room for the scroll bar, and a page result focuses the page.</summary>
    public static void SearchCyclesFocusAndHighlightsLikeTheRail()
    {
        using TestShell shell = new();
        ShellWindow window = shell.Window;
        Program.True(KeyboardNavigation.GetTabNavigation(window.SearchCard) == KeyboardNavigationMode.Cycle
            && KeyboardNavigation.GetControlTabNavigation(window.SearchCard) == KeyboardNavigationMode.Cycle,
            "Tab and Ctrl+Tab must cycle inside the search overlay.");

        window.OpenSearchForCapture("e");
        window.UpdateLayout();
        Pump();
        Program.True(window.SearchResults.Items.Count > 0 && window.SearchResults.SelectedIndex == 0, "Setup: search found nothing.");
        ListBoxItem first = (ListBoxItem)window.SearchResults.ItemContainerGenerator.ContainerFromIndex(0);
        Border firstFill = (Border)first.Template.FindName("Fill", first);
        Program.True(ReferenceEquals(first.Style, window.FindResource("Shell.SearchResultItem"))
            && ColorOf(firstFill.Background) == ColorOf(window.FindResource("Studio.SelectedBrush")),
            "The selected result must use the rail's violet selection, not a plain grey.");
        ScrollBar? bar = Descendants(window.SearchResults).OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        foreach (ListBoxItem item in Descendants(window.SearchResults).OfType<ListBoxItem>().Where(i => i.IsVisible))
        {
            Border fill = (Border)item.Template.FindName("Fill", item);
            Rect row = fill.TransformToAncestor(window).TransformBounds(new Rect(fill.RenderSize));
            Program.True(item.Margin.Right >= 12, "A result row leaves no lane for the scroll bar.");
            if (bar is not null)
            {
                Rect lane = bar.TransformToAncestor(window).TransformBounds(new Rect(bar.RenderSize));
                Program.True(row.Right <= lane.Left + 0.5, $"A result row ({row.Right:0}) runs under the scroll bar ({lane.Left:0}).");
            }
        }

        // A page result (no single setting to focus) lands on the page's first control or its rail item, never nowhere.
        window.SearchBox.Text = "Tools";
        Pump();
        SearchEntry page = window.SearchResults.Items.Cast<SearchEntry>().First(e => e.Destination == Destination.Tools && e.Anchor == "top");
        window.SearchResults.SelectedItem = page;
        window.OpenSelectedResult();
        Pump();
        DependencyObject? focused = FocusManager.GetFocusedElement(window) as DependencyObject;
        bool inPage = focused is not null && IsWithin(focused, window.Page(Destination.Tools).View);
        bool onRail = focused is ListBoxItem { DataContext: NavItem { Destination: Destination.Tools } };
        Program.True(!window.SearchOverlay.IsVisible && (inPage || onRail), "Opening a page result must focus the page: focus is on " + focused);
    }

    private static bool IsWithin(DependencyObject node, DependencyObject root)
    {
        for (DependencyObject? at = node; at is not null; at = (at is Visual ? VisualTreeHelper.GetParent(at) : null) ?? LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, root)) return true;
        return false;
    }

    /// <summary>Check boxes, radio buttons, slider thumbs and progress bars use the Studio violet, not the Windows accent.</summary>
    public static void FluentControlsUseTheStudioAccent()
    {
        Application app = Application.Current;
        string[] keys = ["CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundStrokeChecked", "RadioButtonOuterEllipseCheckedFill",
            "SliderThumbBackground", "ProgressBarForeground", "ComboBoxItemPillFillBrush"];
        try
        {
            foreach (ThemePreference theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                ThemeService.Apply(app, new AppSettings { Theme = theme });
                Color accent = ColorOf(app.TryFindResource("Studio.AccentBrush"));
                foreach (string key in keys)
                    Program.True(ColorOf(app.TryFindResource(key)) == accent, $"{theme}: {key} is {ColorOf(app.TryFindResource(key))}, not the Studio accent {accent}.");
                // Drawn for real: a checked box, a slider and a progress bar on the page background show violet.
                StackPanel panel = new() { Width = 240, Background = (Brush)app.TryFindResource("SolidBackgroundFillColorBaseBrush") };
                panel.Children.Add(new CheckBox { IsChecked = true, Content = "On" });
                panel.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 200, HorizontalAlignment = HorizontalAlignment.Left });
                panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = 60, Height = 4, Width = 200, HorizontalAlignment = HorizontalAlignment.Left });
                panel.Measure(new Size(240, 140));
                panel.Arrange(new Rect(0, 0, 240, 140));
                panel.UpdateLayout();
                RenderTargetBitmap bitmap = new(240, 140, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                byte[] pixels = new byte[240 * 140 * 4];
                bitmap.CopyPixels(pixels, 240 * 4, 0);
                int violet = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (Math.Abs(pixels[i + 2] - accent.R) + Math.Abs(pixels[i + 1] - accent.G) + Math.Abs(pixels[i] - accent.B) < 40) violet++;
                Program.True(violet > 120, $"{theme}: the check box, slider and progress bar drew {violet} accent pixels.");
            }
        }
        finally { ThemeService.Apply(app, new AppSettings()); }
    }

    /// <summary>Text colours reach 4.5:1: tertiary and caption text, light-theme channel names, and text on a selected row.</summary>
    public static void TextColorsMeetContrast()
    {
        Application app = Application.Current;
        List<string> problems = [];
        try
        {
            foreach (ThemePreference theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                ThemeService.Apply(app, new AppSettings { Theme = theme });
                Color Of(string key) => ColorOf(app.TryFindResource(key));
                void Check(string what, Color foreground, Color background)
                {
                    double ratio = Contrast(foreground, background);
                    if (ratio < 4.5) problems.Add($"{theme} {what}: {ratio:F2}:1");
                }
                (string Name, Color Color)[] surfaces = [("card", Of("CardBackgroundFillColorDefaultBrush")), ("base", Of("SolidBackgroundFillColorBaseBrush")),
                    ("rail", Of("Studio.RailBrush")), ("raised", Of("Studio.RaisedBrush"))];
                TextBlock eyebrow = new() { Style = (Style)app.FindResource("Studio.Eyebrow"), Text = "CAPTION" };
                foreach ((string name, Color surface) in surfaces)
                {
                    foreach (string key in new[] { "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "Studio.TextTertiaryBrush" })
                        Check(key + " on " + name, Of(key), surface);
                    Check("caption (Studio.Eyebrow) on " + name, ColorOf(eyebrow.Foreground), surface);
                }
                foreach (string channel in new[] { "Microphone", "Game", "Chat", "Media", "Aux", "Master" })
                    foreach ((string name, Color surface) in surfaces.Take(2))
                        Check($"{channel} channel name on {name}", Of("Studio.Channel." + channel), surface);
                // The selected search result sits on the search card (the base colour) under the violet tint.
                Color selected = Over(Of("Studio.SelectedBrush"), Of("SolidBackgroundFillColorBaseBrush"));
                Check("selected result title", Of("Studio.SelectedTextBrush"), selected);
                Check("selected result location", Of("Studio.SelectedSecondaryTextBrush"), selected);
                Color listSelected = Over(Of("ListBoxItemSelectedBackgroundThemeBrush"), Of("CardBackgroundFillColorDefaultBrush"));
                Check("selected list row", Of("ListBoxItemSelectedForegroundThemeBrush"), listSelected);
            }
        }
        finally { ThemeService.Apply(app, new AppSettings()); }
        Program.True(problems.Count == 0, "Contrast below 4.5:1: " + string.Join("; ", problems));
    }

    /// <summary>A switch that is on but unavailable still reads as on: muted violet track and a solid, readable knob.</summary>
    public static void DisabledSwitchStillReadsOn()
    {
        Application app = Application.Current;
        try
        {
            foreach (ThemePreference theme in new[] { ThemePreference.Dark, ThemePreference.Light })
            {
                ThemeService.Apply(app, new AppSettings { Theme = theme });
                Style style = (Style)app.FindResource("Studio.Switch");
                CheckBox on = new() { Style = style, IsChecked = true, IsEnabled = false, Content = "On" };
                CheckBox off = new() { Style = style, IsChecked = false, IsEnabled = false, Content = "Off" };
                foreach (CheckBox box in new[] { on, off })
                {
                    box.Measure(new Size(200, 40));
                    box.Arrange(new Rect(0, 0, 200, 40));
                }
                FrameworkElement Part(CheckBox box, string name) => (FrameworkElement)box.Template.FindName(name, box);
                Color track = ColorOf(((Border)Part(on, "Track")).Background), knob = ColorOf(((Border)Part(on, "Knob")).Background);
                Color card = ColorOf(app.TryFindResource("CardBackgroundFillColorDefaultBrush"));
                Program.True(on.Opacity == 1 && Part(on, "SwitchBox").Opacity == 1, $"{theme}: an unavailable switch that is on must not fade into grey.");
                Program.True(track == ColorOf(app.TryFindResource("Studio.SwitchOnDisabledBrush")) && Contrast(knob, track) >= 3,
                    $"{theme}: the knob ({knob}) must stand out on the muted track ({track}): {Contrast(knob, track):F2}:1.");
                Program.True(Math.Max(Contrast(track, card), Contrast(knob, card)) >= 3, $"{theme}: an unavailable switch that is on must still show on the card.");
                Program.True(Part(off, "SwitchBox").Opacity < 1 && Part(off, "Label").Opacity < 1, $"{theme}: an unavailable switch that is off fades.");
            }
        }
        finally { ThemeService.Apply(app, new AppSettings()); }
    }

    /// <summary>Put back stays on offer until answered: a newer notice covers it, closing that brings it back, closing it remembers.</summary>
    public static void DriftOfferStaysUntilAnswered()
    {
        MemoryRegistry registry = new();
        using TestShell shell = new(calmFactory: journal => new ChangeEngine(registry, new ChangeJournal(journal), 26300));
        ShellWindow window = shell.Window;
        SettingDefinition widgets = CalmCatalog.All.Single(d => d.Id == "taskbar.widgets");
        RegistryTarget target = widgets.Targets[0];
        Program.True(shell.Services.Calm.ApplyChoice(widgets, 0).Outcome == ChangeOutcome.Applied, "Setup: the change did not apply.");
        RegistryValue set = registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name);
        registry.Write(target.Path, target.Name, set == RegistryValue.Dword(1) ? RegistryValue.Dword(0) : RegistryValue.Dword(1)); // Windows changed it back

        DriftReport found = Wait(shell.Services.Drift.CheckAsync()) ?? throw new InvalidOperationException("The drift check failed.");
        Pump();
        Program.True(found is { Count: 1 } && window.PendingDrift is not null && window.NoticeText.Text == found.Summary
            && window.NoticeAction.IsVisible && (string)window.NoticeAction.Content == "Put back", "The difference must be offered with Put back.");
        Program.True(shell.Services.Settings.Current.ReportedDrift is null, "An offer nobody answered must not be remembered yet.");

        window.ShowNotice(NoticeSeverity.Information, "Something else happened.", null, null);
        Program.True(window.NoticeText.Text == "Something else happened.", "Setup: the newer notice did not show.");
        Click(window.NoticeClose);
        Pump();
        Program.True(window.NoticeBar.IsVisible && window.NoticeText.Text == found.Summary && window.NoticeAction.IsVisible,
            "Closing the newer notice must bring the Put back offer back.");
        Program.True(shell.Services.Notices.Count(n => n.Message == found.Summary) == 1, "Bringing the offer back must not add it to Recent events again.");

        // Unanswered, it is offered again by the next check (the twice-a-day check or the next start).
        window.ShowNotice(NoticeSeverity.Information, "Covering the offer again.", null, null);
        Wait(shell.Services.Drift.CheckAsync());
        Pump();
        Program.True(window.NoticeText.Text == found.Summary && window.PendingDrift is not null && shell.Services.Settings.Current.ReportedDrift is null,
            "The next check must offer an unanswered difference again.");

        Click(window.NoticeClose); // the answer: leave it as it is
        Pump();
        Program.True(window.PendingDrift is null && !window.NoticeBar.IsVisible
            && AppSettings.NormalizeIdList(shell.Services.Settings.Current.ReportedDrift) == AppSettings.NormalizeIdList(widgets.Id),
            "Closing the offer itself must remember the answer.");
        Wait(shell.Services.Drift.CheckAsync());
        Pump();
        Program.True(!window.NoticeBar.IsVisible && window.PendingDrift is null, "An answered difference must not be offered again.");
    }

    /// <summary>Tray "Open audio" (and --audio) shows Audio selected in the rail even when Audio is hidden in Settings.</summary>
    public static void HiddenPageOpensSelected()
    {
        bool preview = AudioPreview.Enabled, sonar = AudioPage.SonarInstalled;
        AudioPreview.Enabled = true;
        AudioPage.SonarInstalled = true;
        try
        {
            using TestShell shell = new(s => s with { HiddenDestinations = [Destination.Audio] });
            ShellWindow window = shell.Window;
            ListBox nav = window.PrimaryNav;
            Program.True(nav.Items.Cast<NavItem>().All(i => i.Destination != Destination.Audio), "Setup: Audio is not hidden.");
            window.Navigate(Destination.Audio); // what OpenAudio does after showing the window
            Pump();
            Program.True(nav.SelectedItem is NavItem { Destination: Destination.Audio } && window.Title.StartsWith("Audio", StringComparison.Ordinal),
                "A hidden page that is opened must show selected in the rail.");
            ListBoxItem? row = nav.ItemContainerGenerator.ContainerFromItem(nav.SelectedItem) as ListBoxItem;
            Program.True(row is { IsVisible: true, IsSelected: true }, "The opened hidden page has no visible, selected rail item.");
            shell.Services.Settings.Update(s => s with { StartPage = Destination.Tools });
            Pump();
            Program.True(nav.SelectedItem is NavItem { Destination: Destination.Audio }, "An unrelated settings change must not close the open hidden page.");
            window.Navigate(Destination.MyPc);
            Pump();
            Program.True(nav.Items.Cast<NavItem>().All(i => i.Destination != Destination.Audio) && nav.SelectedItem is NavItem { Destination: Destination.MyPc },
                "Once left, the hidden page leaves the rail again.");
        }
        finally
        {
            AudioPreview.Enabled = preview;
            AudioPage.SonarInstalled = sonar;
        }
    }

    // ---------- helpers ----------
    private sealed class TestShell : IDisposable
    {
        public TestShell(Func<AppSettings, AppSettings>? configure = null, Func<string, ChangeEngine>? calmFactory = null, Action<string>? prepareRoot = null)
        {
            string root = NewRoot();
            prepareRoot?.Invoke(root);
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

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static T Wait<T>(Task<T> task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(5);
        }
        Program.True(task.IsCompleted, "A background task did not finish.");
        return task.GetAwaiter().GetResult();
    }

    private static Color ColorOf(object? brush) => brush is SolidColorBrush { Color: var c } ? c : throw new InvalidOperationException("Not a solid brush: " + brush);

    /// <summary>WCAG contrast of two colours; a translucent foreground is laid over the background first.</summary>
    internal static double Contrast(Color foreground, Color background)
    {
        Color over = Over(foreground, background);
        double a = RelativeLuminance(over), b = RelativeLuminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    internal static Color Over(Color top, Color bottom)
    {
        double alpha = top.A / 255d;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * alpha + u * (1 - alpha));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255d;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

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
