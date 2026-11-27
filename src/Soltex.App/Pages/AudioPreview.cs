using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.Sessions;
using Soltex.Audio.Sonar;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>
/// Representative audio state for render captures and layout checks: five channels on long device names, apps on
/// several outputs, a Sonar setup with ChatMix. Nothing here opens a device, starts the audio engine, reads Windows
/// sessions or contacts SteelSeries Sonar; every value is made up in memory.
/// </summary>
internal static class AudioPreview
{
    /// <summary>When true the Audio page shows this data instead of connecting to anything (render captures and tests only).</summary>
    internal static bool Enabled { get; set; }

    private static readonly HostEndpoint[] Endpoints =
    [
        new("interface-in", "Analog 1 + 2 (Studio USB Audio Interface with Two Inputs)", true, true),
        new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" },
        new("cable-a-out", "CABLE-A Output (VB-Audio Cable A)", true, true) { TransportId = "vb-audio-cable-a" },
        new("cable-b-out", "CABLE-B Output (VB-Audio Cable B)", true, true) { TransportId = "vb-audio-cable-b" },
        new("voicemeeter-out", "Voicemeeter Out B1 (VB-Audio Voicemeeter VAIO)", true, true),
        new("headphones", "Headphones (Studio USB Audio Interface with Two Inputs)", false, true),
        new("monitor", "27-inch Gaming Monitor (High Definition Audio Device)", false, true),
        new("speakers", "Speakers (Onboard Audio)", false, true),
        new("cable-in", "CABLE Input (VB-Audio Virtual Cable)", false, true) { TransportId = "vb-audio-base-cable" }
    ];

    internal static IIndependentMixerConnection CreateMixerConnection() => new PreviewMixerConnection();

    private static AudioHostProfile Profile()
    {
        DspSettings voice = MicrophoneSetup.ApplySound(MicrophoneSound.Broadcast, new DspSettings { InputChannel = 0 });
        HostChannelProfile Channel(AudioBus bus, bool enabled, string? capture, string? render, double gain, bool mute = false) =>
            new() { Bus = bus, Enabled = enabled, CaptureEndpointId = capture, RenderEndpointId = render, GainDb = gain, Mute = mute };
        return AudioHostProfile.CreateDefault() with
        {
            Name = "Evening games",
            MasterGainDb = -6,
            ChatMix = 0.25,
            Channels =
            [
                Channel(AudioBus.Game, true, "cable-a-out", "headphones", -3.5),
                Channel(AudioBus.Chat, true, "cable-b-out", "headphones", 0),
                Channel(AudioBus.Media, true, "voicemeeter-out", "headphones", -12),
                Channel(AudioBus.Aux, false, null, "monitor", -80, mute: true),
                Channel(AudioBus.Microphone, true, "interface-in", "cable-in", 1.5) with { Dsp = voice }
            ]
        };
    }

    private static HostSnapshot Snapshot(AudioHostProfile profile, bool started, MicrophoneProbeStatus? test)
    {
        HostChannelStatus Status(AudioBus bus, float input, float output)
        {
            HostChannelProfile channel = profile.Channels.Single(c => c.Bus == bus);
            if (!started || !channel.Enabled)
                return new(bus, channel.Enabled ? "Stopped" : "Off", null, null, channel.Enabled ? "Audio is stopped." : "This channel is off.", null, null);
            return new(bus, "Running", channel.CaptureEndpointId, channel.RenderEndpointId, "Playing on the chosen output.", input,
                new BridgeStatus(true, 480, 0, 0, 0, null) { OutputPeak = output });
        }
        return new HostSnapshot(started, profile,
        [
            Status(AudioBus.Game, 0.48f, 0.42f), Status(AudioBus.Chat, 0.22f, 0.18f), Status(AudioBus.Media, 0.71f, 0.63f),
            Status(AudioBus.Aux, 0, 0), Status(AudioBus.Microphone, 0.35f, 0.31f)
        ], Endpoints, null)
        { StartRequested = started, MicrophoneTest = test };
    }

    /// <summary>The helper's side of the preview: keeps the profile in memory and answers at once.</summary>
    private sealed class PreviewMixerConnection : IIndependentMixerConnection
    {
        private AudioHostProfile profile = Profile();
        private bool started = true;
        private string? probe;

        private HostSnapshot Current() => Snapshot(profile, started,
            probe is null ? null : new MicrophoneProbeStatus(probe, true, 48000, 2, 48000, [0.02f, 0.34f], [0.01f, 0.12f], null));

        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current());
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current());
        public Task<HostSnapshot> ApplyAsync(AudioHostProfile next, CancellationToken cancellationToken = default)
        {
            profile = next;
            return Task.FromResult(Current());
        }
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) { started = true; return Task.FromResult(Current()); }
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) { started = false; return Task.FromResult(Current()); }
        public Task<HostSnapshot> StartMicrophoneTestAsync(string endpointId, CancellationToken cancellationToken = default) { probe = endpointId; return Task.FromResult(Current()); }
        public Task<HostSnapshot> StopMicrophoneTestAsync(CancellationToken cancellationToken = default) { probe = null; return Task.FromResult(Current()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Windows apps on two outputs, including long names and a read-only system session.</summary>
    internal static AudioSessionSnapshot Sessions()
    {
        DateTimeOffset start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
        AudioSessionInfo App(string endpoint, int pid, string name, string path, double volume, double peak, bool muted = false, bool active = true) =>
            new(new AudioSessionKey(endpoint, "session-" + pid, pid, start.AddSeconds(pid)), name, path,
                active ? AudioSessionState.Active : AudioSessionState.Inactive, volume, muted, peak, false, true);
        AudioSessionInfo[] headphones =
        [
            App("headphones", 4120, "Counter-Strike 2", @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 0.82, 0.46),
            App("headphones", 5230, "Discord", @"C:\Users\Player\AppData\Local\Discord\app-1.0.9170\Discord.exe", 1.0, 0.21),
            App("headphones", 6340, "Microsoft Flight Simulator 2024 — Deluxe Edition (Ultra settings, VR mode)", @"C:\XboxGames\Microsoft Flight Simulator 2024\Content\FlightSimulator2024.exe", 0.64, 0.58),
            App("headphones", 7450, "Spotify", @"C:\Users\Player\AppData\Roaming\Spotify\Spotify.exe", 0.37, 0.0, muted: true, active: false),
            new(new AudioSessionKey("headphones", "system", 0, null), "System sounds", null, AudioSessionState.Active, 0.5, false, 0.04, true, false)
        ];
        return new AudioSessionSnapshot(start,
        [
            new("headphones", "Headphones (Studio USB Audio Interface with Two Inputs)", AudioEndpointClassification.Unknown, 0.72, false, 0.52, headphones),
            new("monitor", "27-inch Gaming Monitor (High Definition Audio Device)", AudioEndpointClassification.Unknown, 0.5, false, 0, []),
            new("sonar-game", "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", AudioEndpointClassification.KnownVirtual, 1, false, 0, [])
        ], []);
    }

    /// <summary>A connected Sonar with all five channels running, several routed apps and ChatMix in use.</summary>
    internal static SonarSnapshot Sonar()
    {
        SonarDevice[] devices =
        [
            new("vad-game", "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", "render", "game", "active", true),
            new("vad-chat", "SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", "render", "chatRender", "active", true),
            new("vad-media", "SteelSeries Sonar - Media (SteelSeries Sonar Virtual Audio Device)", "render", "media", "active", true),
            new("vad-aux", "SteelSeries Sonar - Aux (SteelSeries Sonar Virtual Audio Device)", "render", "aux", "active", true),
            new("vad-mic", "SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", "capture", "chatCapture", "active", true),
            new("headset", "Headphones (Wireless Gaming Headset Base Station)", "render", "", "active", false),
            new("monitor", "27-inch Gaming Monitor (High Definition Audio Device)", "render", "", "active", false),
            new("speakers", "Speakers (Onboard Audio)", "render", "", "active", false),
            new("headset-mic", "Microphone (Wireless Gaming Headset Base Station)", "capture", "", "active", false),
            new("interface-in", "Analog 1 + 2 (Studio USB Audio Interface with Two Inputs)", "capture", "", "active", false)
        ];
        SonarChannel Channel(string id, string name, string role, double volume, bool muted, string device, string vad) =>
            new(id, name, volume, muted, device, true, role) { VirtualDeviceId = vad };
        SonarChannel[] channels =
        [
            new("master", "Master", 0.8, false, null, true, "Master"),
            Channel("game", "Game", "game", 0.76, false, "headset", "vad-game"),
            Channel("chat", "Chat", "chatRender", 0.9, false, "headset", "vad-chat"),
            Channel("media", "Media", "media", 0.45, false, "monitor", "vad-media"),
            Channel("aux", "Aux", "aux", 0.6, true, "headset", "vad-aux"),
            Channel("mic", "Mic", "chatCapture", 0.85, false, "headset-mic", "vad-mic")
        ];
        SonarApplication App(uint pid, string name, string device, string flow = "render") =>
            new(pid, name, device, flow) { SessionId = "session-" + pid, State = "active" };
        SonarApplication[] applications =
        [
            App(4120, "Counter-Strike 2", "vad-game"),
            App(5230, "Discord", "vad-chat"),
            App(5231, "Discord", "vad-mic", "capture"),
            App(6340, "Microsoft Flight Simulator 2024 — Deluxe Edition (Ultra settings, VR mode)", "vad-game"),
            App(7450, "Spotify", "vad-media"),
            App(8560, "Google Chrome", "headset") with { State = "inactive" }
        ];
        return new SonarSnapshot(devices, channels, applications) { Timestamp = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero) };
    }

    internal static SonarChatMix SonarChatMix() => new(0.25, "enabled");

    /// <summary>Linear peaks per Sonar channel id; the muted Aux channel shows no signal.</summary>
    internal static IReadOnlyDictionary<string, double> SonarPeaks() => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["master"] = 0.62, ["game"] = 0.55, ["chat"] = 0.28, ["media"] = 0.71, ["aux"] = 0, ["mic"] = 0.4
    };
}
