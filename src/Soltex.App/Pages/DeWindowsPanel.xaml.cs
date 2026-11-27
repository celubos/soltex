using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Soltex.App.Shell;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>
/// The one-click De-Windows profile: shows how much of it is in place, lists exactly what would change (each item can
/// be left out), applies it through the change engine and undoes the profile's settings this app changed. Changes made
/// on the other pages are undone there or in History.
/// </summary>
public partial class DeWindowsPanel : UserControl
{
    private sealed class PlanRow(SettingDefinition definition, string target) : INotifyPropertyChanged
    {
        private bool include = true;
        public SettingDefinition Definition { get; } = definition;
        public string Title { get; } = target;
        public bool Include { get => include; set { include = value; PropertyChanged?.Invoke(this, new(nameof(Include))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private ShellContext? context;
    private bool busy, raisingCalm;

    public DeWindowsPanel() => InitializeComponent();

    internal void Attach(ShellContext shell)
    {
        context = shell;
        // This card's own changes refresh once when they finish; changes from elsewhere refresh it when idle.
        shell.Services.CalmChanged += () =>
        {
            if (!raisingCalm) Dispatcher.InvokeAsync(() => { if (!busy) Refresh(); });
        };
        Refresh();
    }

    private ChangeEngine Engine => context!.Services.Calm;

    internal void Refresh()
    {
        if (context is null) return;
        try
        {
            SettingState[] states = Engine.InspectAll(CalmCatalog.All).ToArray();
            SettingState[] profile = states.Where(s => s.Definition.InCalmProfile && s.NotApplicable is null).ToArray();
            int done = profile.Count(s => s.IsCalm);
            // Counts what is set now, whoever set it; History lists only what this app changed.
            Summary.Text = done == profile.Length
                ? "All " + profile.Length.ToString(CultureInfo.CurrentCulture) + " already in place"
                : done.ToString(CultureInfo.CurrentCulture) + " of " + profile.Length.ToString(CultureInfo.CurrentCulture) + " already in place";
            // Not tied to busy: a running change blocks the buttons in their handlers, so the pressed one keeps focus.
            ReviewButton.IsEnabled = done < profile.Length;
            UndoAllButton.IsEnabled = states.Any(s => s.CanUndo && s.Definition.InCalmProfile);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Summary.Text = "Could not read the current settings: " + error.Message;
        }
    }

    /// <summary>What the profile would change now, each phrased as the resulting state.</summary>
    internal IReadOnlyList<string> PendingTitles() => Plan().Select(r => r.Title).ToArray();

    private PlanRow[] Plan() => Engine.InspectAll(CalmCatalog.All)
        .Where(s => s.Definition.InCalmProfile && s.NotApplicable is null && !s.IsCalm)
        .Select(s => new PlanRow(s.Definition, Describe(s.Definition))).ToArray();

    private static string Describe(SettingDefinition d)
    {
        if (d.Choices is null) return d.Title;
        IReadOnlyList<RegistryValue> calm = d.Targets.Select(t => t.Calm).ToArray();
        SettingChoice? target = d.Options.FirstOrDefault(o => o.Values.SequenceEqual(calm));
        string area = CalmCatalog.GroupTitle(d.Group);
        return d.IsSwitch ? area + ": " + d.Title + (target?.Label == "On" ? " on" : " off") : area + ": " + d.Title + ": " + target?.Label;
    }

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (busy || context is null) return;
        PlanRow[] plan = Plan();
        foreach (PlanRow row in plan) row.PropertyChanged += (_, _) => UpdateApply(plan);
        PlanList.ItemsSource = plan;
        UpdateApply(plan);
        PlanPanel.Visibility = Visibility.Visible;
        ApplyPlanButton.Focus();
    }

    private void UpdateApply(PlanRow[] plan)
    {
        int count = plan.Count(r => r.Include);
        ApplyPlanButton.Content = count == 1 ? "Apply 1 change" : "Apply " + count.ToString(CultureInfo.CurrentCulture) + " changes";
        ApplyPlanButton.IsEnabled = count > 0;
    }

    private void CancelPlan_Click(object sender, RoutedEventArgs e)
    {
        PlanPanel.Visibility = Visibility.Collapsed;
        ReviewButton.Focus();
    }

    private void ApplyPlan_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(ApplyPlanAsync);

    private async Task ApplyPlanAsync()
    {
        if (busy || context is null || PlanList.ItemsSource is not PlanRow[] plan) return;
        SettingDefinition[] chosen = plan.Where(r => r.Include).Select(r => r.Definition).ToArray();
        // The list closes; the keyboard goes back to Review (or Undo, once everything is in place).
        FocusSnapshot focus = FocusSnapshot.Take(PlanPanel);
        PlanPanel.Visibility = Visibility.Collapsed;
        IReadOnlyList<ChangeResult> results = await RunAsync(() => Engine.ApplyMany(chosen, calm: true));
        focus.Restore(this, ReviewButton, UndoAllButton, Summary);
        ChangeResult[] failed = results.Where(r => r.Outcome == ChangeOutcome.Failed).ToArray();
        if (failed.Length > 0)
            context.ShowNotice(NoticeSeverity.Caution, "Not changed: " + string.Join("; ", failed.Select(f => f.Definition.Title + " (" + f.Message + ")")));
        if (results.Any(r => r.Outcome == ChangeOutcome.Applied && r.Definition.TakesEffect == TakesEffect.ExplorerRestart))
        {
            // The same reminder the Windows settings pages show, so restarting from either place clears both.
            ExplorerRestartReminder restart = ExplorerRestartReminder.For(context.Services);
            restart.Set(true);
            context.ShowNotice(NoticeSeverity.Information, "Some changes show after File Explorer restarts.", "Restart File Explorer",
                () => Dispatcher.Handle(() => RestartExplorerAsync(restart)));
        }
    }

    private async Task RestartExplorerAsync(ExplorerRestartReminder restart)
    {
        if (context is null || restart.Restarting) return;
        if (!await restart.RestartAsync()) context.ShowNotice(NoticeSeverity.Caution, ExplorerRestartReminder.FailedMessage);
    }

    private void UndoAll_Click(object sender, RoutedEventArgs e) => Dispatcher.Handle(UndoAllAsync);

    private async Task UndoAllAsync()
    {
        if (busy || context is null) return;
        IReadOnlyList<SettingDefinition> undoable = Engine.UndoableSettings(CalmCatalog.All.Where(d => d.InCalmProfile));
        if (undoable.Count == 0) return;
        string question = undoable.Count == 1
            ? "Undo 1 De-Windows change? Anything changed since stays as it is."
            : "Undo " + undoable.Count.ToString(CultureInfo.CurrentCulture) + " De-Windows changes? Anything changed since stays as it is.";
        if (MessageBox.Show(context.Owner, question, "Undo De-Windows", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        FocusSnapshot focus = FocusSnapshot.Take(UndoAllButton);
        IReadOnlyList<UndoResult> results = await RunAsync(() => Engine.UndoMany(undoable));
        // Undo has nothing left to do once everything is back: the keyboard moves to Review, or to the summary.
        focus.Restore(this, ReviewButton, Summary);
        UndoResult[] failed = results.Where(r => r.Outcome == UndoOutcome.Failed).ToArray();
        int kept = results.Count(r => r.Outcome == UndoOutcome.ChangedSince);
        if (failed.Length > 0 || kept > 0)
            context.ShowNotice(failed.Length > 0 ? NoticeSeverity.Caution : NoticeSeverity.Information,
                (kept > 0 ? kept.ToString(CultureInfo.CurrentCulture) + " kept because they changed since. " : "") +
                (failed.Length > 0 ? "Could not undo: " + string.Join("; ", failed.Select(f => f.Definition.Title)) : ""));
        if (results.Any(r => r.Outcome == UndoOutcome.Restored && r.Definition.TakesEffect == TakesEffect.ExplorerRestart))
            ExplorerRestartReminder.For(context.Services).Set(true);
    }

    /// <summary>Runs a change off the UI thread. Nothing is refreshed or disabled before it; the card refreshes once after.</summary>
    private async Task<T> RunAsync<T>(Func<T> work)
    {
        busy = true;
        Busy.Visibility = Visibility.Visible;
        try { return await Task.Run(work); }
        finally
        {
            busy = false;
            Busy.Visibility = Visibility.Collapsed;
            raisingCalm = true;
            try { context?.Services.RaiseCalmChanged(); }
            finally { raisingCalm = false; }
            Refresh();
        }
    }
}
