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

/// <summary>
/// What is going on around a taskbar. Each state can have its own look; when several apply, the most specific enabled
/// one wins (see <see cref="Shell.TaskbarStateRules.Precedence"/>). Saved by name, so new states can be added later.
/// </summary>
public enum TaskbarState { Desktop, VisibleWindow, MaximizedWindow, StartOpen, SearchOpen, TaskViewOpen, BatterySaver }

/// <summary>
/// The taskbar's look in one state. <paramref name="Color"/> is 0xAARRGGBB; alpha is the opacity of the tint (Clear,
/// Acrylic) and is ignored by Opaque. With <paramref name="UseAccent"/> the colour follows Windows' accent colour and only
/// the alpha of <paramref name="Color"/> is used. The Desktop state is always enabled.
/// </summary>
public sealed record TaskbarStateLook(bool Enabled, TaskbarLookKind Kind, uint Color, bool Line, bool UseAccent = false)
{
    /// <summary>The look a state starts with: Desktop as Windows draws it, the others off with a look that suits them.</summary>
    public static TaskbarStateLook DefaultFor(TaskbarState state) => state switch
    {
        TaskbarState.Desktop => new(true, TaskbarLookKind.Default, 0, true),
        TaskbarState.VisibleWindow => new(false, TaskbarLookKind.Clear, 0x33000000, true),
        TaskbarState.MaximizedWindow => new(false, TaskbarLookKind.Opaque, 0xFF202020, true),
        TaskbarState.BatterySaver => new(false, TaskbarLookKind.Opaque, 0xFF202020, true),
        _ => new(false, TaskbarLookKind.Acrylic, 0x66202020, true) // Start, Search and Task View
    };
}

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
    /// <summary>
    /// The taskbar's look per state (TranslucentTB-class). <see cref="Normalize"/> keeps every state, in order; the Desktop
    /// look with Windows' background and line leaves the taskbar exactly as Windows draws it.
    /// </summary>
    [JsonConverter(typeof(TaskbarStatesJsonConverter))]
    public IReadOnlyDictionary<TaskbarState, TaskbarStateLook> TaskbarStates { get; init; } = DefaultTaskbarStates;

    /// <summary>Every state at its starting look (see <see cref="TaskbarStateLook.DefaultFor"/>).</summary>
    public static IReadOnlyDictionary<TaskbarState, TaskbarStateLook> DefaultTaskbarStates { get; } =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(Enum.GetValues<TaskbarState>(), s => s, TaskbarStateLook.DefaultFor);

    // Version 1.0 kept one look, its colour and line, and an optional look while a window was maximized. They are read
    // only so Normalize can turn them into states, and never written again.
    [JsonInclude, JsonPropertyName("TaskbarLook"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal TaskbarLookKind? LegacyTaskbarLook { get; init; }
    [JsonInclude, JsonPropertyName("TaskbarColor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal uint? LegacyTaskbarColor { get; init; }
    [JsonInclude, JsonPropertyName("TaskbarLine"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal bool? LegacyTaskbarLine { get; init; }
    [JsonInclude, JsonPropertyName("TaskbarMaximizedLook"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    internal TaskbarLookKind? LegacyTaskbarMaximizedLook { get; init; }

    /// <summary>The look saved for <paramref name="state"/> (its starting look when none is saved).</summary>
    public TaskbarStateLook LookFor(TaskbarState state) =>
        TaskbarStates is not null && TaskbarStates.TryGetValue(state, out TaskbarStateLook? look) && look is not null ? look : TaskbarStateLook.DefaultFor(state);

    /// <summary>These settings with the look of <paramref name="state"/> changed by <paramref name="change"/>.</summary>
    public AppSettings WithTaskbarState(TaskbarState state, Func<TaskbarStateLook, TaskbarStateLook> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Dictionary<TaskbarState, TaskbarStateLook> states = Enum.GetValues<TaskbarState>().ToDictionary(s => s, LookFor);
        states[state] = change(LookFor(state));
        return this with { TaskbarStates = states };
    }
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
        TaskbarStates = NormalizeTaskbarStates(),
        LegacyTaskbarLook = null,
        LegacyTaskbarColor = null,
        LegacyTaskbarLine = null,
        LegacyTaskbarMaximizedLook = null,
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

    /// <summary>
    /// Every state once, in order, with a defined style; Desktop always enabled. A file from version 1.0 is converted:
    /// its look becomes the Desktop look, and its maximized look (when it made a difference: only over a chosen look,
    /// and only when it differed from it) becomes the enabled Window maximized look with the same colour and line. A
    /// maximized look that had no effect stays saved there, switched off.
    /// </summary>
    private Dictionary<TaskbarState, TaskbarStateLook> NormalizeTaskbarStates()
    {
        Dictionary<TaskbarState, TaskbarStateLook> states = [];
        foreach (TaskbarState state in Enum.GetValues<TaskbarState>())
        {
            TaskbarStateLook look = LookFor(state);
            if (!Enum.IsDefined(look.Kind)) look = look with { Kind = TaskbarLookKind.Default };
            states[state] = state == TaskbarState.Desktop ? look with { Enabled = true } : look;
        }
        if (LegacyTaskbarLook is null && LegacyTaskbarColor is null && LegacyTaskbarLine is null && LegacyTaskbarMaximizedLook is null) return states;

        TaskbarLookKind kind = LegacyTaskbarLook is { } old && Enum.IsDefined(old) ? old : TaskbarLookKind.Default;
        uint color = LegacyTaskbarColor ?? 0;
        bool line = LegacyTaskbarLine ?? true;
        states[TaskbarState.Desktop] = new TaskbarStateLook(true, kind, color, line);
        if (LegacyTaskbarMaximizedLook is { } maximized && Enum.IsDefined(maximized))
        {
            bool effective = kind != TaskbarLookKind.Default && maximized != kind;
            // Version 1.0 drew its "Windows default" maximized look exactly as Windows does, line included.
            states[TaskbarState.MaximizedWindow] = new TaskbarStateLook(effective, maximized, color, maximized == TaskbarLookKind.Default || line);
        }
        return states;
    }

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
/// Taskbar states as a JSON object keyed by state name, written in state order. Read leniently: a name this version does
/// not know (a state added by a newer version), or a look it cannot read (say, a style added by a newer version), skips
/// only that state, which then keeps its starting look; the other states and every other setting still load.
/// </summary>
internal sealed class TaskbarStatesJsonConverter : JsonConverter<IReadOnlyDictionary<TaskbarState, TaskbarStateLook>>
{
    public override IReadOnlyDictionary<TaskbarState, TaskbarStateLook>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip(); // not an object: every state keeps its starting look
            return null;
        }
        Dictionary<TaskbarState, TaskbarStateLook> states = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            if (!Enum.GetNames<TaskbarState>().Contains(name, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse(name, ignoreCase: true, out TaskbarState state))
            {
                reader.Skip();
                continue;
            }
            // Read the whole value first, so a look that cannot be converted leaves the reader past it.
            using JsonDocument value = JsonDocument.ParseValue(ref reader);
            try
            {
                if (value.RootElement.Deserialize<TaskbarStateLook>(options) is { } look) states[state] = look;
            }
            catch (JsonException)
            {
                // A look from a newer version: this state keeps its starting look.
            }
        }
        return states;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<TaskbarState, TaskbarStateLook> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (TaskbarState state in Enum.GetValues<TaskbarState>())
        {
            if (!value.TryGetValue(state, out TaskbarStateLook? look) || look is null) continue;
            writer.WritePropertyName(state.ToString());
            JsonSerializer.Serialize(writer, look, options);
        }
        writer.WriteEndObject();
    }
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
