using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Soltex.App.Settings;
using Soltex.Customize;
using Soltex.Security;

namespace Soltex.App.Shell;

public enum NoticeSeverity { Information, Success, Caution, Critical }

/// <summary>One plain-language event the user may want to see later (Settings › Recent events).</summary>
public sealed record Notice(DateTimeOffset At, NoticeSeverity Severity, string Area, string Message)
{
    public string When => At.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>
/// Services shared by every page. Owns long-lived backends; pages only borrow them.
/// Nothing here polls often: the protection monitor's cadence is event-driven
/// (see <see cref="ProtectionMonitor.EventBackedInterval"/>) and the drift check runs twice a day.
/// </summary>
public sealed class ShellServices : IAsyncDisposable
{
    private const int MaximumNotices = 200;
    private readonly Dispatcher dispatcher;
    private ImportFolderMonitor? importMonitor;
    private int disposed;

    public ShellServices(string? profileDirectory, AppSettingsStore settings, Dispatcher dispatcher, Func<string, ChangeEngine>? calmFactory = null)
    {
        this.dispatcher = dispatcher;
        this.calmFactory = calmFactory;
        ProfileDirectory = profileDirectory;
        ExplicitProfile = profileDirectory is not null;
        Settings = settings;
        DataRoot = profileDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.DataFolderName);
        Security = SecurityRuntime.CreateDefault(profileDirectory is null ? null : Path.Combine(profileDirectory, "Security"));
        AudioPreferencesDirectory = Path.Combine(DataRoot, "Audio");
        Utilities = new UtilityService(settings);
        SystemChanges = new SystemChanges(DataRoot, ExplicitProfile, () => dispatcher.InvokeAsync(RaiseSystemChanged));
        TaskbarLook = new TaskbarLookService(settings, DataRoot);
        Desktop = new Desktop.DesktopService(settings);
        Drift = new DriftWatcher(this);
        Wallpaper = new Personalize.LiveWallpaper(settings);
        settings.Changed += next => { if (keysAttached) Keys.Set(next.KeyRemaps); };
    }

    private bool keysAttached;

    /// <summary>Notices when something this app set is different now, and offers to put it back.</summary>
    public DriftWatcher Drift { get; }

    /// <summary>Automatic dark mode by time of day. Created by <see cref="AttachInputTools"/>, because it follows settings changes from then on.</summary>
    public Personalize.ThemeSchedule? Theme { get; private set; }

    /// <summary>Video desktop background.</summary>
    public Personalize.LiveWallpaper Wallpaper { get; }

    /// <summary>Shift+drag window zones. Created by <see cref="AttachInputTools"/>, because it follows settings changes from then on.</summary>
    public Tools.WindowZonesService? Zones { get; private set; }

    /// <summary>Key remapping (a hook runs only while a remap exists).</summary>
    public Tools.KeyRemapper Keys { get; } = new();

    /// <summary>
    /// Starts zones, the dark mode schedule, the live wallpaper, the drift check and key remapping for the real session
    /// (never in tests or render captures). With default settings none of them hooks input or writes to Windows.
    /// </summary>
    public void AttachInputTools()
    {
        if (disposed != 0) return;
        Zones ??= new Tools.WindowZonesService(Settings);
        Zones.Attach();
        if (Theme is null)
        {
            Theme = new Personalize.ThemeSchedule(Settings);
            // App mode and Windows mode rows show the new values when the schedule switches.
            Theme.Switched += _ => dispatcher.InvokeAsync(RaiseCalmChanged);
        }
        Theme.Attach();
        Wallpaper.Attach();
        Drift.Attach();
        keysAttached = true;
        Keys.Set(Settings.Current.KeyRemaps);
    }

    /// <summary>
    /// Saves a settings change. When the file cannot be written the problem is reported in Recent events instead of
    /// thrown. Returns false when the change was not saved.
    /// </summary>
    public bool TryUpdateSettings(Func<AppSettings, AppSettings> change, string area = "Settings")
    {
        try
        {
            Settings.Update(change);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report(NoticeSeverity.Caution, area, "A change could not be saved: " + error.Message);
            return false;
        }
    }

    /// <summary>Desktop groups and widgets. Attached to the shell window when it opens.</summary>
    public Desktop.DesktopService Desktop { get; }

    /// <summary>Taskbar look (clear, solid, acrylic, blur). Attached to the shell window when it opens.</summary>
    public TaskbarLookService TaskbarLook { get; }

    /// <summary>The full (administrator) De-Windows profile.</summary>
    internal SystemChanges SystemChanges { get; }

    /// <summary>Keep awake, always-on-top and colour-picker shortcuts. Attached to the shell window when it opens.</summary>
    public UtilityService Utilities { get; }

    public string? ProfileDirectory { get; }
    public bool ExplicitProfile { get; }
    public string DataRoot { get; }
    public string AudioPreferencesDirectory { get; }
    public string CrashReportDirectory => Path.Combine(DataRoot, "logs", "crash");
    public AppSettingsStore Settings { get; }
    public SecurityRuntime Security { get; }
    public ProtectionMonitor? Protection { get; private set; }
    public ObservableCollection<Notice> Notices { get; } = [];

    private ChangeEngine? calm;
    private readonly Func<string, ChangeEngine>? calmFactory;

    /// <summary>The Calm Windows engine, created on first use so startup never reads the journal.</summary>
    public ChangeEngine Calm
    {
        get
        {
            if (calm is not null) return calm;
            string journalPath = Path.Combine(DataRoot, "Customize", "journal-v1.jsonl");
            calm = calmFactory?.Invoke(journalPath) ?? new ChangeEngine(
                new WindowsRegistryStore(),
                new ChangeJournal(journalPath),
                Environment.OSVersion.Version.Build,
                new WindowsSettingsNotifier());
            if (calm.RecoveredOnStart > 0)
                Report(NoticeSeverity.Information, "History", $"{calm.RecoveredOnStart} change(s) interrupted last time were checked and recorded.");
            return calm;
        }
    }

    /// <summary>Raised after any Calm Windows change so pages showing state can refresh.</summary>
    public event Action? CalmChanged;
    public void RaiseCalmChanged() => CalmChanged?.Invoke();

    /// <summary>
    /// Raised on the UI thread after the administrator helper ran (full De-Windows apply or undo, including Put back),
    /// whatever its result, so pages showing system state can read it again.
    /// </summary>
    public event Action? SystemChanged;
    public void RaiseSystemChanged() => SystemChanged?.Invoke();

    public event Action<ProtectionMonitorUpdate>? ProtectionUpdated;

    public void Start()
    {
        if (Protection is not null) return;
        Protection = new ProtectionMonitor(Security.Defender, new WindowsSecurityChangeMonitor());
        Protection.Updated += update => dispatcher.InvokeAsync(() => ProtectionUpdated?.Invoke(update));
        Protection.Start();
        importMonitor = new ImportFolderMonitor(Security.ImportsPath, Security.Assessor, HandleImportAsync);
        if (Settings.LoadProblem is { } problem) Report(NoticeSeverity.Caution, "Settings", problem);
    }

    public void Report(NoticeSeverity severity, string area, string message)
    {
        void Add()
        {
            Notices.Insert(0, new Notice(DateTimeOffset.Now, severity, area, message));
            while (Notices.Count > MaximumNotices) Notices.RemoveAt(Notices.Count - 1);
        }
        if (dispatcher.CheckAccess()) Add(); else dispatcher.InvokeAsync(Add);
    }

    public async Task AuditAsync(string kind, SecurityEventSeverity severity, string summary, string? subject = null, string? detail = null)
    {
        try { await Security.AuditLog.AppendAsync(kind, severity, summary, subject, detail).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Report(NoticeSeverity.Caution, "Activity log", "An activity record could not be saved: " + error.Message);
        }
    }

    private async Task HandleImportAsync(FileAssessment assessment)
    {
        SecurityEventSeverity severity = assessment.ShouldBlock ? SecurityEventSeverity.Critical
            : assessment.Verdict == ContentVerdict.ReviewRecommended ? SecurityEventSeverity.Warning
            : SecurityEventSeverity.Information;
        await AuditAsync("import.assessment", severity, assessment.Verdict.ToString(), assessment.Path, assessment.Detail).ConfigureAwait(false);
        Report(assessment.ShouldBlock ? NoticeSeverity.Critical : NoticeSeverity.Information, "Protected imports",
            $"{Path.GetFileName(assessment.Path)}: {Plain.Verdict(assessment.Verdict)}");
    }

    /// <summary>
    /// Await from the UI thread: the continuations must stay there, because the services below own windows, hotkeys and
    /// event hooks. Each one is disposed even when another fails.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { if (importMonitor is not null) await importMonitor.DisposeAsync().ConfigureAwait(true); }
        catch (Exception error) { Trace.WriteLine("Import monitor shutdown failed: " + error); }
        try { if (Protection is not null) await Protection.DisposeAsync().ConfigureAwait(true); }
        catch (Exception error) { Trace.WriteLine("Protection monitor shutdown failed: " + error); }
        IDisposable?[] owned = [Utilities, TaskbarLook, Desktop, Zones, Keys, Theme, Drift, Wallpaper, Security];
        foreach (IDisposable? service in owned)
        {
            try { service?.Dispose(); }
            catch (Exception error) { Trace.WriteLine(service!.GetType().Name + " shutdown failed: " + error); }
        }
    }
}

/// <summary>Plain-language wording for backend states. One place so pages stay consistent.</summary>
internal static class Plain
{
    public static string Verdict(ContentVerdict verdict) => verdict switch
    {
        ContentVerdict.Clean => "no threat found",
        ContentVerdict.Allowed => "allowed by your earlier choice",
        ContentVerdict.ReviewRecommended => "worth a second look before you open it",
        ContentVerdict.BlockedByPolicy => "blocked by a policy on this PC",
        ContentVerdict.Malware => "Microsoft Defender identified a threat",
        ContentVerdict.Unavailable => "could not be checked right now",
        _ => "the check failed"
    };

    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : bytes < 10 ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Ago(DateTimeOffset? when)
    {
        if (when is null) return "unknown";
        TimeSpan age = DateTimeOffset.UtcNow - when.Value;
        if (age < TimeSpan.FromMinutes(2)) return "just now";
        if (age < TimeSpan.FromHours(2)) return $"{(int)age.TotalMinutes} minutes ago";
        if (age < TimeSpan.FromDays(2)) return $"{(int)age.TotalHours} hours ago";
        return $"{(int)age.TotalDays} days ago";
    }
}
