using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Soltex.Audio.WindowsRouting;

public sealed record AppRoutingRule(string ExecutablePath, RoutingDirection Direction, string? EndpointId);
public sealed record AppRoutingProfile(int Version, IReadOnlyList<AppRoutingRule> Rules);

/// <summary>Intent only. Loading/importing these rules never changes a Windows policy.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The async-only semaphore never exposes or allocates its WaitHandle. Retain it while pending store operations drain.")]
public sealed class RoutingProfileStore
{
    internal const int MaximumBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    public RoutingProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        path = Path.Combine(Path.GetFullPath(directory), "application-routes-v1.json");
    }
    public async Task<AppRoutingProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCore(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(AppRoutingProfile profile, CancellationToken cancellationToken = default)
    {
        AppRoutingProfile valid = Validate(profile);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteAtomic(path, valid, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async Task<AppRoutingProfile> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var profile = Validate(await ReadBounded<AppRoutingProfile>(sourcePath, cancellationToken).ConfigureAwait(false));
        await SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default) =>
        await WriteAtomic(Path.GetFullPath(destinationPath), await LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    public async Task UpsertAsync(AppRoutingRule rule, CancellationToken cancellationToken = default)
    {
        rule = Validate(new(1, [rule])).Rules[0];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await LoadCore(cancellationToken).ConfigureAwait(false);
            await WriteAtomic(path, Validate(new(1, profile.Rules.Where(r => !SameKey(r, rule)).Append(rule).ToArray())), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string executablePath, RoutingDirection direction, CancellationToken cancellationToken = default)
    {
        var key = Validate(new(1, [new(executablePath, direction, null)])).Rules[0];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await LoadCore(cancellationToken).ConfigureAwait(false);
            await WriteAtomic(path, new AppRoutingProfile(1, profile.Rules.Where(r => !SameKey(r, key)).ToArray()), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    internal async Task RestoreIfOwnedAsync(AppRoutingRule expected, AppRoutingRule? previous)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var profile = await LoadCore(CancellationToken.None).ConfigureAwait(false);
            var current = profile.Rules.FirstOrDefault(r => SameKey(r, expected));
            if (current is not null && current.EndpointId == expected.EndpointId)
            {
                var retained = profile.Rules.Where(r => !SameKey(r, expected));
                await WriteAtomic(path, new AppRoutingProfile(1, (previous is null ? retained : retained.Append(previous)).ToArray()), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }
    private async Task<AppRoutingProfile> LoadCore(CancellationToken cancellationToken) =>
        File.Exists(path) ? Validate(await ReadBounded<AppRoutingProfile>(path, cancellationToken).ConfigureAwait(false)) : new(1, []);
    private static bool SameKey(AppRoutingRule a, AppRoutingRule b) => a.Direction == b.Direction &&
        string.Equals(a.ExecutablePath, b.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    internal static AppRoutingProfile Validate(AppRoutingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version != 1 || profile.Rules is null || profile.Rules.Count > 512)
            throw new InvalidDataException("Unsupported routing profile version or too many rules (maximum 512).");
        List<AppRoutingRule> rules = [];
        foreach (var rule in profile.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.ExecutablePath) || rule.ExecutablePath.Length > 32767 ||
                !Path.IsPathFullyQualified(rule.ExecutablePath) || !rule.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Enum.IsDefined(rule.Direction) || rule.EndpointId is { Length: > 2048 } || rule.EndpointId is not null && string.IsNullOrWhiteSpace(rule.EndpointId))
                throw new InvalidDataException("Each rule requires an absolute executable path, valid direction and a bounded endpoint identity or null.");
            var normalized = rule with { ExecutablePath = Path.GetFullPath(rule.ExecutablePath) };
            if (rules.Any(r => SameKey(r, normalized))) throw new InvalidDataException("Duplicate executable/direction rule.");
            rules.Add(normalized);
        }
        return new(1, rules.AsReadOnly());
    }
    internal static async Task<T> ReadBounded<T>(string source, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Routing document exceeds 1 MiB.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty routing document.");
    }
    internal static async Task WriteAtomic<T>(string destination, T value, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(destination), directory = Path.GetDirectoryName(full)!;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Routing document exceeds 1 MiB.");
        Directory.CreateDirectory(directory);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Soltex.Shared.AtomicFileReplace.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
