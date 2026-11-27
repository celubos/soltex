using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Monitoring;
using Soltex.Security;

namespace Soltex.App.Pages;

public partial class MyPcPage : UserControl, IShellPage, IDisposable
{
    internal static readonly SearchEntry[] Entries =
    [
        new("Home", "Home", Destination.MyPc, "top", "my pc", "overview", "dashboard", "status"),
        new("De-Windows: apply the recommended set", "Home", Destination.MyPc, "dewindows", "debloat", "de-windows", "declutter", "ads", "bloat", "telemetry", "calm", "profile"),
        new("Protection and antivirus status", "Home", Destination.MyPc, "protection", "defender", "antivirus", "security", "virus", "smartscreen", "firewall"),
        new("Processor, memory, storage and network", "Home", Destination.MyPc, "performance", "cpu", "ram", "disk", "speed", "usage", "monitor", "task manager"),
        new("Apps using the most processor time", "Home", Destination.MyPc, "processes", "processes", "slow", "busy", "running apps"),
        new("This PC: Windows version and hardware", "Home", Destination.MyPc, "thispc", "windows version", "build", "edition", "computer name", "specs")
    ];

    private const int HistoryLength = 60;
    private static readonly TimeSpan ProtectionStaleAfter = TimeSpan.FromMinutes(15);
    private readonly ShellContext context;
    private readonly PageAnchors anchors = new();
    private readonly Queue<double> cpuHistory = new();
    private CancellationTokenSource? sampling;
    private bool shown, checking;

    internal MyPcPage(ShellContext context)
    {
        this.context = context;
        InitializeComponent();
        Subtitle.Text = WindowsDescription.Describe() + " · " + Environment.MachineName;
        anchors.Add("top", Subtitle);
        anchors.Add("dewindows", DeWindows);
        anchors.Add("protection", OpenWindowsSecurity);
        anchors.Add("performance", PerformanceCard);
        anchors.Add("processes", ProcessesExpander);
        anchors.Add("thispc", ThisPcCard);
        context.Services.ProtectionUpdated += RenderProtection;
        if (context.Services.Protection?.LastUpdate is { } last) RenderProtection(last);
        FillFacts(null);
        DeWindows.Attach(context);
        SystemPanel.Attach(context);
    }

    public Destination Destination => Destination.MyPc;
    public string Title => "Home";
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => Entries;
    public bool Reveal(string anchor) => anchors.Reveal(anchor);

    public void OnShown()
    {
        if (shown) return;
        shown = true;
        sampling = new CancellationTokenSource();
        _ = SampleLoopAsync(sampling.Token);
        DeWindows.Refresh();
        SystemPanel.OnShown();
        // Each protection check starts PowerShell, so showing the page only refreshes a result that has gone stale. The
        // monitor's own first check, Windows Security change events and its baseline cover the rest; Check again forces one.
        if (context.Services.Protection is { LastUpdate: { } last } monitor && DateTimeOffset.UtcNow - last.Observed.CheckedAtUtc > ProtectionStaleAfter)
        {
            try { monitor.RequestRefresh(); } catch (ObjectDisposedException) { }
        }
    }

    public void OnHidden()
    {
        if (!shown) return;
        shown = false;
        sampling?.Cancel();
        sampling?.Dispose();
        sampling = null;
        SystemPanel.OnHidden();
    }

    public void Dispose()
    {
        OnHidden();
        SystemPanel.Detach();
        context.Services.ProtectionUpdated -= RenderProtection;
        GC.SuppressFinalize(this);
    }

    // ---------- performance ----------
    private async Task SampleLoopAsync(CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool processes = ProcessesExpander.IsExpanded;
                SystemTelemetrySnapshot snapshot = await SystemTelemetryProvider.CaptureAsync(
                    TimeSpan.FromMilliseconds(300), processes, token);
                if (token.IsCancellationRequested) return;
                RenderPerformance(snapshot, processes);
                failures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                failures++;
                PerformanceState.Text = "Readings paused, retrying";
                if (failures == 1) context.Services.Report(NoticeSeverity.Caution, "Home", "Performance readings were unavailable for a moment: " + error.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 + failures * 2)), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void RenderPerformance(SystemTelemetrySnapshot snapshot, bool includesProcesses)
    {
        PerformanceState.Text = "Updated " + snapshot.CapturedAtUtc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
        if (snapshot.CpuPercent is double cpu)
        {
            CpuValue.Text = cpu.ToString("0", CultureInfo.CurrentCulture) + "%";
            cpuHistory.Enqueue(cpu);
            while (cpuHistory.Count > HistoryLength) cpuHistory.Dequeue();
            CpuHistory.Values = cpuHistory.ToArray();
            // One sample would draw a flat line; the chart appears with the second (its space is kept, so nothing jumps).
            CpuHistoryFrame.Visibility = cpuHistory.Count >= 2 ? Visibility.Visible : Visibility.Hidden;
        }
        else CpuValue.Text = "Unavailable";

        if (snapshot.Memory is { } memory)
        {
            MemoryValue.Text = memory.UsedPercent.ToString("0", CultureInfo.CurrentCulture) + "%";
            MemoryDetail.Text = Unbroken(Plain.Bytes(memory.UsedBytes)) + " of " + Unbroken(Plain.Bytes(memory.TotalBytes)) + " in use";
            FillFacts(memory);
        }
        else { MemoryValue.Text = "Unavailable"; MemoryDetail.Text = ""; }

        StorageVolumeTelemetry? system = snapshot.Volumes.FirstOrDefault(v =>
            v.Name.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:", StringComparison.OrdinalIgnoreCase))
            ?? (snapshot.Volumes.Count > 0 ? snapshot.Volumes[0] : null);
        if (system is not null)
        {
            StorageLabel.Text = "Storage (" + system.Name.TrimEnd('\\') + ")";
            StorageValue.Text = system.UsedPercent.ToString("0", CultureInfo.CurrentCulture) + "%";
            StorageDetail.Text = Unbroken(Plain.Bytes(system.AvailableBytes)) + " free of " + Unbroken(Plain.Bytes(system.TotalBytes));
        }
        else { StorageValue.Text = "Unavailable"; StorageDetail.Text = ""; }

        if (snapshot.Network is { } network)
        {
            NetworkValue.Text = Unbroken(Plain.Rate(network.TotalBytesPerSecond));
            NetworkDetail.Text = NetworkLine(network.ReceiveBytesPerSecond, network.SendBytesPerSecond);
        }
        else { NetworkValue.Text = "No connection"; NetworkDetail.Text = ""; }

        if (includesProcesses)
        {
            ProcessList.ItemsSource = snapshot.Processes
                .OrderByDescending(p => p.CpuPercent).Take(8)
                .Select(p => new
                {
                    p.Name,
                    Cpu = p.CpuPercent.ToString("0.0", CultureInfo.CurrentCulture) + "%",
                    Memory = Plain.Bytes(p.WorkingSetBytes)
                }).ToArray();
            ProcessesHint.Text = snapshot.InaccessibleProcessCount > 0
                ? "Some system apps are hidden because Windows does not share their details with standard users."
                : "";
            ProcessesHint.Visibility = ProcessesHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// A reading that never breaks across lines: a no-break space between number and unit, and word joiners around the
    /// slash of a rate, so a narrow column wraps between readings instead of leaving "13 KB/" on one line and "s" on the next.
    /// </summary>
    internal static string Unbroken(string reading) => reading.Replace(' ', ' ').Replace("/", "⁠/⁠", StringComparison.Ordinal);

    /// <summary>"Down 13 KB/s · Up 2 KB/s", breakable only around the dot.</summary>
    internal static string NetworkLine(double down, double up) =>
        "Down " + Unbroken(Plain.Rate(down)) + " · Up " + Unbroken(Plain.Rate(up));

    /// <summary>The narrowest the readings grid may be for four columns; below it the readings sit two by two.</summary>
    internal const double FourColumnWidth = 680;

    private void Metrics_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutMetrics(e.NewSize.Width);
    }

    /// <summary>Four readings in a row with room to spare; two by two when narrow (small window, large text).</summary>
    internal void LayoutMetrics(double width)
    {
        int columns = width >= FourColumnWidth ? 4 : 2;
        if (Metrics.Columns == columns) return;
        Metrics.Columns = columns;
        for (int i = 0; i < Metrics.Children.Count; i++)
        {
            if (Metrics.Children[i] is not FrameworkElement cell) continue;
            bool lastInRow = (i + 1) % columns == 0, firstRow = i < columns;
            cell.Margin = new Thickness(0, 0, lastInRow ? 0 : 16, columns == 2 && firstRow ? 16 : 0);
        }
    }

    private void ProcessesExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (!ProcessesExpander.IsExpanded) ProcessList.ItemsSource = null;
        ProcessesHint.Text = ProcessesExpander.IsExpanded ? "Collecting…" : "";
        ProcessesHint.Visibility = Visibility.Visible;
    }

    private void FillFacts(MemoryTelemetry? memory)
    {
        List<(string, string)> facts =
        [
            ("Computer name", Environment.MachineName),
            ("Windows", WindowsDescription.Describe()),
            ("Processor", ProcessorName()),
            ("Memory", memory is null ? "Reading…" : Plain.Bytes(memory.TotalBytes) + " installed"),
            ("Running for", Uptime()),
            ("App", ProductIdentity.DisplayName + " " + (typeof(MyPcPage).Assembly.GetName().Version?.ToString(3) ?? ""))
        ];
        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        for (int i = 0; i < facts.Count; i++)
        {
            FactsGrid.RowDefinitions.Add(new RowDefinition());
            TextBlock label = new() { Text = facts[i].Item1, Style = (Style)FindResource("Shell.Secondary"), Margin = new Thickness(0, 3, 0, 3) };
            TextBlock value = new() { Text = facts[i].Item2, Style = (Style)FindResource("Shell.Body"), Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            FactsGrid.Children.Add(label); FactsGrid.Children.Add(value);
        }
    }

    private static string ProcessorName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
            return (name.Length > 0 ? name : "Unknown processor") + " · " + Environment.ProcessorCount + " logical processors";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return Environment.ProcessorCount + " logical processors";
        }
    }

    private static string Uptime()
    {
        TimeSpan up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return up.TotalDays >= 1 ? Units((int)up.TotalDays, "day") + ", " + Units(up.Hours, "hour") : Units(up.Hours, "hour") + ", " + Units(up.Minutes, "minute");
    }

    private static string Units(int count, string unit) =>
        count.ToString(CultureInfo.CurrentCulture) + " " + unit + (count == 1 ? "" : "s");

    // ---------- protection ----------
    private void RenderProtection(ProtectionMonitorUpdate update)
    {
        DefenderHealthSnapshot h = update.Observed.ObservationSucceeded ? update.Observed : update.LastKnownGood ?? update.Observed;
        IReadOnlyList<PolicyFinding> policies = ProtectionPolicies.Read();
        List<PolicyFinding> issues = [];
        string headline, detail, glyph;

        if (!h.ObservationSucceeded)
        {
            headline = "Protection status is unavailable right now";
            detail = "Windows did not answer. Open Windows Security to check, or try again in a moment.";
            glyph = "";
        }
        else if (!h.IsProtected)
        {
            headline = "Your PC may not be protected";
            detail = "Windows reports a protection problem. Open Windows Security to see what needs fixing.";
            glyph = "";
        }
        else
        {
            bool defenderActive = h.StatusQuerySucceeded && h.RealTimeProtectionEnabled;
            headline = defenderActive ? "Protected by Microsoft Defender" : "Protected by your antivirus";
            detail = h.StatusQuerySucceeded
                ? "Real-time protection is " + (h.RealTimeProtectionEnabled ? "on" : "off") + ". Security intelligence updated " + Plain.Ago(h.AntivirusSignatureUpdatedAt) + "."
                : "Windows Security reports that an antivirus is active.";
            glyph = "";
            if (h.StatusQuerySucceeded && h.SignaturesOutOfDate)
                issues.Add(new("Security intelligence is out of date", "New threats may not be recognized until it updates."));
            if (h.StatusQuerySucceeded && !h.TamperProtected)
                issues.Add(new("Tamper protection is off", "Other software can change Microsoft Defender settings."));
            if (h.StatusQuerySucceeded && !h.CloudProtectionEnabled && !policies.Any(p => p.Feature.StartsWith("Cloud", StringComparison.Ordinal)))
                issues.Add(new("Cloud-delivered protection is off", "Brand-new threats are caught later. You can turn it on in Windows Security."));
        }

        issues.AddRange(policies);
        ProtectionHeadline.Text = headline;
        ProtectionDetail.Text = detail;
        ProtectionDetail.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProtectionGlyph.Text = issues.Count > 0 && glyph == "" ? "" : glyph;
        // The tile's colour carries the verdict at a glance: green when nothing needs attention, amber otherwise.
        bool healthy = issues.Count == 0 && h.StatusQuerySucceeded;
        ProtectionTile.SetResourceReference(Border.BackgroundProperty, healthy ? "Studio.SuccessSurfaceBrush" : "Studio.CautionSurfaceBrush");
        ProtectionGlyph.SetResourceReference(TextBlock.ForegroundProperty, healthy ? "Studio.SuccessBrush" : "Studio.CautionBrush");
        ProtectionIssues.ItemsSource = issues;
        ProtectionIssues.Visibility = issues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PolicyNote.Visibility = policies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDefinitions.Visibility = h.StatusQuerySucceeded && h.SignaturesOutOfDate ? Visibility.Visible : Visibility.Collapsed;
        ProtectionTechnical.Text =
            $"Windows Security Center: {h.WindowsSecurityCenterHealth}. Defender mode: {h.AMRunningMode ?? "not reported"}. " +
            $"Real-time {OnOff(h.RealTimeProtectionEnabled)}, behavior monitoring {OnOff(h.BehaviorMonitorEnabled)}, cloud {OnOff(h.CloudProtectionEnabled)}, tamper protection {OnOff(h.TamperProtected)}. " +
            $"Intelligence version {h.AntivirusSignatureVersion ?? "unknown"}. Last checked {h.CheckedAtUtc.ToLocalTime():g}. " +
            (update.ConsecutiveFailures > 0 ? $"The last {update.ConsecutiveFailures} checks failed; showing the last good result." : "");
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    private void OpenWindowsSecurity_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "Windows Security could not be opened: " + error.Message);
        }
    }

    private async void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        if (checking || context.Services.Protection is not { } monitor) return;
        checking = true; CheckAgain.IsEnabled = false; ProtectionHeadline.Text = "Checking protection…";
        try { RenderProtection(await monitor.RefreshOnceAsync()); }
        catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException) { _ = error; }
        finally { checking = false; CheckAgain.IsEnabled = true; }
    }

    private async void UpdateDefinitions_Click(object sender, RoutedEventArgs e)
    {
        UpdateDefinitions.IsEnabled = false;
        try
        {
            DefenderCommandResult result = await context.Services.Security.Defender.UpdateSecurityIntelligenceAsync();
            await context.Services.AuditAsync("defender.command", result.Succeeded ? SecurityEventSeverity.Information : SecurityEventSeverity.Warning, result.Operation, detail: result.Message);
            context.ShowNotice(result.Succeeded ? NoticeSeverity.Success : NoticeSeverity.Caution,
                result.Succeeded ? "Security intelligence updated." : "The update did not finish: " + result.Message);
            context.Services.Protection?.RequestRefresh();
        }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or OperationCanceledException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "The update could not start: " + error.Message);
        }
        finally { UpdateDefinitions.IsEnabled = true; }
    }
}
