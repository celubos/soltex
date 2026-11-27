using System.Globalization;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

/// <summary>Named starting points for the microphone's processing. Custom means the saved settings match none of them.</summary>
public enum MicrophoneSound { Clean, Natural, Broadcast, NoisyRoom, Custom }

public sealed record MicrophoneSoundChoice(MicrophoneSound Sound, string Title, string Description)
{
    /// <summary>What a screen reader announces for the list item.</summary>
    public override string ToString() => Title;
}

/// <summary>A device in a setup list. For destinations, <see cref="Receiving"/> is the input name other apps choose.</summary>
public sealed record SetupEndpointOption(string Id, string Label, bool Available, string? Receiving = null)
{
    public override string ToString() => Label;
}

public sealed record MicrophoneInputOption(int? Channel, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Everything the microphone setup needs to decide, without UI: which devices to offer, sensible defaults, the
/// sound presets, and validation of the result. The output is one microphone channel profile for the helper.
/// </summary>
public static class MicrophoneSetup
{
    public static IReadOnlyList<MicrophoneSoundChoice> Sounds { get; } =
    [
        new(MicrophoneSound.Natural, "Natural voice", "Removes rumble below 80 Hz and evens out loud and soft words. A good default for most microphones."),
        new(MicrophoneSound.Broadcast, "Broadcast", "For dynamic microphones: low cut, a little less boom, more clarity, firmer leveling and a gentle gate between words."),
        new(MicrophoneSound.NoisyRoom, "Noisy room", "Natural voice plus AI noise removal: keyboards, fans, hum and room noise drop away while you talk. Adds about 30 ms."),
        new(MicrophoneSound.Clean, "Clean (no processing)", "Your microphone exactly as the interface delivers it, with only a clipping guard.")
    ];

    public static MicrophoneSoundChoice Custom { get; } = new(MicrophoneSound.Custom, "Custom (keep my settings)", "Your own adjustments from Adjust sound. Choosing a preset replaces them.");

    public static IReadOnlyList<MicrophoneInputOption> Inputs { get; } =
    [
        new(null, "All inputs (keep the device's stereo)"),
        new(0, "Input 1 only, sent to both sides"),
        new(1, "Input 2 only, sent to both sides")
    ];

    /// <summary>Virtual devices (cables, Sonar, mixers) are not offered as the physical microphone.</summary>
    public static bool IsVirtual(HostEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.TransportId is not null || ContainsAny(endpoint.Name, "VB-Audio", "Virtual", "CABLE", "Sonar", "Voicemeeter");
    }

    public static IReadOnlyList<SetupEndpointOption> Microphones(IReadOnlyList<HostEndpoint> endpoints, string? keep)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        HostEndpoint[] eligible = endpoints.Where(e => e.IsCapture && e.IsActive && !IsVirtual(e)).ToArray();
        List<SetupEndpointOption> options = eligible.Select(e => new SetupEndpointOption(e.Id, Label(e, eligible), true)).ToList();
        AddMissing(options, endpoints, keep, capture: true);
        return options;
    }

    /// <summary>Only destinations another app can pick as a microphone: playback devices with a paired input.</summary>
    public static IReadOnlyList<SetupEndpointOption> Destinations(IReadOnlyList<HostEndpoint> endpoints, string? keep)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        List<SetupEndpointOption> options = [];
        HostEndpoint[] renders = endpoints.Where(e => !e.IsCapture && e.IsActive && !ContainsAny(e.Name, "Sonar")).ToArray();
        foreach (HostEndpoint render in renders)
        {
            if (MicrophonePath.ReceivingInput(render, endpoints) is { } receiving)
                options.Add(new SetupEndpointOption(render.Id, Label(render, renders), true, receiving.Name));
        }
        AddMissing(options, endpoints, keep, capture: false);
        return options;
    }

    /// <summary>
    /// The saved microphone, otherwise the most likely real microphone: audio interfaces before webcams and capture cards.
    /// Windows' default input is no help when it is a virtual device (Sonar, Voicemeeter), which is common.
    /// </summary>
    public static string? SuggestMicrophone(IReadOnlyList<SetupEndpointOption> microphones, string? current)
    {
        ArgumentNullException.ThrowIfNull(microphones);
        if (microphones.FirstOrDefault(m => m.Id == current && m.Available) is { } saved) return saved.Id;
        return microphones.Where(m => m.Available)
            .Select((m, order) => (m.Id, Score: Likelihood(m.Label), order))
            .OrderByDescending(m => m.Score).ThenBy(m => m.order)
            .Select(m => m.Id).FirstOrDefault() ?? microphones.FirstOrDefault(m => m.Id == current)?.Id;
    }

    private static readonly string[] InterfaceHints = ["Focusrite", "Scarlett", "Clarett", "Analog", "Audient", "MOTU", "Universal Audio", "Apollo",
        "Volt", "SSL", "UMC", "Behringer", "PreSonus", "Steinberg", "Yamaha", "RODE", "RØDE", "Shure", "Wave:", "GoXLR", "XLR", "Line In", "Mic In", "Interface"];
    private static readonly string[] UnlikelyHints = ["Webcam", "Camera", "Cam)", "USB2.0 Device", "Capture", "HDMI", "Stereo Mix", "What U Hear", "Wave Out", "Monitor"];

    internal static int Likelihood(string name) =>
        3 * InterfaceHints.Count(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)) - 3 * UnlikelyHints.Count(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    public static string? SuggestDestination(IReadOnlyList<SetupEndpointOption> destinations, string? current) =>
        destinations.FirstOrDefault(d => d.Id == current)?.Id
        ?? destinations.FirstOrDefault(d => d.Available && d.Receiving?.StartsWith("CABLE Output", StringComparison.OrdinalIgnoreCase) == true)?.Id
        ?? destinations.FirstOrDefault(d => d.Available)?.Id;

    /// <summary>
    /// Replaces the processing with a preset. Keeps the input choice, level trim and mute. The presets that add make-up
    /// gain under a 0.89 ceiling also turn the safety limiter on, so loud words are turned down instead of clipped. Only a
    /// newly chosen preset does this: a saved preset is still recognised (<see cref="Identify"/>) and kept as it is.
    /// </summary>
    public static DspSettings ApplySound(MicrophoneSound sound, DspSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        DspSettings clean = new()
        {
            InputChannel = current.InputChannel, GainDb = current.GainDb, Mute = current.Mute, GainSmoothingMs = current.GainSmoothingMs,
            HeadroomDb = 0, OutputCeiling = 0.98, Bands = DspSettings.FlatBands()
        };
        EqBand[] natural = DspSettings.FlatBands();
        natural[0] = new EqBand(80, 0, 0.7071067811865476, EqFilterType.HighPass);
        CompressorSettings leveling = new(Enabled: true, ThresholdDb: -24, Ratio: 3, AttackMs: 10, ReleaseMs: 150, MakeupDb: 6);
        SafetyLimiterSettings limiter = new(Enabled: true);
        return sound switch
        {
            MicrophoneSound.Clean => clean,
            MicrophoneSound.Natural => clean with { HeadroomDb = -3, OutputCeiling = 0.89, Bands = natural, Compressor = leveling, Limiter = limiter },
            MicrophoneSound.Broadcast => clean with
            {
                HeadroomDb = -6, OutputCeiling = 0.89, Bands = BroadcastBands(), Limiter = limiter,
                Compressor = new CompressorSettings(Enabled: true, ThresholdDb: -26, Ratio: 4, AttackMs: 8, ReleaseMs: 120, MakeupDb: 9),
                Gate = new GateSettings(Enabled: true, ThresholdDb: -55, HysteresisDb: 6, AttackMs: 3, HoldMs: 120, ReleaseMs: 150, ClosedGainDb: -24)
            },
            MicrophoneSound.NoisyRoom => clean with
            {
                HeadroomDb = -3, OutputCeiling = 0.89, Bands = natural, Compressor = leveling, Limiter = limiter,
                NeuralNoiseRemoval = new NeuralNoiseRemovalSettings(Enabled: true)
            },
            _ => current
        };
    }

    /// <summary>
    /// "Noisy room" as saved before it used neural noise removal: steady-noise suppression and a downward expander. Still
    /// recognised as Noisy room, so a saved copy is kept as it is; choosing the preset again gives the current one.
    /// </summary>
    private static DspSettings LegacyNoisyRoom(DspSettings current)
    {
        EqBand[] natural = DspSettings.FlatBands();
        natural[0] = new EqBand(80, 0, 0.7071067811865476, EqFilterType.HighPass);
        return new DspSettings
        {
            InputChannel = current.InputChannel, GainDb = current.GainDb, Mute = current.Mute, GainSmoothingMs = current.GainSmoothingMs,
            HeadroomDb = -3, OutputCeiling = 0.89, Bands = natural,
            Compressor = new CompressorSettings(Enabled: true, ThresholdDb: -24, Ratio: 3, AttackMs: 10, ReleaseMs: 150, MakeupDb: 6),
            Limiter = new SafetyLimiterSettings(Enabled: true),
            NoiseSuppression = new SpectralNoiseSuppressionSettings(Enabled: true, NoiseFloorDb: -50, ReductionDb: 15),
            NoiseAttenuation = new DownwardExpanderSettings(Enabled: true, ThresholdDb: -48, Ratio: 2, MaximumAttenuationDb: 18)
        };
    }

    /// <summary>Which preset the settings match, ignoring the input choice, level trim and mute.</summary>
    public static MicrophoneSound Identify(DspSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (MicrophoneSoundChoice choice in Sounds)
            if (SameSound(ApplySound(choice.Sound, settings), settings)) return choice.Sound;
        return SameSound(LegacyNoisyRoom(settings), settings) ? MicrophoneSound.NoisyRoom : MicrophoneSound.Custom;
    }

    /// <summary>
    /// True when two settings sound the same. The safety limiter is left out: it only acts on peaks above the ceiling,
    /// and presets saved before it existed (limiter off) must still be recognised as the preset they are.
    /// </summary>
    public static bool SameSound(DspSettings a, DspSettings b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.HeadroomDb.Equals(b.HeadroomDb) && a.OutputCeiling.Equals(b.OutputCeiling) && a.EqBypass == b.EqBypass &&
            a.Bands.SequenceEqual(b.Bands) && a.Gate == b.Gate && a.Compressor == b.Compressor &&
            a.NoiseAttenuation == b.NoiseAttenuation && a.NoiseSuppression == b.NoiseSuppression && a.NeuralNoiseRemoval == b.NeuralNoiseRemoval &&
            a.Stereo == b.Stereo;
    }

    /// <summary>Builds the microphone channel to save, or explains in one sentence what is missing.</summary>
    public static (HostChannelProfile? Profile, string? Problem) Build(HostChannelProfile current, IReadOnlyList<HostEndpoint> endpoints,
        string? microphoneId, int? inputChannel, string? destinationId, MicrophoneSound sound)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (current.Bus != AudioBus.Microphone) throw new ArgumentException("Only the microphone channel is set up here.", nameof(current));
        if (string.IsNullOrEmpty(microphoneId)) return (null, "Choose your microphone.");
        if (string.IsNullOrEmpty(destinationId))
            return (null, Destinations(endpoints, null).Count == 0
                ? "No virtual cable is installed, so other apps have nothing to pick as your processed microphone. Install a virtual cable such as VB-CABLE, then come back."
                : "Choose where to send your voice.");
        if (inputChannel is int index && index is < 0 or > 7) return (null, "Choose input 1 or input 2, or all inputs.");
        HostEndpoint? microphone = endpoints.FirstOrDefault(e => e.Id == microphoneId && e.IsCapture);
        HostEndpoint? destination = endpoints.FirstOrDefault(e => e.Id == destinationId && !e.IsCapture);
        if (microphone?.TransportId is not null && microphone.TransportId == destination?.TransportId)
            return (null, "That microphone and destination are the two ends of one cable, which would feed your voice back into itself. Choose your physical microphone.");
        // The preset the saved settings already match is kept as saved (a save that changes only the device must not
        // rewrite the sound); only a different, newly chosen preset replaces the processing.
        DspSettings dsp = sound == MicrophoneSound.Custom || (current.Enabled && sound == Identify(current.Dsp)) ? current.Dsp : ApplySound(sound, current.Dsp);
        return (current with
        {
            Enabled = true, CaptureEndpointId = microphoneId, RenderEndpointId = destinationId, ManualPin = true,
            Dsp = dsp with { InputChannel = inputChannel }
        }, null);
    }

    private static EqBand[] BroadcastBands()
    {
        EqBand[] bands = DspSettings.FlatBands();
        bands[0] = new EqBand(80, 0, 0.7071067811865476, EqFilterType.HighPass);
        bands[3] = new EqBand(300, -2, 1);
        bands[7] = new EqBand(4000, 3, 0.9);
        bands[9] = new EqBand(10000, 2, 0.7071067811865476, EqFilterType.HighShelf);
        return bands;
    }

    // Devices that share a name are numbered "(2)", "(3)" in list order rather than shown with internal IDs.
    private static string Label(HostEndpoint endpoint, IReadOnlyList<HostEndpoint> peers) =>
        endpoint.Name + IndependentMixerWorkspace.DuplicateSuffix(peers, endpoint);

    private static void AddMissing(List<SetupEndpointOption> options, IReadOnlyList<HostEndpoint> endpoints, string? keep, bool capture)
    {
        if (string.IsNullOrEmpty(keep) || options.Any(o => o.Id == keep)) return;
        HostEndpoint? known = endpoints.FirstOrDefault(e => e.Id == keep && e.IsCapture == capture);
        string name = known?.Name ?? "Saved device";
        options.Add(new SetupEndpointOption(keep, string.Create(CultureInfo.InvariantCulture, $"{name} (not connected)"), false));
    }

    private static bool ContainsAny(string text, params string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}
