using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.RemoteAssist;
using Soltex.Security;

namespace Soltex.App.Pages;

public partial class ToolsPage : UserControl, IShellPage, IDisposable
{
    internal static readonly SearchEntry[] Entries =
    [
        new("Tools", "Tools", Destination.Tools, "top", "utilities"),
        new("Text extractor", "Tools › More tools", Destination.Tools, "textextractor", "PowerToys", "OCR", "copy text from screen", "screenshot text"),
        new("Window zones", "Tools › More tools", Destination.Tools, "zones", "PowerToys", "FancyZones", "snap", "layout", "tile windows"),
        new("Batch rename", "Tools › More tools", Destination.Tools, "rename", "PowerToys", "PowerRename", "rename files", "regex"),
        new("Key remapping", "Tools › More tools", Destination.Tools, "keys", "PowerToys", "Keyboard Manager", "remap", "caps lock"),
        new("Startup apps", "Tools", Destination.Tools, "startup", "apps that start at sign-in", "autostart", "boot", "run at startup", "task manager"),
        new("Let someone help me remotely", "Tools › Remote help", Destination.Tools, "remote", "rustdesk", "remote desktop", "share screen", "support", "teamviewer"),
        new("Connect to someone else's PC", "Tools › Remote help", Destination.Tools, "connect", "rustdesk id", "help a friend", "remote control"),
        new("Quick scan with Microsoft Defender", "Tools › Microsoft Defender", Destination.Tools, "scan", "virus scan", "antivirus", "malware", "defender"),
        new("Scan a file or folder", "Tools › Microsoft Defender", Destination.Tools, "scanfile", "check file", "is this file safe", "download"),
        new("Update security intelligence", "Tools › Microsoft Defender", Destination.Tools, "update", "definitions", "signatures"),
        new("Recent Microsoft Defender events", "Tools › Microsoft Defender", Destination.Tools, "events", "detections", "threat history"),
        new("Protected imports folder", "Tools › Protected imports and quarantine", Destination.Tools, "imports", "untrusted files", "downloads"),
        new("Quarantined files", "Tools › Protected imports and quarantine", Destination.Tools, "quarantine", "restore file", "delete threat")
    ];

    private sealed record QuarantineRow(QuarantineEntry Entry)
    {
        public string Title => Path.GetFileName(Entry.OriginalPath);
        public string Detail => Entry.Detection + " · quarantined " + Entry.QuarantinedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    private readonly ShellContext context;
    private readonly PageAnchors anchors = new();
    private readonly StartupAppsPanel startup;
    private RemoteAssistExecutable? rustDesk;
    private AuthenticodeVerificationResult? rustDeskTrust;
    private CancellationTokenSource? operation;
    private bool quarantineLoaded;

    internal ToolsPage(ShellContext context)
    {
        this.context = context;
        InitializeComponent();
        context.Services.Utilities.Changed += () => Dispatcher.InvokeAsync(RenderUtilities);
        RenderUtilities();
        PowerToolsPanel more = new(context);
        MoreTools.Content = more;
        startup = new StartupAppsPanel(context);
        StartupAppsHost.Content = startup;
        anchors.Add("startup", startup);
        anchors.Add("textextractor", more.TextExtractorSwitch);
        anchors.Add("zones", more.ZonesList);
        anchors.Add("rename", more.RenameRow);
        anchors.Add("keys", more.RemapRow);
        anchors.Add("top", Intro);
        anchors.Add("remote", ShareButton);
        anchors.Add("connect", PeerId);
        anchors.Add("scan", QuickScan);
        anchors.Add("scanfile", ScanFile);
        anchors.Add("update", UpdateIntelligence);
        anchors.Add("events", EventsExpander);
        anchors.Add("imports", OpenImports);
        // The list is hidden while quarantine is empty; search then lands on the section's own button.
        anchors.Add("quarantine", QuarantineList, OpenImports);
        SetRustDesk(RemoteAssistExecutableLocator.FindInstalled());
    }

    public Destination Destination => Destination.Tools;
    public string Title => "Tools";
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => Entries;
    public bool Reveal(string anchor) => anchors.Reveal(anchor);

    public void OnShown()
    {
        if (!quarantineLoaded) _ = RefreshQuarantineAsync();
        // Startup apps change outside this app (Task Manager, installers, Settings › Start when I sign in to Windows).
        startup.Refresh();
    }

    public void OnHidden() { }

    public void Dispose()
    {
        operation?.Cancel();
        operation?.Dispose();
        operation = null;
        GC.SuppressFinalize(this);
    }

    // ---------- remote help ----------
    private void SetRustDesk(RemoteAssistExecutable? executable)
    {
        rustDesk = executable;
        rustDeskTrust = null;
        if (executable is not null)
        {
            try { rustDeskTrust = AuthenticodeVerifier.Verify(executable.FullPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                rustDeskTrust = new AuthenticodeVerificationResult(AuthenticodeStatus.Error, -1, "The RustDesk app could not be verified: " + error.Message);
            }
        }
        bool trusted = rustDeskTrust?.IsTrusted == true;
        RemoteState.Text = executable is null ? "RustDesk is not installed"
            : trusted ? "Ready to use RustDesk" : "This RustDesk copy could not be trusted";
        RemoteDetail.Text = executable is null
            ? "Install RustDesk from rustdesk.com, then choose it here."
            : trusted ? "Found at " + executable.FullPath
            : "Its digital signature did not check out, so it will not be started. Reinstall RustDesk from rustdesk.com.";
        RemoteGlyph.Text = trusted ? "" : "";
        RemoteTechnical.Text = executable is null ? "No executable selected."
            : $"Path: {executable.FullPath}\nSHA-256: {executable.Sha256}\nSignature: {rustDeskTrust?.Status} — {rustDeskTrust?.Detail}";
        ShareButton.IsEnabled = trusted;
        UpdateConnectState();
    }

    private void UpdateConnectState()
    {
        bool valid = RemotePeerId.TryCreate(PeerId.Text, out _, out string error);
        ConnectButton.IsEnabled = valid && rustDeskTrust?.IsTrusted == true;
        PeerHint.Text = PeerId.Text.Length == 0 ? "Enter the ID shown in their RustDesk window."
            : valid ? "Only connect to a PC whose owner asked for your help." : error;
    }

    private void PeerId_TextChanged(object sender, TextChangedEventArgs e) => UpdateConnectState();

    private void ChooseRustDesk_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new() { Title = "Choose RustDesk.exe", CheckFileExists = true, FileName = "RustDesk.exe", Filter = "RustDesk (RustDesk.exe)|RustDesk.exe" };
        if (dialog.ShowDialog(context.Owner) != true) return;
        if (!RemoteAssistExecutable.TryCreate(dialog.FileName, out RemoteAssistExecutable? executable, out string error))
        {
            context.ShowNotice(NoticeSeverity.Caution, "That file can't be used: " + error);
            return;
        }
        SetRustDesk(executable);
    }

    private bool TrustedRustDesk(out RemoteAssistExecutable executable)
    {
        executable = rustDesk!;
        if (rustDesk is null) return false;
        if (!rustDesk.VerifyUnchanged(out string changed))
        {
            context.ShowNotice(NoticeSeverity.Critical, "RustDesk changed since it was checked, so it was not started. " + changed);
            SetRustDesk(null);
            return false;
        }
        SetRustDesk(rustDesk);
        return rustDeskTrust?.IsTrusted == true;
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!TrustedRustDesk(out RemoteAssistExecutable executable)) return;
        if (MessageBox.Show(context.Owner,
                "RustDesk will open so you can share this PC with someone you trust. RustDesk asks before anyone can see or control your screen.",
                "Let someone help me", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await RecordLaunchAsync("sharing", RustDeskExternalClient.Launch(RustDeskExternalClient.CreateSharePlan(executable)));
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!TrustedRustDesk(out RemoteAssistExecutable executable)) return;
        if (!RemotePeerId.TryCreate(PeerId.Text, out RemotePeerId? peer, out string error)) { PeerHint.Text = error; return; }
        if (MessageBox.Show(context.Owner,
                "Connect only to a PC whose owner asked for your help. RustDesk will ask them to accept.",
                "Connect to their PC", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        await RecordLaunchAsync("connection", RustDeskExternalClient.Launch(RustDeskExternalClient.CreateControlPlan(executable, peer!)));
    }

    private async Task RecordLaunchAsync(string what, RemoteAssistLaunchResult result)
    {
        context.ShowNotice(result.Started ? NoticeSeverity.Success : NoticeSeverity.Caution,
            result.Started ? "RustDesk opened. Follow its prompts to finish." : "RustDesk did not open: " + result.Message);
        await context.Services.AuditAsync("remote_assist.launch",
            result.Started ? SecurityEventSeverity.Information : SecurityEventSeverity.Warning,
            $"External RustDesk {what} launch {(result.Started ? "started" : "failed")}.", detail: result.Message);
    }

    // ---------- scans ----------
    /// <summary>
    /// Runs one Microsoft Defender or quarantine task at a time. The buttons stay enabled while it runs (a second press
    /// says to wait), so the pressed button keeps focus; afterwards the keyboard returns to it, or to the nearest control
    /// left when the task removed it (Restore and Delete once the file is gone).
    /// </summary>
    private async Task RunAsync(string startedText, Func<CancellationToken, Task<string>> work, UIElement? origin = null)
    {
        if (BusyNotice()) return;
        FocusSnapshot focus = origin is null ? default : FocusSnapshot.Take(origin);
        operation = new CancellationTokenSource();
        SetScanBusy(true);
        ScanResult.Text = startedText;
        try { ScanResult.Text = await work(operation.Token); }
        catch (OperationCanceledException) { ScanResult.Text = "Stopped waiting. A scan Windows already started may continue in Windows Security."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ScanResult.Text = "That did not work: " + error.Message;
            context.Services.Report(NoticeSeverity.Caution, "Tools", ScanResult.Text);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            SetScanBusy(false);
            focus.Restore(this, QuarantineList, OpenImports, QuickScan);
        }
    }

    private void SetScanBusy(bool busy)
    {
        RestoreButton.IsEnabled = DeleteButton.IsEnabled = QuarantineList.SelectedItem is not null;
        ScanProgress.Visibility = CancelScan.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<string> DefenderAsync(Func<CancellationToken, Task<DefenderCommandResult>> command, string success, CancellationToken token)
    {
        DefenderCommandResult result = await command(token);
        await context.Services.AuditAsync("defender.command",
            result.Succeeded ? SecurityEventSeverity.Information : SecurityEventSeverity.Warning, result.Operation, detail: result.Message);
        context.Services.Protection?.RequestRefresh();
        return result.Succeeded ? success : "Microsoft Defender reported a problem: " + result.Message;
    }

    private async void QuickScan_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Quick scan running…", t => DefenderAsync(context.Services.Security.Defender.RunQuickScanAsync,
            "Quick scan finished. Any threats found are handled by Microsoft Defender and listed in Windows Security.", t), QuickScan);

    private async void UpdateIntelligence_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Updating security intelligence…", t => DefenderAsync(context.Services.Security.Defender.UpdateSecurityIntelligenceAsync,
            "Security intelligence is up to date.", t), UpdateIntelligence);

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        if (BusyNotice()) return;
        OpenFolderDialog dialog = new() { Title = "Choose a folder to scan" };
        if (dialog.ShowDialog(context.Owner) != true) return;
        string folder = dialog.FolderName;
        await RunAsync("Scanning " + folder + "…", t => DefenderAsync(c => context.Services.Security.Defender.RunCustomScanAsync(folder, c),
            "Scan of " + folder + " finished. Any threats found are handled by Microsoft Defender.", t), ScanFolder);
    }

    /// <summary>True (and says so) while a task runs, so no file or folder is asked for that could not be scanned now.</summary>
    private bool BusyNotice()
    {
        if (operation is null) return false;
        context.ShowNotice(NoticeSeverity.Information, "Another Microsoft Defender task is still running. Wait for it to finish, or choose Stop waiting.");
        return true;
    }

    private async void ScanFile_Click(object sender, RoutedEventArgs e)
    {
        if (BusyNotice()) return;
        OpenFileDialog dialog = new() { Title = "Choose a file to check", CheckFileExists = true };
        if (dialog.ShowDialog(context.Owner) != true) return;
        string path = dialog.FileName;
        await RunAsync("Checking " + Path.GetFileName(path) + "…", async token =>
        {
            FileAssessment assessment = await context.Services.Security.Assessor.AssessAsync(path, token);
            SecurityEventSeverity severity = assessment.ShouldBlock ? SecurityEventSeverity.Critical
                : assessment.Verdict == ContentVerdict.ReviewRecommended ? SecurityEventSeverity.Warning : SecurityEventSeverity.Information;
            await context.Services.AuditAsync("file.assessment", severity, assessment.Verdict.ToString(), assessment.Path, assessment.Detail);
            string name = Path.GetFileName(path);
            if (assessment.ShouldBlock && File.Exists(path) &&
                MessageBox.Show(context.Owner, $"{name}: {Plain.Verdict(assessment.Verdict)}.\n\n{assessment.Detail}\n\nMove it to quarantine? You can restore it later.",
                    "Possible threat", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                QuarantineEntry entry = await context.Services.Security.Quarantine.QuarantineAsync(path, assessment.Detail, token);
                await context.Services.AuditAsync("quarantine.add", SecurityEventSeverity.Critical, "A file was moved into quarantine.", entry.OriginalPath, entry.Detection);
                await RefreshQuarantineAsync(token);
                return name + " was moved to quarantine.";
            }
            if (File.Exists(path)) await context.Services.Security.Defender.RunCustomScanAsync(path, token);
            return $"{name}: {Plain.Verdict(assessment.Verdict)}.";
        }, ScanFile);
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async void EventsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        EventsHint.Text = "Reading Microsoft Defender's log…";
        DefenderEventQueryResult result = await context.Services.Security.Defender.GetRecentEventsAsync(TimeSpan.FromDays(7), maximumEvents: 24);
        if (!result.Succeeded)
        {
            EventsHint.Text = "Windows did not share recent events right now. Windows Security › Protection history shows them.";
            EventsList.ItemsSource = null;
            return;
        }
        EventsList.ItemsSource = result.Events.Select(ev => new
        {
            When = ev.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            What = ev.Title + (string.IsNullOrWhiteSpace(ev.Detail) ? "" : " — " + ev.Detail)
        }).ToArray();
        EventsHint.Text = result.Events.Count == 0 ? "No Microsoft Defender events in the last 7 days." : "Last 7 days, newest first. File paths are hidden.";
    }

    // ---------- imports & quarantine ----------
    private void OpenImports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(context.Services.Security.ImportsPath);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { context.Services.Security.ImportsPath }, UseShellExecute = false });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            context.ShowNotice(NoticeSeverity.Caution, "The folder could not be opened: " + error.Message);
        }
    }

    private async Task RefreshQuarantineAsync(CancellationToken token = default)
    {
        try
        {
            IReadOnlyList<QuarantineEntry> entries = await context.Services.Security.Quarantine.ListAsync(token);
            QuarantineList.ItemsSource = entries.Select(e => new QuarantineRow(e)).ToArray();
            QuarantineSummary.Text = entries.Count switch
            {
                0 => "Nothing is in quarantine.",
                1 => "1 file is in quarantine.",
                _ => entries.Count + " files are in quarantine."
            };
            QuarantineList.Visibility = QuarantineActions.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            quarantineLoaded = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            QuarantineSummary.Text = "The quarantine list could not be read: " + error.Message;
        }
    }

    private void QuarantineList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = DeleteButton.IsEnabled = QuarantineList.SelectedItem is not null;

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BusyNotice() || QuarantineList.SelectedItem is not QuarantineRow row) return;
        if (MessageBox.Show(context.Owner, $"Only restore {row.Title} if you are sure it is safe. It goes back to:\n{row.Entry.OriginalPath}",
                "Restore file", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync("Restoring…", async token =>
        {
            string restored = await context.Services.Security.Quarantine.RestoreAsync(row.Entry.Id, token);
            await context.Services.AuditAsync("quarantine.restore", SecurityEventSeverity.Warning, "A quarantine item was restored by the user.", restored);
            await RefreshQuarantineAsync(token);
            return row.Title + " was restored to " + restored + ".";
        }, RestoreButton);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (BusyNotice() || QuarantineList.SelectedItem is not QuarantineRow row) return;
        if (MessageBox.Show(context.Owner, $"Permanently delete {row.Title}? This cannot be undone.",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync("Deleting…", async token =>
        {
            await context.Services.Security.Quarantine.DeleteAsync(row.Entry.Id, token);
            await context.Services.AuditAsync("quarantine.delete", SecurityEventSeverity.Warning, "A quarantine item was permanently deleted by the user.");
            await RefreshQuarantineAsync(token);
            return row.Title + " was deleted.";
        }, DeleteButton);
    }

    private bool renderingUtilities;

    private void RenderUtilities()
    {
        renderingUtilities = true;
        try
        {
            UtilityService utilities = context.Services.Utilities;
            KeepAwakeSwitch.IsChecked = utilities.KeepAwake;
            AlwaysOnTopSwitch.IsChecked = context.Services.Settings.Current.AlwaysOnTopShortcut;
            ColorPickerSwitch.IsChecked = context.Services.Settings.Current.ColorPickerShortcut;
            AlwaysOnTopNote.Text = utilities.AlwaysOnTopProblem ?? "Win+Ctrl+T pins the active window";
            ColorPickerNote.Text = utilities.ColorPickerProblem ?? "Win+Shift+C copies the color under the pointer";
        }
        finally { renderingUtilities = false; }
    }

    private void KeepAwake_Changed(object sender, RoutedEventArgs e)
    {
        if (!renderingUtilities) context.Services.Utilities.KeepAwake = KeepAwakeSwitch.IsChecked == true;
    }

    private void Shortcut_Changed(object sender, RoutedEventArgs e)
    {
        if (renderingUtilities) return;
        bool top = AlwaysOnTopSwitch.IsChecked == true, picker = ColorPickerSwitch.IsChecked == true;
        // A settings file that can't be written is reported, and the switches show what is saved.
        if (!PageSettings.TrySave(context, s => s with { AlwaysOnTopShortcut = top, ColorPickerShortcut = picker })) RenderUtilities();
    }
}
