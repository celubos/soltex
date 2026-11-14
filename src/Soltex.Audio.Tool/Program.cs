using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Soltex.Audio;

namespace Soltex.Audio.Tool;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            await using AudioDeviceService devices = new();
            switch (args[0])
            {
                case "list" when args.Length == 1:
                    Print(await devices.GetSnapshotAsync());
                    return 0;
                case "snapshot" when args.Length == 2:
                    AudioDeviceSnapshot snapshot = await devices.GetSnapshotAsync();
                    WriteNew(args[1], snapshot);
                    Print(new { path = Path.GetFullPath(args[1]), snapshot.State, count = snapshot.Endpoints.Count });
                    return snapshot.State == AudioObservationState.Unavailable ? 1 : 0;
                case "peak" when args.Length == 2:
                    AudioPeakResult peak = await devices.ReadPeakAsync(args[1]);
                    Print(peak);
                    return peak.Peak is null ? 1 : 0;
                case "watch" when args.Length == 3:
                    if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 180)
                        throw new ArgumentException("Watch duration must be an integer from 1 to 180 seconds.");
                    return await Watch(devices, args[1], seconds);
                case "set-volume" when args.Length == 4:
                    if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double scalar) || !double.IsFinite(scalar) || scalar is < 0 or > 1)
                        throw new ArgumentException("Volume must be a finite scalar from 0 to 1.");
                    return await Change(devices, args[1], "volume", scalar, null, args[3]);
                case "set-mute" when args.Length == 4:
                    if (!bool.TryParse(args[2], out bool muted)) throw new ArgumentException("Mute must be true or false.");
                    return await Change(devices, args[1], "mute", null, muted, args[3]);
                case "restore" when args.Length is 2 or 3:
                    return await Restore(devices, args[1], ParseApply(args, 2));
                case "restore-defaults" when args.Length is 3 or 4:
                    return await RestoreDefaults(devices, args[1], args[2], ParseApply(args, 3));
                default:
                    return Usage();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static bool ParseApply(string[] args, int index)
    {
        if (args.Length == index) return false;
        if (args[index] == "--apply") return true;
        if (args[index] == "--dry-run") return false;
        throw new ArgumentException("Use --apply to restore; the default is --dry-run.");
    }

    private static async Task<int> Watch(AudioDeviceService devices, string path, int seconds)
    {
        const int maximumEvents = 128;
        ConcurrentQueue<WatchEvent> events = new();
        int observed = 0;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        void Receive(object? sender, AudioDeviceSnapshot snapshot)
        {
            int sequence = Interlocked.Increment(ref observed);
            if (sequence <= maximumEvents) events.Enqueue(new(sequence, DateTimeOffset.UtcNow, snapshot));
        }
        AudioDeviceSnapshot initial;
        devices.SnapshotChanged += Receive;
        try
        {
            initial = await devices.GetSnapshotAsync();
            WriteNew(path, new WatchCapture(1, "Watching", started, null, seconds, maximumEvents, initial, [], null, 0, 0));
            Print(new { state = "Watching", path = Path.GetFullPath(path), seconds, initial.NotificationsAvailable,
                note = "Initial snapshot saved; read-only notification capture is ready." });
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        finally { devices.SnapshotChanged -= Receive; }
        // This worker command also orders completion after any already executing event handler.
        AudioDeviceSnapshot final = await devices.GetSnapshotAsync();
        WatchEvent[] retained = events.ToArray();
        int total = Volatile.Read(ref observed);
        int dropped = Math.Max(0, total - retained.Length);
        Replace(path, new WatchCapture(1, "Completed", started, DateTimeOffset.UtcNow, seconds, maximumEvents,
            initial, retained, final, total, dropped));
        Print(new { state = "Completed", path = Path.GetFullPath(path), observedEvents = total,
            retainedEvents = retained.Length, droppedEvents = dropped, finalState = final.State });
        return initial.NotificationsAvailable && final.NotificationsAvailable &&
            initial.State != AudioObservationState.Unavailable && final.State != AudioObservationState.Unavailable ? 0 : 1;
    }

    private static async Task<int> Change(AudioDeviceService devices, string id, string operation, double? volume, bool? mute, string path)
    {
        AudioControlResult read = await devices.ReadEndpointAsync(id);
        if (!read.Succeeded || read.ConfirmedState is not AudioEndpoint before ||
            (operation == "volume" ? before.VolumeScalar is null : before.IsMuted is null))
        { Print(read); return 1; }
        ChangeJournal journal = new(1, Guid.NewGuid(), Environment.MachineName, DateTimeOffset.UtcNow,
            id, operation, "Prepared", before, null, volume, mute);
        WriteNew(path, journal); // Recovery intent must be durable before the command.
        AudioControlResult result = operation == "volume"
            ? await devices.SetVolumeAsync(id, volume!.Value)
            : await devices.SetMuteAsync(id, mute!.Value);
        journal = journal with { State = result.Succeeded ? "Applied" : "Unconfirmed", After = result.ConfirmedState };
        Replace(path, journal);
        Print(result);
        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> Restore(AudioDeviceService devices, string path, bool apply)
    {
        ChangeJournal journal = JsonSerializer.Deserialize<ChangeJournal>(File.ReadAllText(path), Json)
            ?? throw new JsonException("Empty journal.");
        if (journal.Version != 1 || journal.Machine != Environment.MachineName || journal.State is not ("Applied" or "Restored") ||
            journal.After is null || journal.Operation is not ("volume" or "mute") ||
            journal.EndpointId != journal.Before.EndpointId || journal.EndpointId != journal.After.EndpointId)
            throw new ArgumentException("Journal is unconfirmed, incompatible, or belongs to another machine. Reconcile it manually before recovery.");
        AudioEndpointRestoreResult restoration = await devices.RestoreEndpointAsync(journal.Before, journal.After, journal.Operation, dryRun: !apply);
        AudioRestoreDecision decision = restoration.Decision;
        AudioControlResult? result = restoration.Result;
        if (result?.Succeeded == true) Replace(path, journal with { State = "Restored" });
        Print(new { journal.TransactionId, dryRun = !apply, decision, result });
        return result?.Succeeded == false || decision is AudioRestoreDecision.CurrentValueChanged or AudioRestoreDecision.Unavailable ? 1 : 0;
    }

    private static async Task<int> RestoreDefaults(AudioDeviceService devices, string beforePath, string afterPath, bool apply)
    {
        // Imports only explicit role changes. Endpoint state, volume, app assignments and preferences are untouched.
        DefaultBaseline before = JsonSerializer.Deserialize<DefaultBaseline>(File.ReadAllText(beforePath), Json) ?? throw new JsonException("Missing before baseline.");
        DefaultBaseline after = JsonSerializer.Deserialize<DefaultBaseline>(File.ReadAllText(afterPath), Json) ?? throw new JsonException("Missing after baseline.");
        ValidateDefaults(before);
        ValidateDefaults(after);
        List<AudioDefaultRestoreResult> results = [];
        foreach (AudioDefaultEndpoint previous in before.Defaults)
        {
            AudioDefaultEndpoint changed = after.Defaults.Single(item => item.Direction == previous.Direction && item.Role == previous.Role);
            results.Add(await devices.RestoreDefaultAsync(previous.Direction, previous.Role,
                previous.EndpointId ?? "", changed.EndpointId ?? "", dryRun: !apply));
        }
        Print(new { dryRun = !apply, results });
        return results.Any(result => result.Error is not null || result.Decision is AudioRestoreDecision.CurrentValueChanged or AudioRestoreDecision.Unavailable) ? 1 : 0;
    }

    private static void ValidateDefaults(DefaultBaseline baseline)
    {
        if (baseline.Defaults is null || baseline.Defaults.Count != 6 ||
            baseline.Defaults.Select(item => (item.Direction, item.Role)).Distinct().Count() != 6 ||
            baseline.Defaults.Any(item => !Enum.IsDefined(item.Direction) || !Enum.IsDefined(item.Role)))
            throw new ArgumentException("Baseline must contain each of the six direction/role pairs exactly once.");
    }

    private static void WriteNew<T>(string path, T value)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value, Json);
        stream.Flush(flushToDisk: true);
    }

    private static void Replace<T>(string path, T value)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        WriteNew(temporary, value);
        Soltex.Shared.AtomicFileReplace.Move(temporary, path);
    }

    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private static int Usage()
    {
        Console.Error.WriteLine("Soltex.Audio.Tool list | snapshot <new.json> | peak <endpoint-id> | watch <new.json> <seconds:1..180> | set-volume <id> <0..1> <new-journal.json> | set-mute <id> <true|false> <new-journal.json> | restore <journal.json> [--apply|--dry-run] | restore-defaults <before.json> <after.json> [--apply|--dry-run]");
        return 2;
    }

    private sealed record ChangeJournal(int Version, Guid TransactionId, string Machine, DateTimeOffset CreatedAtUtc,
        string EndpointId, string Operation, string State, AudioEndpoint Before, AudioEndpoint? After, double? RequestedVolume, bool? RequestedMute);
    private sealed record DefaultBaseline(IReadOnlyList<AudioDefaultEndpoint> Defaults);
    private sealed record WatchEvent(int Sequence, DateTimeOffset ReceivedAtUtc, AudioDeviceSnapshot Snapshot);
    private sealed record WatchCapture(int Version, string State, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc,
        int RequestedSeconds, int MaximumRetainedEvents, AudioDeviceSnapshot Initial, IReadOnlyList<WatchEvent> Events,
        AudioDeviceSnapshot? Final, int ObservedEvents, int DroppedEvents);
}
