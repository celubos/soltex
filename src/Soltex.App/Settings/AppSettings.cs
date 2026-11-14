using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soltex.App.Settings;

public enum ThemePreference { System, Light, Dark }
public enum DensityPreference { Comfortable, Compact }
public enum MotionPreference { System, Reduced }
/// <summary>Pages. Customize is the Privacy page (name kept so saved settings stay valid); new pages are appended.</summary>
public enum Destination { MyPc, Customize, Audio, Tools, History, Settings, Taskbar, Start, Explorer, Appearance, Multitasking, Desktop }
/// <summary>How the taskbar background is drawn (TranslucentTB-style). Values are shared with the native helper.</summary>
public enum TaskbarLookKind { Default = 0, Clear = 1, Opaque = 2, Acrylic = 3, Blur = 4 }

/// <summary>User preferences for the app itself. Never contains Windows state or secrets.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const int MinimumScalePercent = 100;
    public const int MaximumScalePercent = 200;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    /// <summary>Size of text and controls in this app, in percent. 100 follows the system size.</summary>
    public int ScalePercent { get; init; } = MinimumScalePercent;
    public DensityPreference Density { get; init; } = DensityPreference.Comfortable;
    public MotionPreference Motion { get; init; } = MotionPreference.System;
    public Destination StartPage { get; init; } = Destination.MyPc;
    /// <summary>Destinations the user chose to hide from navigation. Settings and History can never be hidden.</summary>
    public IReadOnlyList<Destination> HiddenDestinations { get; init; } = [];
    public bool StartAudioWhenSignedIn { get; init; }
    public bool CloseToTrayNoticeShown { get; init; }
    /// <summary>The one-time welcome note on the first real start.</summary>
    public bool WelcomeShown { get; init; }
    public bool ConfirmQuitWhileAudioRuns { get; init; } = true;
    /// <summary>Win+Ctrl+T pins the active window on top.</summary>
    public bool AlwaysOnTopShortcut { get; init; } = true;
    /// <summary>Win+Shift+C copies the colour under the pointer.</summary>
    public bool ColorPickerShortcut { get; init; } = true;
    /// <summary>Taskbar background. Default leaves the taskbar exactly as Windows draws it.</summary>
    public TaskbarLookKind TaskbarLook { get; init; }
    /// <summary>Taskbar colour as 0xAARRGGBB; alpha is the tint strength (Clear, Acrylic) and is ignored by Opaque.</summary>
    public uint TaskbarColor { get; init; }
    /// <summary>Show the thin line Windows draws above the taskbar.</summary>
    public bool TaskbarLine { get; init; } = true;
    /// <summary>Look used while a window is maximized; null keeps <see cref="TaskbarLook"/>.</summary>
    public TaskbarLookKind? TaskbarMaximizedLook { get; init; }
    /// <summary>Taskbar details (tweak id → choice key, see <see cref="Shell.TaskbarTweaks"/>); missing ids keep Windows' look.</summary>
    public IReadOnlyDictionary<string, string> TaskbarTweaks { get; init; } = new Dictionary<string, string>();
    /// <summary>Scrolling over the taskbar changes the volume.</summary>
    public bool TaskbarScrollVolume { get; init; }
    /// <summary>Desktop groups (Fences-class), in the order they were added.</summary>
    public IReadOnlyList<DesktopGroup> DesktopGroups { get; init; } = [];
    /// <summary>Desktop widgets (Rainmeter-class); at most one of each kind.</summary>
    public IReadOnlyList<DesktopWidget> DesktopWidgets { get; init; } = [];
    /// <summary>Groups and widgets can be moved and resized.</summary>
    public bool DesktopArrange { get; init; }
    /// <summary>Win+Shift+T copies the text in an area of the screen.</summary>
    public bool TextExtractorShortcut { get; init; } = true;
    /// <summary>Zones for Shift+drag window snapping.</summary>
    public WindowZonesLayout WindowZones { get; init; }
    /// <summary>Key remaps (each key once).</summary>
    public IReadOnlyList<KeyRemap> KeyRemaps { get; init; } = [];
    /// <summary>Switch Windows between light and dark mode by time of day.</summary>
    public bool DarkModeSchedule { get; init; }
    public TimeSpan LightFrom { get; init; } = TimeSpan.FromHours(7);
    public TimeSpan DarkFrom { get; init; } = TimeSpan.FromHours(19);
    /// <summary>A video shown as the desktop background (looping, muted), or null.</summary>
    public string? LiveWallpaper { get; init; }
    /// <summary>
    /// Setting and De-Windows item ids last reported as different from what this app set ('|'-separated, sorted),
    /// so the same report is not repeated at every start. Null when nothing is different.
    /// </summary>
    public string? ReportedDrift { get; init; }

    /// <summary>Longest path kept for the wallpaper and desktop groups; longer ones are dropped.</summary>
    public const int MaximumPathLength = 1024;
    /// <summary>Longest desktop group title kept.</summary>
    public const int MaximumTitleLength = 100;

    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        ScalePercent = Math.Clamp(ScalePercent, MinimumScalePercent, MaximumScalePercent),
        Theme = Enum.IsDefined(Theme) ? Theme : ThemePreference.System,
        Density = Enum.IsDefined(Density) ? Density : DensityPreference.Comfortable,
        Motion = Enum.IsDefined(Motion) ? Motion : MotionPreference.System,
        StartPage = Enum.IsDefined(StartPage) && StartPage is not (Destination.Settings or Destination.History) ? StartPage : Destination.MyPc,
        HiddenDestinations = (HiddenDestinations ?? []).Where(d => Enum.IsDefined(d) && d is not (Destination.Settings or Destination.History)).Distinct().ToArray(),
        TaskbarLook = Enum.IsDefined(TaskbarLook) ? TaskbarLook : TaskbarLookKind.Default,
        TaskbarMaximizedLook = TaskbarMaximizedLook is { } maximized && !Enum.IsDefined(maximized) ? null : TaskbarMaximizedLook,
        TaskbarTweaks = (TaskbarTweaks ?? new Dictionary<string, string>())
            .Where(t => Shell.TaskbarTweaks.All.Any(k => k.Id == t.Key && k.Options.Skip(1).Any(o => o.Key == t.Value)))
            .ToDictionary(t => t.Key, t => t.Value),
        DesktopGroups = [.. (DesktopGroups ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id)).DistinctBy(g => g.Id).Take(24)
            .Select(g => g with
            {
                Title = string.IsNullOrWhiteSpace(g.Title) ? "Group" : Truncate(g.Title.Trim(), MaximumTitleLength),
                Folder = string.IsNullOrWhiteSpace(g.Folder) || g.Folder.Length > MaximumPathLength ? null : g.Folder,
                Items = [.. (g.Items ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && p.Length <= MaximumPathLength).Take(DesktopGroup.MaximumItems)],
                Width = double.IsFinite(g.Width) ? Math.Clamp(g.Width, DesktopGroup.MinimumWidth, 2000) : 280,
                Height = double.IsFinite(g.Height) ? Math.Clamp(g.Height, DesktopGroup.MinimumHeight, 2000) : 220,
                Left = double.IsFinite(g.Left) ? g.Left : 24,
                Top = double.IsFinite(g.Top) ? g.Top : 24
            })],
        WindowZones = Enum.IsDefined(WindowZones) ? WindowZones : WindowZonesLayout.Off,
        LightFrom = LightFrom >= TimeSpan.Zero && LightFrom < TimeSpan.FromDays(1) ? LightFrom : TimeSpan.FromHours(7),
        DarkFrom = DarkFrom >= TimeSpan.Zero && DarkFrom < TimeSpan.FromDays(1) ? DarkFrom : TimeSpan.FromHours(19),
        LiveWallpaper = string.IsNullOrWhiteSpace(LiveWallpaper) || LiveWallpaper.Length > MaximumPathLength ? null : LiveWallpaper,
        ReportedDrift = NormalizeIdList(ReportedDrift),
        KeyRemaps = [.. (KeyRemaps ?? []).Where(r => r is not null && Tools.KeyRemapper.Keys.Any(k => k.Key == r.From) && (r.To == 0 || Tools.KeyRemapper.Keys.Any(k => k.Key == r.To)))
            .DistinctBy(r => r.From).Take(16)],
        DesktopWidgets = [.. (DesktopWidgets ?? []).Where(w => w is not null && Enum.IsDefined(w.Kind)).DistinctBy(w => w.Kind)
            .Select(w => w with { Left = double.IsFinite(w.Left) ? w.Left : 24, Top = double.IsFinite(w.Top) ? w.Top : 24 })]
    };

    /// <summary>Sorted, distinct, '|'-separated ids (at most 256, each at most 100 characters); null when empty.</summary>
    internal static string? NormalizeIdList(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) return null;
        string[] list = [.. ids.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.Length <= 100).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(256)];
        return list.Length == 0 ? null : string.Join("|", list);
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A corrupt or future-version file is preserved untouched
/// and defaults are used until the user changes something, which then replaces it.
/// </summary>
public sealed class AppSettingsStore
{
    /// <summary>
    /// Largest settings file read back. Above what <see cref="AppSettings.Normalize"/> allows (24 groups of 200 paths of up
    /// to 1,024 characters), so a file this app saved always loads; <see cref="Update"/> refuses anything larger.
    /// </summary>
    internal const long MaximumBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // File names in any language are written as they are, not as six-byte escapes per character.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly object gate = new();

    public AppSettingsStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        (Current, LoadProblem) = Load(Path);
    }

    public string Path { get; }
    public AppSettings Current { get; private set; }
    /// <summary>Plain-language reason the saved file was not used, or null.</summary>
    public string? LoadProblem { get; private set; }
    public event Action<AppSettings>? Changed;

    public static string DefaultPath(string? profileDirectory) => System.IO.Path.Combine(
        profileDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.DataFolderName),
        "Settings", "app-settings-v1.json");

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (gate)
        {
            next = change(Current).Normalize();
            // Records compare list members by reference, so compare the persisted form instead.
            string text = JsonSerializer.Serialize(next, Json);
            if (text == JsonSerializer.Serialize(Current, Json)) return;
            // Never write a file the next start would refuse to read.
            if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
                throw new IOException("Your app settings would be too large to save. Remove some desktop group items and try again.");
            Save(text);
            Current = next;
            LoadProblem = null;
        }
        Changed?.Invoke(next);
    }

    /// <summary>
    /// Keeps a copy of the saved settings file next to it (replacing an older copy) and returns its path, or null when
    /// nothing has been saved yet.
    /// </summary>
    public string? SaveCopy(string suffix)
    {
        lock (gate)
        {
            if (!File.Exists(Path)) return null;
            string copy = System.IO.Path.ChangeExtension(Path, null) + "." + suffix + ".json";
            File.Copy(Path, copy, overwrite: true);
            return copy;
        }
    }

    private void Save(string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            Soltex.Shared.AtomicFileReplace.Move(temporary, Path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static (AppSettings Settings, string? Problem) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return (new AppSettings(), null);
            if (new FileInfo(path).Length > MaximumBytes)
                return (new AppSettings(), "Your saved app settings were too large to read, so defaults are in use. The file was kept.");
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            if (loaded is null) return (new AppSettings(), "Your saved app settings were empty, so defaults are in use.");
            if (loaded.SchemaVersion > AppSettings.CurrentSchemaVersion)
                return (new AppSettings(), "Your app settings were saved by a newer version, so defaults are in use. The file was kept.");
            return (loaded.Normalize(), null);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (new AppSettings(), "Your saved app settings could not be read, so defaults are in use. The file was kept.");
        }
    }
}
