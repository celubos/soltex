using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// The full De-Windows profile (administrator): telemetry, ads, Bing/Copilot/Recall, update behaviour, OneDrive, Edge and
/// built-in apps. Shows each item's real state, applies the ticked ones in one elevated run and can undo them.
/// Also reports whether Windows updates are waiting, so automatic installs can be turned off without missing them.
/// </summary>
public partial class SystemPanel : UserControl
{
    /// <summary>
    /// A showing this soon after the last read (switching pages back and forth, restoring the window) reuses it, unless the
    /// administrator helper ran in between. Each read starts a few dozen short read-only tool processes.
    /// </summary>
    private static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, CheckBox> boxes = [];
    private ShellContext? context;
    private IReadOnlyList<SystemItemStatus> states = [];
    private DateTimeOffset readAt;
    private bool busy, shown, stale = true, refreshing, refreshAgain, updatesChecked;

    public SystemPanel() => InitializeComponent();

    internal void Attach(ShellContext shell)
    {
        context = shell;
        shell.Services.SystemChanged += OnSystemChanged;
    }

    /// <summary>Stops following system changes (the page is closing).</summary>
    internal void Detach()
    {
        if (context is not null) context.Services.SystemChanged -= OnSystemChanged;
        shown = false;
    }

    /// <summary>
    /// Reads the real state each time the page is shown (a few seconds of read-only queries; never on a timer), and again
    /// whenever the administrator helper has run.
    /// </summary>
    internal void OnShown()
    {
        if (context is null || shown) return;
        ShowAvailability();
        // Previews and tests run without live services: no system queries, no update search.
        if (context.Services.Protection is null) { Summary.Text = UpdateText.Text = "Not checked"; return; }
        shown = true;
        if (stale || DateTimeOffset.UtcNow - readAt > RereadAfter) Dispatcher.Handle(RefreshAsync);
        if (!updatesChecked)
        {
            updatesChecked = true;
            Dispatcher.Handle(CheckUpdatesAsync);
        }
    }

    internal void OnHidden() => shown = false;

    /// <summary>The administrator helper ran (here, from Put back or elsewhere): read the state again now, or on the next showing.</summary>
    private void OnSystemChanged()
    {
        stale = true;
        Dispatcher.InvokeAsync(() => { if (shown) Dispatcher.Handle(RefreshAsync); });
    }

    private SystemChanges Changes => context!.Services.SystemChanges;

    private async Task RefreshAsync()
    {
        if (context is null) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                stale = false;
                await ReadStateAsync();
            }
            while (refreshAgain);
        }
        finally { refreshing = false; }
    }

    private async Task ReadStateAsync()
    {
        Summary.Text = "Checking…";
        SystemChanges changes = Changes;
        try
        {
            states = await Task.Run(() => { SystemEngine engine = changes.Inspector(); return SystemCatalog.All.Select(engine.Inspect).ToArray(); });
            readAt = DateTimeOffset.UtcNow;
            SystemItemStatus[] relevant = states.Where(s => s.State != SystemItemState.NotPresent).ToArray();
            int done = relevant.Count(s => s.State == SystemItemState.Applied);
            string count = done == relevant.Length
                ? "All " + relevant.Length.ToString(CultureInfo.CurrentCulture) + " already in place"
                : done.ToString(CultureInfo.CurrentCulture) + " of " + relevant.Length.ToString(CultureInfo.CurrentCulture) + " already in place";
            Summary.Text = count + " · needs administrator";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            stale = true;
            Summary.Text = "Could not read the current state: " + error.Message;
        }
        UpdateButtons();
        if (PlanPanel.Visibility == Visibility.Visible) BuildList();
    }

    /// <summary>
    /// When changes cannot be made from this session (a separate profile folder, or a standard account), says why and
    /// keeps Review and Apply off. Returns false in that case.
    /// </summary>
    private bool ShowAvailability()
    {
        if (Changes.UnavailableReason is not { } reason) return true;
        ReviewButton.IsEnabled = ApplyButton.IsEnabled = UndoButton.IsEnabled = false;
        PlanPanel.Visibility = Visibility.Collapsed;
        ResultText.Text = reason;
        ResultText.Visibility = Visibility.Visible;
        return false;
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowAvailability()) return;
        BuildList();
        PlanPanel.Visibility = Visibility.Visible;
        ResultText.Visibility = Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => PlanPanel.Visibility = Visibility.Collapsed;

    private void BuildList()
    {
        Areas.Children.Clear();
        boxes.Clear();
        foreach (IGrouping<SystemArea, SystemItemStatus> area in states.GroupBy(s => s.Item.Area))
        {
            Areas.Children.Add(new TextBlock { Text = SystemCatalog.AreaTitle(area.Key), Style = (Style)FindResource("Shell.Body"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
            foreach (SystemItemStatus state in area)
            {
                bool actionable = state.State is SystemItemState.NotApplied or SystemItemState.Partial;
                CheckBox box = new() { Content = state.Item.Title, IsChecked = actionable && state.Item.Default, IsEnabled = actionable, Margin = new Thickness(0, 3, 0, 3), Tag = state.Item };
                AutomationProperties.SetName(box, state.Item.Title);
                box.Checked += (_, _) => UpdateApply();
                box.Unchecked += (_, _) => UpdateApply();
                TextBlock stateText = new()
                {
                    Text = state.State switch
                    {
                        SystemItemState.Applied => "Done",
                        SystemItemState.Partial => "Partly done",
                        SystemItemState.NotPresent => "Not on this PC",
                        _ => ""
                    },
                    Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel row = new();
                DockPanel.SetDock(stateText, Dock.Right);
                row.Children.Add(stateText);
                row.Children.Add(box);
                Areas.Children.Add(row);
                if (state.Item.RemovesApps && state.State != SystemItemState.NotPresent) Areas.Children.Add(ReinstallNote(state.Item));
                boxes[state.Item.Id] = box;
            }
        }
        UpdateApply();
    }

    /// <summary>"Can't be undone" and how to get the app back: a download link, or the Microsoft Store.</summary>
    private TextBlock ReinstallNote(SystemItem item)
    {
        TextBlock note = new() { Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(28, 0, 0, 4) };
        note.Inlines.Add(new Run("Can't be undone. "));
        if (SystemCatalog.ReinstallLink(item) is { } link)
        {
            Hyperlink download = new(new Run("Get it again")) { NavigateUri = link };
            AutomationProperties.SetName(download, "Download " + item.Subject + " again");
            download.Click += (_, _) => OpenLink(link);
            note.Inlines.Add(download);
        }
        else note.Inlines.Add(new Run("Reinstall from the Microsoft Store."));
        return note;
    }

    private void OpenLink(Uri link)
    {
        try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            context?.ShowNotice(NoticeSeverity.Caution, "The download page could not be opened: " + error.Message);
        }
    }

    private void UpdateApply()
    {
        int count = boxes.Values.Count(b => b.IsChecked == true);
        ApplyButton.Content = count == 1 ? "Apply 1 change" : "Apply " + count.ToString(CultureInfo.CurrentCulture) + " changes";
        ApplyButton.IsEnabled = !busy && count > 0 && Changes.UnavailableReason is null;
    }

    private void UpdateButtons()
    {
        if (!ShowAvailability()) return;
        UndoButton.IsEnabled = !busy && states.Any(s => s.CanUndo);
        ReviewButton.IsEnabled = !busy;
        if (boxes.Count > 0) UpdateApply();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => RunAsync("apply", boxes.Where(b => b.Value.IsChecked == true).Select(b => b.Key).ToArray()));

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        string[] undoable = states.Where(s => s.CanUndo).Select(s => s.Item.Id).ToArray();
        if (undoable.Length == 0) return;
        string question = undoable.Length == 1
            ? "Undo 1 system change? Removed apps are not reinstalled."
            : "Undo " + undoable.Length.ToString(CultureInfo.CurrentCulture) + " system changes? Removed apps are not reinstalled.";
        if (MessageBox.Show(context!.Owner, question, "Undo system changes", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Dispatcher.Handle(() => RunAsync("undo", undoable));
    }

    private async Task RunAsync(string action, string[] ids)
    {
        if (context is null || ids.Length == 0) return;
        busy = true;
        Busy.Visibility = Visibility.Visible;
        ApplyButton.IsEnabled = UndoButton.IsEnabled = ReviewButton.IsEnabled = false;
        try
        {
            SystemResponse response = await Changes.RunAsync(action, ids);
            ResultText.Text = response.Error ?? Describe(action == "apply", response.Results);
            ResultText.Visibility = Visibility.Visible;
        }
        finally
        {
            busy = false;
            Busy.Visibility = Visibility.Collapsed;
            // When the helper ran, the shell raises SystemChanged and the state is read again from there.
            UpdateButtons();
        }
    }

    /// <summary>One line for the helper's results: how many items finished, anything worth knowing, and what was not done.</summary>
    private static string Describe(bool apply, IReadOnlyList<SystemResult> results)
    {
        static string Title(SystemResult r) => SystemCatalog.All.FirstOrDefault(i => i.Id == r.ItemId)?.Title ?? r.ItemId;
        static bool Plain(SystemResult r) => r.Ok && r.Message is SystemEngine.DoneMessage or SystemEngine.UndoneMessage;
        static string Sentence(string text) => text.EndsWith('.') ? text : text + ".";
        List<string> parts = [];
        int plain = results.Count(Plain);
        if (plain > 0)
        {
            parts.Add((apply ? "Done. " : "Undone. ") + plain.ToString(CultureInfo.CurrentCulture) + (plain == 1 ? " item." : " items.")
                      + (apply ? " Some changes finish after a restart." : ""));
        }
        parts.AddRange(results.Where(r => r.Ok && !Plain(r)).Select(r => Title(r) + ": " + Sentence(r.Message)));
        string[] failed = results.Where(r => !r.Ok).Select(r => Title(r) + ": " + r.Message.TrimEnd('.')).ToArray();
        if (failed.Length > 0) parts.Add("Not done: " + string.Join("; ", failed) + ".");
        return parts.Count == 0 ? "Nothing was changed." : string.Join(" ", parts);
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(CheckUpdatesAsync);

    private async Task CheckUpdatesAsync()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateText.Text = "Checking for updates…";
        UpdateCheck result = await UpdateWatcher.CheckAsync();
        UpdateText.Text = result.Error ?? (result.Titles.Count == 0
            ? "Up to date"
            : result.Titles.Count.ToString(CultureInfo.CurrentCulture) + (result.Titles.Count == 1 ? " update available: " : " updates available: ") + string.Join(", ", result.Titles.Take(3)));
        CheckUpdatesButton.IsEnabled = true;
    }

    private void OpenWindowsUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
