using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soltex.Audio.Engine;

public sealed record BridgeConfiguration(string CaptureEndpointId, string RenderEndpointId)
{
    public DspSettings Dsp { get; init; } = new();
    public int LatencyMs { get; init; } = 40;
    public int BufferMs { get; init; } = 200;
}

public sealed record BridgeStatus(bool Running, int BufferedFrames, long DroppedFrames,
    long SilentFrames, long LimitedSamples, string? Failure)
{
    public float? OutputPeak { get; init; }
}

/// <summary>
/// Explicit capture-to-render shared-mode bridge. Use a supplied cable's capture endpoint to speakers,
/// or a microphone capture endpoint to a supplied cable's render endpoint. Owns no virtual driver.
/// Create/start/stop/dispose on one control thread. Capture and playback run on NAudio workers.
/// </summary>
public sealed class WasapiBridge : IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice input, output;
    private readonly WasapiCapture capture;
    private readonly WasapiOut render;
    private readonly BoundedFloatBus bus;
    private readonly OutputCeilingSampleProvider outputGuard;
    private readonly AdaptiveResamplingSampleProvider resampler;
    private readonly RenderHeadSampleProvider head;
    private readonly int latencyMs;
    private readonly WaveFormat captureFormat;
    private readonly float[] captureSamples;
    private Exception? failure;
    private volatile bool started, disposed;
    private bool hasStarted;
    public PcmDspProcessor Processor { get; }
    public WaveFormat CaptureFormat => captureFormat;
    public WaveFormat RenderFormat => render.OutputWaveFormat;
    /// <summary>Clock-drift loop and resampler telemetry (correction ppm, held fill, underruns).</summary>
    public AdaptiveResamplingSampleProvider Resampler => resampler;
    /// <summary>Dropped counts queue overflow plus backlog skipped after a stall; silent counts priming and concealed underruns.</summary>
    public BridgeStatus Status => new(started && !disposed && Volatile.Read(ref failure) is null,
        bus.BufferedFrames, bus.DroppedFrames + resampler.DiscardedFrames, bus.SilentFrames + resampler.InsertedSilenceFrames, Processor.LimitedSamples,
        Volatile.Read(ref failure)?.Message) { OutputPeak = started && !disposed ? outputGuard.Peak : null };

    public void UpdateSettings(DspSettings settings)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Processor.UpdateSettings(settings);
        outputGuard.SetCeiling(settings.OutputCeiling);
    }

    public WasapiBridge(BridgeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.CaptureEndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.RenderEndpointId);
        DspSettings.Range(configuration.LatencyMs, 10, 500, nameof(configuration.LatencyMs));
        DspSettings.Range(configuration.BufferMs, configuration.LatencyMs * 2, 2000, nameof(configuration.BufferMs));
        enumerator = new MMDeviceEnumerator();
        MMDevice? acquiredInput = null, acquiredOutput = null;
        WasapiCapture? acquiredCapture = null;
        WasapiOut? acquiredRender = null;
        try
        {
            acquiredInput = enumerator.GetDevice(configuration.CaptureEndpointId);
            acquiredOutput = enumerator.GetDevice(configuration.RenderEndpointId);
            if (acquiredInput.DataFlow != DataFlow.Capture || acquiredOutput.DataFlow != DataFlow.Render)
                throw new ArgumentException("Specify a capture input and a render output; loopback capture is not used.", nameof(configuration));
            if (acquiredInput.State != DeviceState.Active || acquiredOutput.State != DeviceState.Active)
                throw new InvalidOperationException("Both explicitly selected endpoints must be active.");
            acquiredCapture = new WasapiCapture(acquiredInput, true, configuration.LatencyMs);
            var format = acquiredCapture.WaveFormat;
            captureFormat = format;
            captureSamples = new float[Math.Max(1, format.SampleRate / 50) * format.Channels];
            if (format.Channels is < 1 or > 2) throw new NotSupportedException("Bridge capture currently supports mono or stereo endpoints.");
            // Validate format before opening a stream, without consuming any real audio.
            PcmDecoder.Decode([], [], format);
            bus = new BoundedFloatBus(format.SampleRate, format.Channels, format.SampleRate * configuration.BufferMs / 1000);
            Processor = new PcmDspProcessor(format.SampleRate, format.Channels, configuration.Dsp);
            ISampleProvider source = new DspSampleProvider(bus, Processor);
            var destination = acquiredOutput.AudioClient.MixFormat;
            source = (source.WaveFormat.Channels, destination.Channels) switch
            {
                (1, 2) => new MonoToStereoSampleProvider(source),
                (2, 1) => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
                (1, 1) or (2, 2) => source,
                _ => throw new NotSupportedException("Bridge output currently supports mono or stereo endpoints.")
            };
            // Always through the sinc converter: it also absorbs the capture/render clock drift (same nominal rate too).
            resampler = new AdaptiveResamplingSampleProvider(source, destination.SampleRate, bus);
            acquiredRender = new WasapiOut(acquiredOutput, AudioClientShareMode.Shared, true, configuration.LatencyMs);
            outputGuard = new OutputCeilingSampleProvider(resampler, configuration.Dsp.OutputCeiling);
            head = new RenderHeadSampleProvider(outputGuard);
            latencyMs = configuration.LatencyMs;
            acquiredRender.Init(head.ToWaveProvider());
            input = acquiredInput; output = acquiredOutput; capture = acquiredCapture; render = acquiredRender;
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnStopped;
            render.PlaybackStopped += OnStopped;
            // Each stream releases its thread's MMCSS registration when it stops (NAudio raises these on the stream thread).
            AudioThreadScheduling.ReleaseOnStop(capture); AudioThreadScheduling.ReleaseOnStop(render);
        }
        catch
        {
            acquiredRender?.Dispose(); acquiredCapture?.Dispose(); acquiredOutput?.Dispose(); acquiredInput?.Dispose();
            enumerator.Dispose(); throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hasStarted) throw new InvalidOperationException("This bridge has already started. Create a new bridge for another session.");
        hasStarted = true;
        started = true;
        try { capture.StartRecording(); render.Play(); }
        catch { Stop(); throw; }
    }

    /// <summary>Starts the 5 ms fade to silence without waiting, so several bridges can be stopped together.</summary>
    public void BeginStop()
    {
        if (!disposed && started && render.PlaybackState == PlaybackState.Playing) head.BeginFadeOut();
    }

    public void Stop()
    {
        if (disposed) return;
        // Fade the render to silence first so the cable/speakers never get a cut mid-waveform (bounded wait).
        if (started && render.PlaybackState == PlaybackState.Playing && Volatile.Read(ref failure) is null) head.FadeOutAndWait(latencyMs);
        // No callback joins its own worker. Control owner performs orderly shutdown.
        started = false;
        try { capture.StopRecording(); }
        finally { render.Stop(); }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        AudioThreadScheduling.RegisterCurrentThread();
        try
        {
            int blockBytes = captureSamples.Length / captureFormat.Channels * captureFormat.BlockAlign;
            for (int offset = 0; offset < args.BytesRecorded; offset += blockBytes)
            {
                int length = Math.Min(blockBytes, args.BytesRecorded - offset);
                int count = PcmDecoder.Decode(args.Buffer.AsSpan(offset, length), captureSamples, captureFormat);
                bus.Write(captureSamples.AsSpan(0, count));
            }
        }
        catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        var error = args.Exception ?? (started ? new IOException("An owned WASAPI stream stopped unexpectedly.") : null);
        if (error is not null) Interlocked.CompareExchange(ref failure, error, null);
    }

    public void Dispose()
    {
        if (disposed) return;
        List<Exception> errors = [];
        void Attempt(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
        Attempt(Stop);
        disposed = true;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnStopped;
        render.PlaybackStopped -= OnStopped;
        Attempt(render.Dispose); Attempt(capture.Dispose); Attempt(output.Dispose); Attempt(input.Dispose); Attempt(enumerator.Dispose);
        if (errors.Count != 0) throw new AggregateException("One or more owned audio resources failed to close.", errors);
    }
}
