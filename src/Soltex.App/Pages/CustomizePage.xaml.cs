using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Audio.UI;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// One settings area (Taskbar, Start, File Explorer, Appearance, Multitasking, Privacy): a title and plain rows,
/// each with a switch or a list of options. Every change goes through the change engine (journal, read-back, undo).
/// </summary>
public partial class CustomizePage : UserControl, IShellPage
{
    /// <param name="SettingsUri">Windows Settings' own page for this area (null: there is none).</param>
    private sealed record Area(string Title, SettingGroup[] Groups, string? SettingsUri);

    private static readonly Dictionary<Destination, Area> Areas = new()
    {
        [Destination.Taskbar] = new("Taskbar", [SettingGroup.Taskbar], "ms-settings:taskbar"),
        [Destination.Start] = new("Start", [SettingGroup.StartAndSearch], "ms-settings:personalization-start"),
        // Windows Settings has no File Explorer page; its File Explorer options (extensions, hidden files) are under System › Advanced.
        [Destination.Explorer] = new("File Explorer", [SettingGroup.FileExplorer], "ms-settings:developers"),
        [Destination.Appearance] = new("Appearance", [SettingGroup.Appearance, SettingGroup.DesktopIcons], "ms-settings:personalization-colors"),
        [Destination.Multitasking] = new("Multitasking", [SettingGroup.Multitasking, SettingGroup.Input, SettingGroup.Gaming], "ms-settings:multitasking"),
        [Destination.Customize] = new("Privacy", [SettingGroup.Privacy, SettingGroup.TipsAndSuggestions], "ms-settings:privacy-general"),
    };

    private const string AutoHideTitle = "Automatically hide the taskbar";
    /// <summary>The width of every list on these pages, so the lists line up row under row.</summary>
    internal const double ChoiceWidth = 220;

    /// <summary>
    /// How a list shows its choices at <see cref="ChoiceWidth"/>: a long one is trimmed in the closed box (the open list
    /// and the box's tooltip show all of it). <paramref name="path"/> names the property shown; null shows the item itself.
    /// </summary>
    internal static DataTemplate TrimmedChoice(string? path = null)
    {
        FrameworkElementFactory text = new(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, path is null ? new System.Windows.Data.Binding() : new System.Windows.Data.Binding(path));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        DataTemplate template = new() { VisualTree = text };
        template.Seal();
        return template;
    }

    /// <summary>The Windows Settings page a customize page links to, or null when it has none.</summary>
    internal static string? SettingsUriFor(Destination destination) => Areas[destination].SettingsUri;

    public static bool Handles(Destination destination) => Areas.ContainsKey(destination);

    internal static IEnumerable<SearchEntry> EntriesFor(Destination destination)
    {
        Area area = Areas[destination];
        yield return new SearchEntry(area.Title, area.Title, destination, "top", area.Title.ToLowerInvariant());
        if (destination == Destination.Taskbar)
        {
            yield return new SearchEntry("Taskbar look", area.Title, destination, "look",
                ["Taskbar", "transparent", "translucent", "TranslucentTB", "clear", "acrylic", "blur", "opaque", "solid", "color", "colour", "accent",
                 "opacity", "tint", "maximized", "line", "states"]);
            // Each state of the look (TranslucentTB's dynamic modes) is found by name.
            foreach (TaskbarLookPanel.StateInfo state in TaskbarLookPanel.States)
                yield return new SearchEntry(state.SearchTitle, area.Title, destination, TaskbarLookPanel.AnchorFor(state.State),
                    ["Taskbar", "TranslucentTB", "look", .. state.Synonyms]);
            foreach (TaskbarTweak tweak in TaskbarTweaks.All)
                yield return new SearchEntry(tweak.Title, area.Title, destination, "tweak." + tweak.Id,
                    ["Taskbar", "Windhawk", .. tweak.Options.Select(o => o.Label)]);
            yield return new SearchEntry("Scroll over the taskbar to change volume", area.Title, destination, "scrollvolume",
                ["Taskbar", "Windhawk", "volume", "mouse wheel", "sound"]);
            yield return new SearchEntry(AutoHideTitle, area.Title, destination, "autohide",
                ["Taskbar", "auto-hide", "autohide", "hide the taskbar", "taskbar behaviors"]);
        }
        foreach (SearchEntry extra in Personalize.ExtrasPanel.EntriesFor(destination)) yield return extra;
        foreach (SettingDefinition d in Definitions(area))
        {
            // A row under its own heading (Desktop icons, Gaming) is also found by that heading's words.
            string group = CalmCatalog.GroupTitle(d.Group);
            string[] heading = string.Equals(group, area.Title, StringComparison.Ordinal) ? [] : [group];
            yield return new SearchEntry(d.Title, area.Title, destination, d.Id,
                [area.Title, .. heading, .. d.Options.Select(o => o.Label), .. d.CalmEffect.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 4)]);
        }
    }

    private static IEnumerable<SettingDefinition> Definitions(Area area) =>
        area.Groups.SelectMany(g => CalmCatalog.All.Where(d => d.Group == g));

    private sealed record Row(SettingDefinition Definition, Control Control, TextBlock Note, Button Undo);

    private readonly ShellContext context;
    private readonly Destination destination;
    private readonly Area area;
    private readonly PageAnchors anchors = new();
    private readonly ExplorerRestartReminder restart;
    /// <summary>Rows the automatic dark mode schedule sets by time of day while it is on.</summary>
    private static readonly string[] ScheduledIds = ["appearance.apps", "appearance.windows"];

    private readonly Dictionary<string, Row> rows = [];
    /// <summary>
    /// Rows whose last change Windows refused (access denied), with the choice Windows kept (null until the next refresh
    /// reads it). They say so until a change goes through or the value changes outside this app.
    /// </summary>
    private readonly Dictionary<string, int?> blocked = new(StringComparer.Ordinal);
    /// <summary>Changes asked for while another was being applied; each row keeps only its latest choice.</summary>
    private readonly List<(SettingDefinition Definition, int Choice)> queued = [];
    private bool loading, busy, raisingCalm, darkModeScheduled;

    internal CustomizePage(ShellContext context, Destination destination = Destination.Customize)
    {
        this.context = context;
        this.destination = destination;
        area = Areas[destination];
        restart = ExplorerRestartReminder.For(context.Services);
        InitializeComponent();
        PageTitleText.Text = area.Title;
        anchors.Add("top", PageTitleText);
        if (area.SettingsUri is not null)
        {
            SettingsLinkText.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(SettingsLink, "Opens the " + area.Title + " options in Windows Settings");
        }
        BuildRows();
        // This page's own changes refresh once when they finish; changes from elsewhere refresh it when idle.
        context.Services.CalmChanged += () =>
        {
            if (!raisingCalm) Dispatcher.InvokeAsync(() => { if (!busy) Refresh(); });
        };
        restart.Changed += () => Dispatcher.InvokeAsync(ShowRestartBar);
        if (destination == Destination.Appearance)
        {
            darkModeScheduled = context.Services.Settings.Current.DarkModeSchedule;
            context.Services.Settings.Changed += settings => Dispatcher.InvokeAsync(() =>
            {
                if (settings.DarkModeSchedule == darkModeScheduled) return;
                darkModeScheduled = settings.DarkModeSchedule;
                if (!busy) Refresh();
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
        if (!busy) Refresh();
        RefreshAutoHide();
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
            anchors.Add("look", look.HeaderFor(TaskbarState.Desktop));
            foreach (TaskbarLookPanel.StateInfo state in TaskbarLookPanel.States) anchors.Add(TaskbarLookPanel.AnchorFor(state.State), look.HeaderFor(state.State));
            Groups.Children.Add(SectionTitle("Details", "Inspired by Windhawk mods"));
            details = new TaskbarDetailsPanel();
            details.Attach(context);
            Groups.Children.Add(details);
            foreach (TaskbarTweak tweak in TaskbarTweaks.All) anchors.Add("tweak." + tweak.Id, details.ControlFor(tweak.Id));
            anchors.Add("scrollvolume", details.ScrollVolumeSwitch);
            Groups.Children.Add(SectionTitle("Behavior", null));
            Groups.Children.Add(BuildAutoHideRow());
        }
        if (destination is Destination.Start or Destination.Appearance)
        {
            extras = new Personalize.ExtrasPanel(context, destination);
            foreach ((string anchor, FrameworkElement target) in extras.Anchors) anchors.Add(anchor, target);
        }
        foreach (SettingGroup group in area.Groups)
        {
            // Groups get a heading when the page has more than one section (other groups, or the extras below them). A
            // heading that repeats the page title (Privacy on the Privacy page) is left out.
            string heading = CalmCatalog.GroupTitle(group);
            if ((area.Groups.Length > 1 || extras is not null) && !string.Equals(heading, area.Title, StringComparison.Ordinal))
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
        // A narrow page or large text moves Undo and the control below the title instead of squeezing it.
        ResponsiveRow layout = new() { MinPrimaryWidth = 160, Spacing = 16 };

        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = d.Title, Style = (Style)FindResource("Shell.Body") });
        TextBlock note = new() { Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(note);
        layout.Children.Add(text);

        Button undo = new() { Content = "Undo", Tag = d, Style = (Style)FindResource("Studio.GhostButton"), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(undo, "Undo: " + d.Title);
        undo.Click += UndoRow_Click;
        layout.Children.Add(undo);

        Control control;
        if (d.Options.Count == 2 && (d.IsSwitch || d.Choices is null))
        {
            // Switch: still a CheckBox (Toggle pattern); Checked/Unchecked so mouse, keyboard and UI Automation all apply.
            CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), Tag = d, VerticalAlignment = VerticalAlignment.Center };
            toggle.Checked += Toggle_Changed;
            toggle.Unchecked += Toggle_Changed;
            control = toggle;
        }
        else
        {
            ComboBox list = new() { Tag = d, Width = ChoiceWidth, VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = d.Options.Select(o => o.Label).ToArray(), ItemTemplate = TrimmedChoice() };
            list.SelectionChanged += Choice_Changed;
            control = list;
        }
        AutomationProperties.SetName(control, d.Title);
        layout.Children.Add(control);

        Border row = new() { Style = (Style)FindResource("Studio.Row"), Child = layout };
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
                    case ComboBox list:
                        list.SelectedIndex = state.Choice;
                        // The box trims a long choice; the tooltip has all of it.
                        list.ToolTip = list.SelectedItem as string;
                        break;
                }
                // While automatic dark mode is on it rewrites these values by time of day, so a choice here would not last.
                bool scheduled = darkModeScheduled && ScheduledIds.Contains(state.Definition.Id, StringComparer.Ordinal);
                // Rows stay enabled while a change runs, so the focused switch keeps focus; the running change blocks others.
                row.Control.IsEnabled = state.NotApplicable is null && !scheduled;
                List<string> notes = [.. state.PolicyNotes];
                if (state.NotApplicable is { } reason) notes.Insert(0, reason);
                if (scheduled) notes.Insert(0, "Set by automatic dark mode.");
                else if (state.Choice < 0 && row.Control is ComboBox) notes.Add("Set to a value not listed here.");
                bool refused = StillBlocked(state);
                if (refused) notes.Insert(0, ChangeEngine.BlockedByWindowsMessage);
                SetNote(row, string.Join(" ", notes), refused);
                row.Note.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                row.Undo.Visibility = state.CanUndo ? Visibility.Visible : Visibility.Collapsed;
                row.Undo.IsEnabled = !scheduled;
            }
            ShowRestartBar();
        }
        finally { loading = false; }
    }

    /// <summary>
    /// True while Windows' refusal still describes the row: the value is what Windows kept when it refused. Once the value
    /// changes outside this app (in Windows Settings, say) the refusal no longer applies and the note goes.
    /// </summary>
    private bool StillBlocked(SettingState state)
    {
        string id = state.Definition.Id;
        if (!blocked.TryGetValue(id, out int? kept)) return false;
        if (kept is null)
        {
            blocked[id] = state.Choice;
            return true;
        }
        if (kept == state.Choice) return true;
        blocked.Remove(id);
        return false;
    }

    private void ShowRestartBar() => RestartBar.Visibility = restart.Pending ? Visibility.Visible : Visibility.Collapsed;

    private void Toggle_Changed(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => ToggleAsync(sender));

    private async Task ToggleAsync(object sender)
    {
        if (loading || sender is not CheckBox { Tag: SettingDefinition definition } toggle) return;
        await ApplyAsync(definition, toggle.IsChecked == true ? 1 : 0, toggle);
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) => Dispatcher.Handle(() => ChoiceAsync(sender));

    private async Task ChoiceAsync(object sender)
    {
        if (loading || sender is not ComboBox { Tag: SettingDefinition definition, SelectedIndex: >= 0 } list) return;
        list.ToolTip = list.SelectedItem as string;
        await ApplyAsync(definition, list.SelectedIndex, list);
    }

    /// <summary>
    /// Applies a row's new value. The page is not refreshed or disabled before the write, so the switch keeps its new
    /// position and the keyboard stays on it. A change asked for while another runs waits for it; then the page shows
    /// Windows' state once.
    /// </summary>
    private async Task ApplyAsync(SettingDefinition definition, int choice, Control origin)
    {
        queued.RemoveAll(q => q.Definition.Id == definition.Id);
        queued.Add((definition, choice));
        if (busy) return; // the running change applies it next
        FocusSnapshot focus = FocusSnapshot.Take(origin);
        List<TextBlock> refused = [];
        Begin();
        try
        {
            while (queued.Count > 0)
            {
                (SettingDefinition next, int wanted) = queued[0];
                queued.RemoveAt(0);
                ChangeResult result = await Task.Run(() => Engine.ApplyChoice(next, wanted));
                // Windows guards some values from apps: the row itself says so and links to Windows Settings.
                if (result.BlockedByWindows)
                {
                    blocked[next.Id] = null;
                    refused.Add(rows[next.Id].Note);
                }
                else blocked.Remove(next.Id);
                if (result.Outcome == ChangeOutcome.Applied && next.TakesEffect == TakesEffect.ExplorerRestart) restart.Set(true);
                else if (!result.BlockedByWindows && result.Outcome is not (ChangeOutcome.Applied or ChangeOutcome.AlreadyInPlace))
                    context.ShowNotice(NoticeSeverity.Caution, next.Title + ": " + result.Message);
            }
        }
        finally
        {
            queued.Clear();
            Finish(focus);
        }
        // The row's note now says Windows refused; screen readers read it without the keyboard leaving the switch.
        foreach (TextBlock note in refused) Controls.LiveRegion.Notify(note);
    }

    private void Begin()
    {
        busy = true;
        Busy.Visibility = Visibility.Visible;
    }

    /// <summary>Ends a change: other pages refresh, this one refreshes once, and the keyboard returns where it was.</summary>
    private void Finish(FocusSnapshot focus, params UIElement?[] fallbacks)
    {
        busy = false;
        Busy.Visibility = Visibility.Collapsed;
        raisingCalm = true;
        try { context.Services.RaiseCalmChanged(); }
        finally { raisingCalm = false; }
        Refresh();
        focus.Restore(this, fallbacks);
    }

    /// <summary>The row's note: plain text, plus a link to Windows Settings when Windows refused the change.</summary>
    private void SetNote(Row row, string text, bool refused)
    {
        string key = text + (refused ? "\n+link" : "");
        if (row.Note.Tag is string shown && shown == key) return; // keep a focused link focused across refreshes
        row.Note.Tag = key;
        row.Note.Inlines.Clear();
        row.Note.Inlines.Add(new Run(text));
        if (!refused) return;
        string uri = area.SettingsUri ?? "ms-settings:";
        Hyperlink open = new(new Run("Open Windows Settings"));
        AutomationProperties.SetName(open, "Open Windows Settings");
        AutomationProperties.SetHelpText(open, row.Definition.Title + " can be changed there");
        open.Click += (_, _) => OpenSettings(uri);
        row.Note.Inlines.Add(new Run(" "));
        row.Note.Inlines.Add(open);
    }

    private void SettingsLink_Click(object sender, RoutedEventArgs e)
    {
        if (area.SettingsUri is { } uri) OpenSettings(uri);
    }

    private void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { context.ShowNotice(NoticeSeverity.Caution, "Windows Settings could not be opened."); }
    }

    // ---- Automatically hide the taskbar: Windows' live state, not a registry row (see TaskbarAutoHide) ----

    private CheckBox? autoHide;
    private TextBlock? autoHideNote;
    private bool autoHideLoading, autoHideBusy;

    /// <summary>The auto-hide switch on the Taskbar page (null on other pages).</summary>
    internal CheckBox? AutoHideSwitch => autoHide;

    /// <summary>The note under a setting's row (policies, problems, and "Windows doesn't let apps change this").</summary>
    internal TextBlock? NoteFor(string settingId) => rows.TryGetValue(settingId, out Row? row) ? row.Note : null;

    /// <summary>The switch or list of a setting's row.</summary>
    internal Control? ControlFor(string settingId) => rows.TryGetValue(settingId, out Row? row) ? row.Control : null;

    private Border BuildAutoHideRow()
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = AutoHideTitle, Style = (Style)FindResource("Shell.Body") });
        autoHideNote = new TextBlock { Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(autoHideNote);
        grid.Children.Add(text);

        // Off and disabled until Windows' state has been read.
        autoHide = new CheckBox { Style = (Style)FindResource("Studio.Switch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), IsEnabled = false };
        AutomationProperties.SetName(autoHide, AutoHideTitle);
        autoHide.Checked += AutoHide_Changed;
        autoHide.Unchecked += AutoHide_Changed;
        Grid.SetColumn(autoHide, 1);
        grid.Children.Add(autoHide);
        anchors.Add("autohide", autoHide);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid };
    }

    private void RefreshAutoHide()
    {
        if (autoHide is null || autoHideBusy) return;
        Dispatcher.Handle(async () => ShowAutoHide(await TaskbarCallAsync(TaskbarAutoHide.IsOn)));
    }

    private void AutoHide_Changed(object sender, RoutedEventArgs e) => Dispatcher.Handle(AutoHideAsync);

    private async Task AutoHideAsync()
    {
        // While a change runs the switch stays enabled (and focused); a second press waits for Windows' answer.
        if (autoHideLoading || autoHideBusy || autoHide is null) return;
        bool wanted = autoHide.IsChecked == true;
        FocusSnapshot focus = FocusSnapshot.Take(autoHide);
        autoHideBusy = true;
        bool? now;
        try { now = await TaskbarCallAsync(() => TaskbarAutoHide.Set(wanted) ? TaskbarAutoHide.IsOn() : null); }
        finally { autoHideBusy = false; }
        ShowAutoHide(now);
        focus.Restore(this);
        if (now is not null && now != wanted)
            context.ShowNotice(NoticeSeverity.Caution, "Windows did not change the taskbar's auto-hide. Try again, or use Windows Settings.");
    }

    private void ShowAutoHide(bool? on)
    {
        if (autoHide is null || autoHideNote is null) return;
        autoHideLoading = true;
        try
        {
            autoHide.IsChecked = on == true;
            autoHide.IsEnabled = on is not null;
            // Windows remembers the state itself, so there is no Undo button: switching back is the undo.
            autoHideNote.Text = on switch
            {
                null => "The taskbar isn't running right now, so this can't be changed.",
                true => "Changes right away. Turn it off to undo.",
                false => "Changes right away."
            };
        }
        finally { autoHideLoading = false; }
    }

    /// <summary>Asks the taskbar off the UI thread, bounded so a hung Explorer cannot freeze the page.</summary>
    private static async Task<bool?> TaskbarCallAsync(Func<bool?> call)
    {
        try { return await Task.Run(call).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return null; }
    }

    private void UndoRow_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => UndoRowAsync(sender));

    private async Task UndoRowAsync(object sender)
    {
        if (busy || sender is not Button { Tag: SettingDefinition definition } button) return;
        // Undo goes away once used; the keyboard moves to the row's own switch or list.
        FocusSnapshot focus = FocusSnapshot.Take(button);
        Begin();
        UndoResult? result = null;
        try
        {
            result = await Task.Run(() => Engine.Undo(definition));
            if (result.Outcome == UndoOutcome.Restored)
            {
                blocked.Remove(definition.Id);
                if (definition.TakesEffect == TakesEffect.ExplorerRestart) restart.Set(true);
            }
        }
        finally { Finish(focus, rows[definition.Id].Control); }
        if (result.Outcome != UndoOutcome.Restored)
            context.ShowNotice(result.Outcome == UndoOutcome.Failed ? NoticeSeverity.Caution : NoticeSeverity.Information, definition.Title + ": " + result.Message);
    }

    private void RestartExplorer_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(RestartExplorerAsync);

    private async Task RestartExplorerAsync()
    {
        if (restart.Restarting) return;
        // The button stays enabled while File Explorer restarts (a second press waits), so the keyboard stays on it.
        FocusSnapshot focus = FocusSnapshot.Take(RestartExplorer);
        bool ok = await restart.RestartAsync();
        if (!ok) context.ShowNotice(NoticeSeverity.Caution, ExplorerRestartReminder.FailedMessage);
        ShowRestartBar();
        // Once File Explorer is back the bar goes away; the keyboard moves to the page's first control.
        focus.Restore(this, PageAnchors.FirstFocusable(Groups) as UIElement);
    }
}

/// <summary>
/// Whether a change made in this app waits for File Explorer to restart. One per running app, shared by every page and
/// the De-Windows card's notice, so restarting File Explorer from any of them clears the reminder everywhere.
/// </summary>
internal sealed class ExplorerRestartReminder
{
    internal const string FailedMessage = "File Explorer did not come back by itself. Press Ctrl+Shift+Esc, choose Run new task, and type explorer.";
    private static readonly ConditionalWeakTable<ShellServices, ExplorerRestartReminder> PerApp = new();

    public static ExplorerRestartReminder For(ShellServices services) => PerApp.GetValue(services, _ => new ExplorerRestartReminder());

    /// <summary>True after a change that shows only once File Explorer restarts, until it has restarted.</summary>
    public bool Pending { get; private set; }

    /// <summary>True while File Explorer is being restarted.</summary>
    public bool Restarting { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Pending"/> changes.</summary>
    public event Action? Changed;

    public void Set(bool pending)
    {
        if (Pending == pending) return;
        Pending = pending;
        Changed?.Invoke();
    }

    /// <summary>Restarts File Explorer (once at a time) and clears the reminder when it came back. False when it did not.</summary>
    public async Task<bool> RestartAsync(Func<Task<bool>>? restart = null)
    {
        if (Restarting) return false;
        Restarting = true;
        try
        {
            bool ok = await (restart ?? ExplorerRestart.RestartAsync)();
            if (ok) Set(false);
            return ok;
        }
        finally { Restarting = false; }
    }
}
