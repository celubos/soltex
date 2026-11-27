using System.Diagnostics;
using System.Runtime.InteropServices;
using Soltex.Customize;

// All checks use an in-memory registry and fake system host. No Windows setting is changed by this suite; two checks
// only read the real state (per-user catalogue keys, and the full De-Windows items without administrator rights), and
// the helper-files check reads the Windows folder's permissions and makes files and links in a temporary folder.
List<(string Name, Action Test)> tests =
[
    ("Catalogue is well formed and never targets protected areas", CatalogueIsSafe),
    ("Never-touch guard refuses security, update and integrity values", NeverTouchRefuses),
    ("Apply is idempotent and journals intent before completion", ApplyIsIdempotentAndJournalled),
    ("Apply back to Windows default removes the value", ApplyDefaultRemovesValue),
    ("A write that does not stick is reported failed, not applied", VerifyCatchesFailedWrite),
    ("A write Windows denies says so plainly, rolls back and leaves nothing to undo", AccessDeniedIsPlain),
    ("New taskbar, File Explorer, desktop icon, input and gaming rows are well formed", NewRowsAreWellFormed),
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
    ("System history lists changes and removals", SystemHistoryIsListed),
    ("Accessibility shortcuts change only their bit of Flags and undo puts the text back", AccessibilityShortcutsChangeOneBit),
    ("Drift and undo count a missing value as the value Windows assumes", DriftAndUndoUseImpliedValues),
    ("Registry values keep their type through a change and its undo", RegistryValuesKeepTheirType),
    ("Compaction keeps each value's newest records and unfinished intents", CompactionKeepsWhatUndoNeeds),
    ("The journal shares its file with readers and waits out a brief lock", JournalSharesItsFile),
    ("A journal that moved is carried over once", JournalIsCarriedOver),
    ("The administrator helper settles interrupted changes before changing anything", SystemReconcilesInterruptedChanges),
    ("Undoing the telemetry services starts DiagTrack again", SystemUndoStartsAutomaticServices),
    ("A policy stored as another kind of value is left alone", SystemLeavesOtherTypedPolicies),
    ("Review fixes: Game Bar, OneDrive, Start layout, Windows mode and update wording", CatalogueReviewFixes),
    ("Helper files: the state folder is administrator-only and links are refused", HelperFilesAreChecked)
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
    // Access denied surfaces in plain words (no raw exception text); see AccessDeniedIsPlain.
    Check(denied.Outcome == ChangeOutcome.Failed && denied.BlockedByWindows && denied.Message == ChangeEngine.BlockedByWindowsMessage, "Access denied must surface.");
}

static void NewRowsAreWellFormed()
{
    string[] ids =
    [
        "taskbar.alldisplays", "taskbar.mmmode", "explorer.expandfolder", "explorer.allfolders", "explorer.statusbar", "explorer.separateprocess",
        "appearance.scrollbars", "desktop.thispc", "desktop.recyclebin", "desktop.userfiles", "desktop.network", "desktop.controlpanel",
        "multitasking.shake", "input.clipboard", "input.inactivescroll", "input.stickykeys", "input.filterkeys", "input.togglekeys",
        "gaming.gamebar", "gaming.gamemode", "gaming.captures"
    ];
    (ChangeEngine engine, _, _, _) = New();
    foreach (string id in ids)
    {
        Check(CalmCatalog.All.Count(d => d.Id == id) == 1, id + " must exist exactly once.");
        SettingDefinition d = Find(id);
        Check(!d.InCalmProfile, id + " must not join the De-Windows profile.");
        Check(d.Targets.All(t => t.Implied is { Exists: true }), id + " needs the value Windows assumes while nothing is set.");
        Check(d.Options.Count >= 2 && d.Options.All(o => o.Label.Length > 0 && o.Values.Count == d.Targets.Count && o.Values.All(v => v.Exists)), id + " has malformed options.");
        Check(d.Options.Select(o => string.Join("|", o.Values)).Distinct().Count() == d.Options.Count, id + " has two options with the same values.");
        Check(d.IsSwitch == (d.Options.Count == 2 && d.Options[0].Label == "Off"), id + ": a switch is [Off, On], a list is not a switch.");
        // A PC that never changed the value shows Windows' own default as one of the options, never "custom".
        Check(engine.Inspect(d).Choice >= 0, id + ": Windows' implied value is not one of its options.");
    }
    Check(Find("taskbar.mmmode").Options.Select(o => o.Label).SequenceEqual(["All taskbars", "Main taskbar and where the window is open", "Taskbar where the window is open"]),
        "The taskbar apps list must offer Windows' three choices in order.");

    const string NewStartPanel = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    SettingDefinition[] icons = [.. CalmCatalog.All.Where(d => d.Group == SettingGroup.DesktopIcons)];
    Check(icons.Length == 5 && icons.All(d => d.Id.StartsWith("desktop.", StringComparison.Ordinal)), "Five desktop icons are expected.");
    foreach (SettingDefinition d in icons)
    {
        RegistryTarget t = d.Targets.Single();
        Check(t.Path == NewStartPanel, d.Id + " must use the NewStartPanel key.");
        Check(t.Name.Length == 38 && t.Name[0] == '{' && t.Name[^1] == '}' && Guid.TryParseExact(t.Name, "B", out _), d.Id + " must be named by its CLSID in braces: " + t.Name);
        Check(d.Options[1].Values[0] == RegistryValue.Dword(0) && d.Options[0].Values[0] == RegistryValue.Dword(1), d.Id + ": shown is 0, hidden is 1.");
    }
    Check(icons.Select(d => d.Targets[0].Name.ToUpperInvariant()).Distinct().Count() == icons.Length, "Each desktop icon needs its own CLSID.");
    Check(engine.Inspect(Find("desktop.recyclebin")).Choice == 1 && engine.Inspect(Find("desktop.thispc")).Choice == 0,
        "Windows shows the Recycle Bin and hides This PC until told otherwise.");

    Check(CalmCatalog.GroupTitle(SettingGroup.DesktopIcons) == "Desktop icons" && CalmCatalog.GroupTitle(SettingGroup.Input) == "Keyboard, mouse and clipboard"
        && CalmCatalog.GroupTitle(SettingGroup.Gaming) == "Gaming", "The new groups need their headings.");
    Check(Find("multitasking.pointerprecision").Group == SettingGroup.Input, "Pointer precision moves to the keyboard and mouse group, keeping its id.");
    Check(CalmCatalog.All.Where(d => d.Group == SettingGroup.Input).Select(d => d.Id)
            .SequenceEqual(["input.clipboard", "multitasking.pointerprecision", "input.inactivescroll", "input.stickykeys", "input.filterkeys", "input.togglekeys"]),
        "Keyboard, mouse and clipboard rows are out of order.");
    Check(CalmCatalog.All.Where(d => d.Group == SettingGroup.Gaming).Select(d => d.Id).SequenceEqual(["gaming.gamebar", "gaming.gamemode", "gaming.captures"]),
        "Gaming rows are out of order.");

    // Game captures: both values change together, and undo puts both back.
    (ChangeEngine captures, MemoryRegistry registry, _, _) = New();
    SettingDefinition clips = Find("gaming.captures");
    Check(clips.Targets.Count == 2 && captures.Inspect(clips).Choice == 1, "Game captures are two values, on by default.");
    Check(captures.ApplyChoice(clips, 0).Outcome == ChangeOutcome.Applied && clips.Targets.All(t => registry.Get(t) == RegistryValue.Dword(0)), "Off must write 0 to both values.");
    Check(captures.Undo(clips).Outcome == UndoOutcome.Restored && clips.Targets.All(t => !registry.Get(t).Exists), "Undo must remove both values again.");
    // Sticky Keys: the pop-up bit only, as text, the way Windows stores the flags.
    SettingDefinition sticky = Find("input.stickykeys");
    Check(captures.ApplyChoice(sticky, 0).Outcome == ChangeOutcome.Applied && registry.Get(sticky.Targets[0]) == RegistryValue.FromText("506"), "Sticky Keys off must write \"506\".");
}

static void AccessDeniedIsPlain()
{
    (ChangeEngine engine, MemoryRegistry registry, ChangeJournal journal, _) = New();
    SettingDefinition widgets = Find("taskbar.widgets");
    registry.Throw.Add(widgets.Targets[0].Name); // as Windows' UCPD driver refuses TaskbarDa on 24H2 and later
    ChangeResult result = engine.ApplyChoice(widgets, 0);
    Check(result.Outcome == ChangeOutcome.Failed && result.BlockedByWindows, "A denied write must be reported as blocked by Windows.");
    Check(result.Message.StartsWith("Windows doesn't let apps change this", StringComparison.Ordinal) && result.Message.Contains("Windows Settings", StringComparison.Ordinal)
        && !result.Message.Contains("registry", StringComparison.OrdinalIgnoreCase), "The message must be plain, not the raw exception: " + result.Message);
    Check(journal.Records[^1].Kind == JournalKind.Failed && !engine.Inspect(widgets).CanUndo, "A blocked change is journalled as failed and has nothing to undo.");
    Check(engine.History(CalmCatalog.All) is [{ } entry] && entry.Summary == "Could not change: " + ChangeEngine.BlockedByWindowsMessage,
        "History must use the same plain words.");

    // Any denied value, not one setting in particular: the second value of a two-value switch, and the other exception Windows uses.
    registry.Throw.Add("AppCaptureEnabled");
    SettingDefinition clips = Find("gaming.captures");
    ChangeResult partial = engine.ApplyChoice(clips, 0);
    Check(partial.BlockedByWindows && !registry.Get(clips.Targets[0]).Exists, "The value written first must be rolled back when the second is blocked.");
    registry.ThrowSecurity = true;
    registry.Throw.Add("ShowTaskViewButton");
    ChangeResult security = engine.ApplyChoice(Find("taskbar.taskview"), 0);
    Check(security.BlockedByWindows && security.Message == ChangeEngine.BlockedByWindowsMessage, "A security exception from the registry is access denied too.");

    // A write Windows quietly discards is a failure, but not "Windows doesn't let apps": it may work another time.
    registry.Ignore.Add("TaskbarBadges");
    ChangeResult discarded = engine.ApplyChoice(Find("taskbar.badges"), 0);
    Check(discarded.Outcome == ChangeOutcome.Failed && !discarded.BlockedByWindows && discarded.Message.Contains("did not keep", StringComparison.Ordinal),
        "A discarded write keeps its own message.");

    // Undo that Windows refuses says so in the same words.
    SettingDefinition seconds = Find("taskbar.seconds");
    Check(engine.ApplyChoice(seconds, 1).Outcome == ChangeOutcome.Applied, "Setup: seconds on.");
    registry.Throw.Add("ShowSecondsInSystemClock");
    UndoResult undo = engine.Undo(seconds);
    Check(undo.Outcome == UndoOutcome.Failed && undo.Message == "Could not undo: " + ChangeEngine.BlockedByWindowsMessage, "A refused undo must be plain: " + undo.Message);
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

static void AccessibilityShortcutsChangeOneBit()
{
    (ChangeEngine engine, MemoryRegistry registry, ChangeJournal journal, ManualClock clock) = New();
    SettingDefinition sticky = Find("input.stickykeys");
    RegistryTarget flags = sticky.Targets.Single();
    Check(flags.Mask == 0x4 && sticky.Title.Contains("shortcut", StringComparison.Ordinal), "Sticky Keys must own only the shortcut bit.");

    // Nothing stored: Windows assumes 510, so the shortcut is on; off writes 506 as text.
    Check(engine.Inspect(sticky).Choice == 1, "With no value the shortcut is on.");
    Check(engine.ApplyChoice(sticky, 0).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("506"), "Off from nothing must write \"506\".");
    clock.Advance();
    Check(engine.Undo(sticky).Outcome == UndoOutcome.Restored && !registry.Get(flags).Exists, "Undo must remove the value again.");

    // Sticky Keys itself on (bit 0): off keeps it on.
    registry.Set(flags, RegistryValue.FromText("511"));
    Check(engine.Inspect(sticky).Choice == 1, "511 has the shortcut on.");
    Check(engine.ApplyChoice(sticky, 0).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("507"), "511 off must be 507, not 506.");
    Check(engine.Inspect(sticky).Choice == 0, "507 has the shortcut off.");
    clock.Advance();
    Check(engine.ApplyChoice(sticky, 1).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("511"), "507 on must be 511.");
    clock.Advance();
    Check(engine.Undo(sticky).Outcome == UndoOutcome.Restored && registry.Get(flags) == RegistryValue.FromText("507"), "Undo must put back the exact earlier text.");
    clock.Advance();
    Check(engine.Undo(sticky).Outcome == UndoOutcome.NothingToUndo, "The earlier change was replaced by the undone one.");

    // 506 (shortcut off) on is 510.
    registry.Set(flags, RegistryValue.FromText("506"));
    Check(engine.Inspect(sticky).Choice == 0 && engine.ApplyChoice(sticky, 1).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("510"),
        "506 on must be 510.");
    clock.Advance();

    // Another bit changing since is not drift, and undo keeps it while putting this bit back.
    registry.Set(flags, RegistryValue.FromText("511")); // the user turned Sticky Keys on
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "Another bit changing is not this setting drifting.");
    Check(engine.Undo(sticky).Outcome == UndoOutcome.Restored && registry.Get(flags) == RegistryValue.FromText("507"),
        "Undo must clear only the shortcut bit and keep Sticky Keys on: " + registry.Get(flags));
    clock.Advance();

    // The bit itself changed back: drift, and undo keeps the newer value.
    Check(engine.ApplyChoice(sticky, 1).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("511"), "Setup: shortcut on.");
    registry.Set(flags, RegistryValue.FromText("507"));
    Check(engine.Drifted(CalmCatalog.All) is [{ Id: "input.stickykeys" }], "The shortcut bit turned back off must be drift.");
    Check(engine.Reapply(sticky).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.FromText("511"), "Putting it back sets only the bit.");
    registry.Set(flags, RegistryValue.FromText("507"));
    Check(engine.Undo(sticky).Outcome == UndoOutcome.ChangedSince && registry.Get(flags) == RegistryValue.FromText("507"), "Undo must keep a newer value of its own bit.");

    // A DWORD copy of the flags keeps its type; unreadable text is never changed.
    registry.Set(flags, RegistryValue.Dword(510));
    Check(engine.ApplyChoice(sticky, 0).Outcome == ChangeOutcome.Applied && registry.Get(flags) == RegistryValue.Dword(506), "A DWORD must stay a DWORD.");
    registry.Set(flags, RegistryValue.FromText("not a number"));
    int records = journal.Records.Count;
    ChangeResult refused = engine.ApplyChoice(sticky, 1);
    Check(refused.Outcome == ChangeOutcome.Refused && registry.Get(flags) == RegistryValue.FromText("not a number") && journal.Records.Count == records,
        "Flags that are not a number must be left alone: " + refused.Message);

    // Filter Keys and Toggle Keys: the same bit of their own defaults.
    SettingDefinition filter = Find("input.filterkeys"), toggle = Find("input.togglekeys");
    Check(engine.ApplyChoice(filter, 0).Outcome == ChangeOutcome.Applied && registry.Get(filter.Targets[0]) == RegistryValue.FromText("122"), "Filter Keys off is 122.");
    Check(engine.ApplyChoice(toggle, 0).Outcome == ChangeOutcome.Applied && registry.Get(toggle.Targets[0]) == RegistryValue.FromText("58"), "Toggle Keys off is 58.");
    registry.Set(toggle.Targets[0], RegistryValue.FromText("63"));
    Check(engine.ApplyChoice(toggle, 0).Outcome == ChangeOutcome.Applied && registry.Get(toggle.Targets[0]) == RegistryValue.FromText("59"), "Toggle Keys itself must stay on.");
}

static void DriftAndUndoUseImpliedValues()
{
    (ChangeEngine engine, MemoryRegistry registry, _, ManualClock clock) = New();
    SettingDefinition taskView = Find("taskbar.taskview"); // Windows assumes 1 (shown)
    RegistryTarget target = taskView.Targets[0];
    registry.Set(target, RegistryValue.Dword(0));
    Check(engine.ApplyChoice(taskView, 1).Outcome == ChangeOutcome.Applied && registry.Get(target) == RegistryValue.Dword(1), "Setup: On writes 1.");
    registry.Set(target, RegistryValue.Absent); // something removed the value: still on as far as Windows is concerned
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "A missing value that Windows reads as 1 is not drift from 1.");
    clock.Advance();
    Check(engine.Undo(taskView).Outcome == UndoOutcome.Restored && registry.Get(target) == RegistryValue.Dword(0), "Undo must treat the missing value as 1 and restore 0.");

    // The other way round: this app handed the value back to Windows (removed it), the Settings app then wrote 1.
    SettingDefinition ads = Find("privacy.advertisingid");
    Check(engine.ApplyChoice(ads, 0).Outcome == ChangeOutcome.Applied, "Setup: advertising ID off.");
    clock.Advance();
    Check(engine.ApplyChoice(ads, 1).Outcome == ChangeOutcome.Applied && !registry.Get(ads.Targets[0]).Exists, "Setup: On removes the value.");
    registry.Set(ads.Targets[0], RegistryValue.Dword(1));
    Check(engine.Drifted(CalmCatalog.All).Count == 0, "An explicit 1 equals the missing value Windows reads as 1.");
    registry.Set(ads.Targets[0], RegistryValue.Dword(0));
    Check(engine.Drifted(CalmCatalog.All) is [{ Id: "privacy.advertisingid" }], "A real change back is still drift.");
    registry.Set(ads.Targets[0], RegistryValue.Dword(1));
    clock.Advance();
    Check(engine.Undo(ads).Outcome == UndoOutcome.Restored && registry.Get(ads.Targets[0]) == RegistryValue.Dword(0), "Undo must restore the earlier 0.");
}

static void RegistryValuesKeepTheirType()
{
    string path = TempJournal();
    MemoryRegistry registry = new();
    SettingDefinition taskView = Find("taskbar.taskview"), badges = Find("taskbar.badges"), seconds = Find("taskbar.seconds");
    ChangeEngine engine = new(registry, new ChangeJournal(path), 26300);
    RegistryValue qword = RegistryValue.Qword(1), binary = RegistryValue.Binary([1, 0, 0, 0]);
    registry.Set(taskView.Targets[0], qword);
    registry.Set(badges.Targets[0], binary);
    Check(engine.Inspect(taskView).Choice == -1, "A QWORD is not the DWORD option.");
    Check(engine.ApplyChoice(taskView, 0).Outcome == ChangeOutcome.Applied && engine.ApplyChoice(badges, 0).Outcome == ChangeOutcome.Applied, "Changes over typed values failed.");
    // Undo after a restart reads the journal back from disk.
    ChangeEngine restarted = new(registry, new ChangeJournal(path), 26300);
    Check(restarted.Undo(taskView).Outcome == UndoOutcome.Restored && registry.Get(taskView.Targets[0]) == qword, "Undo must restore the QWORD: " + registry.Get(taskView.Targets[0]));
    Check(restarted.Undo(badges).Outcome == UndoOutcome.Restored && registry.Get(badges.Targets[0]) == binary, "Undo must restore the binary value.");

    RegistryValue unknown = new(true, ValueKind.Unsupported, "AAAA");
    registry.Set(seconds.Targets[0], unknown);
    Check(restarted.ApplyChoice(seconds, 1).Outcome == ChangeOutcome.Refused && registry.Get(seconds.Targets[0]) == unknown, "A value of an unsupported type must not be changed.");

    // The codec: every type round-trips, and what cannot be written is refused.
    static bool Encodes(RegistryValue value, out object data, out Microsoft.Win32.RegistryValueKind kind) => RegistryData.TryEncode(value, out data, out kind, out _);
    string[] parts = ["a", "", "b"];
    byte[] nine = [9], one = [1];
    RegistryValue multi = RegistryData.From(Microsoft.Win32.RegistryValueKind.MultiString, parts);
    Check(multi.Kind == ValueKind.MultiText && multi.Strings() is ["a", "", "b"] && Encodes(multi, out object strings, out _) && strings is string[] { Length: 3 },
        "A multi-string must keep its empty entry.");
    Check(RegistryValue.MultiText([]) != RegistryValue.MultiText([""]) && RegistryValue.MultiText([""]).Strings() is [""] && RegistryValue.MultiText([]).Strings() is [],
        "No strings and one empty string are different multi-strings.");
    RegistryValue big = RegistryData.From(Microsoft.Win32.RegistryValueKind.QWord, long.MaxValue);
    Check(Encodes(big, out object number, out var bigKind) && number is long.MaxValue && bigKind == Microsoft.Win32.RegistryValueKind.QWord, "QWORD must round-trip.");
    RegistryValue expand = RegistryData.From(Microsoft.Win32.RegistryValueKind.ExpandString, @"%SystemRoot%\x");
    Check(expand == RegistryValue.ExpandText(@"%SystemRoot%\x") && Encodes(expand, out _, out var expandKind) && expandKind == Microsoft.Win32.RegistryValueKind.ExpandString,
        "Expandable text must stay unexpanded and expandable.");
    RegistryValue none = RegistryData.From(Microsoft.Win32.RegistryValueKind.None, nine);
    Check(none.Kind == ValueKind.Untyped && Encodes(none, out object raw, out var noneKind) && raw is byte[] rawBytes && rawBytes.SequenceEqual(nine) && noneKind == Microsoft.Win32.RegistryValueKind.None,
        "REG_NONE data must round-trip.");
    Check(!Encodes(RegistryData.From(Microsoft.Win32.RegistryValueKind.Unknown, one), out _, out _), "An unknown type must never be written.");
    Check(!Encodes(new RegistryValue(true, ValueKind.DWord, "x"), out _, out _) && !Encodes(new RegistryValue(true, ValueKind.Binary, "%%"), out _, out _),
        "Malformed data must be refused, not written.");
}

static void CompactionKeepsWhatUndoNeeds()
{
    string path = TempJournal();
    MemoryRegistry registry = new();
    ChangeJournal journal = new(path);
    ChangeEngine engine = new(registry, journal, 26300);
    SettingDefinition oldest = Find("privacy.typinginsights");
    Check(engine.Apply(oldest, calm: true).Outcome == ChangeOutcome.Applied, "Setup: change applied.");
    // A later attempt on the same value fails, so the value's newest record is a failure, not the change.
    registry.Ignore.Add(oldest.Targets[0].Name);
    Check(engine.Apply(oldest, calm: false).Outcome == ChangeOutcome.Failed, "Setup: the later attempt fails.");
    registry.Ignore.Clear();
    // An intent a crash left open.
    SettingDefinition open = Find("taskbar.seconds");
    journal.Append(new(JournalKind.Intent, Guid.NewGuid(), open.Id, open.Targets[0].Path, open.Targets[0].Name, RegistryValue.Absent, RegistryValue.Dword(1), DateTimeOffset.UtcNow));
    // Later history on another setting, written in one go, then one more record to start compaction.
    SettingDefinition churn = Find("taskbar.widgets");
    System.Text.Json.JsonSerializerOptions json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    File.AppendAllLines(path, Enumerable.Range(0, ChangeJournal.CompactAbove).Select(i => System.Text.Json.JsonSerializer.Serialize(
        new JournalRecord(i % 2 == 0 ? JournalKind.Intent : JournalKind.Applied, new Guid(i / 2, 0, 0, new byte[8]), churn.Id, churn.Targets[0].Path, churn.Targets[0].Name,
            RegistryValue.Absent, RegistryValue.Dword(0), DateTimeOffset.UtcNow), json)));
    journal = new ChangeJournal(path);
    Check(journal.Records.Count > ChangeJournal.CompactAbove, "Setup: the journal is over its bound.");
    journal.Append(new(JournalKind.Undone, Guid.NewGuid(), churn.Id, churn.Targets[0].Path, churn.Targets[0].Name, RegistryValue.Dword(0), RegistryValue.Absent, DateTimeOffset.UtcNow));
    Check(journal.Records.Count <= ChangeJournal.CompactAbove, "The journal must stay bounded.");
    ChangeJournal reloaded = new(path);
    Check(reloaded.Records.Count == journal.Records.Count, "Compaction must have reached the disk.");
    Check(reloaded.Records.Any(r => r.Kind == JournalKind.Failed && r.SettingId == oldest.Id), "The newest failure is kept too.");
    Check(new ChangeEngine(registry, reloaded, 26300).Inspect(oldest).CanUndo, "Compaction dropped the change behind a later failure.");
    Check(new ChangeJournal(path).Records.Any(r => r.Kind == JournalKind.Abandoned && r.SettingId == open.Id), "The open intent must survive to be reconciled.");
}

static void JournalSharesItsFile()
{
    string path = TempJournal();
    ChangeJournal journal = new(path);
    JournalRecord Record(string id) => new(JournalKind.Applied, Guid.NewGuid(), id, "p", "n", RegistryValue.Absent, RegistryValue.Dword(1), DateTimeOffset.UtcNow);
    journal.Append(Record("first"));
    // The app reading while the helper appends, and the reverse.
    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        journal.Append(Record("while read"));
    using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        Check(new ChangeJournal(path).Records.Count == 2, "Reading must work while the file is open for writing.");
    // Another program holding the file without sharing it for a moment.
    FileStream exclusive = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
    Task release = Task.Run(() => { Thread.Sleep(150); exclusive.Dispose(); });
    journal.Append(Record("after lock"));
    release.Wait();
    Check(new ChangeJournal(path).Records.Select(r => r.SettingId).SequenceEqual(["first", "while read", "after lock"]), "The append after a brief lock is missing.");
}

static void JournalIsCarriedOver()
{
    string earlierPath = TempJournal(), path = TempJournal();
    ChangeJournal earlier = new(earlierPath);
    SystemItem services = Item("system.telemetry.services");
    earlier.Append(new(JournalKind.Applied, Guid.NewGuid(), services.Id, "service:DiagTrack", "Start", RegistryValue.Dword(2), RegistryValue.Dword(4), DateTimeOffset.UtcNow));
    File.AppendAllText(earlierPath, "torn");
    ChangeJournal journal = new(path);
    using (StreamReader reader = new(earlierPath)) Check(journal.Import(reader) == 1, "The earlier record must be carried over.");
    using (StreamReader reader = new(earlierPath)) Check(journal.Import(reader) == 0, "A journal with records is never overwritten.");
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 4;
    Check(new SystemEngine(host, new ChangeJournal(path)).Inspect(services).CanUndo, "A carried-over change must stay undoable.");
}

static void SystemReconcilesInterruptedChanges()
{
    const string proxy = @"\Microsoft\Windows\Autochk\Proxy";
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 4; // the write happened before the helper stopped
    host.Services["dmwappushservice"] = 3; // this one did not
    host.Tasks[proxy] = false;
    host.UnreadableTasks.Add(proxy);
    ChangeJournal journal = new(SystemJournal());
    SystemItem services = Item("system.telemetry.services"), tasks = Item("system.telemetry.tasks");
    DateTimeOffset now = DateTimeOffset.UtcNow;
    Guid operation = Guid.NewGuid();
    journal.Append(new(JournalKind.Intent, operation, services.Id, "service:DiagTrack", "Start", RegistryValue.Dword(2), RegistryValue.Dword(4), now));
    journal.Append(new(JournalKind.Intent, operation, services.Id, "service:dmwappushservice", "Start", RegistryValue.Dword(3), RegistryValue.Dword(4), now));
    journal.Append(new(JournalKind.Intent, operation, tasks.Id, "task:" + proxy, "Enabled", RegistryValue.Dword(1), RegistryValue.Dword(0), now));
    SystemEngine engine = new(host, journal);
    Check(!engine.Inspect(services).CanUndo, "Nothing is undoable before the intents are settled.");
    Check(engine.Reconcile() == 2, "Both services must be settled; the unreadable task waits.");
    Check(journal.Records.Count(r => r.Kind == JournalKind.Recovered) == 1 && journal.Records.Count(r => r.Kind == JournalKind.Abandoned) == 1, "One took effect, one did not.");
    Check(engine.Inspect(services).CanUndo, "The change that took effect must be undoable.");
    Check(engine.Reconcile() == 0, "Settled intents are not settled again.");
    host.UnreadableTasks.Clear();
    Check(engine.Reconcile() == 1 && engine.Inspect(tasks).CanUndo, "The task is settled once it can be read.");
    Check(engine.Undo([services]).Single().Ok && host.Services["DiagTrack"] == 2 && host.Services["dmwappushservice"] == 3, "Undo restores only what took effect.");
}

static void SystemUndoStartsAutomaticServices()
{
    FakeSystemHost host = new();
    host.Services["DiagTrack"] = 2; host.Services["dmwappushservice"] = 3;
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()));
    SystemItem services = Item("system.telemetry.services");
    Check(engine.Apply([services]).Single().Ok, "Setup: services disabled.");
    SystemResult undo = engine.Undo([services]).Single();
    Check(undo.Ok && undo.Message == SystemEngine.UndoneMessage && host.Started.SequenceEqual(["DiagTrack"]),
        "Only the automatic service is started again: " + string.Join(",", host.Started) + " / " + undo.Message);
    host.FailStart.Add("DiagTrack");
    Check(engine.Apply([services]).Single().Ok, "Setup: disabled again.");
    undo = engine.Undo([services]).Single();
    Check(undo.Ok && host.Services["DiagTrack"] == 2 && undo.Message.Contains("DiagTrack service starts again after a restart", StringComparison.Ordinal),
        "A service that cannot start now must say it starts after a restart: " + undo.Message);
}

static void SystemLeavesOtherTypedPolicies()
{
    const string dataCollection = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    FakeSystemHost host = new();
    host.Write(RegistryHive2.LocalMachine, dataCollection, "AllowTelemetry", RegistryValue.FromText("1"));
    SystemEngine engine = new(host, new ChangeJournal(SystemJournal()));
    SystemResult result = engine.Apply([Item("system.telemetry.policy")]).Single();
    Check(!result.Ok && result.Message.StartsWith("AllowTelemetry: stored as another kind of value", StringComparison.Ordinal), "The text policy must be reported: " + result.Message);
    Check(host.Read(RegistryHive2.LocalMachine, dataCollection, "AllowTelemetry") == RegistryValue.FromText("1"), "The text policy must be left as it is.");
    Check(host.Read(RegistryHive2.LocalMachine, dataCollection, "MaxTelemetryAllowed") == RegistryValue.Dword(0), "The item's other values still apply.");
}

static void CatalogueReviewFixes()
{
    SettingDefinition gameBar = Find("gaming.gamebar");
    Check(!gameBar.Title.Contains("Win+G", StringComparison.Ordinal) && gameBar.Title.Contains("Xbox button", StringComparison.Ordinal), "Game Bar row: " + gameBar.Title);

    (ChangeEngine engine, MemoryRegistry registry, _, ManualClock clock) = New();
    SettingDefinition oneDrive = Find("explorer.onedrive");
    RegistryTarget pin = oneDrive.Targets[0];
    Check(engine.Inspect(oneDrive).Choice == 0, "Without the value OneDrive is not in the navigation pane.");
    Check(engine.ApplyChoice(oneDrive, 1).Outcome == ChangeOutcome.Applied && registry.Get(pin) == RegistryValue.Dword(1), "On must write 1, not remove the value.");
    clock.Advance();
    Check(engine.Undo(oneDrive).Outcome == UndoOutcome.Restored && !registry.Get(pin).Exists, "Undo must remove the value again.");
    registry.Set(pin, RegistryValue.Dword(1)); // as OneDrive registers itself
    Check(engine.Inspect(oneDrive).Choice == 1 && engine.ApplyChoice(oneDrive, 0).Outcome == ChangeOutcome.Applied && registry.Get(pin) == RegistryValue.Dword(0), "Off must write 0.");
    clock.Advance();
    Check(engine.Undo(oneDrive).Outcome == UndoOutcome.Restored && registry.Get(pin) == RegistryValue.Dword(1), "Undo must restore OneDrive's 1.");

    SettingDefinition layout = Find("start.layout");
    SettingState layoutState = engine.Inspect(layout);
    Check(!layout.InCalmProfile && layoutState.PolicyNotes is [{ } note] && note.Contains("classic Start", StringComparison.Ordinal),
        "Start layout must leave the profile and say it is for the classic Start only.");

    SettingDefinition windowsMode = Find("appearance.windows");
    Check(windowsMode.Options[engine.Inspect(windowsMode).Choice].Label == "Dark", "With no value Windows mode is dark.");
    Check(Find("appearance.apps").Options[engine.Inspect(Find("appearance.apps")).Choice].Label == "Light", "With no value app mode is light.");

    const string AutoUpdate = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    Check(Item("system.updates.notify").Actions.OfType<PolicyAction>().Any(p => p.Name == "AUOptions" && p.Value == RegistryValue.Dword(2))
          && !SystemCatalog.WeakensUpdates(AutoUpdate, "AUOptions", RegistryValue.Dword(2)), "Notify before download stays the chosen, allowed mode.");
}

static void HelperFilesAreChecked()
{
    // The state folder's security: only administrators and Windows can change it; a folder anyone else can change is refused.
    Check(AdministratorFolder.IsAdministratorsOnly(AdministratorFolder.Security()), "The helper's own folder security must count as administrator-only.");
    System.Security.AccessControl.DirectorySecurity loose = AdministratorFolder.Security();
    loose.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-5-32-545"),
        System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Allow));
    Check(!AdministratorFolder.IsAdministratorsOnly(loose), "Users able to create files must be refused.");
    System.Security.AccessControl.DirectorySecurity creator = AdministratorFolder.Security();
    creator.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-3-0"),
        System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.InheritOnly, System.Security.AccessControl.AccessControlType.Allow));
    Check(!AdministratorFolder.IsAdministratorsOnly(creator), "A CREATOR OWNER entry for what is created inside must be refused.");
    // What Windows reports for its own folder (generic rights for CREATOR OWNER) counts as changeable by others too.
    Check(!AdministratorFolder.IsAdministratorsOnly(new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        .GetAccessControl(System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Access)),
        "The Windows folder's CREATOR OWNER entry must count.");
    string root = Path.Combine(Path.GetTempPath(), "soltex-helper-files-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        try { AdministratorFolder.Check(root); Check(false, "A folder this user owns must be refused."); }
        catch (UnauthorizedAccessException) { }
        try { AdministratorFolder.Ensure(root, Path.Combine(Path.GetTempPath(), "elsewhere")); Check(false, "A folder outside the base must be refused."); }
        catch (ArgumentException) { }

        // Plain files are read and written in place.
        string request = Path.Combine(root, "request.json"), result = Path.Combine(root, "result.json");
        File.WriteAllText(request, "{\"a\":1}");
        Check(VerifiedFile.ReadAllText(root, request) == "{\"a\":1}", "A plain file must be read.");
        VerifiedFile.WriteNew(root, result, "one");
        VerifiedFile.WriteNew(root, result, "two");
        Check(File.ReadAllText(result) == "two", "A result must replace the previous one.");

        // A second hard link is refused for reading, and writing replaces the link without touching its target.
        string protectedFile = Path.Combine(root, "protected.txt"), link = Path.Combine(root, "link.json");
        File.WriteAllText(protectedFile, "keep");
        Mklink("/H", link, protectedFile);
        try { VerifiedFile.ReadAllText(root, link); Check(false, "A hard link must not be read."); }
        catch (UnauthorizedAccessException) { }
        VerifiedFile.WriteNew(root, link, "new");
        Check(File.ReadAllText(protectedFile) == "keep" && File.ReadAllText(link) == "new", "Writing over a hard link must not write through it.");

        // A folder swapped for a junction is detected on the opened handle.
        string target = Path.Combine(root, "target"), junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "request.json"), "{}");
        Mklink("/J", junction, target);
        try { VerifiedFile.ReadAllText(root, Path.Combine(junction, "request.json")); Check(false, "A file behind a junction must not be read."); }
        catch (UnauthorizedAccessException) { }
        try { VerifiedFile.WriteNew(root, Path.Combine(junction, "result.json"), "x"); Check(false, "A file behind a junction must not be written."); }
        catch (UnauthorizedAccessException) { }
        Check(!File.Exists(Path.Combine(target, "result.json")), "Nothing may be left behind the junction.");
        try { VerifiedFile.WriteNew(root, Path.Combine(junction, "request.json"), "x"); Check(false, "A file behind a junction must not be removed."); }
        catch (UnauthorizedAccessException) { }
        Check(File.ReadAllText(Path.Combine(target, "request.json")) == "{}", "The file behind the junction must be untouched.");
        try { VerifiedFile.ReadAllText(junction, Path.Combine(junction, "request.json")); Check(false, "A root that is itself a link must be refused."); }
        catch (UnauthorizedAccessException) { }
        try { VerifiedFile.ReadAllText(target, request); Check(false, "A file outside the root must be refused."); }
        catch (ArgumentException) { }
        // Above the root Windows may redirect (as it does for a packaged parent app); where the root really is counts.
        Directory.CreateDirectory(Path.Combine(target, "inner"));
        File.WriteAllText(Path.Combine(target, "inner", "request.json"), "inner");
        string redirectedRoot = Path.Combine(junction, "inner");
        Check(VerifiedFile.ReadAllText(redirectedRoot, Path.Combine(redirectedRoot, "request.json")) == "inner", "A real root reached through a redirected parent must work.");
    }
    finally
    {
        string junction = Path.Combine(root, "junction");
        if (Directory.Exists(junction)) Directory.Delete(junction); // the link only
        Directory.Delete(root, recursive: true);
    }
}

/// <summary>Makes a hard link (/H) or a directory junction (/J) the way any user can, with cmd's mklink.</summary>
static void Mklink(string kind, string link, string target)
{
    ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "/d", "/c", "mklink", kind, link, target }) start.ArgumentList.Add(argument);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("cmd did not start.");
    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    Check(process.ExitCode == 0, "mklink " + kind + " failed: " + output.Trim());
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
    /// <summary>Throw as the registry does when the key cannot be opened for writing, instead of access denied on the value.</summary>
    public bool ThrowSecurity { get; set; }

    private static string Key(RegistryHive2 hive, string path, string name) => hive + "|" + path + "|" + name;

    public RegistryValue Read(RegistryHive2 hive, string path, string name) =>
        values.TryGetValue(Key(hive, path, name), out RegistryValue value) ? value : RegistryValue.Absent;

    public void Write(string currentUserPath, string name, RegistryValue value)
    {
        NeverTouch.Guard(RegistryHive2.CurrentUser, currentUserPath, name);
        if (Throw.Contains(name))
            throw ThrowSecurity ? new System.Security.SecurityException("Requested registry access is not allowed.") : new UnauthorizedAccessException("Access to the registry key is denied.");
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
    /// <summary>Services started (in order), and services whose start fails.</summary>
    public List<string> Started { get; } = [];
    public HashSet<string> FailStart { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void StartService(string service)
    {
        if (FailStart.Contains(service)) throw new InvalidOperationException("sc.exe failed (1058): The service cannot be started.");
        Started.Add(service);
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
