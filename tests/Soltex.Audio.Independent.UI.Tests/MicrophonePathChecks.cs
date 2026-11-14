using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

/// <summary>Microphone path description and the microphone test verdicts. Pure data; no device or process.</summary>
internal static class MicrophonePathChecks
{
    private static readonly HostEndpoint Interface = new("interface", "Analog 1 + 2 (USB Audio Interface)", true, true);
    private static readonly HostEndpoint CableIn = new("cable-in", "CABLE Input (VB-Audio Virtual Cable)", false, true) { TransportId = "vb-audio-base-cable" };
    private static readonly HostEndpoint CableOut = new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" };
    private static readonly HostEndpoint Speakers = new("speakers", "Speakers (Realtek(R) Audio)", false, true);
    private static readonly HostEndpoint[] Endpoints = [Interface, CableIn, CableOut, Speakers];

    private static AudioHostProfile Microphone(Func<HostChannelProfile, HostChannelProfile> change)
    {
        AudioHostProfile profile = AudioHostProfile.CreateDefault();
        return profile with { Channels = profile.Channels.Select(c => c.Bus == AudioBus.Microphone ? change(c) : c).ToArray() };
    }

    private static readonly AudioHostProfile Configured = Microphone(c => c with
    {
        Enabled = true, CaptureEndpointId = "interface", RenderEndpointId = "cable-in",
        Dsp = c.Dsp with { InputChannel = 1, Gate = new GateSettings(Enabled: true) }
    });

    private static HostSnapshot Snapshot(AudioHostProfile profile, bool started, HostChannelStatus? status = null, HostEndpoint[]? endpoints = null) =>
        new(status?.State == "Running", profile, status is null ? [] : [status], endpoints ?? Endpoints, null) { StartRequested = started };

    public static void Run(Action<bool, string> check)
    {
        var unset = MicrophonePath.Describe(Snapshot(AudioHostProfile.CreateDefault(), false));
        check(unset.State == MicrophonePathState.NotSetUp && !unset.CanTest, "an unconfigured microphone reads as not set up and offers no test");

        var stopped = MicrophonePath.Describe(Snapshot(Configured, false));
        check(stopped.State == MicrophonePathState.Stopped && stopped.ReceivingInputName == "CABLE Output (VB-Audio Virtual Cable)",
            "stopped interface → VB-CABLE path names CABLE Output as the input other apps choose");
        check(stopped.SourceName == Interface.Name && stopped.SourceDetail.StartsWith("Input 2 only", StringComparison.Ordinal) && stopped.CanTest && stopped.InputChannel == 1,
            "source shows the interface and which hardware input carries the microphone");
        check(stopped.Processing.Contains("Noise gate") && stopped.Processing[^1].StartsWith("Clipping guard", StringComparison.Ordinal),
            "processing lists only enabled stages, ending with the always-on limiter");
        check(stopped.Explanation.Contains("Start audio", StringComparison.Ordinal), "a stopped path says nothing reaches the destination until audio starts");

        var running = MicrophonePath.Describe(Snapshot(Configured, true,
            new(AudioBus.Microphone, "Running", "interface", "cable-in", "Selected destination.", .25f, new BridgeStatus(true, 0, 0, 0, 0, null) { OutputPeak = .2f })));
        check(running.State == MicrophonePathState.Running && running.InputPeak == .25f && running.OutputPeak == .2f,
            "running path reports the measured input and processed output levels");

        var muted = MicrophonePath.Describe(Snapshot(Configured with { Channels = Configured.Channels.Select(c => c.Bus == AudioBus.Microphone ? c with { Mute = true } : c).ToArray() }, true,
            new(AudioBus.Microphone, "Running", "interface", "cable-in", "Selected destination.", .25f, new BridgeStatus(true, 0, 0, 0, 0, null))));
        check(muted.State == MicrophonePathState.Muted, "an open but muted path says apps receive silence");

        var failed = MicrophonePath.Describe(Snapshot(Configured, true,
            new(AudioBus.Microphone, "Failed", "interface", "cable-in", "Selected destination.", null, new BridgeStatus(false, 0, 0, 0, 0, "Device invalidated"))));
        check(failed.State == MicrophonePathState.Problem && failed.Explanation == "Device invalidated", "a failed stream shows the engine's own failure text");

        var unplugged = MicrophonePath.Describe(Snapshot(Configured, true, endpoints: [CableIn, CableOut]));
        check(unplugged.State == MicrophonePathState.Problem && unplugged.SourceName == "Microphone not connected" && !unplugged.CanTest,
            "an unplugged microphone is a problem with the setup kept, not a silent success");

        var toSpeakers = MicrophonePath.Describe(Snapshot(Microphone(c => c with { Enabled = true, CaptureEndpointId = "interface", RenderEndpointId = "speakers" }), false));
        check(toSpeakers.ReceivingInputName is null && toSpeakers.ReceivingDetail.Contains("speaker or headset", StringComparison.Ordinal),
            "a speaker destination explains that other apps cannot pick it as a microphone");

        HostEndpoint cableA = new("a-in", "CABLE-A Input (VB-Audio Cable A)", false, true);
        HostEndpoint cableAOut = new("a-out", "CABLE-A Output (VB-Audio Cable A)", true, true);
        check(MicrophonePath.ReceivingInput(cableA, [cableA, cableAOut, CableOut]) == cableAOut,
            "without a known transport the cable naming convention pairs CABLE-A Input with CABLE-A Output");
        HostEndpoint owned = new("soltex-owned:Microphone", "Soltex Microphone · owned transport", false, true) { TransportId = "soltex-owned:Microphone" };
        HostEndpoint ownedCapture = new("owned-capture", "Soltex Microphone (Soltex Audio)", true, true) { TransportId = "soltex-owned:Microphone" };
        check(MicrophonePath.ReceivingInput(owned, [owned, ownedCapture]) == ownedCapture, "the owned transport pairs by transport identity");

        // Microphone test verdicts.
        MicrophoneTestAssessment none = new();
        check(none.Result(1).Verdict == MicrophoneTestVerdict.NoSignal, "a test with no delivered frames says no sound arrived");
        check(Assess([.0005f, .3f], null).Verdict == MicrophoneTestVerdict.OneSideOnly && Assess([.0005f, .3f], null).LoudestInput == 1,
            "voice on input 2 of a stereo interface with all inputs selected is reported as one-sided");
        var wrong = Assess([.0005f, .3f], 0);
        check(wrong.Verdict == MicrophoneTestVerdict.WrongInput && wrong.Title.EndsWith("input 2", StringComparison.Ordinal),
            "voice on input 2 while the path listens to input 1 names the right input");
        check(Assess([.0005f, .3f], 1).Verdict == MicrophoneTestVerdict.Good, "voice on the chosen input at −10 dBFS sounds good");
        check(Assess([.001f, .002f], 1).Verdict == MicrophoneTestVerdict.Silent, "peaks below −50 dBFS are reported as nothing heard");
        check(Assess([.0005f, .02f], 1).Verdict == MicrophoneTestVerdict.Quiet, "peaks around −34 dBFS are reported as quiet with a target level");
        check(Assess([.0005f, .99f], 1).Verdict == MicrophoneTestVerdict.TooLoud, "peaks at full scale are reported as too loud");
        MicrophoneTestAssessment live = new();
        live.Observe(.1f); live.Observe(.3f); live.Observe(null);
        check(live.Result(null).Verdict == MicrophoneTestVerdict.Good && live.Observations == 2, "a live path meter test keeps the loudest observed level");
        check(MicrophonePath.MeterPosition(1f) == 1 && MicrophonePath.MeterPosition(.0005f) == 0 && MicrophonePath.MeterPosition(null) == 0,
            "meter maps −60…0 dBFS onto the bar and unknown levels to empty");
    }

    private static MicrophoneTestResult Assess(float[] peaks, int? configured)
    {
        MicrophoneTestAssessment assessment = new();
        assessment.Observe(peaks.Select(p => p / 2).ToArray(), 4800);
        assessment.Observe(peaks, 9600);
        return assessment.Result(configured);
    }
}
