using System.Diagnostics;
using System.Runtime.InteropServices;
using Soltex.Customize;

// All checks use an in-memory registry and fake system host. No Windows setting is changed by this suite; two checks
// only read the real state (per-user catalogue keys, and the full De-Windows items without administrator rights).
List<(string Name, Action Test)> tests =
[
    ("Catalogue is well formed and never targets protected areas", CatalogueIsSafe),
    ("Never-touch guard refuses security, update and integrity values", NeverTouchRefuses),
    ("Apply is idempotent and journals intent before completion", ApplyIsIdempotentAndJournalled),
    ("Apply back to Windows default removes the value", ApplyDefaultRemovesValue),
    ("A write that does not stick is reported failed, not applied", VerifyCatchesFailedWrite),
    ("A multi-value setting rolls back when one value fails", MultiValueRollsBack),
    ("Undo restores the previous value and absence", UndoRestoresPrevious),
    ("Undo keeps a newer change made by the user or another app", UndoKeepsNewerChange),
    ("An interrupted change is reconciled from what is on disk", CrashIsReconciled),
    ("Torn journal lines are skipped and history survives a restart", TornJournalTolerated),
    ("Policy hints are reported, not trusted, and builds gate settings", PolicyAndBuildReported),
    ("History lists operations newest first with undo availability", HistoryIsAccurate),
    ("Journal compaction keeps undo possible for old changes", CompactionKeepsUndo),
    ("Real registry read of every catalogue key is read-only and bounded", RealRegistryReadIsReadOnly),
    ("Switches read Windows' implied value when nothing is set", SwitchUsesImpliedValue),
    ("A choice can return to the Windows default by removing the value", ChoiceDefaultRemovesValue),
    ("Undo after a choice restores exactly what was there", UndoAfterChoice),
    ("Every De-Windows profile value is one of the setting's own options", ProfileValuesAreOptions),
    ("System catalogue stays inside policy locations and never weakens protection", SystemCatalogueIsSafe),
    ("System apply journals, reads back and undo restores every kind of change", SystemApplyAndUndo),
    ("System undo keeps later changes and never claims to restore removed apps", SystemUndoBoundaries),
    ("Edge is never removed when it is the only browser", EdgeNeedsAnotherBrowser),
    ("Real system state is readable without administrator rights", RealSystemInspectIsReadOnly),
    ("Drift: a setting changed back is found and put back; undone ones are not drift", SettingDriftIsFoundAndPutBack),
    ("Drift: a De-Windows item turned back on is found; undone items are not", SystemDriftIsFound),
    ("History leaves out values that were rolled back", HistoryOmitsRolledBack),
    ("Privacy switches are named after the feature: On shows it, Off is the De-Windows value", CalmSwitchesArePositive),
    ("A host failure on one system change is recorded and the next items still run", SystemApplySurvivesHostFailures),
    ("A failed app removal leaves the item's policy alone; a done one can undo its policy", RemovalPolicyIsUndoable),
    ("System undo never writes a forged journal value or turns automatic updates off", SystemUndoRejectsForgedValues),
    ("System undo of a task that no longer exists stops being undoable", SystemUndoOfMissingTarget),
    ("System drift ignores failed parts and a kept reinstall", SystemDriftIsPerValue),
    ("Firewall rules from earlier versions are recognised and undone", LegacyFirewallRulesAreRecognised),
    ("System history lists changes and removals", SystemHistoryIsListed)
];

int failed = 0;
Stopwatch suite = Stopwatch.StartNew();
foreach ((string name, Action test) in tests)
{
    Stopwatch timer = Stopwatch.StartNew();
    try { test(); Console.WriteLine($"PASS  {name} ({timer.Elapsed.TotalMilliseconds:F1} ms)"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL  {name}: {error.Message}"); }
}
Console.WriteLine();
Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed. In-memory registry only; no Windows setting changed.");
Console.WriteLine($"MEASURE customize_suite tests={tests.Count} failed={failed} total_ms={suite.Elapsed.TotalMilliseconds:F1}");
return failed == 0 ? 0 : 1;

static void CatalogueIsSafe()
{
    Check(CalmCatalog.All.Count >= 25, "The v1 catalogue lost rows.");
    Check(CalmCatalog.All.Select(d => d.Id).Distinct().Count() == CalmCatalog.All.Count, "Setting ids must be unique.");
    foreach (SettingDefinition d in CalmCatalog.All)
    {
        Check(d.Targets.Count > 0 && d.Title.Length > 0, d.Id + " is incomplete.");
        Check(d.Options.Count >= 2 && d.Options.All(o => o.Values.Count == d.Targets.Count && o.Label.Length > 0), d.Id + " has malformed options.");
        Check(!d.IsSwitch || (d.Options.Count == 2 && d.Options[0].Label == "Off" && d.Options[1].Label == "On"), d.Id + " switch must be [Off, On].");
        foreach (RegistryTarget t in d.Targets)
        {
            Check(!NeverTouch.IsDenied(RegistryHive2.CurrentUser, t.Path, t.Name), d.Id + " targets a protected value.");
            Check(!t.Path.StartsWith("Software\\Policies", StringComparison.OrdinalIgnoreCase), d.Id + " must not write a policy key.");
            Check(!d.InCalmProfile || t.Calm.Exists || d.Options.Any(o => !o.Values[d.Targets.ToList().IndexOf(t)].Exists), d.Id + " has no calm value.");
        }
    }
    Check(CalmCatalog.All.Count(d => d.InCalmProfile) >= 20, "The default Calm profile should cover most rows.");
    Check(CalmCatalog.All.Count >= 55, "The catalogue should cover taskbar, Start, File Explorer, appearance and multitasking (" + CalmCatalog.All.Count + ").");
}

static void NeverTouchRefuses()
{
    (string Path, string Name)[] denied =
    [
        (@"Software\Policies\Microsoft\Windows Defender\Spynet", "SpynetReporting"),
        (@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen"),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA"),
        (@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate"),
        (@"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState"),
        (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden"),
        (@"SOFTWARE\Policies\Microsoft\WindowsFirewall\DomainProfile", "EnableFirewall")
    ];
    foreach ((string path, string name) in denied)
        Check(NeverTouch.IsDenied(RegistryHive2.LocalMachine, path, name), "Not refused: " + path + "\\" + name);
    Check(!NeverTouch.IsDenied(RegistryHive2.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SystemSettings\AccountNotifications", "EnableAccountNotifications"),
        "A key-name prefix must not over-match (SystemSettings is not System).");
    MemoryRegistry registry = new();
    try { registry.Write(@"Software\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", RegistryValue.Dword(1)); Check(false, "Guarded write succeeded."); }
    catch (UnauthorizedAccessException) { }
    try { new WindowsRegistryStore().Write(@"Software\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", RegistryValue.Dword(1)); Check(false, "Real store accepted a guarded write."); }
    catch (UnauthorizedAccessException) { }
}

static void ApplyIsIdempotentAndJournalled()
{
    (ChangeEngine engine, MemoryRegistry registry, ChangeJournal journal, _) = New();
    SettingDefinition widgets = Find("taskbar.widgets");
    ChangeResult first = engine.Apply(widgets, calm: true);
    Check(first.Outcome == ChangeOutcome.Applied, "First apply: " + first.Message);
    Check(registry.Get(widgets.Targets[0]) == RegistryValue.Dword(0), "Value was not written.");
    Check(engine.Inspect(widgets).IsCalm, "Inspect does not see the change.");
    JournalKind[] kinds = journal.Records.Select(r => r.Kind).ToArray();
    Check(kinds.SequenceEqual([JournalKind.Intent, JournalKind.Applied]), "Intent must precede completion: " + string.Join(",", kinds));
    ChangeResult second = engine.Apply(widgets, calm: true);
    Check(second.Outcome == ChangeOutcome.AlreadyInPlace && journal.Records.Count == 2, "A second apply must not write again.");
}

static void ApplyDefaultRemovesValue()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition recs = Find("start.recommendations");
    registry.Set(recs.Targets[0], RegistryValue.Dword(0)); // calm already, set by someone else
    ChangeResult result = engine.Apply(recs, calm: false);
    Check(result.Outcome == ChangeOutcome.Applied && !registry.Get(recs.Targets[0]).Exists, "Default should remove the value.");
    SettingDefinition ext = Find("explorer.extensions");
    registry.Set(ext.Targets[0], RegistryValue.Dword(0)); // extensions shown
    engine.Apply(ext, calm: false);
    Check(registry.Get(ext.Targets[0]) == RegistryValue.Dword(1), "An explicit Windows default must be written as a value.");
}

static void VerifyCatchesFailedWrite()
{
    (ChangeEngine engine, MemoryRegistry registry, ChangeJournal journal, _) = New();
    SettingDefinition widgets = Find("taskbar.widgets");
    registry.Ignore.Add(widgets.Targets[0].Name); // like a protection driver discarding the write
    ChangeResult result = engine.Apply(widgets, calm: true);
    Check(result.Outcome == ChangeOutcome.Failed, "A discarded write must fail.");
    Check(journal.Records[^1].Kind == JournalKind.Failed && !engine.Inspect(widgets).CanUndo, "A failed change must be journalled and not be undoable.");
    registry.Throw.Add("Enabled");
    ChangeResult denied = engine.Apply(Find("privacy.inking"), calm: true);
    Check(denied.Outcome == ChangeOutcome.Failed && denied.Message.Contains("denied", StringComparison.OrdinalIgnoreCase), "Access denied must surface.");
}

static void MultiValueRollsBack()
{
    (ChangeEngine engine, MemoryRegistry registry, ChangeJournal journal, _) = New();
    SettingDefinition settingsTips = Find("tips.settings");
    registry.Ignore.Add(settingsTips.Targets[2].Name);
    ChangeResult result = engine.Apply(settingsTips, calm: true);
    Check(result.Outcome == ChangeOutcome.Failed, "Partial multi-value change must fail.");
    Check(settingsTips.Targets.Take(2).All(t => !registry.Get(t).Exists), "Values already written in this operation must be rolled back.");
    Check(journal.Records.Count(r => r.Kind == JournalKind.RolledBack) == 2, "Rollback must be journalled.");
    Check(!engine.Inspect(settingsTips).CanUndo, "Nothing is left to undo after a rollback.");
}

static void HistoryOmitsRolledBack()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition settingsTips = Find("tips.settings");
    registry.Ignore.Add(settingsTips.Targets[2].Name);
    Check(engine.Apply(settingsTips, calm: true).Outcome == ChangeOutcome.Failed, "Setup: the change must fail.");
    IReadOnlyList<HistoryEntry> history = engine.History(CalmCatalog.All);
    Check(history is [{ } only] && only.Summary.StartsWith("Could not change", StringComparison.Ordinal) && !only.CanUndo,
        "A rolled-back attempt must show once, as not changed: " + string.Join("; ", history.Select(h => h.Summary)));
}

static void UndoRestoresPrevious()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition lockScreen = Find("tips.lockscreen");
    registry.Set(lockScreen.Targets[0], RegistryValue.Dword(1)); // first value existed, second absent
    engine.Apply(lockScreen, calm: true);
    UndoResult undo = engine.Undo(lockScreen);
    Check(undo.Outcome == UndoOutcome.Restored, "Undo: " + undo.Message);
    Check(registry.Get(lockScreen.Targets[0]) == RegistryValue.Dword(1) && !registry.Get(lockScreen.Targets[1]).Exists,
        "Undo must restore both the old value and the old absence.");
    Check(engine.Undo(lockScreen).Outcome == UndoOutcome.NothingToUndo, "A second undo has nothing to do.");
}

static void UndoKeepsNewerChange()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition search = Find("taskbar.search");
    registry.Set(search.Targets[0], RegistryValue.Dword(2));
    engine.Apply(search, calm: true);
    registry.Set(search.Targets[0], RegistryValue.Dword(3)); // the user picked "icon and label" afterwards
    UndoResult undo = engine.Undo(search);
    Check(undo.Outcome == UndoOutcome.ChangedSince, "Undo must not overwrite a newer choice.");
    Check(registry.Get(search.Targets[0]) == RegistryValue.Dword(3), "The newer value was overwritten.");
    Check(!engine.Inspect(search).CanUndo, "A kept value is no longer undoable.");
}

static void CrashIsReconciled()
{
    string path = TempJournal();
    MemoryRegistry registry = new();
    SettingDefinition widgets = Find("taskbar.widgets"), taskView = Find("taskbar.taskview");
    ChangeJournal journal = new(path);
    // Simulate two crashes: one after the write, one before it.
    journal.Append(new(JournalKind.Intent, Guid.NewGuid(), widgets.Id, widgets.Targets[0].Path, widgets.Targets[0].Name, RegistryValue.Absent, RegistryValue.Dword(0), DateTimeOffset.UtcNow));
    registry.Set(widgets.Targets[0], RegistryValue.Dword(0));
    journal.Append(new(JournalKind.Intent, Guid.NewGuid(), taskView.Id, taskView.Targets[0].Path, taskView.Targets[0].Name, RegistryValue.Absent, RegistryValue.Dword(0), DateTimeOffset.UtcNow));
    ChangeEngine engine = new(registry, new ChangeJournal(path), 26300);
    Check(engine.RecoveredOnStart == 2, "Both interrupted changes must be resolved.");
    Check(engine.Inspect(widgets).CanUndo && !engine.Inspect(taskView).CanUndo, "Only the change that took effect is undoable.");
    Check(engine.Undo(widgets).Outcome == UndoOutcome.Restored && !registry.Get(widgets.Targets[0]).Exists, "A recovered change must undo cleanly.");
    ChangeEngine again = new(registry, new ChangeJournal(path), 26300);
    Check(again.RecoveredOnStart == 0, "Reconciliation must not repeat.");
}

static void TornJournalTolerated()
{
    string path = TempJournal();
    MemoryRegistry registry = new();
    ChangeEngine engine = new(registry, new ChangeJournal(path), 26300);
    engine.Apply(Find("privacy.inking"), calm: true);
    File.AppendAllText(path, "{\"Kind\":\"App"); // torn final line
    ChangeJournal reloaded = new(path);
    Check(reloaded.SkippedLines == 1, "The torn line must be skipped, not fatal.");
    ChangeEngine restarted = new(registry, reloaded, 26300);
    Check(restarted.Inspect(Find("privacy.inking")).CanUndo, "History must survive the torn line.");
}

static void PolicyAndBuildReported()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New(build: 19045);
    SettingDefinition highlights = Find("search.highlights");
    PolicyHint hint = highlights.Policies[0];
    registry.SetAny(hint.Hive, hint.Path, hint.Name, RegistryValue.Dword(0));
    registry.SetAny(RegistryHive2.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", RegistryValue.FromText("Core"));
    SettingState state = engine.Inspect(highlights);
    Check(state.PolicyNotes is [{ } home] && home.Contains("Windows Home may ignore", StringComparison.Ordinal) && !home.Contains(hint.Feature, StringComparison.Ordinal),
        "On Home a set policy must be reported once, without repeating the row title.");
    Check(state.NotApplicable is not null, "A Windows 11 setting must not apply on build 19045.");
    Check(engine.Apply(highlights, calm: true).Outcome == ChangeOutcome.NotApplicable, "Inapplicable settings must not be written.");

    (ChangeEngine pro, MemoryRegistry proRegistry, _, _) = New();
    proRegistry.SetAny(hint.Hive, hint.Path, hint.Name, RegistryValue.Dword(0));
    proRegistry.SetAny(RegistryHive2.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID", RegistryValue.FromText("Professional"));
    Check(pro.Inspect(highlights).PolicyNotes is [{ } other] && other.Contains("may not take effect", StringComparison.Ordinal) && !other.Contains("Home", StringComparison.Ordinal),
        "On other editions the policy wins, and the note must say so.");
}

static void HistoryIsAccurate()
{
    (ChangeEngine engine, MemoryRegistry registry, _, ManualClock clock) = New();
    engine.Apply(Find("privacy.inking"), calm: true); clock.Advance();
    engine.Apply(Find("taskbar.widgets"), calm: true); clock.Advance();
    engine.Undo(Find("privacy.inking")); clock.Advance();
    registry.Ignore.Add("TaskbarDa");
    IReadOnlyList<HistoryEntry> history = engine.History(CalmCatalog.All);
    Check(history.Count == 3, "Expected apply, apply, undo.");
    Check(history[0].Summary == "Undone" && history[1].SettingId == "taskbar.widgets" && history[1].CanUndo && !history[2].CanUndo,
        "History order or undo availability is wrong.");
}

static void CompactionKeepsUndo()
{
    string path = TempJournal();
    MemoryRegistry registry = new();
    ChangeJournal journal = new(path);
    ChangeEngine engine = new(registry, journal, 26300);
    SettingDefinition oldest = Find("privacy.typinginsights");
    engine.Apply(oldest, calm: true);
    SettingDefinition churn = Find("taskbar.widgets");
    for (int i = 0; i < ChangeJournal.CompactAbove; i++) engine.Apply(churn, calm: i % 2 == 0);
    Check(journal.Records.Count <= ChangeJournal.CompactAbove, "The journal must stay bounded.");
    Check(new ChangeEngine(registry, new ChangeJournal(path), 26300).Inspect(oldest).CanUndo, "Compaction dropped an undoable change.");
}

static void RealRegistryReadIsReadOnly()
{
    WindowsRegistryStore store = new();
    Stopwatch timer = Stopwatch.StartNew();
    int read = 0;
    foreach (RegistryTarget target in CalmCatalog.All.SelectMany(d => d.Targets))
    {
        _ = store.Read(RegistryHive2.CurrentUser, target.Path, target.Name);
        read++;
    }
    Check(read > 25 && timer.Elapsed < TimeSpan.FromSeconds(2), "Reading the catalogue state must be quick.");
}

// ---------- helpers ----------
static SettingDefinition Find(string id) => CalmCatalog.All.Single(d => d.Id == id);

static (ChangeEngine Engine, MemoryRegistry Registry, ChangeJournal Journal, ManualClock Clock) New(int build = 26300)
{
    MemoryRegistry registry = new();
    ChangeJournal journal = new(TempJournal());
    ManualClock clock = new();
    return (new ChangeEngine(registry, journal, build, null, clock.Now), registry, journal, clock);
}

static string TempJournal() => Path.Combine(Path.GetTempPath(), "soltex-customize-tests", Guid.NewGuid().ToString("N"), "journal-v1.jsonl");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static SettingDefinition Def(string id) => CalmCatalog.All.Single(d => d.Id == id);

static void SwitchUsesImpliedValue()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition taskView = Def("taskbar.taskview");
    Check(engine.Inspect(taskView).Choice == 1, "Task view is on when Windows has no value.");
    Check(engine.ApplyChoice(taskView, 0).Outcome == ChangeOutcome.Applied && registry.Get(taskView.Targets[0]) == RegistryValue.Dword(0), "Off did not write 0.");
    Check(engine.Inspect(taskView).Choice == 0, "Inspect does not see Off.");
    Check(engine.ApplyChoice(taskView, 0).Outcome == ChangeOutcome.AlreadyInPlace, "A second Off must not write again.");
    Check(engine.ApplyChoice(taskView, 1).Outcome == ChangeOutcome.Applied && engine.Inspect(taskView).Choice == 1, "On did not apply.");
    SettingDefinition extensions = Def("explorer.extensions");
    Check(engine.Inspect(extensions).Choice == 0, "Extensions are hidden by default.");
    engine.ApplyChoice(extensions, 1);
    Check(registry.Get(extensions.Targets[0]) == RegistryValue.Dword(0), "Show extensions must write HideFileExt=0.");
}

static void ChoiceDefaultRemovesValue()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition search = Def("taskbar.search");
    Check(engine.Inspect(search).Choice == 0, "No value means Windows default.");
    engine.ApplyChoice(search, 2);
    Check(registry.Get(search.Targets[0]) == RegistryValue.Dword(1) && engine.Inspect(search).Choice == 2, "Icon only must write 1.");
    engine.ApplyChoice(search, 0);
    Check(!registry.Get(search.Targets[0]).Exists && engine.Inspect(search).Choice == 0, "Windows default must remove the value.");
    SettingDefinition classic = Def("explorer.classicmenu");
    Check(engine.Inspect(classic).Choice == 0, "Classic menu is off without the override.");
    engine.ApplyChoice(classic, 1);
    Check(registry.Get(classic.Targets[0]) == RegistryValue.FromText("") && engine.Inspect(classic).Choice == 1, "Classic menu override not written.");
    engine.ApplyChoice(classic, 0);
    Check(!registry.Get(classic.Targets[0]).Exists, "Turning the classic menu off must remove the override.");
}

static void UndoAfterChoice()
{
    (ChangeEngine engine, MemoryRegistry registry, _, ManualClock clock) = New();
    SettingDefinition combine = Def("taskbar.combine");
    registry.Set(combine.Targets[0], RegistryValue.Dword(1));
    engine.ApplyChoice(combine, 2);
    clock.Advance();
    Check(engine.Undo(combine).Outcome == UndoOutcome.Restored && registry.Get(combine.Targets[0]) == RegistryValue.Dword(1), "Undo did not restore 'when full'.");
    SettingDefinition pointer = Def("multitasking.pointerprecision");
    Check(engine.Inspect(pointer).Choice == 1, "Pointer precision is on by Windows default.");
    engine.ApplyChoice(pointer, 0);
    Check(pointer.Targets.All(t => registry.Get(t) == RegistryValue.FromText("0")), "All three mouse values must change together.");
    clock.Advance();
    engine.Undo(pointer);
    Check(pointer.Targets.All(t => !registry.Get(t).Exists), "Undo must restore the earlier absence of all three values.");
}

static void ProfileValuesAreOptions()
{
    foreach (SettingDefinition d in CalmCatalog.All.Where(d => d.InCalmProfile && d.Choices is not null))
    {
        IReadOnlyList<RegistryValue> calm = d.Targets.Select(t => t.Calm).ToArray();
        Check(d.Options.Any(o => o.Values.SequenceEqual(calm)), d.Id + ": the profile value is not one of its options.");
    }
}

static void SystemCatalogueIsSafe()
{
    Check(SystemCatalog.All.Select(i => i.Id).Distinct().Count() == SystemCatalog.All.Count, "System ids must be unique.");
    foreach (SystemItem item in SystemCatalog.All)
    {
        Check(item.Actions.Count > 0 && item.Title.Length > 0, item.Id + " is incomplete.");
        foreach (PolicyAction p in item.Actions.OfType<PolicyAction>())
        {
            Check(SystemCatalog.AllowedPolicy(p.Hive, p.Path), item.Id + " writes outside policy locations: " + p.Path);
            Check(!SystemCatalog.Forbidden(p.Path, p.Name), item.Id + " weakens protection: " + p.Path + "\\" + p.Name);
            Check(!SystemCatalog.WeakensUpdates(p.Path, p.Name, p.Value), item.Id + " turns automatic updates off: " + p.Name);
        }
        Check(item.Actions.OfType<FirewallAction>().All(f => f.RuleName.StartsWith(SystemCatalog.RuleTag, StringComparison.Ordinal)), item.Id + " has an untagged firewall rule.");
        Check(!item.Actions.OfType<RemoveAppAction>().Any(a => string.Equals(a.PackageName, "MSTeams", StringComparison.OrdinalIgnoreCase)),
            item.Id + " must not remove the work and school Teams client.");
        Check(item.Subject.Length > 0 && !item.Subject.StartsWith("Remove", StringComparison.Ordinal), item.Id + " needs a short subject.");
        Check(!item.Actions.OfType<ServiceAction>().Any(a => a.Service.Contains("Defend", StringComparison.OrdinalIgnoreCase) || a.Service is "mpssvc" or "BFE" or "wuauserv" or "WinDefend"),
            item.Id + " must not disable protection or the update service.");
        Check(item.Reversible || item.Reinstall is not null, item.Id + " removes an app without saying how to get it back.");
    }
    Check(SystemCatalog.All.Any(i => i.Id == "system.updates.notify"), "Updates must be switchable to notify-only.");
    Check(!SystemCatalog.AllowedPolicy(RegistryHive2.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend"), "Service keys must not be writable as policies.");
    Check(SystemCatalog.Forbidden(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware"), "Defender policies must be refused.");
    const string AutoUpdate = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    Check(SystemCatalog.WeakensUpdates(AutoUpdate, "NoAutoUpdate", RegistryValue.Dword(1)) && SystemCatalog.WeakensUpdates(AutoUpdate, "AUOptions", RegistryValue.Dword(1))
          && SystemCatalog.WeakensUpdates(AutoUpdate, "NoAutoUpdate", RegistryValue.FromText("0")),
        "Values that turn automatic updates off must be recognised.");
    Check(!SystemCatalog.WeakensUpdates(AutoUpdate, "NoAutoUpdate", RegistryValue.Absent) && !SystemCatalog.WeakensUpdates(AutoUpdate, "AUOptions", RegistryValue.Dword(4)),
        "Windows' own update modes must stay restorable.");
}

static void SettingDriftIsFoundAndPutBack()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    SettingDefinition widgets = Find("taskbar.widgets"), search = Find("taskbar.search");
    Check(engine.ApplyChoice(widgets, 0).Outcome == ChangeOutcome.Applied && engine.ApplyChoice(search, 1).Outcome == ChangeOutcome.Applied, "Setup changes failed.");
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "Nothing has drifted yet.");
    // A Windows update writes the old value back.
    RegistryTarget target = widgets.Targets[0];
    registry.Set(target, widgets.Options[1].Values[0]);
    Check(engine.Drifted(CalmCatalog.All) is [var drifted] && drifted.Id == widgets.Id, "The changed-back setting was not found.");
    Check(engine.Reapply(widgets).Outcome == ChangeOutcome.Applied && engine.Inspect(widgets).Choice == 0, "Putting it back failed.");
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "Drift remains after putting it back.");
    // A setting the user undid is not drift, whatever happens to it later.
    Check(engine.Undo(search).Outcome == UndoOutcome.Restored, "Undo failed.");
    registry.Set(search.Targets[0], search.Options[2].Values[0]);
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "An undone setting must not be reported as drift.");
}

static void SystemDriftIsFound()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    ChangeJournal journal = new(Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl"));
    SystemEngine engine = new(host, journal);
    SystemItem[] items = SystemCatalog.All.Where(i => i.Id is "system.telemetry.policy" or "system.telemetry.services").ToArray();
    Check(engine.Apply(items).All(r => r.Ok) && engine.Drifted(SystemCatalog.All).Count == 0, "Apply failed or drift reported too early.");
    host.Services["DiagTrack"] = 2; // an update turned the service back on
    Check(engine.Drifted(SystemCatalog.All) is [var drifted] && drifted.Item.Id == "system.telemetry.services" && drifted.State == SystemItemState.Partial, "The re-enabled service was not found.");
    Check(engine.Undo(items).All(r => r.Ok) && engine.Drifted(SystemCatalog.All).Count == 0, "Undone items must not be drift.");
}

static void SystemApplyAndUndo()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    host.Tasks[@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] = true;
    ChangeJournal journal = new(Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl"));
    SystemEngine engine = new(host, journal);
    SystemItem[] items = SystemCatalog.All.Where(i => i.Id is "system.telemetry.policy" or "system.telemetry.services" or "system.telemetry.tasks" or "system.telemetry.firewall").ToArray();
    Check(items.All(i => engine.Inspect(i).State is SystemItemState.NotApplied), "Nothing is applied on a fresh PC.");
    IReadOnlyList<SystemResult> results = engine.Apply(items);
    Check(results.All(r => r.Ok), "Apply failed: " + string.Join("; ", results.Where(r => !r.Ok).Select(r => r.Message)));
    Check(host.Read(RegistryHive2.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry") == RegistryValue.Dword(0), "Telemetry policy not written.");
    Check(host.Services["DiagTrack"] == 4 && host.Services["dmwappushservice"] == 4, "Services not disabled.");
    Check(host.Tasks[@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] == false, "Task not disabled.");
    Check(host.Rules.Contains("Soltex - Block DiagTrack"), "Firewall block not added.");
    Check(items.All(i => engine.Inspect(i).State == SystemItemState.Applied && engine.Inspect(i).CanUndo), "Applied items must report applied and undoable.");
    Check(journal.Records.Count(r => r.Kind == JournalKind.Intent) == journal.Records.Count(r => r.Kind == JournalKind.Applied), "Every intent needs a completion.");

    results = engine.Undo(items);
    Check(results.All(r => r.Ok), "Undo failed: " + string.Join("; ", results.Where(r => !r.Ok).Select(r => r.Message)));
    Check(!host.Read(RegistryHive2.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry").Exists, "Undo must remove a policy that was absent.");
    Check(host.Services["DiagTrack"] == 2 && host.Services["dmwappushservice"] == 3, "Undo must restore each service's own start type.");
    Check(host.Tasks[@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"] == true, "Undo must re-enable the task.");
    Check(host.Rules.Count == 0, "Undo must remove the rules it added.");
}

static void SystemUndoBoundaries()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    host.Packages.Add("Microsoft.BingNews");
    SystemEngine engine = new(host, new ChangeJournal(Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl")));
    SystemItem services = SystemCatalog.All.Single(i => i.Id == "system.telemetry.services");
    engine.Apply([services]);
    host.Services["DiagTrack"] = 3; // the user changed it again elsewhere
    engine.Undo([services]);
    Check(host.Services["DiagTrack"] == 3 && host.Services["dmwappushservice"] == 3, "Undo must keep the user's later change and restore the rest.");
    SystemItem news = SystemCatalog.All.Single(i => i.Id == "system.apps.news");
    Check(engine.Inspect(news).State == SystemItemState.NotApplied, "News is installed.");
    Check(engine.Apply([news]).Single().Ok && !host.Packages.Contains("Microsoft.BingNews"), "News was not removed.");
    Check(!engine.Inspect(news).CanUndo && !engine.Undo([news]).Single().Ok, "Removed apps must not claim to be undoable.");
}

static void EdgeNeedsAnotherBrowser()
{
    FakeSystemHost host = new() { EdgeInstalled = true, OtherBrowser = false };
    SystemEngine engine = new(host, new ChangeJournal(Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl")));
    SystemItem edge = SystemCatalog.All.Single(i => i.Id == "system.apps.edge");
    SystemResult refused = engine.Apply([edge]).Single();
    Check(!refused.Ok && host.EdgeInstalled && !host.Read(RegistryHive2.LocalMachine, @"SOFTWARE\Microsoft\EdgeUpdate", "DoNotUpdateToEdgeWithChromium").Exists,
        "Edge must not be touched when it is the only browser.");
    host.OtherBrowser = true;
    Check(engine.Apply([edge]).Single().Ok && !host.EdgeInstalled, "Edge removal with another browser installed failed.");
}

static void RealSystemInspectIsReadOnly()
{
    SystemEngine engine = new(new WindowsSystemHost(), new ChangeJournal(Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl")));
    Stopwatch timer = Stopwatch.StartNew();
    SystemItemStatus[] states = SystemCatalog.All.Select(engine.Inspect).ToArray();
    Check(states.Length == SystemCatalog.All.Count && timer.Elapsed < TimeSpan.FromSeconds(60), "Inspecting the real system took too long: " + timer.Elapsed);
    Console.WriteLine("      real system: " + string.Join(", ", states.GroupBy(s => s.State).Select(g => g.Key + " " + g.Count())));
}

static string SystemJournal() => Path.Combine(Path.GetTempPath(), "soltex-system-" + Guid.NewGuid().ToString("N"), "journal.jsonl");

static SystemItem Item(string id) => SystemCatalog.All.Single(i => i.Id == id);

static void CalmSwitchesArePositive()
{
    (ChangeEngine engine, MemoryRegistry registry, _, _) = New();
    string[] negative = ["No ", "Don't ", "Hide ", "Turn off "];
    foreach (SettingDefinition d in CalmCatalog.All.Where(d => d.Source.StartsWith("DW-", StringComparison.Ordinal)))
    {
        Check(d.IsSwitch && d.InCalmProfile && d.Options[0].Values.SequenceEqual(d.Targets.Select(t => t.Calm)), d.Id + ": Off must be the De-Windows value.");
        Check(!negative.Any(n => d.Title.StartsWith(n, StringComparison.Ordinal)), d.Id + ": the title must name the feature, not its absence.");
        Check(engine.Inspect(d).Choice == 1 && !engine.Inspect(d).IsCalm, d.Id + ": an unset value means the feature is on.");
    }
    SettingDefinition ads = Find("privacy.advertisingid");
    registry.Set(ads.Targets[0], RegistryValue.Dword(1)); // as the Settings app writes it
    Check(engine.Inspect(ads).Choice == 1, "An explicit 1 must read as On.");
    Check(engine.ApplyChoice(ads, 0).Outcome == ChangeOutcome.Applied && registry.Get(ads.Targets[0]) == RegistryValue.Dword(0) && engine.Inspect(ads).IsCalm,
        "Off must write the De-Windows value.");
    Check(engine.ApplyChoice(ads, 1).Outcome == ChangeOutcome.Applied && !registry.Get(ads.Targets[0]).Exists && engine.Inspect(ads).Choice == 1,
        "On must hand the value back to Windows.");
}

static void SystemApplySurvivesHostFailures()
{
    const string proxy = @"\Microsoft\Windows\Autochk\Proxy";
    FakeSystemHost host = new();
    host.Packages.UnionWith(["Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.Todos"]);
    host.FailRemoval.Add("Microsoft.BingNews");
    host.Tasks[proxy] = true;
    host.UnreadableTasks.Add(proxy);
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    ChangeJournal journal = new(SystemJournal());
    SystemEngine engine = new(host, journal);
    IReadOnlyList<SystemResult> results = engine.Apply([Item("system.apps.news"), Item("system.telemetry.tasks"), Item("system.telemetry.services"), Item("system.apps.todo")]);
    Check(results.Count == 4, "Every item needs a result.");
    Check(!results[0].Ok && results[0].Message.StartsWith("News", StringComparison.Ordinal) && !host.Packages.Contains("Microsoft.BingWeather"),
        "A failed removal is reported and the item's other removals still run: " + results[0].Message);
    Check(!results[1].Ok && results[1].Message.Contains("did not finish", StringComparison.Ordinal), "A task that cannot be read must be reported, not crash the run.");
    Check(results[2].Ok && host.Services["DiagTrack"] == 4 && results[3].Ok && !host.Packages.Contains("Microsoft.Todos"), "Items after a failure must still run.");
    Check(journal.Records.Where(r => r.Kind == JournalKind.Intent)
            .All(i => journal.Records.Any(r => r.Kind is JournalKind.Applied or JournalKind.Failed && r.OperationId == i.OperationId && r.TargetKey == i.TargetKey)),
        "Every intent needs a completion.");
}

static void RemovalPolicyIsUndoable()
{
    FakeSystemHost host = new() { OneDriveInstalled = true };
    host.FailRemoval.Add("OneDrive");
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()));
    SystemItem oneDrive = Item("system.apps.onedrive");
    PolicyAction sync = oneDrive.Actions.OfType<PolicyAction>().Single();
    Check(!engine.Apply([oneDrive]).Single().Ok && host.OneDriveInstalled && !host.Read(sync.Hive, sync.Path, sync.Name).Exists,
        "When OneDrive stays installed, its syncing must not be blocked.");
    host.FailRemoval.Clear();
    Check(engine.Apply([oneDrive]).Single().Ok && !host.OneDriveInstalled && host.Read(sync.Hive, sync.Path, sync.Name) == RegistryValue.Dword(1), "Removing OneDrive failed.");
    SystemItemStatus status = engine.Inspect(oneDrive);
    Check(status.State == SystemItemState.Applied && status.CanUndo, "The sync policy must stay undoable after the removal.");
    SystemResult undo = engine.Undo([oneDrive]).Single();
    Check(undo.Ok && !host.Read(sync.Hive, sync.Path, sync.Name).Exists && !host.OneDriveInstalled && undo.Message.Contains("To get OneDrive back", StringComparison.Ordinal),
        "Undo must lift the policy, leave the removal and say how to get OneDrive back: " + undo.Message);
    Check(!engine.Inspect(oneDrive).CanUndo, "Nothing is left to undo.");
}

static void SystemUndoRejectsForgedValues()
{
    const string autoUpdate = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 4;
    host.Write(RegistryHive2.LocalMachine, autoUpdate, "NoAutoUpdate", RegistryValue.Dword(0));
    host.Write(RegistryHive2.LocalMachine, autoUpdate, "NoAutoRebootWithLoggedOnUsers", RegistryValue.Dword(5));
    ChangeJournal journal = new(SystemJournal());
    SystemEngine engine = new(host, journal);
    SystemItem updates = Item("system.updates.notify"), services = Item("system.telemetry.services");
    // Another program running as the user appends records the elevated helper would otherwise trust.
    Guid forged = Guid.NewGuid();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    journal.Append(new(JournalKind.Applied, forged, updates.Id, @"HKLM\" + autoUpdate, "NoAutoUpdate", RegistryValue.Dword(1), RegistryValue.Dword(0), now));
    journal.Append(new(JournalKind.Applied, forged, updates.Id, @"HKLM\" + autoUpdate, "NoAutoRebootWithLoggedOnUsers", RegistryValue.Dword(0), RegistryValue.Dword(5), now));
    journal.Append(new(JournalKind.Applied, forged, services.Id, "service:DiagTrack", "Start", RegistryValue.Dword(1), RegistryValue.Dword(4), now));
    Check(!engine.Inspect(updates).CanUndo && !engine.Inspect(services).CanUndo, "Forged values must not offer Undo.");
    IReadOnlyList<SystemResult> results = engine.Undo([updates, services]);
    Check(host.Read(RegistryHive2.LocalMachine, autoUpdate, "NoAutoUpdate") == RegistryValue.Dword(0), "Undo turned automatic updates off.");
    Check(host.Read(RegistryHive2.LocalMachine, autoUpdate, "NoAutoRebootWithLoggedOnUsers") == RegistryValue.Dword(5), "Undo wrote over a value this app never sets.");
    Check(host.Services["DiagTrack"] == 4, "Undo wrote a start type the catalogue never uses.");
    Check(results[0].Message.Contains("Automatic updates stay on", StringComparison.Ordinal), "The kept update setting must be explained: " + results[0].Message);
    Check(engine.Undo([updates, services]).All(r => r.Ok && r.Message.StartsWith("Nothing left to undo", StringComparison.Ordinal)), "Refused records must not stay live.");
}

static void SystemUndoOfMissingTarget()
{
    const string proxy = @"\Microsoft\Windows\Autochk\Proxy";
    FakeSystemHost host = new();
    host.Tasks[proxy] = true;
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()));
    SystemItem tasks = Item("system.telemetry.tasks");
    Check(engine.Apply([tasks]).Single().Ok && !host.Tasks[proxy] && engine.Inspect(tasks).CanUndo, "Setup failed.");
    host.Tasks.Remove(proxy); // a feature update removed the task
    SystemResult undo = engine.Undo([tasks]).Single();
    Check(undo.Ok && undo.Message.StartsWith("Nothing left to undo", StringComparison.Ordinal), "Undo must not claim to have undone a missing task: " + undo.Message);
    Check(!engine.Inspect(tasks).CanUndo, "A task that is gone must not stay undoable.");
}

static void SystemDriftIsPerValue()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    host.FailServices.Add("dmwappushservice");
    host.Packages.Add("Microsoft.BingNews");
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()));
    SystemItem services = Item("system.telemetry.services"), news = Item("system.apps.news");
    IReadOnlyList<SystemResult> results = engine.Apply([services, news]);
    Check(!results[0].Ok && results[1].Ok && engine.Inspect(services).State == SystemItemState.Partial, "Setup: one service fails and News is removed.");
    Check(engine.Drifted(SystemCatalog.All).Count == 0, "A part that never applied is not something Windows turned back on.");
    host.Packages.Add("Microsoft.BingNews"); // reinstalled from the Store
    Check(engine.Drifted(SystemCatalog.All) is [{ Item.Id: "system.apps.news" }], "A removed app that came back must be reported.");
    Check(engine.Keep([news]) == 1 && engine.Drifted(SystemCatalog.All).Count == 0, "A reinstall the user keeps must not be reported again.");
}

static void LegacyFirewallRulesAreRecognised()
{
    FakeSystemHost host = new();
    SystemItem firewall = Item("system.telemetry.firewall");
    ChangeJournal journal = new(SystemJournal());
    // What an earlier version added and recorded.
    foreach (FirewallAction rule in firewall.Actions.OfType<FirewallAction>())
    {
        string legacy = SystemCatalog.LegacyRuleTag + rule.RuleName[SystemCatalog.RuleTag.Length..];
        host.Rules.Add(legacy);
        journal.Append(new(JournalKind.Applied, Guid.NewGuid(), firewall.Id, "firewall:" + legacy, "Exists", RegistryValue.Dword(0), RegistryValue.Dword(1), DateTimeOffset.UtcNow));
    }
    SystemEngine engine = new(host, journal);
    SystemItemStatus status = engine.Inspect(firewall);
    Check(status.State == SystemItemState.Applied && status.CanUndo, "Rules from an earlier version count as in place and undoable.");
    Check(engine.Apply([firewall]).Single().Ok && host.Rules.Count == 3, "No second set of rules may be added.");
    Check(engine.Undo([firewall]).Single().Ok && host.Rules.Count == 0, "Undo must remove the earlier version's rules.");
}

static void SystemHistoryIsListed()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    host.Packages.Add("Microsoft.BingNews");
    ManualClock clock = new();
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()), clock.Now);
    engine.Apply([Item("system.telemetry.services")]); clock.Advance();
    engine.Apply([Item("system.apps.news")]); clock.Advance();
    engine.Undo([Item("system.telemetry.services")]);
    IReadOnlyList<HistoryEntry> history = engine.History();
    Check(history.Select(h => h.Summary).SequenceEqual(["Undone", "Removed", "Changed"]) && history.All(h => !h.CanUndo && h.Title.Length > 0),
        "System history is wrong: " + string.Join(", ", history.Select(h => h.Summary)));
}

internal sealed class ManualClock
{
    private DateTimeOffset now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    public DateTimeOffset Now() => now;
    public void Advance() => now = now.AddMinutes(1);
}

/// <summary>In-memory registry. Writes go through the same never-touch guard as the real store.</summary>
internal sealed class MemoryRegistry : IRegistryStore
{
    private readonly Dictionary<string, RegistryValue> values = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Ignore { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Throw { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(RegistryHive2 hive, string path, string name) => hive + "|" + path + "|" + name;

    public RegistryValue Read(RegistryHive2 hive, string path, string name) =>
        values.TryGetValue(Key(hive, path, name), out RegistryValue value) ? value : RegistryValue.Absent;

    public void Write(string currentUserPath, string name, RegistryValue value)
    {
        NeverTouch.Guard(RegistryHive2.CurrentUser, currentUserPath, name);
        if (Throw.Contains(name)) throw new UnauthorizedAccessException("Access to the registry key is denied.");
        if (Ignore.Contains(name)) return;
        if (value.Exists) values[Key(RegistryHive2.CurrentUser, currentUserPath, name)] = value;
        else values.Remove(Key(RegistryHive2.CurrentUser, currentUserPath, name));
    }

    public RegistryValue Get(RegistryTarget target) => Read(RegistryHive2.CurrentUser, target.Path, target.Name);
    public void Set(RegistryTarget target, RegistryValue value) => SetAny(RegistryHive2.CurrentUser, target.Path, target.Name, value);
    public void SetAny(RegistryHive2 hive, string path, string name, RegistryValue value) => values[Key(hive, path, name)] = value;
}

internal sealed class FakeSystemHost : ISystemHost
{
    private readonly Dictionary<string, RegistryValue> values = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Rules { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool EdgeInstalled { get; set; }
    public bool OneDriveInstalled { get; set; }
    public bool OtherBrowser { get; set; } = true;
    /// <summary>Apps (package name, or name) whose removal fails as the package manager reports it.</summary>
    public HashSet<string> FailRemoval { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailServices { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnreadableTasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public RegistryValue Read(RegistryHive2 hive, string path, string name) => values.TryGetValue(hive + "|" + path + "|" + name, out RegistryValue v) ? v : RegistryValue.Absent;
    public void Write(RegistryHive2 hive, string path, string name, RegistryValue value)
    {
        if (value.Exists) values[hive + "|" + path + "|" + name] = value; else values.Remove(hive + "|" + path + "|" + name);
    }
    public int? ServiceStart(string service) => Services.TryGetValue(service, out int s) ? s : null;
    public void SetServiceStart(string service, int startType)
    {
        if (FailServices.Contains(service)) throw new InvalidOperationException("sc.exe failed (5): Access is denied.");
        Services[service] = startType;
    }
    public bool? TaskEnabled(string taskPath) =>
        UnreadableTasks.Contains(taskPath) ? throw new TimeoutException("schtasks.exe did not finish.") : Tasks.TryGetValue(taskPath, out bool e) ? e : null;
    public void SetTaskEnabled(string taskPath, bool enabled) => Tasks[taskPath] = enabled;
    public bool FirewallRuleExists(string ruleName) => Rules.Contains(ruleName);
    public void AddFirewallBlock(FirewallAction rule) => Rules.Add(rule.RuleName);
    public void RemoveFirewallRule(string ruleName) => Rules.Remove(ruleName);
    public bool? AppInstalled(RemoveAppAction app) => app.Kind switch
    {
        AppKind.Edge => EdgeInstalled,
        AppKind.OneDrive => OneDriveInstalled,
        _ => Packages.Contains(app.PackageName ?? "") ? true : null
    };
    public void RemoveApp(RemoveAppAction app)
    {
        if (FailRemoval.Contains(app.PackageName ?? app.Name))
            throw new AggregateException(Marshal.GetExceptionForHR(unchecked((int)0x80073CFA))!); // as RemovePackageAsync faults
        if (app.Kind == AppKind.Edge) EdgeInstalled = false;
        else if (app.Kind == AppKind.OneDrive) OneDriveInstalled = false;
        else Packages.Remove(app.PackageName ?? "");
    }
    public bool OtherBrowserInstalled() => OtherBrowser;
}
