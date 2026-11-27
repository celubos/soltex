using System.Text.Json;
using System.Text.Json.Serialization;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

public static class HostProfileStore
{
    public const int MaximumBytes = 262144;
    internal static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = false, MaxDepth = 24, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static AudioHostProfile Validate(AudioHostProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version != 1) throw new ArgumentException("Unsupported audio profile version.");
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 80 || profile.Name.Any(char.IsControl))
            throw new ArgumentException("Profile name must contain 1–80 printable characters.");
        Range(profile.MasterGainDb, -80, 12); Range(profile.ChatMix, -1, 1);
        if (profile.Channels is null || profile.Channels.Count != 5 ||
            profile.Channels.Any(c => c is null || !Enum.IsDefined(c.Bus)) || profile.Channels.Select(c => c.Bus).Distinct().Count() != 5)
            throw new ArgumentException("A profile must contain each of the five buses exactly once.");
        List<HostChannelProfile> channels = [];
        foreach (var channel in profile.Channels)
        {
            Range(channel.GainDb, -80, 12);
            ValidateId(channel.CaptureEndpointId); ValidateId(channel.RenderEndpointId);
            if (channel.CaptureEndpointId is not null && channel.CaptureEndpointId == channel.RenderEndpointId)
                throw new ArgumentException("A channel's source and output must be different devices.");
            ArgumentNullException.ThrowIfNull(channel.Dsp);
            // Validates every DSP value without opening a device. Actual formats are checked on start. Disposed at once so
            // the native neural noise removal states a valid profile may have created do not wait for finalization.
            using (new PcmDspProcessor(192000, 2, channel.Dsp)) { }
            string[] priority = CopyIds(channel.OutputPriorityIds, 32), excluded = CopyIds(channel.ExcludedOutputIds, 64);
            channels.Add(channel with { OutputPriorityIds = priority, ExcludedOutputIds = excluded,
                Dsp = channel.Dsp with { Bands = channel.Dsp.Bands.ToArray() } });
        }
        var enabledCaptures = channels.Where(c => c.Enabled && c.CaptureEndpointId is not null).Select(c => c.CaptureEndpointId).ToArray();
        if (enabledCaptures.Distinct(StringComparer.Ordinal).Count() != enabledCaptures.Length)
            throw new ArgumentException("Two channels that are on can't use the same source. Choose a different source for one of them.");
        ArgumentNullException.ThrowIfNull(profile.StreamMix);
        ValidateId(profile.StreamMix.PersonalRenderEndpointId); ValidateId(profile.StreamMix.StreamRenderEndpointId);
        var sends = profile.StreamMix.Sends;
        if (sends is null || sends.Count != 5 || sends.Any(s => s is null || !Enum.IsDefined(s.Bus)) || sends.Select(s => s.Bus).Distinct().Count() != 5)
            throw new ArgumentException("Stream mix must contain each of the five sends exactly once.");
        foreach (var send in sends) { Range(send.PersonalGainDb, -80, 12); Range(send.StreamGainDb, -80, 12); }
        return profile with { Channels = channels.ToArray(), StreamMix = profile.StreamMix with { Sends = sends.ToArray() } };
    }

    public static AudioHostProfile Load(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new IOException("Audio profile exceeds the bounded file size.");
        return Validate(JsonSerializer.Deserialize<AudioHostProfile>(file, Json) ?? throw new JsonException("Missing profile."));
    }

    public static void Save(string path, AudioHostProfile profile)
    {
        var valid = Validate(profile);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(valid, Json);
        if (bytes.Length > MaximumBytes) throw new IOException("Audio profile exceeds the bounded file size.");
        string full = Path.GetFullPath(path), temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            Soltex.Shared.AtomicFileReplace.Move(temporary, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string[] CopyIds(IReadOnlyList<string> ids, int maximum)
    {
        if (ids is null || ids.Count > maximum) throw new ArgumentException("Device preference list exceeds its bound.");
        foreach (string id in ids) { ValidateId(id); if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Device IDs cannot be blank."); }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) throw new ArgumentException("Repeated device ID in preference list.");
        return ids.ToArray();
    }
    private static void ValidateId(string? id)
    {
        if (id is not null && (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || id.Any(char.IsControl)))
            throw new ArgumentException("Invalid endpoint ID.");
    }
    private static void Range(double value, double minimum, double maximum)
    { if (!double.IsFinite(value) || value < minimum || value > maximum) throw new ArgumentOutOfRangeException(nameof(value)); }
}
