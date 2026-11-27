using System.Globalization;

namespace Soltex.Customize;

public enum ChangeOutcome { Applied, AlreadyInPlace, NotApplicable, Refused, Failed }
public enum UndoOutcome { Restored, ChangedSince, NothingToUndo, Failed }

/// <summary>What is true right now for one setting.</summary>
/// <param name="PolicyNotes">
/// Notes shown with the setting: the catalogue's fixed remark (<see cref="SettingDefinition.Note"/>) first, then a policy
/// that may also govern it.
/// </param>
public sealed record SettingState(
    SettingDefinition Definition,
    bool IsCalm,
    bool IsWindowsDefault,
    IReadOnlyList<RegistryValue> Current,
    IReadOnlyList<string> PolicyNotes,
    string? NotApplicable,
    bool CanUndo)
{
    /// <summary>Index into <see cref="SettingDefinition.Options"/> that matches what is set now, or -1 for a custom value.</summary>
    public int Choice { get; init; } = -1;
}

public sealed record ChangeResult(SettingDefinition Definition, ChangeOutcome Outcome, string Message)
{
    /// <summary>
    /// The change failed because Windows denied the write. Windows guards some values from apps (for example the
    /// Widgets button on 24H2 and later), so only Windows Settings can change them; <see cref="Message"/> says so plainly.
    /// </summary>
    public bool BlockedByWindows { get; init; }
}
public sealed record UndoResult(SettingDefinition Definition, UndoOutcome Outcome, string Message);

/// <summary>One operation as the user sees it in History.</summary>
public sealed record HistoryEntry(Guid OperationId, string SettingId, string Title, DateTimeOffset At, string Summary, bool CanUndo);

/// <summary>Sends Windows the "settings changed" broadcast so running shell components re-read values.</summary>
public interface ISettingsChangeNotifier
{
    void Notify();
}

/// <summary>
/// Inspect, apply, verify and undo per-user Windows settings. Each registry write is journalled before it happens and
/// read back after; a setting with several values is all-or-nothing within one operation. Undo restores only values
/// that still hold what this app wrote, so a newer change by the user or another tool is kept.
/// </summary>
public sealed class ChangeEngine
{
    private readonly IRegistryStore registry;
    private readonly ChangeJournal journal;
    private readonly ISettingsChangeNotifier? notifier;
    private readonly int windowsBuild;
    private readonly Func<DateTimeOffset> clock;
    private bool? home;

    /// <summary>What a change or undo says when Windows denied the write, instead of the raw access-denied error.</summary>
    public const string BlockedByWindowsMessage = "Windows doesn't let apps change this. Use Windows Settings instead.";

    /// <summary>What a change says when a value is stored in a form this app does not change (so undo could not put it back).</summary>
    public const string UnchangeableMessage = "This is stored in a form this app does not change, so it was left as it is.";

    /// <summary>Access denied: Windows (or a driver guarding the value) refused the write to this user's own key.</summary>
    private static bool IsAccessDenied(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException;

    public ChangeEngine(IRegistryStore registry, ChangeJournal journal, int windowsBuild, ISettingsChangeNotifier? notifier = null, Func<DateTimeOffset>? clock = null)
    {
        this.registry = registry;
        this.journal = journal;
        this.notifier = notifier;
        this.windowsBuild = windowsBuild;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        RecoveredOnStart = Reconcile();
    }

    /// <summary>Number of interrupted changes found and resolved when the engine started.</summary>
    public int RecoveredOnStart { get; }

    public SettingState Inspect(SettingDefinition definition)
    {
        RegistryValue[] current = definition.Targets.Select(t => registry.Read(RegistryHive2.CurrentUser, t.Path, t.Name)).ToArray();
        bool calm = definition.Targets.Select((t, i) => Matches(t, current[i], t.Calm)).All(x => x);
        bool windowsDefault = definition.Targets.Select((t, i) => Matches(t, current[i], t.WindowsDefault)).All(x => x);
        IReadOnlyList<SettingChoice> options = definition.Options;
        int choice = -1;
        for (int c = 0; c < options.Count && choice < 0; c++)
            if (definition.Targets.Select((t, i) => Matches(t, current[i], options[c].Values[i])).All(x => x)) choice = c;
        List<string> notes = [];
        if (definition.Note is { Length: > 0 } note) notes.Add(note);
        if (definition.Policies.Any(hint => registry.Read(hint.Hive, hint.Path, hint.Name).Exists))
        {
            notes.Add(IsHomeEdition()
                ? "A policy for this is also set; Windows Home may ignore it."
                : "A policy on this PC controls this, so a change here may not take effect.");
        }
        string? notApplicable = definition.MinimumBuild > 0 && windowsBuild > 0 && windowsBuild < definition.MinimumBuild
            ? "Needs a newer version of Windows 11."
            : null;
        return new SettingState(definition, calm, windowsDefault, current, notes, notApplicable, LiveChanges(definition.Id).Length > 0) { Choice = choice };
    }

    public IReadOnlyList<SettingState> InspectAll(IEnumerable<SettingDefinition> definitions) => definitions.Select(Inspect).ToArray();

    /// <summary>Windows Home editions (EditionID Core…) mostly ignore policies; other editions apply them over the setting.</summary>
    private bool IsHomeEdition() => home ??= registry.Read(RegistryHive2.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID")
        is { Exists: true, Text: { } edition } && edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    public ChangeResult Apply(SettingDefinition definition, bool calm) => Apply(definition, calm, notify: true);

    private ChangeResult Apply(SettingDefinition definition, bool calm, bool notify)
    {
        SettingState state = Inspect(definition);
        if (state.NotApplicable is { } reason) return new(definition, ChangeOutcome.NotApplicable, reason);
        if (calm ? state.IsCalm : state.IsWindowsDefault) return new(definition, ChangeOutcome.AlreadyInPlace, "Already set.");
        return Write(definition, definition.Targets.Select(t => (RegistryValue?)(calm ? t.Calm : t.WindowsDefault)).ToArray(), notify,
            calm ? "Changed. " + definition.When + "." : "Back to the Windows default. " + definition.When + ".");
    }

    /// <summary>Selects one of the setting's options (a switch: 0 = off, 1 = on).</summary>
    public ChangeResult ApplyChoice(SettingDefinition definition, int choice)
    {
        ArgumentNullException.ThrowIfNull(definition);
        IReadOnlyList<SettingChoice> options = definition.Options;
        ArgumentOutOfRangeException.ThrowIfNegative(choice);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(choice, options.Count);
        SettingState state = Inspect(definition);
        if (state.NotApplicable is { } reason) return new(definition, ChangeOutcome.NotApplicable, reason);
        if (state.Choice == choice) return new(definition, ChangeOutcome.AlreadyInPlace, "Already set.");
        return Write(definition, [.. options[choice].Values.Select(v => (RegistryValue?)v)], notify: true, "Changed. " + definition.When + ".");
    }

    /// <summary>
    /// Writes the setting's values (<paramref name="values"/> in target order; null leaves that value alone). A masked
    /// target gets its bits merged into the stored value. Nothing is written when any value is stored in a form this app
    /// does not change.
    /// </summary>
    private ChangeResult Write(SettingDefinition definition, RegistryValue?[] values, bool notify, string success)
    {
        if (definition.Targets.Any(t => NeverTouch.IsDenied(RegistryHive2.CurrentUser, t.Path, t.Name)))
            return new(definition, ChangeOutcome.Refused, "This affects security or updates, so it is not changed.");

        List<(RegistryTarget Target, RegistryValue Before, RegistryValue Desired)> plan = [];
        for (int index = 0; index < definition.Targets.Count; index++)
        {
            RegistryTarget target = definition.Targets[index];
            if (values[index] is not { } wanted) continue;
            RegistryValue before = registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name);
            if (Matches(target, before, wanted)) continue;
            // A value of a type this app never writes could not be put back by undo, so it is not changed at all.
            if (before is { Exists: true, Kind: ValueKind.Unsupported } || Compose(target, before, wanted) is not { } desired)
                return new(definition, ChangeOutcome.Refused, UnchangeableMessage);
            plan.Add((target, before, desired));
        }

        Guid operation = Guid.NewGuid();
        List<(RegistryTarget Target, RegistryValue Before, RegistryValue After)> done = [];
        foreach ((RegistryTarget target, RegistryValue before, RegistryValue desired) in plan)
        {
            journal.Append(new(JournalKind.Intent, operation, definition.Id, target.Path, target.Name, before, desired, clock()));
            string? failure = null;
            bool blocked = false;
            try { registry.Write(target.Path, target.Name, desired); }
            catch (Exception error) when (IsAccessDenied(error))
            {
                failure = BlockedByWindowsMessage;
                blocked = true;
            }
            catch (IOException error)
            {
                failure = error.Message;
            }
            RegistryValue observed = registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name);
            if (failure is null && Same(observed, desired))
            {
                journal.Append(new(JournalKind.Applied, operation, definition.Id, target.Path, target.Name, before, desired, clock()));
                done.Add((target, before, desired));
                continue;
            }

            failure ??= "Windows did not keep the new value (it read back as " + observed + ").";
            journal.Append(new(JournalKind.Failed, operation, definition.Id, target.Path, target.Name, before, desired, clock(), failure));
            RollBack(operation, definition, done);
            if (notify) notifier?.Notify();
            return new(definition, ChangeOutcome.Failed, failure) { BlockedByWindows = blocked };
        }
        if (notify) notifier?.Notify();
        return new(definition, ChangeOutcome.Applied, success);
    }

    /// <summary>Applies several settings, broadcasting the change to Windows once at the end.</summary>
    public IReadOnlyList<ChangeResult> ApplyMany(IEnumerable<SettingDefinition> definitions, bool calm)
    {
        ChangeResult[] results = definitions.Select(d => Apply(d, calm, notify: false)).ToArray();
        if (results.Any(r => r.Outcome is ChangeOutcome.Applied or ChangeOutcome.Failed)) notifier?.Notify();
        return results;
    }

    private void RollBack(Guid operation, SettingDefinition definition, List<(RegistryTarget Target, RegistryValue Before, RegistryValue After)> done)
    {
        foreach ((RegistryTarget target, RegistryValue before, RegistryValue after) in Enumerable.Reverse(done))
        {
            RegistryValue now = registry.Read(RegistryHive2.CurrentUser, target.Path, target.Name);
            if (!Same(now, after)) continue; // someone changed it in between; keep theirs
            try { registry.Write(target.Path, target.Name, before); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { continue; }
            journal.Append(new(JournalKind.RolledBack, operation, definition.Id, target.Path, target.Name, after, before, clock(), "Rolled back because another value of this setting failed."));
        }
    }

    /// <summary>Undoes this app's latest change to a setting, value by value, keeping anything changed since.</summary>
    public UndoResult Undo(SettingDefinition definition) => Undo(definition, notify: true);

    /// <summary>Undoes every setting this app changed, broadcasting once.</summary>
    public IReadOnlyList<UndoResult> UndoMany(IEnumerable<SettingDefinition> definitions)
    {
        UndoResult[] results = definitions.Select(d => Undo(d, notify: false)).ToArray();
        if (results.Any(r => r.Outcome is UndoOutcome.Restored or UndoOutcome.Failed)) notifier?.Notify();
        return results;
    }

    private UndoResult Undo(SettingDefinition definition, bool notify)
    {
        JournalRecord[] live = LiveChanges(definition.Id);
        if (live.Length == 0) return new(definition, UndoOutcome.NothingToUndo, "Nothing to undo.");
        int restored = 0, kept = 0;
        string? failure = null;
        foreach (JournalRecord change in live)
        {
            RegistryTarget? target = TargetOf(definition, change);
            RegistryValue now = registry.Read(RegistryHive2.CurrentUser, change.Path, change.Name);
            if (!Holds(target, now, change.After))
            {
                journal.Append(change with { Kind = JournalKind.UndoSkipped, Before = change.After, After = now, At = clock(), Note = "Changed since; the newer value was kept." });
                kept++;
                continue;
            }
            // Normally exactly what was there before. When only other bits of a masked value changed since, just this
            // setting's bits go back, so the newer bits are kept.
            RegistryValue restore = change.Before;
            if (target is { Mask: not 0 } masked && !Same(now, change.After) && Bits(masked, change.Before) is long bits)
                restore = Compose(masked, now, RegistryValue.Dword(unchecked((int)bits))) ?? change.Before;
            try
            {
                registry.Write(change.Path, change.Name, restore);
                if (!Same(registry.Read(RegistryHive2.CurrentUser, change.Path, change.Name), restore))
                    throw new IOException("Windows did not keep the previous value.");
                journal.Append(change with { Kind = JournalKind.Undone, Before = now, After = restore, At = clock(), Note = null });
                restored++;
            }
            catch (Exception error) when (IsAccessDenied(error) || error is IOException)
            {
                failure = IsAccessDenied(error) ? BlockedByWindowsMessage : error.Message;
            }
        }
        if (notify) notifier?.Notify();
        if (failure is not null) return new(definition, UndoOutcome.Failed, "Could not undo: " + failure);
        if (restored == 0) return new(definition, UndoOutcome.ChangedSince, "This was changed since by you or another app, so the newer value was kept.");
        return new(definition, UndoOutcome.Restored, kept > 0 ? "Partly undone; a value changed since was kept." : "Undone.");
    }

    /// <summary>Settings that this app changed and that are not undone yet.</summary>
    public IReadOnlyList<SettingDefinition> UndoableSettings(IEnumerable<SettingDefinition> catalogue) =>
        catalogue.Where(d => LiveChanges(d.Id).Length > 0).ToArray();

    public IReadOnlyList<HistoryEntry> History(IEnumerable<SettingDefinition> catalogue, int limit = 200)
    {
        Dictionary<string, SettingDefinition> byId = catalogue.ToDictionary(d => d.Id);
        IReadOnlyList<JournalRecord> records = journal.Records;
        // A value written and then rolled back because another value of the same setting failed was never really changed.
        HashSet<(Guid, string)> rolledBack = [.. records.Where(r => r.Kind == JournalKind.RolledBack).Select(r => (r.OperationId, r.TargetKey))];
        return records
            .Where(r => r.Kind is JournalKind.Applied or JournalKind.Failed or JournalKind.Undone or JournalKind.UndoSkipped or JournalKind.Recovered)
            .Where(r => r.Kind != JournalKind.Applied || !rolledBack.Contains((r.OperationId, r.TargetKey)))
            .GroupBy(r => (r.OperationId, r.Kind))
            .Select(g =>
            {
                JournalRecord first = g.First();
                string title = byId.TryGetValue(first.SettingId, out SettingDefinition? d) ? d.Title : first.SettingId;
                string summary = first.Kind switch
                {
                    JournalKind.Applied or JournalKind.Recovered => "Changed",
                    JournalKind.Failed => "Could not change: " + first.Note,
                    JournalKind.Undone => "Undone",
                    _ => "Undo kept a newer value"
                };
                bool canUndo = first.Kind is JournalKind.Applied or JournalKind.Recovered &&
                    LiveChanges(first.SettingId).Any(c => c.OperationId == first.OperationId);
                return new HistoryEntry(first.OperationId, first.SettingId, title, g.Max(r => r.At), summary, canUndo);
            })
            .OrderByDescending(h => h.At)
            .Take(limit)
            .ToArray();
    }

    /// <summary>
    /// Settings this app changed (and did not undo) whose values were changed back since, usually by a Windows update.
    /// </summary>
    public IReadOnlyList<SettingDefinition> Drifted(IEnumerable<SettingDefinition> catalogue) =>
        [.. catalogue.Where(d => LiveChanges(d.Id) is { Length: > 0 } live
                                 && live.Any(r => !Holds(TargetOf(d, r), registry.Read(RegistryHive2.CurrentUser, r.Path, r.Name), r.After)))];

    /// <summary>Writes this app's last values for a drifted setting again (journalled and undoable like any change).</summary>
    public ChangeResult Reapply(SettingDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        JournalRecord[] live = LiveChanges(definition.Id);
        if (live.Length == 0) return new(definition, ChangeOutcome.AlreadyInPlace, "Nothing to put back.");
        // Values this app did not change (or whose change was undone) are left as they are.
        RegistryValue?[] values = [.. definition.Targets.Select(t =>
            live.LastOrDefault(r => IsTarget(r, t)) is { } record ? Logical(t, record.After) : (RegistryValue?)null)];
        return Write(definition, values, notify: true, "Put back.");
    }

    /// <summary>Latest applied (and not undone, rolled back or superseded) record for each value of a setting.</summary>
    private JournalRecord[] LiveChanges(string settingId) =>
        journal.Records
            .Where(r => r.SettingId == settingId && r.Kind is JournalKind.Applied or JournalKind.Recovered or JournalKind.Undone or JournalKind.UndoSkipped or JournalKind.RolledBack)
            .GroupBy(r => r.TargetKey)
            .Select(g => g.Last())
            .Where(r => r.Kind is JournalKind.Applied or JournalKind.Recovered)
            .ToArray();

    /// <summary>Resolves intents left without completion by a crash: the value on disk decides what happened.</summary>
    private int Reconcile()
    {
        int resolved = 0;
        IReadOnlyList<JournalRecord> all = journal.Records;
        HashSet<(Guid, string)> completed = all.Where(r => r.Kind != JournalKind.Intent).Select(r => (r.OperationId, r.TargetKey)).ToHashSet();
        foreach (JournalRecord intent in all.Where(r => r.Kind == JournalKind.Intent && !completed.Contains((r.OperationId, r.TargetKey))))
        {
            RegistryValue now = registry.Read(RegistryHive2.CurrentUser, intent.Path, intent.Name);
            journal.Append(Same(now, intent.After)
                ? intent with { Kind = JournalKind.Recovered, At = clock(), Note = "The app stopped during this change; the change had taken effect." }
                : intent with { Kind = JournalKind.Abandoned, At = clock(), Note = "The app stopped before this change took effect." });
            resolved++;
        }
        return resolved;
    }

    /// <summary>Same value, treating "not set" as the value Windows assumes when that is known.</summary>
    private static bool Equivalent(RegistryValue current, RegistryValue wanted, RegistryValue? implied) =>
        Same(current, wanted) || (implied is { } assumed && Same(current.Exists ? current : assumed, wanted.Exists ? wanted : assumed));

    /// <summary>
    /// Whether the stored value gives the setting the value <paramref name="wanted"/>: an equivalent value, or for a
    /// masked target the same bits (counting "not set" as the whole value Windows assumes).
    /// </summary>
    private static bool Matches(RegistryTarget target, RegistryValue stored, RegistryValue wanted)
    {
        if (target.Mask == 0 || !wanted.Exists) return Equivalent(stored, wanted, target.Implied);
        return Bits(target, stored) is long bits && Number(wanted) is long value && bits == (value & target.Mask);
    }

    /// <summary>
    /// Whether a value still holds what this app wrote: equivalent to it, or for a masked target the same bits (other
    /// bits may have changed since). A value of a target the setting no longer has must be exactly the same.
    /// </summary>
    private static bool Holds(RegistryTarget? target, RegistryValue now, RegistryValue written)
    {
        if (target is null) return Same(now, written);
        if (target.Mask == 0) return Equivalent(now, written, target.Implied);
        return Bits(target, now) is long a && Bits(target, written) is long b ? a == b : Same(now, written);
    }

    /// <summary>The value to store so the target gives <paramref name="wanted"/>; null when a masked value cannot be read as a number.</summary>
    private static RegistryValue? Compose(RegistryTarget target, RegistryValue before, RegistryValue wanted)
    {
        if (target.Mask == 0 || !wanted.Exists) return wanted;
        RegistryValue basis = before.Exists ? before : target.Implied ?? RegistryValue.Absent;
        if (Number(basis) is not long number || Number(wanted) is not long bits) return null;
        long next = (number & ~(long)target.Mask) | (bits & target.Mask);
        // Written back in the value's own type: decimal text stays text.
        return basis.Kind switch
        {
            ValueKind.DWord => RegistryValue.Dword(unchecked((int)next)),
            ValueKind.QWord => RegistryValue.Qword(next),
            _ => RegistryValue.FromText(next.ToString(CultureInfo.InvariantCulture))
        };
    }

    /// <summary>The setting's own value of a stored value: its bits for a masked target, the value itself otherwise.</summary>
    private static RegistryValue Logical(RegistryTarget target, RegistryValue stored) =>
        target.Mask != 0 && stored.Exists && Bits(target, stored) is long bits ? RegistryValue.Dword(unchecked((int)bits)) : stored;

    /// <summary>The target's bits of a stored value (or of the value Windows assumes when none is stored); null when it is not a number.</summary>
    private static long? Bits(RegistryTarget target, RegistryValue stored) =>
        Number(stored.Exists ? stored : target.Implied ?? RegistryValue.Absent) is long number ? number & target.Mask : null;

    /// <summary>A DWORD, QWORD or decimal text value as a number; null otherwise.</summary>
    private static long? Number(RegistryValue value) =>
        value.Exists && value.Kind is ValueKind.DWord or ValueKind.QWord or ValueKind.Text
        && long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) ? number : null;

    private static RegistryTarget? TargetOf(SettingDefinition definition, JournalRecord record) =>
        definition.Targets.FirstOrDefault(t => IsTarget(record, t));

    private static bool IsTarget(JournalRecord record, RegistryTarget target) =>
        string.Equals(record.Path, target.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(record.Name, target.Name, StringComparison.OrdinalIgnoreCase);

    private static bool Same(RegistryValue a, RegistryValue b) =>
        a.Exists == b.Exists && (!a.Exists || (a.Kind == b.Kind && string.Equals(a.Text, b.Text, StringComparison.Ordinal)));
}
