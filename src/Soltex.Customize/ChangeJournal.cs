using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soltex.Customize;

/// <summary>
/// What one journal record says. <see cref="Kept"/> means the user chose to keep a value Windows or another app set
/// since, so it is no longer reported as changed back.
/// </summary>
public enum JournalKind { Intent, Applied, Failed, RolledBack, Undone, UndoSkipped, Recovered, Abandoned, Kept }

/// <summary>One durable fact about one registry value. Intent is written before the change, completion after.</summary>
public sealed record JournalRecord(
    JournalKind Kind,
    Guid OperationId,
    string SettingId,
    string Path,
    string Name,
    RegistryValue Before,
    RegistryValue After,
    DateTimeOffset At,
    string? Note = null)
{
    internal string TargetKey => SettingId + "|" + Path + "|" + Name;
}

/// <summary>
/// Append-only JSON-lines journal. Every record is flushed to disk before the caller continues, so a crash
/// leaves at worst an intent without a completion, which <see cref="ChangeEngine"/> reconciles on start.
/// Malformed lines (for example a torn final write) are skipped, never fatal.
/// </summary>
public sealed class ChangeJournal
{
    public const int CompactAbove = 4000;
    public const int KeepAfterCompaction = 2000;
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly List<JournalRecord> records = [];

    public ChangeJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (File.Exists(Path))
        {
            foreach (string line in File.ReadLines(Path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<JournalRecord>(line, Json) is { } record) records.Add(record);
                    else SkippedLines++;
                }
                catch (JsonException) { SkippedLines++; }
            }
        }
    }

    public string Path { get; }
    public int SkippedLines { get; }

    public IReadOnlyList<JournalRecord> Records
    {
        get { lock (gate) return records.ToArray(); }
    }

    public void Append(JournalRecord record)
    {
        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using (FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (StreamWriter writer = new(stream))
            {
                writer.WriteLine(JsonSerializer.Serialize(record, Json));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            records.Add(record);
            if (records.Count > CompactAbove) Compact();
        }
    }

    /// <summary>Keeps recent history plus the newest record of every target, so undo stays possible for old changes.</summary>
    private void Compact()
    {
        HashSet<JournalRecord> keep = [.. records.Skip(records.Count - KeepAfterCompaction)];
        foreach (IGrouping<string, JournalRecord> target in records.GroupBy(r => r.TargetKey))
            keep.Add(target.Last());
        List<JournalRecord> kept = records.Where(keep.Contains).ToList();
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, kept.Select(r => JsonSerializer.Serialize(r, Json)));
            Soltex.Shared.AtomicFileReplace.Move(temporary, Path);
            records.Clear();
            records.AddRange(kept);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
