using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.Transport;

namespace Soltex.Audio.OwnedEndpoints;

internal sealed class OwnedRenderPath : IHostAudioPath
{
    private readonly OwnedAudioRouter router;
    private readonly OwnedAudioBus bus;
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice output;
    private readonly WasapiOut render;
    private readonly BoundedFloatBus buffer = new(48000, 2, 9600);
    private readonly PcmDspProcessor processor;
    private readonly OutputCeilingSampleProvider guard;
    private readonly AdaptiveResamplingSampleProvider resampler;
    private readonly RenderHeadSampleProvider head;
    private OwnedAudioRouter.RenderSubscription? subscription;
    private string? failure;
    private bool started, disposed;
    public BridgeStatus Status => new(started && !disposed && failure is null && router.Failure is null,
        buffer.BufferedFrames, buffer.DroppedFrames + resampler.DiscardedFrames, buffer.SilentFrames + resampler.InsertedSilenceFrames,
        processor.LimitedSamples, failure ?? router.Failure) { OutputPeak = started && !disposed ? guard.Peak : null };
    public OwnedRenderPath(OwnedAudioRouter router, OwnedAudioBus bus, string endpointId, DspSettings settings)
    {
        this.router = router; this.bus = bus; enumerator = new();
        MMDevice? acquired = null; WasapiOut? opened = null;
        try
        {
            acquired = enumerator.GetDevice(endpointId);
            if (acquired.DataFlow != DataFlow.Render || acquired.State != DeviceState.Active) throw new InvalidOperationException("Owned playback requires an active physical render endpoint.");
            processor = new(48000, 2, settings);
            ISampleProvider source = new DspSampleProvider(buffer, processor);
            var destination = acquired.AudioClient.MixFormat;
            if (destination.Channels == 1) source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (destination.Channels != 2) throw new NotSupportedException("Owned playback supports mono or stereo destinations.");
            // Sinc conversion that also follows the transport/device clock difference (also at 48 kHz to 48 kHz).
            resampler = new AdaptiveResamplingSampleProvider(source, destination.SampleRate, buffer);
            guard = new(resampler, settings.OutputCeiling);
            head = new RenderHeadSampleProvider(guard);
            opened = new(acquired, AudioClientShareMode.Shared, true, 40); opened.Init(head.ToWaveProvider());
            output = acquired; render = opened; render.PlaybackStopped += OnStopped; AudioThreadScheduling.ReleaseOnStop(render);
        }
        catch (Exception error) { ResourceCleanup.Run(error, () => opened?.Dispose(), () => acquired?.Dispose(), enumerator.Dispose); throw; }
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this); if (started) throw new InvalidOperationException("Path has already started.");
        subscription = router.Subscribe(bus, buffer);
        try
        {
            started = true;
            render.Play();
        }
        catch { Dispose(); throw; }
    }
    public void Update(DspSettings settings) { processor.UpdateSettings(settings); guard.SetCeiling(settings.OutputCeiling); }
    public void BeginStop() { if (started && !disposed) head.BeginFadeOut(); }
    private void OnStopped(object? sender, StoppedEventArgs args) { if (started) Volatile.Write(ref failure, args.Exception?.Message ?? "Owned render stream stopped unexpectedly."); }
    public void Dispose()
    {
        if (disposed) return;
        if (started && failure is null && render.PlaybackState == PlaybackState.Playing) head.FadeOutAndWait(40);
        disposed = true; started = false;
        render.PlaybackStopped -= OnStopped;
        ResourceCleanup.Run(null, () => subscription?.Dispose(), render.Stop, render.Dispose, output.Dispose, enumerator.Dispose, processor.Dispose);
    }
}

internal sealed class OwnedMicrophonePath : IHostAudioPath
{
    private readonly OwnedAudioRouter router;
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice input;
    private readonly WasapiCapture capture;
    private readonly WaveFormat format;
    private readonly BoundedFloatBus buffer;
    private readonly PcmDspProcessor processor;
    private readonly OutputCeilingSampleProvider guard;
    private readonly AdaptiveResamplingSampleProvider resampler;
    private readonly RenderHeadSampleProvider head;
    private readonly float[] decoded;
    private OwnedAudioRouter.MicrophoneSubscription? injection;
    private string? failure;
    private bool started, disposed;
    public BridgeStatus Status => new(started && !disposed && failure is null && router.Failure is null,
        buffer.BufferedFrames, buffer.DroppedFrames + resampler.DiscardedFrames, buffer.SilentFrames + resampler.InsertedSilenceFrames,
        processor.LimitedSamples, failure ?? router.Failure)
        { OutputPeak = started && !disposed ? guard.Peak : null };
    public OwnedMicrophonePath(OwnedAudioRouter router, string endpointId, DspSettings settings)
    {
        this.router = router; enumerator = new(); MMDevice? acquired = null; WasapiCapture? opened = null;
        try
        {
            acquired = enumerator.GetDevice(endpointId);
            if (acquired.DataFlow != DataFlow.Capture || acquired.State != DeviceState.Active) throw new InvalidOperationException("Owned microphone requires an active physical capture endpoint.");
            opened = new(acquired, true, 40); format = opened.WaveFormat;
            if (format.Channels is < 1 or > 2) throw new NotSupportedException("Owned microphone supports mono or stereo input.");
            PcmDecoder.Decode([], [], format);
            decoded = new float[format.SampleRate / 50 * format.Channels];
            buffer = new(format.SampleRate, format.Channels, format.SampleRate / 5);
            processor = new(format.SampleRate, format.Channels, settings);
            ISampleProvider source = new DspSampleProvider(buffer, processor);
            if (format.Channels == 1) source = new MonoToStereoSampleProvider(source);
            // The transport consumes on the driver's clock: convert and follow that clock against the microphone's.
            resampler = new AdaptiveResamplingSampleProvider(source, 48000, buffer);
            guard = new(resampler, settings.OutputCeiling);
            head = new RenderHeadSampleProvider(guard);
            input = acquired; capture = opened;
            capture.DataAvailable += OnData; capture.RecordingStopped += OnStopped; AudioThreadScheduling.ReleaseOnStop(capture);
        }
        catch (Exception error) { ResourceCleanup.Run(error, () => opened?.Dispose(), () => acquired?.Dispose(), enumerator.Dispose); throw; }
    }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this); if (started) throw new InvalidOperationException("Path has already started.");
        try { started = true; capture.StartRecording(); injection = router.Inject(head); }
        catch { Dispose(); throw; }
    }
    public void Update(DspSettings settings) { processor.UpdateSettings(settings); guard.SetCeiling(settings.OutputCeiling); }
    public void BeginStop() { if (started && !disposed) head.BeginFadeOut(); }
    private void OnData(object? sender, WaveInEventArgs args)
    {
        AudioThreadScheduling.RegisterCurrentThread();
        try
        {
            int block = decoded.Length / format.Channels * format.BlockAlign;
            for (int offset = 0; offset < args.BytesRecorded; offset += block)
            {
                int count = PcmDecoder.Decode(args.Buffer.AsSpan(offset, Math.Min(block, args.BytesRecorded - offset)), decoded, format);
                buffer.Write(decoded.AsSpan(0, count));
            }
        }
        catch (Exception error) { Volatile.Write(ref failure, error.Message); }
    }
    private void OnStopped(object? sender, StoppedEventArgs args) { if (started) Volatile.Write(ref failure, args.Exception?.Message ?? "Owned microphone stream stopped unexpectedly."); }
    public void Dispose()
    {
        if (disposed) return;
        // The transport keeps about 20 ms queued in the driver; fade out before the injection lease ends.
        if (started && failure is null && router.Failure is null && injection is not null) head.FadeOutAndWait(20);
        disposed = true; started = false;
        capture.DataAvailable -= OnData; capture.RecordingStopped -= OnStopped;
        ResourceCleanup.Run(null, () => injection?.Dispose(), capture.StopRecording, capture.Dispose, input.Dispose, enumerator.Dispose, processor.Dispose);
    }
}

internal static class ResourceCleanup
{
    internal static void Run(Exception? original, params Action[] actions)
    {
        List<Exception> errors = original is null ? [] : [original];
        foreach (var action in actions) { try { action(); } catch (Exception error) { errors.Add(error); } }
        if (errors.Count != 0) throw new AggregateException("One or more owned audio operations failed; all acquired resources were asked to close.", errors);
    }
}
