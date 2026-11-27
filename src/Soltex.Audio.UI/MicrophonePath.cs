using System.Globalization;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

public enum MicrophonePathState { NotSetUp, Stopped, Starting, Running, Muted, Problem }

/// <summary>
/// One plain-language description of the microphone route: physical source → processing → destination → the input
/// name other apps must choose. Built only from the helper's confirmed snapshot; nothing here is inferred from intent.
/// </summary>
public sealed record MicrophonePathModel(
    MicrophonePathState State,
    string StateText,
    string Explanation,
    string SourceName,
    string SourceDetail,
    bool SourceAvailable,
    IReadOnlyList<string> Processing,
    string DestinationName,
    bool DestinationAvailable,
    string? ReceivingInputName,
    string ReceivingDetail,
    float? InputPeak,
    float? OutputPeak)
{
    public string? CaptureEndpointId { get; init; }
    public int? InputChannel { get; init; }
    public bool CanTest => CaptureEndpointId is not null && SourceAvailable;
}

public static class MicrophonePath
{
    public static MicrophonePathModel Describe(HostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HostChannelProfile? channel = snapshot.Profile.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone);
        HostChannelStatus? status = snapshot.Channels.FirstOrDefault(c => c.Bus == AudioBus.Microphone);
        HostEndpoint? source = Find(snapshot.Endpoints, channel?.CaptureEndpointId, capture: true);
        HostEndpoint? destination = Find(snapshot.Endpoints, status?.ActiveRenderId ?? channel?.RenderEndpointId, capture: false);
        HostEndpoint? receiving = destination is null ? null : ReceivingInput(destination, snapshot.Endpoints);

        string sourceName = source?.Name ?? (channel?.CaptureEndpointId is null ? "No microphone chosen" : "Microphone not connected");
        string sourceDetail = channel?.Dsp.InputChannel is int index
            ? string.Create(CultureInfo.InvariantCulture, $"Input {index + 1} only, sent to both sides")
            : source is null ? "Plug it in or choose another input" : "All inputs of this device";
        string destinationName = destination?.Name ?? (channel?.RenderEndpointId is null ? "No destination chosen" : "Destination not available");
        string receivingName = receiving?.Name ?? "";
        string receivingDetail = receiving is not null
            ? "Choose this as the microphone in Discord, OBS, Teams or a game"
            : destination is null ? "Pick a virtual cable as the destination" :
              "This destination is a speaker or headset, so apps cannot pick it as a microphone. Choose a virtual cable to use your voice in other apps.";

        IReadOnlyList<string> processing = channel is null ? [] : DescribeProcessing(channel);
        float? inputPeak = status?.Peak;
        float? outputPeak = status?.Bridge?.OutputPeak;

        (MicrophonePathState state, string text, string explanation) = Classify(snapshot, channel, status, source, destination, receiving);
        return new MicrophonePathModel(state, text, explanation, sourceName, sourceDetail, source is not null, processing,
            destinationName, destination is not null, receiving?.Name, receivingDetail, inputPeak, outputPeak)
        {
            CaptureEndpointId = source?.Id,
            InputChannel = channel?.Dsp.InputChannel
        };
    }

    private static (MicrophonePathState, string, string) Classify(HostSnapshot snapshot, HostChannelProfile? channel,
        HostChannelStatus? status, HostEndpoint? source, HostEndpoint? destination, HostEndpoint? receiving)
    {
        string apps = receiving is null ? "" : " Apps hear you on “" + receiving.Name + "”.";
        if (channel is null || !channel.Enabled || channel.CaptureEndpointId is null || channel.RenderEndpointId is null)
            return (MicrophonePathState.NotSetUp, "Not set up", "Choose your microphone and where its processed sound should go.");
        if (source is null)
            return (MicrophonePathState.Problem, "Microphone missing", "The chosen microphone is not connected or is disabled in Windows. Your setup is kept and resumes when it returns.");
        if (destination is null)
            return (MicrophonePathState.Problem, "Destination missing", "The chosen destination is not available. If it is a virtual cable, check that the cable driver is still installed.");
        if (snapshot.StreamMix?.Running == true)
            return (MicrophonePathState.Stopped, "Stream mix is on", "The stream mix is using audio. Stop it and start normal audio to use this microphone path.");
        if (!snapshot.StartRequested)
            return (MicrophonePathState.Stopped, "Stopped", receiving is null
                ? "Audio is stopped, so nothing reaches the destination. Start audio to send your voice."
                : "Audio is stopped, so apps listening to “" + receiving.Name + "” hear silence. Start audio to send your voice.");
        if (status is null || status.State is "Unavailable")
            return (MicrophonePathState.Starting, "Waiting", status?.Reason ?? "Waiting for audio to open the microphone.");
        if (status.State is "Failed")
            return (MicrophonePathState.Problem, "Stopped by an error", status.Bridge?.Failure ?? status.Reason);
        if (status.State is not "Running")
            return (MicrophonePathState.Starting, "Waiting", status.Reason);
        if (channel.Mute || channel.Dsp.Mute || channel.GainDb <= -80)
            return (MicrophonePathState.Muted, "Muted", "The path is open but muted, so apps receive silence." + apps);
        return (MicrophonePathState.Running, "On", "Your processed voice is being sent now." + apps);
    }

    public static IReadOnlyList<string> DescribeProcessing(HostChannelProfile channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        DspSettings dsp = channel.Dsp;
        List<string> steps = [];
        double gain = dsp.GainDb + channel.GainDb;
        if (Math.Abs(gain) >= 0.05) steps.Add("Gain " + Decibels(gain));
        if (dsp.Gate.Enabled) steps.Add("Noise gate");
        if (dsp.NoiseAttenuation.Enabled) steps.Add("Quiet-noise reduction");
        if (dsp.NoiseSuppression.Enabled) steps.Add("Background noise suppression");
        if (!dsp.EqBypass)
        {
            EqBand[] active = dsp.Bands.Where(b => !b.Bypass).ToArray();
            // A low cut is named with its frequency; "Equalizer" is reserved for actual tone shaping.
            foreach (EqBand cut in active.Where(b => b.Type == EqFilterType.HighPass))
                steps.Add("Low cut at " + cut.FrequencyHz.ToString("0", CultureInfo.InvariantCulture) + " Hz");
            if (active.Any(b => b.Type is not EqFilterType.HighPass && (Math.Abs(b.GainDb) >= 0.05 || b.Type is EqFilterType.LowPass or EqFilterType.Notch)))
                steps.Add("Equalizer");
        }
        if (dsp.Compressor.Enabled) steps.Add("Compressor");
        if (dsp.Stereo.Enabled) steps.Add("Stereo image");
        // The output stage is a hard clamp at the ceiling, so it is named for what it does rather than called a limiter.
        steps.Add((dsp.Limiter.Enabled ? "Safety limiter at " : "Clipping guard at ") + Decibels(20 * Math.Log10(Math.Clamp(dsp.OutputCeiling, 0.01, 1))));
        return steps;
    }

    /// <summary>
    /// The capture endpoint other apps select to receive what is played into <paramref name="destination"/>.
    /// Shared transport identity first (virtual cables and the owned transport), then the cable naming convention.
    /// </summary>
    public static HostEndpoint? ReceivingInput(HostEndpoint destination, IReadOnlyList<HostEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(endpoints);
        if (destination.IsCapture) return null;
        if (destination.TransportId is { } transport)
        {
            HostEndpoint? paired = endpoints.Where(e => e.IsCapture && e.TransportId == transport)
                .OrderByDescending(e => e.IsActive).FirstOrDefault();
            if (paired is not null) return paired;
        }
        // "CABLE Input (VB-Audio Virtual Cable)" → "CABLE Output (VB-Audio Virtual Cable)"; "CABLE-A Input (…)" likewise.
        int bracket = destination.Name.IndexOf('(', StringComparison.Ordinal);
        string head = bracket < 0 ? destination.Name : destination.Name[..bracket];
        string tail = bracket < 0 ? "" : destination.Name[bracket..];
        int input = head.LastIndexOf("Input", StringComparison.OrdinalIgnoreCase);
        if (input < 0) return null;
        string expected = head[..input] + "Output" + head[(input + "Input".Length)..] + tail;
        return endpoints.Where(e => e.IsCapture && string.Equals(e.Name, expected, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsActive).FirstOrDefault();
    }

    public static string Decibels(double value) =>
        (value > 0 ? "+" : value < 0 ? "−" : "") + Math.Abs(value).ToString("0.#", CultureInfo.InvariantCulture) + " dB";

    /// <summary>Peak in dBFS, floored at −90 for silence.</summary>
    public static double PeakDbfs(float peak) => peak <= 0.0000316f ? -90 : 20 * Math.Log10(peak);

    /// <summary>Maps a linear peak to a 0–1 meter position over −60…0 dBFS (speech sits in the upper half).</summary>
    public static double MeterPosition(float? peak) => peak is not { } value ? 0 : Math.Clamp((PeakDbfs(value) + 60) / 60, 0, 1);

    private static HostEndpoint? Find(IReadOnlyList<HostEndpoint> endpoints, string? id, bool capture) =>
        id is null ? null : endpoints.FirstOrDefault(e => e.Id == id && e.IsCapture == capture && e.IsActive);
}

public enum MicrophoneTestVerdict { NoSignal, Silent, Quiet, Good, TooLoud, WrongInput, OneSideOnly }

public sealed record MicrophoneTestResult(MicrophoneTestVerdict Verdict, string Title, string Advice, double PeakDbfs, int? LoudestInput);

/// <summary>
/// Accumulates bounded test observations (per hardware input when available) into one verdict a person can act on.
/// Thresholds: speech should peak between −30 and −3 dBFS; below −50 dBFS is treated as no voice at all.
/// </summary>
public sealed class MicrophoneTestAssessment
{
    public const double SilentBelowDbfs = -50, QuietBelowDbfs = -30, TooLoudFromDbfs = -1, ChannelGapDb = 20;
    private float[] maxima = [];
    public long Frames { get; private set; }
    public int Observations { get; private set; }

    /// <summary>Per-input peaks from the capture-only test.</summary>
    public void Observe(IReadOnlyList<float> peaks, long totalFrames)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (maxima.Length < peaks.Count) Array.Resize(ref maxima, peaks.Count);
        for (int i = 0; i < peaks.Count; i++) if (float.IsFinite(peaks[i])) maxima[i] = Math.Max(maxima[i], Math.Abs(peaks[i]));
        Frames = Math.Max(Frames, totalFrames);
        Observations++;
    }

    /// <summary>A single device-level peak (live path meter); input-level detection is unavailable.</summary>
    public void Observe(float? peak)
    {
        if (peak is not { } value || !float.IsFinite(value)) return;
        Observe([value], Frames + 1);
    }

    public MicrophoneTestResult Result(int? configuredInput)
    {
        if (Frames == 0 || maxima.Length == 0)
            return new(MicrophoneTestVerdict.NoSignal, "No sound arrived",
                "Windows opened the microphone but delivered no audio. Check that the device is not in use exclusively by another app, then try again.", -90, null);
        int loudest = 0;
        for (int i = 1; i < maxima.Length; i++) if (maxima[i] > maxima[loudest]) loudest = i;
        double peak = MicrophonePath.PeakDbfs(maxima[loudest]);
        string level = peak.ToString("0", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal) + " dBFS";
        if (peak < SilentBelowDbfs)
            return new(MicrophoneTestVerdict.Silent, "Nothing heard",
                "Your voice did not register (loudest " + level + "). Check the cable and the gain knob on your interface; dynamic microphones need a lot of gain.", peak, null);
        if (maxima.Length > 1)
        {
            double Db(int i) => MicrophonePath.PeakDbfs(maxima[i]);
            int voice = loudest + 1;
            if (configuredInput is int chosen && chosen < maxima.Length && chosen != loudest && Db(loudest) - Db(chosen) >= ChannelGapDb)
                return new(MicrophoneTestVerdict.WrongInput, "Your voice is on input " + voice.ToString(CultureInfo.InvariantCulture),
                    "The path listens to input " + (chosen + 1).ToString(CultureInfo.InvariantCulture) + ", which stayed quiet. Change setup and choose input " +
                    voice.ToString(CultureInfo.InvariantCulture) + ".", peak, loudest);
            if (configuredInput is null && maxima.Length == 2 && Db(loudest) - Db(1 - loudest) >= ChannelGapDb)
                return new(MicrophoneTestVerdict.OneSideOnly, "Your voice is only on input " + voice.ToString(CultureInfo.InvariantCulture),
                    "People would hear you in one ear. Change setup and choose “input " + voice.ToString(CultureInfo.InvariantCulture) + " only” so both sides carry your voice.", peak, loudest);
        }
        if (peak >= TooLoudFromDbfs)
            return new(MicrophoneTestVerdict.TooLoud, "Too loud", "Your voice reached the top of the scale (" + level + ") and may distort. Turn the gain knob down a little.", peak, loudest);
        if (peak < QuietBelowDbfs)
            return new(MicrophoneTestVerdict.Quiet, "Quiet", "The loudest sound reached " + level + ". If that was your normal speaking voice, raise the gain until speech peaks around −12 dBFS.", peak, loudest);
        return new(MicrophoneTestVerdict.Good, "Sounds good", "The loudest sound reached " + level + ", a healthy speaking level with room before distortion.", peak, loudest);
    }
}
