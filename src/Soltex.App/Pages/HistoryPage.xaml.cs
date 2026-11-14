using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// Every change this app recorded: setting changes (undoable here) and Full De-Windows changes (listed here, undone on
/// Home because they need administrator rights).
/// </summary>
public partial class HistoryPage : UserControl, IShellPage
{
    internal static readonly SearchEntry[] Entries =
    [
        new("History of changes", "History", Destination.History, "top", "undo", "changes", "log", "revert", "what changed")
    ];

    private sealed record Row(DateTimeOffset At, string When, string Title, string Summary, string SettingId, Visibility UndoVisibility, string UndoName);

    private readonly ShellContext context;
    private readonly PageAnchors anchors = new();
    private bool shown;

    internal HistoryPage(ShellContext context)
    {
        this.context = context;
        InitializeComponent();
        anchors.Add("top", UndoAllButton);
        context.Services.CalmChanged += () => Dispatcher.InvokeAsync(() => { if (shown) Refresh(); });
        context.Services.SystemChanged += () => Dispatcher.InvokeAsync(() => { if (shown) Refresh(); });
    }

    public Destination Destination => Destination.History;
    public string Title => "History";
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => Entries;
    public bool Reveal(string anchor) => anchors.Reveal(anchor);
    public void OnShown() { shown = true; Refresh(); }
    public void OnHidden() => shown = false;

    private void Refresh()
    {
        IReadOnlyList<HistoryEntry> history = context.Services.Calm.History(CalmCatalog.All);
        Row[] rows =
        [
            .. history.Select(h => new Row(h.At, When(h.At), h.Title, h.Summary, h.SettingId,
                h.CanUndo ? Visibility.Visible : Visibility.Collapsed, "Undo: " + h.Title)),
            .. SystemHistory().Select(h => new Row(h.At, When(h.At), h.Title, SystemSummary(h), h.SettingId, Visibility.Collapsed, ""))
        ];
        ChangeList.ItemsSource = rows.OrderByDescending(r => r.At).ToArray();
        int undoable = history.Count(h => h.CanUndo);
        Summary.Text = rows.Length == 0 ? "" : Count(rows.Length, "change") + " recorded; " + undoable.ToString(CultureInfo.CurrentCulture) + " can be undone here.";
        HeaderRow.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UndoAllButton.IsEnabled = undoable > 0;
    }

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + noun + (count == 1 ? "" : "s");

    /// <summary>Full De-Windows changes from the administrator journal (read-only here).</summary>
    private IReadOnlyList<HistoryEntry> SystemHistory()
    {
        try { return context.Services.SystemChanges.Inspector().History(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            context.Services.Report(NoticeSeverity.Caution, "History", "Full De-Windows changes could not be read: " + error.Message);
            return [];
        }
    }

    private static string SystemSummary(HistoryEntry entry)
    {
        SystemItem? item = SystemCatalog.All.FirstOrDefault(i => i.Id == entry.SettingId);
        return entry.Summary switch
        {
            "Changed" => "Changed by Full De-Windows. Undo it on Home.",
            "Removed" => "Removed by Full De-Windows. " + (item is null ? "Can't be undone." : SystemCatalog.ReinstallHint(item) ?? "Can't be undone."),
            _ => "Full De-Windows: " + entry.Summary
        };
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(() => UndoAsync(sender));

    private async Task UndoAsync(object sender)
    {
        if (sender is not Button { Tag: string id } button) return;
        SettingDefinition? definition = CalmCatalog.All.FirstOrDefault(d => d.Id == id);
        if (definition is null) return;
        button.IsEnabled = false;
        UndoResult result = await Task.Run(() => context.Services.Calm.Undo(definition));
        context.ShowNotice(result.Outcome == UndoOutcome.Failed ? NoticeSeverity.Caution : NoticeSeverity.Success, definition.Title + ": " + result.Message);
        context.Services.RaiseCalmChanged();
        Refresh();
    }

    private void UndoAll_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(UndoAllAsync);

    private async Task UndoAllAsync()
    {
        IReadOnlyList<SettingDefinition> undoable = context.Services.Calm.UndoableSettings(CalmCatalog.All);
        if (undoable.Count == 0) return;
        if (MessageBox.Show(context.Owner,
                "Undo " + Count(undoable.Count, "change") + "? Anything you or another app changed since stays as it is.",
                "Undo all", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        UndoAllButton.IsEnabled = false;
        IReadOnlyList<UndoResult> results = await Task.Run(() => context.Services.Calm.UndoMany(undoable));
        int restored = results.Count(r => r.Outcome == UndoOutcome.Restored);
        int failed = results.Count(r => r.Outcome == UndoOutcome.Failed);
        context.ShowNotice(failed > 0 ? NoticeSeverity.Caution : NoticeSeverity.Success,
            restored.ToString(CultureInfo.CurrentCulture) + " undone." + (failed > 0 ? " " + failed.ToString(CultureInfo.CurrentCulture) + " could not be undone; see the list." : ""));
        context.Services.RaiseCalmChanged();
        Refresh();
    }
}
