using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soltex.Customize;

namespace Soltex.App.Settings;

/// <summary>
/// An exported profile: this app's preferences, the Windows options this app has set (setting id → choice), and the full
/// De-Windows items it applied. Restoring it on another PC (or after a reinstall) gives the same setup.
/// </summary>
public sealed record SettingsProfile(int FormatVersion, AppSettings App, IReadOnlyDictionary<string, int> WindowsChoices, IReadOnlyList<string> SystemItems)
{
    /// <summary>
    /// 2: the privacy, tips, Start/search and File Explorer switches name the feature (Off = the De-Windows value), so their
    /// two options swapped places compared with format 1. Format 1 files are converted on load.
    /// </summary>
    public const int CurrentFormat = 2;
    // The app settings inside may be as large as the settings store allows, plus room for the Windows choices.
    private const long MaximumBytes = AppSettingsStore.MaximumBytes + 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Captures the current setup (reads Windows; changes nothing).</summary>
    public static SettingsProfile Capture(AppSettings app, ChangeEngine engine, SystemEngine system)
    {
        Dictionary<string, int> choices = [];
        foreach (SettingDefinition definition in engine.UndoableSettings(CalmCatalog.All))
            if (engine.Inspect(definition).Choice is int choice and >= 0) choices[definition.Id] = choice;
        string[] items = [.. SystemCatalog.All.Select(system.Inspect).Where(s => s.State == SystemItemState.Applied && s.CanUndo).Select(s => s.Item.Id)];
        // What was reported as changed on this PC means nothing on another one.
        return new SettingsProfile(CurrentFormat, app with { ReportedDrift = null }, choices, items);
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Reads a profile. Unknown settings and items are dropped; app preferences are normalized.</summary>
    public static SettingsProfile Load(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("That file is too large to be a settings profile.");
        SettingsProfile profile = JsonSerializer.Deserialize<SettingsProfile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The file is empty.");
        if (profile.FormatVersion > CurrentFormat) throw new InvalidDataException("This profile was saved by a newer version.");
        return profile with
        {
            App = ((profile.App ?? new AppSettings()) with { ReportedDrift = null }).Normalize(),
            WindowsChoices = (profile.WindowsChoices ?? new Dictionary<string, int>())
                .Where(c => CalmCatalog.All.FirstOrDefault(d => d.Id == c.Key) is { } d && c.Value >= 0 && c.Value < d.Options.Count)
                .ToDictionary(c => c.Key, c => profile.FormatVersion < 2 && SwappedInFormat2(c.Key) ? 1 - c.Value : c.Value),
            SystemItems = [.. (profile.SystemItems ?? []).Where(id => SystemCatalog.All.Any(i => i.Id == id)).Distinct()]
        };
    }

    /// <summary>The feature-named switches whose [Off, On] order is the reverse of format 1's [Windows default, De-Windows].</summary>
    internal static bool SwappedInFormat2(string id) =>
        CalmCatalog.All.FirstOrDefault(d => d.Id == id) is { IsSwitch: true, Options.Count: 2 } d && d.Source.StartsWith("DW-", StringComparison.Ordinal);

    /// <summary>
    /// Paths in the app settings that are not on a local drive: network shares, mapped network drives, device paths or
    /// relative paths. Opening them can send the user's Windows sign-in to another computer.
    /// </summary>
    public IReadOnlyList<string> NonLocalPaths() =>
        [.. AppPaths(App).Where(p => !IsLocalPath(p)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The same profile without any path that <see cref="NonLocalPaths"/> lists.</summary>
    public SettingsProfile WithoutNonLocalPaths() => this with
    {
        App = App with
        {
            LiveWallpaper = App.LiveWallpaper is { } wallpaper && !IsLocalPath(wallpaper) ? null : App.LiveWallpaper,
            DesktopGroups = [.. App.DesktopGroups.Select(g => g with
            {
                Folder = g.Folder is { } folder && !IsLocalPath(folder) ? null : g.Folder,
                Items = [.. g.Items.Where(IsLocalPath)]
            })]
        }
    };

    private static IEnumerable<string> AppPaths(AppSettings app)
    {
        if (app.LiveWallpaper is { } wallpaper) yield return wallpaper;
        foreach (DesktopGroup group in app.DesktopGroups)
        {
            if (group.Folder is { } folder) yield return folder;
            foreach (string item in group.Items) yield return item;
        }
    }

    /// <summary>True for a fully qualified path on a drive letter that is not a mapped network drive.</summary>
    internal static bool IsLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string candidate = path.Trim().Replace('/', '\\');
        if (candidate.StartsWith(@"\\?\", StringComparison.Ordinal) && !candidate.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[4..];
        if (candidate.Length < 3 || !char.IsAsciiLetter(candidate[0]) || candidate[1] != ':' || candidate[2] != '\\') return false;
        try { return new DriveInfo(candidate[..1]).DriveType != DriveType.Network; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Applies the Windows choices through the change engine (journalled and undoable). Returns the results.</summary>
    public IReadOnlyList<ChangeResult> ApplyWindowsChoices(ChangeEngine engine) =>
        [.. WindowsChoices.Select(c => engine.ApplyChoice(CalmCatalog.All.First(d => d.Id == c.Key), c.Value))];
}
