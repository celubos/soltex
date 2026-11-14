using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// One settings area (Taskbar, Start, File Explorer, Appearance, Multitasking, Privacy): a title and plain rows,
/// each with a switch or a list of options. Every change goes through the change engine (journal, read-back, undo).
/// </summary>
public partial class CustomizePage : UserControl, IShellPage
{
    private sealed record Area(string Title, SettingGroup[] Groups);

    private static readonly Dictionary<Destination, Area> Areas = new()
    {
        [Destination.Taskbar] = new("Taskbar", [SettingGroup.Taskbar]),
        [Destination.Start] = new("Start", [SettingGroup.StartAndSearch]),
        [Destination.Explorer] = new("File Explorer", [SettingGroup.FileExplorer]),
        [Destination.Appearance] = new("Appearance", [SettingGroup.Appearance]),
        [Destination.Multitasking] = new("Multitasking", [SettingGroup.Multitasking]),
        [Destination.Customize] = new("Privacy", [SettingGroup.Privacy, SettingGroup.TipsAndSuggestions]),
    };

    public static bool Handles(Destination destination) => Areas.ContainsKey(destination);

    internal static IEnumerable<SearchEntry> EntriesFor(Destination destination)
    {
        Area area = Areas[destination];
        yield return new SearchEntry(area.Title, area.Title, destination, "top", area.Title.ToLowerInvariant());
        if (destination == Destination.Taskbar)
        {
            yield return new SearchEntry("Taskbar look", area.Title, destination, "look",
                ["Taskbar", "transparent", "translucent", "TranslucentTB", "clear", "acrylic", "blur", "solid", "color", "colour", "tint", "maximized", "line"]);
            foreach (TaskbarTweak tweak in TaskbarTweaks.All)
                yield return new SearchEntry(tweak.Title, area.Title, destination, "tweak." + tweak.Id,
                    ["Taskbar", "Windhawk", .. tweak.Options.Select(o => o.Label)]);
            yield return new SearchEntry("Scroll over the taskbar to change volume", area.Title, destination, "scrollvolume",
                ["Taskbar", "Windhawk", "volume", "mouse wheel", "sound"]);
        }
        foreach (SearchEntry extra in Personalize.ExtrasPanel.EntriesFor(destination)) yield return extra;
        foreach (SettingDefinition d in Definitions(area))
            yield return new SearchEntry(d.Title, area.Title, destination, d.Id,
                [area.Title, .. d.Options.Select(o => o.Label), .. d.CalmEffect.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 4)]);
    }

    private static IEnumerable<SettingDefinition> Definitions(Area area) =>
        area.Groups.SelectMany(g => CalmCatalog.All.Where(d => d.Group == g));

    private sealed record Row(SettingDefinition Definition, Control Control, TextBlock Note, Button Undo);

    private readonly ShellContext context;
    private readonly Destination destination;
    private readonly Area area;
    private readonly PageAnchors anchors = new();
    /// <summary>Rows the automatic dark mode schedule sets by time of day while it is on.</summary>
    private static readonly string[] ScheduledIds = ["appearance.apps", "appearance.windows"];

    private readonly Dictionary<string, Row> rows = [];
    private bool loading, busy, explorerRestartPending, darkModeScheduled;

    internal CustomizePage(ShellContext context, Destination destination = Destination.Customize)
    {
        this.context = context;
        this.destination = destination;
        area = Areas[destination];
        InitializeComponent();
        PageTitleText.Text = area.Title;
        anchors.Add("top", PageTitleText);
        BuildRows();
        context.Services.CalmChanged += () => Dispatcher.InvokeAsync(Refresh);
        if (destination == Destination.Appearance)
        {
            darkModeScheduled = context.Services.Settings.Current.DarkModeSchedule;
            context.Services.Settings.Changed += settings => Dispatcher.InvokeAsync(() =>
            {
                if (settings.DarkModeSchedule == darkModeScheduled) return;
                darkModeScheduled = settings.DarkModeSchedule;
                Refresh();
            });
        }
    }

    public Destination Destination => destination;
    public string Title => area.Title;
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => EntriesFor(destination);
    public bool Reveal(string anchor) => anchors.Reveal(anchor);
    public void OnShown()
    {
        Refresh();
        look?.Refresh();
        details?.Refresh();
        extras?.Refresh();
    }
    public void OnHidden() { }

    private ChangeEngine Engine => context.Services.Calm;

    private TaskbarLookPanel? look;
    private TaskbarDetailsPanel? details;
    private Personalize.ExtrasPanel? extras;

    private void BuildRows()
    {
        if (destination == Destination.Taskbar)
        {
            Groups.Children.Add(SectionTitle("Look", "Inspired by TranslucentTB"));
            look = new TaskbarLookPanel();
            look.Attach(context);
            Groups.Children.Add(look);
            anchors.Add("look", look.LookList);
            Groups.Children.Add(SectionTitle("Details", "Inspired by Windhawk mods"));
            details = new TaskbarDetailsPanel();
            details.Attach(context);
            Groups.Children.Add(details);
            foreach (TaskbarTweak tweak in TaskbarTweaks.All) anchors.Add("tweak." + tweak.Id, details.ControlFor(tweak.Id));
            anchors.Add("scrollvolume", details.ScrollVolumeSwitch);
            Groups.Children.Add(SectionTitle("Behavior", null));
        }
        if (destination is Destination.Start or Destination.Appearance)
        {
            extras = new Personalize.ExtrasPanel(context, destination);
            foreach ((string anchor, FrameworkElement target) in extras.Anchors) anchors.Add(anchor, target);
        }
        foreach (SettingGroup group in area.Groups)
        {
            // A group heading that repeats the page title (Privacy on the Privacy page) is left out.
            string heading = CalmCatalog.GroupTitle(group);
            if (area.Groups.Length > 1 && !string.Equals(heading, area.Title, StringComparison.Ordinal))
                Groups.Children.Add(SectionTitle(heading, null));
            foreach (SettingDefinition d in CalmCatalog.All.Where(x => x.Group == group))
                Groups.Children.Add(BuildRow(d));
        }
        if (extras is not null) Groups.Children.Add(extras);
    }

    private StackPanel SectionTitle(string title, string? credit)
    {
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, Groups.Children.Count == 0 ? 0 : 18, 0, 8) };
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.CardTitle"), VerticalAlignment = VerticalAlignment.Bottom });
        if (credit is not null)
            heading.Children.Add(new TextBlock { Text = credit, Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom });
        return heading;
    }

    private Border BuildRow(SettingDefinition d)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = d.Title, Style = (Style)FindResource("Shell.Body") });
        TextBlock note = new() { Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(note);
        grid.Children.Add(text);

        Button undo = new() { Content = "Undo", Tag = d, Style = (Style)FindResource("Studio.GhostButton"), Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        AutomationProperties.SetName(undo, "Undo: " + d.Title);
        undo.Click += UndoRow_Click;
        Grid.SetColumn(undo, 1);
        grid.Children.Add(undo);

        Control control;
        if (d.Options.Count == 2 && (d.IsSwitch || d.Choices is null))
        {
            // Switch: still a CheckBox (Toggle pattern); Checked/Unchecked so mouse, keyboard and UI Automation all apply.
            CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), Tag = d, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            toggle.Checked += Toggle_Changed;
            toggle.Unchecked += Toggle_Changed;
            control = toggle;
        }
        else
        {
            ComboBox list = new() { Tag = d, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
                ItemsSource = d.Options.Select(o => o.Label).ToArray() };
            list.SelectionChanged += Choice_Changed;
            control = list;
        }
        AutomationProperties.SetName(control, d.Title);
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);

        Border row = new() { Style = (Style)FindResource("Studio.Row"), Child = grid };
        rows[d.Id] = new Row(d, control, note, undo);
        anchors.Add(d.Id, control);
        return row;
    }

    private void Refresh()
    {
        loading = true;
        try
        {
            foreach (SettingState state in Engine.InspectAll(rows.Values.Select(r => r.Definition)))
            {
                Row row = rows[state.Definition.Id];
                switch (row.Control)
                {
                    case CheckBox toggle: toggle.IsChecked = state.Choice == 1; break;
                    case ComboBox list: list.SelectedIndex = state.Choice; break;
                }
                // While automatic dark mode is on it rewrites these values by time of day, so a choice here would not last.
                bool scheduled = darkModeScheduled && ScheduledIds.Contains(state.Definition.Id, StringComparer.Ordinal);
                row.Control.IsEnabled = state.NotApplicable is null && !busy && !scheduled;
                List<string> notes = [.. state.PolicyNotes];
                if (state.NotApplicable is { } reason) notes.Insert(0, reason);
                if (scheduled) notes.Insert(0, "Set by automatic dark mode.");
                else if (state.Choice < 0 && row.Control is ComboBox) notes.Add("Set to a value not listed here.");
                row.Note.Text = string.Join(" ", notes);
                row.Note.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                row.Undo.Visibility = state.CanUndo ? Visibility.Visible : Visibility.Collapsed;
                row.Undo.IsEnabled = !busy && !scheduled;
            }
            RestartBar.Visibility = explorerRestartPending ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { loading = false; }
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => ToggleAsync(sender));

    private async Task ToggleAsync(object sender)
    {
        if (loading || busy || sender is not CheckBox { Tag: SettingDefinition definition } toggle) return;
        await ApplyAsync(definition, toggle.IsChecked == true ? 1 : 0);
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) => Dispatcher.Handle(() => ChoiceAsync(sender));

    private async Task ChoiceAsync(object sender)
    {
        if (loading || busy || sender is not ComboBox { Tag: SettingDefinition definition, SelectedIndex: >= 0 } list) return;
        await ApplyAsync(definition, list.SelectedIndex);
    }

    private async Task ApplyAsync(SettingDefinition definition, int choice)
    {
        ChangeResult result = await RunAsync(() => Engine.ApplyChoice(definition, choice));
        if (result.Outcome == ChangeOutcome.Applied) NoteRestart([definition]);
        else if (result.Outcome is not ChangeOutcome.AlreadyInPlace) context.ShowNotice(NoticeSeverity.Caution, definition.Title + ": " + result.Message);
    }

    private void UndoRow_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => UndoRowAsync(sender));

    private async Task UndoRowAsync(object sender)
    {
        if (sender is not Button { Tag: SettingDefinition definition }) return;
        UndoResult result = await RunAsync(() => Engine.Undo(definition));
        if (result.Outcome == UndoOutcome.Restored) NoteRestart([definition]);
        else context.ShowNotice(result.Outcome == UndoOutcome.Failed ? NoticeSeverity.Caution : NoticeSeverity.Information, definition.Title + ": " + result.Message);
    }

    private void NoteRestart(IEnumerable<SettingDefinition> changed)
    {
        if (changed.Any(d => d.TakesEffect == TakesEffect.ExplorerRestart)) explorerRestartPending = true;
        Refresh();
    }

    private async Task<T> RunAsync<T>(Func<T> work)
    {
        busy = true;
        Busy.Visibility = Visibility.Visible;
        Refresh();
        try { return await Task.Run(work); }
        finally
        {
            busy = false;
            Busy.Visibility = Visibility.Collapsed;
            context.Services.RaiseCalmChanged();
            Refresh();
        }
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(RestartExplorerAsync);

    private async Task RestartExplorerAsync()
    {
        RestartExplorer.IsEnabled = false;
        bool ok = await ExplorerRestart.RestartAsync();
        RestartExplorer.IsEnabled = true;
        if (ok) explorerRestartPending = false;
        else context.ShowNotice(NoticeSeverity.Caution, "File Explorer did not come back by itself. Press Ctrl+Shift+Esc, choose Run new task, and type explorer.");
        Refresh();
    }
}
