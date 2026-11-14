using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

public enum AudioBus { Game, Chat, Media, Aux, Microphone }

public sealed record HostChannelProfile
{
    public AudioBus Bus { get; init; }
    public bool Enabled { get; init; }
    public string? CaptureEndpointId { get; init; }
    public string? RenderEndpointId { get; init; }
    public double GainDb { get; init; }
    public bool Mute { get; init; }
    public DspSettings Dsp { get; init; } = new();
    public IReadOnlyList<string> OutputPriorityIds { get; init; } = [];
    public IReadOnlyList<string> ExcludedOutputIds { get; init; } = [];
    public bool ManualPin { get; init; } = true;
    public bool AutoReturn { get; init; } = true;
}

public sealed record AudioHostProfile
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Default";
    public double MasterGainDb { get; init; }
    public bool MasterMute { get; init; }
    public double ChatMix { get; init; }
    public HostStreamMixProfile StreamMix { get; init; } = new();
    public IReadOnlyList<HostChannelProfile> Channels { get; init; } = Enum.GetValues<AudioBus>()
        .Select(bus => new HostChannelProfile { Bus = bus }).ToArray();
    public static AudioHostProfile CreateDefault() => new();
}

public sealed record HostEndpoint(string Id, string Name, bool IsCapture, bool IsActive)
{
    /// <summary>Known shared transport identity, not inferred from an endpoint's unique ID.</summary>
    public string? TransportId { get; init; }
}

/// <summary>Peak is the observed capture endpoint input peak, not a simulated post-DSP meter.</summary>
public sealed record HostChannelStatus(AudioBus Bus, string State, string? ActiveCaptureId,
    string? ActiveRenderId, string Reason, float? Peak, BridgeStatus? Bridge);

public sealed record HostSnapshot(bool Running, AudioHostProfile Profile,
    IReadOnlyList<HostChannelStatus> Channels, IReadOnlyList<HostEndpoint> Endpoints, string? LastError)
{
    /// <summary>Explicit start intent, even while every configured endpoint is unavailable.</summary>
    public bool StartRequested { get; init; }
    public WasapiStreamMixStatus? StreamMix { get; init; }
    public MicrophoneProbeStatus? MicrophoneTest { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
