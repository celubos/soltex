using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

/// <summary>Microphone setup choices, presets and validation. Pure data; no device or process.</summary>
internal static class MicrophoneSetupChecks
{
    private static readonly HostEndpoint Interface = new("interface", "Analog 1 + 2 (USB Audio Interface)", true, true);
    private static readonly HostEndpoint Webcam = new("webcam", "Microphone (USB2.0 Device)", true, true);
    private static readonly HostEndpoint Unplugged = new("unplugged", "Microphone (Old Headset)", true, false);
    private static readonly HostEndpoint CableIn = new("cable-in", "CABLE Input (VB-Audio Virtual Cable)", false, true) { TransportId = "vb-audio-base-cable" };
    private static readonly HostEndpoint CableOut = new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" };
    private static readonly HostEndpoint SonarMic = new("sonar-mic", "SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", true, true);
    private static readonly HostEndpoint SonarChat = new("sonar-chat", "SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", false, true);
    private static readonly HostEndpoint Speakers = new("speakers", "Speakers (USB Audio Interface)", false, true);
    private static readonly HostEndpoint[] Endpoints = [Interface, Webcam, Unplugged, CableIn, CableOut, SonarMic, SonarChat, Speakers];
    private static readonly HostChannelProfile Blank = new() { Bus = AudioBus.Microphone };

    public static void Run(Action<bool, string> check)
    {
        IReadOnlyList<SetupEndpointOption> microphones = MicrophoneSetup.Microphones(Endpoints, null);
        check(microphones.Select(m => m.Id).SequenceEqual(["interface", "webcam"]),
            "Setup offers only connected physical microphones (no cable outputs, Sonar devices or unplugged inputs)");
        IReadOnlyList<SetupEndpointOption> kept = MicrophoneSetup.Microphones(Endpoints, "unplugged");
        check(kept.Count == 3 && kept[^1] is { Id: "unplugged", Available: false } && kept[^1].Label.Contains("not connected", StringComparison.Ordinal),
            "A saved microphone that is unplugged stays selectable and is labeled not connected");

        IReadOnlyList<SetupEndpointOption> destinations = MicrophoneSetup.Destinations(Endpoints, null);
        check(destinations.Count == 1 && destinations[0] is { Id: "cable-in", Receiving: "CABLE Output (VB-Audio Virtual Cable)" },
            "Destinations are only playback devices other apps can pick as a microphone, each with that input's name");
        check(MicrophoneSetup.SuggestDestination(destinations, null) == "cable-in" && MicrophoneSetup.SuggestMicrophone(microphones, null) == "interface",
            "First setup suggests the first physical microphone and the cable whose input is CABLE Output");
        check(MicrophoneSetup.SuggestMicrophone(microphones, "webcam") == "webcam", "A saved microphone is kept as the selection");
        // Endpoint order can list a webcam microphone first; the audio interface must still be suggested.
        HostEndpoint captureCard = new("card", "Capture Input terminal (AC Interface)", true, true);
        IReadOnlyList<SetupEndpointOption> webcamFirst = MicrophoneSetup.Microphones([Webcam, captureCard, Interface, CableOut, SonarMic], null);
        check(MicrophoneSetup.SuggestMicrophone(webcamFirst, null) == "interface", "First setup suggests the audio interface over a webcam or capture-card microphone");
        check(MicrophoneSetup.SuggestMicrophone(webcamFirst, "unplugged-old") == "interface", "A saved microphone that is gone falls back to the best connected one");

        (HostChannelProfile? profile, string? problem) = MicrophoneSetup.Build(Blank, Endpoints, "interface", 1, "cable-in", MicrophoneSound.Broadcast);
        check(problem is null && profile is { Enabled: true, CaptureEndpointId: "interface", RenderEndpointId: "cable-in", ManualPin: true } &&
            profile.Dsp.InputChannel == 1 && profile.Dsp.Compressor.Enabled && profile.Dsp.Gate.Enabled && profile.Dsp.Bands[0].Type == EqFilterType.HighPass,
            "Build produces an enabled microphone channel with the chosen input and the Broadcast processing");
        check(MicrophoneSetup.Identify(profile!.Dsp) == MicrophoneSound.Broadcast, "A preset is recognized again after saving, regardless of the input choice");
        IReadOnlyList<string> steps = MicrophonePath.DescribeProcessing(profile);
        check(steps.Contains("Low cut at 80 Hz") && steps.Contains("Equalizer") && steps.Contains("Noise gate") && steps.Contains("Compressor"),
            "The processing step names the low cut with its frequency and still lists real tone shaping");
        IReadOnlyList<string> natural = MicrophonePath.DescribeProcessing(Blank with { Dsp = MicrophoneSetup.ApplySound(MicrophoneSound.Natural, new DspSettings()) });
        check(natural.SequenceEqual(["Low cut at 80 Hz", "Compressor", "Clipping guard at −1 dB"]), "Natural voice is described exactly as what it does: " + string.Join(", ", natural));
        check(MicrophoneSetup.Identify(new DspSettings()) == MicrophoneSound.Custom, "Settings that match no preset are reported as Custom, never as a preset");

        DspSettings trimmed = new() { GainDb = 4.5, InputChannel = 0, Mute = true };
        foreach (MicrophoneSoundChoice choice in MicrophoneSetup.Sounds)
        {
            DspSettings applied = MicrophoneSetup.ApplySound(choice.Sound, trimmed);
            check(applied.GainDb == 4.5 && applied.InputChannel == 0 && applied.Mute && MicrophoneSetup.Identify(applied) == choice.Sound,
                choice.Title + " keeps the input, level trim and mute and is recognized as itself");
            check(Validates(applied), choice.Title + " passes the engine's own validation");
        }
        DspSettings clean = MicrophoneSetup.ApplySound(MicrophoneSound.Clean, trimmed);
        check(!clean.Compressor.Enabled && !clean.Gate.Enabled && !clean.NoiseSuppression.Enabled && clean.HeadroomDb == 0 && clean.Bands.All(b => b.GainDb == 0 && b.Type == EqFilterType.Peak),
            "Clean has no processing and no hidden level drop");

        HostChannelProfile custom = Blank with { Dsp = new DspSettings { GainDb = 2, Compressor = new CompressorSettings(Enabled: true, Ratio: 7) } };
        (HostChannelProfile? keptCustom, _) = MicrophoneSetup.Build(custom, Endpoints, "interface", null, "cable-in", MicrophoneSound.Custom);
        check(keptCustom!.Dsp.Compressor.Ratio == 7 && keptCustom.Dsp.InputChannel is null, "Custom keeps the person's own processing");

        check(MicrophoneSetup.Build(Blank, Endpoints, null, null, "cable-in", MicrophoneSound.Natural).Problem == "Choose your microphone.", "Saving without a microphone explains what is missing");
        string? noCable = MicrophoneSetup.Build(Blank, [Interface, Speakers], "interface", null, null, MicrophoneSound.Natural).Problem;
        check(noCable?.Contains("virtual cable", StringComparison.Ordinal) == true, "Without any virtual cable the problem says one is needed");
        string? loop = MicrophoneSetup.Build(Blank, Endpoints, "cable-out", null, "cable-in", MicrophoneSound.Natural).Problem;
        check(loop?.Contains("feed your voice back", StringComparison.Ordinal) == true, "Both ends of one cable are refused as a feedback loop");
    }

    private static bool Validates(DspSettings settings)
    {
        try
        {
            _ = new PcmDspProcessor(48000, 2, settings);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
