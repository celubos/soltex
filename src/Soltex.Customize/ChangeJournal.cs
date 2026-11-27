using System.Diagnostics;
using System.Text;
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
/// leaves at worst an intent without a completion, which the engines reconcile on start.
/// Malformed lines (for example a torn final write) are skipped, never fatal. The file is opened with read, write and
/// delete sharing, so the app reading it never breaks the administrator helper's append (and the reverse); a brief
/// lock by another program (antivirus, backup) is retried.
/// </summary>
public sealed class ChangeJournal
{
    public const int CompactAbove = 4000;
    public const int KeepAfterCompaction = 2000;
    private const FileShare Sharing = FileShare.ReadWrite | FileShare.Delete;
    private static readonly TimeSpan LockBudget = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly object gate = new();
    private readonly List<JournalRecord> records = [];

    public ChangeJournal(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(Path)) return;
        try
        {
            using StreamReader reader = WithRetry(() => new StreamReader(new FileStream(Path, FileMode.Open, FileAccess.Read, Sharing), Encoding.UTF8));
            records.AddRange(Parse(reader, out int skipped));
            SkippedLines = skipped;
        }
        catch (FileNotFoundException) { } // removed between the check and the open
    }

    public string Path { get; }
    public int SkippedLines { get; }

    public IReadOnlyList<JournalRecord> Records
    {
        get { lock (gate) return records.ToArray(); }
    }

    /// <summary>Reads JSON-lines records, skipping (and counting) lines that are not a record.</summary>
    internal static List<JournalRecord> Parse(TextReader reader, out int skipped)
    {
        ArgumentNullException.ThrowIfNull(reader);
        List<JournalRecord> parsed = [];
        skipped = 0;
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(line, Json) is { } record) parsed.Add(record);
                else skipped++;
            }
            catch (JsonException) { skipped++; }
        }
        return parsed;
    }

    public void Append(JournalRecord record)
    {
        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string line = JsonSerializer.Serialize(record, Json) + Environment.NewLine;
            WithRetry(() =>
            {
                using FileStream stream = new(Path, FileMode.Append, FileAccess.Write, Sharing);
                stream.Write(Encoding.UTF8.GetBytes(line));
                stream.Flush(flushToDisk: true);
                return true;
            });
            records.Add(record);
            if (records.Count > CompactAbove) Compact();
        }
    }

    /// <summary>
    /// Starts an empty journal from the records of an earlier one (for example the journal an earlier version kept in
    /// another folder), written in one durable step. Does nothing when this journal already has records or a file.
    /// Returns how many records were carried over.
    /// </summary>
    public int Import(TextReader earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        lock (gate)
        {
            if (records.Count > 0 || File.Exists(Path)) return 0;
            List<JournalRecord> carried = Parse(earlier, out _);
            if (carried.Count == 0) return 0;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            ReplaceWith(carried);
            if (records.Count > CompactAbove) Compact();
            return carried.Count;
        }
    }

    /// <summary>
    /// Keeps recent history, plus for every target the newest record of each kind (so the engines' "latest applied,
    /// undone, rolled back, kept or failed" reads come out the same and undo stays possible for old changes), plus
    /// intents that never got a completion (they are reconciled on the next start).
    /// </summary>
    private void Compact()
    {
        HashSet<JournalRecord> keep = new(ReferenceEqualityComparer.Instance);
        foreach (JournalRecord recent in records.Skip(records.Count - KeepAfterCompaction)) keep.Add(recent);
        foreach (IGrouping<(string, JournalKind), JournalRecord> newest in records.Where(r => r.Kind != JournalKind.Intent).GroupBy(r => (r.TargetKey, r.Kind)))
            keep.Add(newest.Last());
        HashSet<(Guid, string)> completed = [.. records.Where(r => r.Kind != JournalKind.Intent).Select(r => (r.OperationId, r.TargetKey))];
        foreach (JournalRecord intent in records.Where(r => r.Kind == JournalKind.Intent && !completed.Contains((r.OperationId, r.TargetKey))))
            keep.Add(intent);
        ReplaceWith([.. records.Where(keep.Contains)]);
    }

    /// <summary>Writes <paramref name="kept"/> to a temporary file, forces it to disk, then moves it over the journal.</summary>
    private void ReplaceWith(List<JournalRecord> kept)
    {
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                foreach (JournalRecord record in kept)
                    stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + Environment.NewLine));
                stream.Flush(flushToDisk: true);
            }
            Soltex.Shared.AtomicFileReplace.Move(temporary, Path);
            records.Clear();
            records.AddRange(kept);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Runs a file operation, retrying for a moment while another program holds the file without sharing it.</summary>
    private static T WithRetry<T>(Func<T> operation)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int delay = 10;
        while (true)
        {
            try { return operation(); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33 && elapsed.Elapsed < LockBudget)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 200);
            }
        }
    }
}
