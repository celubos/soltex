using System.Globalization;
using System.Runtime.InteropServices;

namespace Soltex.Customize;

/// <summary>What the system engine needs from Windows. The real implementation needs administrator rights to write.</summary>
public interface ISystemHost
{
    RegistryValue Read(RegistryHive2 hive, string path, string name);
    void Write(RegistryHive2 hive, string path, string name, RegistryValue value);
    /// <summary>Start type, or null when the service is not installed.</summary>
    int? ServiceStart(string service);
    void SetServiceStart(string service, int startType);
    /// <summary>Enabled state, or null when the task does not exist.</summary>
    bool? TaskEnabled(string taskPath);
    void SetTaskEnabled(string taskPath, bool enabled);
    bool FirewallRuleExists(string ruleName);
    void AddFirewallBlock(FirewallAction rule);
    void RemoveFirewallRule(string ruleName);
    /// <summary>Installed state, or null when the app is not available on this PC at all.</summary>
    bool? AppInstalled(RemoveAppAction app);
    void RemoveApp(RemoveAppAction app);
    /// <summary>True when a browser other than Edge is installed (Edge is never removed without one).</summary>
    bool OtherBrowserInstalled();
}

public enum SystemItemState { NotApplied, Partial, Applied, NotPresent }

public sealed record SystemItemStatus(SystemItem Item, SystemItemState State, bool CanUndo);

public sealed record SystemResult(string ItemId, bool Ok, string Message);

/// <summary>
/// Applies and undoes <see cref="SystemCatalog"/> entries. Each action's previous state is journalled before it is
/// changed and read back after. Undo restores only what still holds this app's value, and only values of the kind the
/// action itself handles, because the journal is an ordinary file. App removals are final; an item's other changes
/// can still be undone.
/// </summary>
public sealed class SystemEngine
{
    /// <summary>Message of an item that was applied completely.</summary>
    public const string DoneMessage = "Done";

    /// <summary>Message of an item whose changes were all undone, with nothing else to report.</summary>
    public const string UndoneMessage = "Undone";

    private readonly ISystemHost host;
    private readonly ChangeJournal journal;
    private readonly Func<DateTimeOffset> clock;

    public SystemEngine(ISystemHost host, ChangeJournal journal, Func<DateTimeOffset>? clock = null)
    {
        this.host = host;
        this.journal = journal;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public SystemItemStatus Inspect(SystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        int present = 0, done = 0;
        foreach (SystemAction action in item.Actions)
        {
            if (Read(action) is not { } now) continue;
            present++;
            if (Same(now, Desired(action))) done++;
        }
        SystemItemState state = present == 0 ? SystemItemState.NotPresent : done == present ? SystemItemState.Applied : done == 0 ? SystemItemState.NotApplied : SystemItemState.Partial;
        return new SystemItemStatus(item, state, Undoable(item).Length > 0);
    }

    /// <summary>
    /// Items with a value this app set that no longer reads back as what it set (a Windows update turned something back
    /// on, or a removed app came back). Values whose last attempt failed, that were undone, or that the user chose to
    /// keep are not drift. Read-only; applying them again needs the administrator helper.
    /// </summary>
    public IReadOnlyList<SystemItemStatus> Drifted(IEnumerable<SystemItem> items) =>
        [.. items.Where(i => ChangedBack(i).Count > 0).Select(Inspect).Where(s => s.State is SystemItemState.NotApplied or SystemItemState.Partial)];

    /// <summary>
    /// Records that the user keeps what changed back for these items (for example an app they reinstalled on purpose),
    /// so it is no longer reported as drift. Returns how many values were marked.
    /// </summary>
    public int Keep(IEnumerable<SystemItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        int marked = 0;
        foreach (SystemItem item in items)
        {
            foreach ((JournalRecord change, RegistryValue now) in ChangedBack(item))
            {
                journal.Append(change with { Kind = JournalKind.Kept, Before = change.After, After = now, At = clock(), Note = "Kept as it is now." });
                marked++;
            }
        }
        return marked;
    }

    public IReadOnlyList<SystemResult> Apply(IEnumerable<SystemItem> items)
    {
        List<SystemResult> results = [];
        foreach (SystemItem item in items)
        {
            if (Refusal(item) is { } refusal)
            {
                results.Add(new(item.Id, false, refusal));
                continue;
            }
            Guid operation = Guid.NewGuid();
            List<string> failures = [];
            bool removalFailed = false;
            // Removals first: when an app could not be removed, the item's policies (which assume it is gone) are left alone.
            foreach (SystemAction action in item.Actions.OrderBy(a => a is RemoveAppAction ? 0 : 1))
            {
                if (removalFailed && action is not RemoveAppAction) continue;
                if (ApplyOne(operation, item, action) is not { } failure) continue;
                failures.Add(Describe(action) + ": " + failure);
                removalFailed |= action is RemoveAppAction;
            }
            results.Add(failures.Count == 0 ? new(item.Id, true, DoneMessage) : new(item.Id, false, string.Join("; ", failures)));
        }
        return results;
    }

    public IReadOnlyList<SystemResult> Undo(IEnumerable<SystemItem> items)
    {
        List<SystemResult> results = [];
        foreach (SystemItem item in items)
        {
            List<string> failures = [], notes = [];
            int restored = 0, kept = 0;
            foreach (JournalRecord change in Live(item.Id))
            {
                SystemAction? action = Match(item, change);
                if (action is RemoveAppAction) continue; // final; the result says how to get the app back
                if (action is null || !Same(change.After, Desired(action)))
                {
                    Skip(change, change.After, "Not a change this app makes; left as it is.");
                    continue;
                }
                if (!SystemCatalog.Restorable(action, change.Before))
                {
                    string note = action is PolicyAction p && SystemCatalog.WeakensUpdates(p.Path, p.Name, change.Before)
                        ? "Automatic updates stay on."
                        : "The earlier value is not one this app restores, so it was left as it is.";
                    Skip(change, change.After, note);
                    if (!notes.Contains(note)) notes.Add(note);
                    continue;
                }
                try
                {
                    if (Current(action) is not { } now)
                    {
                        Skip(change, change.After, "No longer on this PC.");
                        continue;
                    }
                    if (!Same(now, change.After))
                    {
                        Skip(change, now, "Changed since; kept.");
                        kept++;
                        continue;
                    }
                    Write(action, change.Before);
                    if (Current(action) is not { } back || !Same(back, change.Before)) throw new IOException("Windows did not keep the previous state.");
                    journal.Append(change with { Kind = JournalKind.Undone, Before = change.After, After = change.Before, At = clock(), Note = null });
                    restored++;
                }
                catch (Exception error) when (IsHostFailure(error))
                {
                    failures.Add(Describe(action) + ": " + error.Message);
                }
            }
            results.Add(UndoResult(item, failures, notes, restored, kept));
        }
        return results;
    }

    /// <summary>What this app changed at administrator level, newest first: one entry per operation and outcome.</summary>
    public IReadOnlyList<HistoryEntry> History(int limit = 200)
    {
        Dictionary<string, SystemItem> byId = SystemCatalog.All.ToDictionary(i => i.Id);
        return journal.Records
            .Where(r => r.Kind is JournalKind.Applied or JournalKind.Recovered or JournalKind.Failed or JournalKind.Undone or JournalKind.UndoSkipped or JournalKind.Kept)
            .GroupBy(r => (r.OperationId, r.Kind))
            .Select(g =>
            {
                JournalRecord first = g.First();
                string title = byId.TryGetValue(first.SettingId, out SystemItem? item) ? item.Title : first.SettingId;
                string summary = first.Kind switch
                {
                    JournalKind.Applied or JournalKind.Recovered => g.Any(r => r.Path.StartsWith("app:", StringComparison.Ordinal)) ? "Removed" : "Changed",
                    JournalKind.Failed => "Could not change: " + first.Note,
                    JournalKind.Undone => "Undone",
                    JournalKind.Kept => "Kept as it is now",
                    _ => first.Note?.TrimEnd('.') ?? "Undo kept a newer value"
                };
                return new HistoryEntry(first.OperationId, first.SettingId, title, g.Max(r => r.At), summary, CanUndo: false);
            })
            .OrderByDescending(h => h.At)
            .Take(limit)
            .ToArray();
    }

    /// <summary>Failures the host reports for one change; the elevated run records them and carries on with the next.</summary>
    internal static bool IsHostFailure(Exception error) =>
        error is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException
            or TimeoutException or ExternalException or AggregateException or ArgumentException or FormatException;

    private string? Refusal(SystemItem item)
    {
        if (item.Actions.OfType<RemoveAppAction>().FirstOrDefault(a => a.Kind == AppKind.Edge) is not { } edge) return null;
        try
        {
            return host.AppInstalled(edge) == true && !host.OtherBrowserInstalled() ? "Install another browser first; Edge is the only one on this PC." : null;
        }
        catch (Exception error) when (IsHostFailure(error))
        {
            return error.Message;
        }
    }

    /// <summary>Applies one action. Returns null when it is in place (or not on this PC), otherwise why it failed.</summary>
    private string? ApplyOne(Guid operation, SystemItem item, SystemAction action)
    {
        RegistryValue desired = Desired(action);
        RegistryValue? before;
        try { before = Current(action); }
        catch (Exception error) when (IsHostFailure(error)) { return error.Message; }
        if (before is null || Same(before.Value, desired)) return null;

        (string path, string name) = Key(action);
        journal.Append(new(JournalKind.Intent, operation, item.Id, path, name, before.Value, desired, clock()));
        string? failure = null;
        RegistryValue? after = null;
        try
        {
            Write(action, desired);
            after = Current(action);
        }
        catch (Exception error) when (IsHostFailure(error))
        {
            failure = error.Message;
        }
        // A removed Store app is simply no longer on this PC, which is the removal succeeding.
        if (failure is null && (after is { } observed ? Same(observed, desired) : action is RemoveAppAction))
        {
            journal.Append(new(JournalKind.Applied, operation, item.Id, path, name, before.Value, desired, clock()));
            return null;
        }
        failure ??= "Windows did not keep the change.";
        journal.Append(new(JournalKind.Failed, operation, item.Id, path, name, before.Value, desired, clock(), failure));
        return failure;
    }

    private static SystemResult UndoResult(SystemItem item, List<string> failures, List<string> notes, int restored, int kept)
    {
        if (failures.Count > 0) return new(item.Id, false, string.Join("; ", failures));
        List<string> extra = [.. notes];
        if (SystemCatalog.ReinstallHint(item) is { } reinstall) extra.Add(reinstall);
        static string With(string head, List<string> tail) => tail.Count == 0 ? head : head + ". " + string.Join(" ", tail);
        if (restored > 0) return new(item.Id, true, With(UndoneMessage, extra));
        if (kept > 0) return new(item.Id, true, With("Changed since; kept", extra));
        if (item.RemovesApps) return new(item.Id, false, string.Join(" ", extra));
        return new(item.Id, true, With("Nothing left to undo", extra));
    }

    private void Skip(JournalRecord change, RegistryValue now, string note) =>
        journal.Append(change with { Kind = JournalKind.UndoSkipped, Before = change.After, After = now, At = clock(), Note = note });

    /// <summary>The newest record of each value this app set for an item that no longer reads back as what it set.</summary>
    private List<(JournalRecord Change, RegistryValue Now)> ChangedBack(SystemItem item)
    {
        List<(JournalRecord, RegistryValue)> found = [];
        foreach (JournalRecord change in Latest(item.Id).Where(r => r.Kind is JournalKind.Applied or JournalKind.Recovered))
        {
            if (Match(item, change) is not { } action || Read(action) is not { } now || Same(now, change.After)) continue;
            found.Add((change, now));
        }
        return found;
    }

    /// <summary>Current state, or null when it is not on this PC. Throws what the host throws.</summary>
    private RegistryValue? Current(SystemAction action) => action switch
    {
        PolicyAction p => host.Read(p.Hive, p.Path, p.Name),
        ServiceAction s => host.ServiceStart(s.Service) is int start ? RegistryValue.Dword(start) : null,
        TaskAction t => host.TaskEnabled(t.TaskPath) is bool enabled ? RegistryValue.Dword(enabled ? 1 : 0) : null,
        FirewallAction f => RegistryValue.Dword(SystemCatalog.RuleNames(f).Any(host.FirewallRuleExists) ? 1 : 0),
        RemoveAppAction a => host.AppInstalled(a) is bool installed ? RegistryValue.Dword(installed ? 1 : 0) : null,
        _ => null
    };

    /// <summary>Current state for display: a value Windows could not report counts as not on this PC.</summary>
    private RegistryValue? Read(SystemAction action)
    {
        try { return Current(action); }
        catch (Exception error) when (IsHostFailure(error)) { return null; }
    }

    private static RegistryValue Desired(SystemAction action) => action switch
    {
        PolicyAction p => p.Value,
        ServiceAction s => RegistryValue.Dword(s.StartType),
        TaskAction t => RegistryValue.Dword(t.Enabled ? 1 : 0),
        FirewallAction => RegistryValue.Dword(1),
        _ => RegistryValue.Dword(0)
    };

    private void Write(SystemAction action, RegistryValue value)
    {
        switch (action)
        {
            case PolicyAction p:
                if (!SystemCatalog.AllowedPolicy(p.Hive, p.Path) || SystemCatalog.Forbidden(p.Path, p.Name))
                    throw new UnauthorizedAccessException("This location is outside what De-Windows may change.");
                if (SystemCatalog.WeakensUpdates(p.Path, p.Name, value))
                    throw new UnauthorizedAccessException("This would turn off automatic updates, so it is not changed.");
                host.Write(p.Hive, p.Path, p.Name, value);
                break;
            case ServiceAction s:
                host.SetServiceStart(s.Service, int.Parse(value.Text!, CultureInfo.InvariantCulture));
                break;
            case TaskAction t:
                host.SetTaskEnabled(t.TaskPath, value.Text == "1");
                break;
            case FirewallAction f:
                if (value.Text == "1") host.AddFirewallBlock(f);
                else
                {
                    foreach (string name in SystemCatalog.RuleNames(f))
                        if (host.FirewallRuleExists(name)) host.RemoveFirewallRule(name);
                }
                break;
            case RemoveAppAction a:
                if (value.Text != "0") throw new InvalidOperationException("Removed apps come back by reinstalling them.");
                host.RemoveApp(a);
                break;
        }
    }

    internal static (string Path, string Name) Key(SystemAction action) => action switch
    {
        PolicyAction p => ((p.Hive == RegistryHive2.LocalMachine ? @"HKLM\" : @"HKCU\") + p.Path, p.Name),
        ServiceAction s => ("service:" + s.Service, "Start"),
        TaskAction t => ("task:" + t.TaskPath, "Enabled"),
        FirewallAction f => ("firewall:" + f.RuleName, "Exists"),
        RemoveAppAction a => ("app:" + a.Kind + ":" + (a.PackageName ?? a.Name), "Installed"),
        _ => ("?", "?")
    };

    /// <summary>Every journal key an action may have been recorded under (firewall rules had another prefix before).</summary>
    private static IEnumerable<(string Path, string Name)> Keys(SystemAction action) =>
        action is FirewallAction f ? SystemCatalog.RuleNames(f).Select(n => ("firewall:" + n, "Exists")) : [Key(action)];

    private static SystemAction? Match(SystemItem item, JournalRecord record) =>
        item.Actions.FirstOrDefault(a => Keys(a).Contains((record.Path, record.Name)));

    private static string Describe(SystemAction action) => action switch
    {
        PolicyAction p => p.Name,
        ServiceAction s => s.Service + " service",
        TaskAction t => t.TaskPath[(t.TaskPath.LastIndexOf('\\') + 1)..] + " task",
        FirewallAction f => f.RuleName,
        RemoveAppAction a => a.Name,
        _ => "change"
    };

    /// <summary>Latest applied (and not undone, skipped or kept) record for each value of an item.</summary>
    private JournalRecord[] Live(string itemId) =>
        journal.Records
            .Where(r => r.SettingId == itemId && r.Kind is JournalKind.Applied or JournalKind.Recovered or JournalKind.Undone or JournalKind.UndoSkipped or JournalKind.Kept)
            .GroupBy(r => r.TargetKey)
            .Select(g => g.Last())
            .Where(r => r.Kind is JournalKind.Applied or JournalKind.Recovered)
            .ToArray();

    /// <summary>Latest completed record (including failures) for each value of an item.</summary>
    private IEnumerable<JournalRecord> Latest(string itemId) =>
        journal.Records
            .Where(r => r.SettingId == itemId && r.Kind is not (JournalKind.Intent or JournalKind.Abandoned or JournalKind.RolledBack))
            .GroupBy(r => r.TargetKey)
            .Select(g => g.Last());

    /// <summary>Live records this app can undo: a catalogue value it set, with an earlier value it may write back.</summary>
    private JournalRecord[] Undoable(SystemItem item) =>
        [.. Live(item.Id).Where(r => Match(item, r) is { } action && action is not RemoveAppAction
                                     && Same(r.After, Desired(action)) && SystemCatalog.Restorable(action, r.Before))];

    private static bool Same(RegistryValue a, RegistryValue b) =>
        a.Exists == b.Exists && (!a.Exists || (a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.Ordinal)));
}
