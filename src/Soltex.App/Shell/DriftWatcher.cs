using System.IO;
using System.Windows.Threading;
using Soltex.App.Settings;
using Soltex.Customize;

namespace Soltex.App.Shell;

/// <summary>What is different now from what this app set: per-user settings and full De-Windows items.</summary>
public sealed record DriftReport(IReadOnlyList<SettingDefinition> Settings, IReadOnlyList<SystemItemStatus> SystemItems)
{
    public int Count => Settings.Count + SystemItems.Count;

    /// <summary>Setting and item ids, as stored in <see cref="AppSettings.ReportedDrift"/>.</summary>
    public IEnumerable<string> Ids => Settings.Select(s => s.Id).Concat(SystemItems.Select(s => s.Item.Id));

    /// <summary>Neutral wording: the user may have made the change themselves.</summary>
    public string Summary => Count == 1
        ? $"A setting you changed with {ProductIdentity.DisplayName} is different now: {Names.First()}."
        : $"{Count} settings you changed with {ProductIdentity.DisplayName} are different now: {DriftWatcher.List([.. Names])}.";

    private IEnumerable<string> Names => Settings.Select(s => s.Title).Concat(SystemItems.Select(s => s.Item.Subject));
}

/// <summary>The outcome of Put back: per-user results and the administrator helper's response (null when no item needed it).</summary>
internal sealed record DriftPutBack(IReadOnlyList<ChangeResult> Settings, IReadOnlyList<string> SystemIds, SystemResponse? System)
{
    private IEnumerable<string> FailedSettingIds => Settings.Where(r => r.Outcome is ChangeOutcome.Failed or ChangeOutcome.Refused).Select(r => r.Definition.Id);

    private IEnumerable<string> FailedSystemIds => System is null ? [] : System.Error is not null
        ? SystemIds
        : SystemIds.Where(id => System.Results.FirstOrDefault(r => r.ItemId == id) is not { Ok: true });

    /// <summary>Ids that are still different after Put back.</summary>
    public IReadOnlyList<string> StillDifferent => [.. FailedSettingIds.Concat(FailedSystemIds)];

    public bool Succeeded => StillDifferent.Count == 0;

    public string Message
    {
        get
        {
            int total = Settings.Count + SystemIds.Count;
            string[] failed = [.. Settings.Where(r => r.Outcome is ChangeOutcome.Failed or ChangeOutcome.Refused).Select(r => r.Definition.Title)
                .Concat(FailedSystemIds.Select(SystemName))];
            int done = total - failed.Length;
            if (failed.Length == 0)
                return (done == 1 ? "Put back 1 setting." : $"Put back {done} settings.") + (SystemIds.Count > 0 ? " Some changes finish after a restart." : "");
            string reason = System?.Error is { } error ? " " + error : "";
            return (done > 0 ? $"Put back {done} of {total}. " : "") + "Not put back: " + DriftWatcher.List(failed) + "." + reason;
        }
    }

    private string SystemName(string id)
    {
        string title = SystemCatalog.All.FirstOrDefault(i => i.Id == id)?.Subject ?? id;
        string? why = System?.Results.FirstOrDefault(r => r.ItemId == id && !r.Ok)?.Message;
        return string.IsNullOrWhiteSpace(why) ? title : title + " (" + why.Trim().TrimEnd('.') + ")";
    }
}

/// <summary>
/// Drift check: a little after start-up and twice a day, compares what this app set with what is set now, and offers to put
/// back what is different. A difference stays on offer until the user puts it back or closes the offer
/// (<see cref="Dismiss"/>); only then is it remembered across launches and not offered again. An offer that was never
/// answered (another notice replaced it, or the app closed) comes back with the next check, at most twice a day.
/// Reads only; nothing changes without the user.
/// </summary>
public sealed class DriftWatcher : IDisposable
{
    /// <summary>Settings the dark mode schedule switches on purpose; never reported while the schedule is on.</summary>
    internal static readonly string[] ScheduledIds = ["appearance.apps", "appearance.windows"];

    private readonly ShellServices services;
    private readonly DispatcherTimer timer;
    private DispatcherTimer? first;
    private bool checking, disposed;

    public DriftWatcher(ShellServices services)
    {
        this.services = services;
        timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher) { Interval = TimeSpan.FromHours(12) };
        timer.Tick += (_, _) => _ = CheckAsync();
    }

    /// <summary>Raised on the UI thread when a check finds a difference the user has not answered yet.</summary>
    public event Action<DriftReport>? Found;

    /// <summary>True while the checks are scheduled.</summary>
    internal bool IsAttached => timer.IsEnabled || first?.IsEnabled == true;

    public void Attach()
    {
        if (disposed) return;
        timer.Start();
        // A little after start-up, so the first check never delays the window.
        first?.Stop();
        first = new DispatcherTimer(DispatcherPriority.Background, timer.Dispatcher) { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first?.Stop(); _ = CheckAsync(); };
        first.Start();
    }

    /// <summary>Compares now. Returns null when a check is already running, the watcher is disposed or the check failed.</summary>
    public async Task<DriftReport?> CheckAsync()
    {
        if (checking || disposed) return null;
        checking = true;
        try
        {
            DriftReport report = await Task.Run(() => new DriftReport(
                services.Calm.Drifted(CalmCatalog.All),
                services.SystemChanges.Inspector().Drifted(SystemCatalog.All))).ConfigureAwait(true);
            report = Filter(report, services.Settings.Current);
            string? reported = services.Settings.Current.ReportedDrift;
            // Something new stays unremembered until the user answers the offer, so it is offered again if they never do.
            // Nothing new: forget what is no longer different, so it counts as new if it changes back again.
            if (IsNew(report.Ids, reported)) { if (!disposed) Found?.Invoke(report); }
            else Remember(report.Ids);
            return report;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            services.Report(NoticeSeverity.Caution, "Your changes", "Could not check your changes: " + error.Message);
            return null;
        }
        finally { checking = false; }
    }

    /// <summary>
    /// Puts everything in the report back: per-user settings directly, full De-Windows items through the administrator
    /// helper. Remembers what is still different, so it is not reported again.
    /// </summary>
    internal async Task<DriftPutBack> PutBackAsync(DriftReport report)
    {
        ChangeResult[] settings = await Task.Run(() => report.Settings.Select(Reapply).ToArray()).ConfigureAwait(true);
        services.RaiseCalmChanged();
        string[] system = [.. report.SystemItems.Select(s => s.Item.Id)];
        SystemResponse? response = system.Length > 0 ? await services.SystemChanges.RunAsync("apply", system).ConfigureAwait(true) : null;
        DriftPutBack result = new(settings, system, response);
        Remember(result.StillDifferent);
        return result;
    }

    /// <summary>The user closed the offer without putting anything back: these differences are not offered again.</summary>
    internal void Dismiss(DriftReport report) => Remember(report.Ids);

    private ChangeResult Reapply(SettingDefinition setting)
    {
        try { return services.Calm.Reapply(setting); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return new ChangeResult(setting, ChangeOutcome.Failed, error.Message);
        }
    }

    /// <summary>Drops differences the dark mode schedule makes on purpose.</summary>
    internal static DriftReport Filter(DriftReport report, AppSettings settings) => settings.DarkModeSchedule
        ? report with { Settings = [.. report.Settings.Where(s => !ScheduledIds.Contains(s.Id, StringComparer.Ordinal))] }
        : report;

    /// <summary>True when <paramref name="ids"/> holds something not in the stored list.</summary>
    internal static bool IsNew(IEnumerable<string> ids, string? reported)
    {
        HashSet<string> seen = [.. (AppSettings.NormalizeIdList(reported) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)];
        return ids.Any(id => !seen.Contains(id.Trim()));
    }

    /// <summary>"A", "A and B", "A, B and C", "A, B, C and 2 more".</summary>
    internal static string List(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        <= 3 => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more"
    };

    private void Remember(IEnumerable<string> ids)
    {
        string? key = AppSettings.NormalizeIdList(string.Join("|", ids));
        if (key == services.Settings.Current.ReportedDrift) return;
        services.TryUpdateSettingsAutomatically(s => s with { ReportedDrift = key });
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        first?.Stop();
    }
}
