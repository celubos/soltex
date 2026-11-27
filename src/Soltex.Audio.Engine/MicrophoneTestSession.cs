using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soltex.Audio.Engine;

/// <summary>Owns its stream. Writes at most the requested number of complete float PCM frames.</summary>
public sealed class BoundedPcmWaveWriter : IDisposable
{
    private readonly WaveFileWriter writer;
    private readonly long maximumFrames;
    private readonly int channels;
    private long framesWritten;
    public long FramesWritten => Interlocked.Read(ref framesWritten);
    public bool Complete => FramesWritten >= maximumFrames;
    public BoundedPcmWaveWriter(Stream stream, int sampleRate, int channels, int seconds)
    {
        ArgumentNullException.ThrowIfNull(stream);
        DspSettings.Range(sampleRate, 8000, 192000, nameof(sampleRate));
        DspSettings.Range(channels, 1, 2, nameof(channels));
        DspSettings.Range(seconds, 1, 30, nameof(seconds));
        this.channels = channels; maximumFrames = (long)sampleRate * seconds;
        writer = new WaveFileWriter(stream, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
    }
    public int Write(float[] samples, int count)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (count < 0 || count > samples.Length || count % channels != 0) throw new ArgumentOutOfRangeException(nameof(count));
        int frames = (int)Math.Min(count / channels, maximumFrames - FramesWritten);
        writer.WriteSamples(samples, 0, frames * channels);
        Interlocked.Add(ref framesWritten, frames);
        return frames;
    }
    public void Dispose() => writer.Dispose();
}

public sealed record MicrophoneTestStatus(double SecondsRecorded, bool Complete, string? Failure);

/// <summary>Explicit bounded input recording through the independent DSP, with no monitoring/playback.</summary>
public sealed class MicrophoneTestRecorder : IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly QueuedPcmRecording recording;
    private readonly TaskCompletionSource captureStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool started, captureStarted, disposed;
    public PcmDspProcessor Processor => recording.Processor;
    public MicrophoneTestStatus Status => new((double)recording.FramesWritten / Processor.SampleRate,
        recording.Complete, recording.Failure?.Message);

    public MicrophoneTestRecorder(string captureId, string absolutePath, DspSettings settings, int seconds = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureId);
        if (SynchronizationContext.Current is not null)
            throw new InvalidOperationException("Create and control the recorder on its owned worker without a synchronization context.");
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Recording destination must be absolute.", nameof(absolutePath));
        DspSettings.Range(seconds, 1, 30, nameof(seconds));
        enumerator = new MMDeviceEnumerator();
        MMDevice? acquiredDevice = null; WasapiCapture? acquiredCapture = null; Stream? acquiredStream = null;
        QueuedPcmRecording? acquiredRecording = null;
        try
        {
            acquiredDevice = enumerator.GetDevice(captureId);
            if (acquiredDevice.DataFlow != DataFlow.Capture || acquiredDevice.State != DeviceState.Active)
                throw new ArgumentException("Select an active capture endpoint.", nameof(captureId));
            acquiredCapture = new WasapiCapture(acquiredDevice, true, 40);
            var format = acquiredCapture.WaveFormat;
            if (format.Channels is < 1 or > 2) throw new NotSupportedException("Microphone test supports mono/stereo capture.");
            PcmDecoder.Decode([], [], format);
            acquiredStream = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            acquiredRecording = new QueuedPcmRecording(acquiredStream, format, settings, seconds);
            recording = acquiredRecording;
            device = acquiredDevice; capture = acquiredCapture;
            capture.DataAvailable += DataAvailable; capture.RecordingStopped += RecordingStopped; AudioThreadScheduling.ReleaseOnStop(capture);
        }
        catch
        {
            try { if (acquiredRecording is not null) acquiredRecording.Dispose(); else acquiredStream?.Dispose(); }
            finally { acquiredCapture?.Dispose(); acquiredDevice?.Dispose(); enumerator.Dispose(); }
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) throw new InvalidOperationException("A test recorder starts once.");
        started = true; capture.StartRecording(); captureStarted = true;
    }

    private void DataAvailable(object? sender, WaveInEventArgs args)
    {
        AudioThreadScheduling.RegisterCurrentThread();
        try
        {
            recording.TryWrite(args.Buffer.AsSpan(0, args.BytesRecorded));
            // NAudio StopRecording only requests a stop; Dispose/join happens on the control owner.
            if (!recording.Accepting) capture.StopRecording();
        }
        catch (Exception error) { recording.CompleteInput(error); capture.StopRecording(); }
    }
    private void RecordingStopped(object? sender, StoppedEventArgs args)
    {
        try { recording.CompleteInput(args.Exception); }
        finally { captureStopped.TrySetResult(); }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // Join the producer before closing its queue; then drain/join the writer and finalize its WAV.
        try { capture.Dispose(); }
        finally
        {
            // NAudio clears its captureThread field before raising RecordingStopped. Its Dispose
            // can therefore return while that final handler is still running. With no captured
            // SynchronizationContext, this handler runs on the capture thread and never waits on us.
            if (captureStarted) captureStopped.Task.GetAwaiter().GetResult();
            capture.DataAvailable -= DataAvailable; capture.RecordingStopped -= RecordingStopped;
            try { recording.Dispose(); } finally { device.Dispose(); enumerator.Dispose(); }
        }
        if (recording.Failure is { } error) throw new IOException("Microphone recording stopped with an error.", error);
    }
}

/// <summary>Explicit playback of a bounded test WAV to one selected output, attenuated by 12 dB.</summary>
public sealed class MicrophoneTestReplay : IDisposable
{
    private readonly MMDeviceEnumerator enumerator;
    private readonly MMDevice device;
    private readonly WaveFileReader reader;
    private readonly MemoryStream replayBuffer;
    private readonly WasapiOut render;
    private volatile bool complete;
    private Exception? failure;
    private bool disposed;
    public bool Complete => complete;
    public string? Failure => Volatile.Read(ref failure)?.Message;

    public MicrophoneTestReplay(string renderId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderId);
        enumerator = new MMDeviceEnumerator();
        MMDevice? acquiredDevice = null; WaveFileReader? acquiredReader = null; WasapiOut? acquiredRender = null;
        MemoryStream? acquiredBuffer = null;
        try
        {
            acquiredDevice = enumerator.GetDevice(renderId);
            if (acquiredDevice.DataFlow != DataFlow.Render || acquiredDevice.State != DeviceState.Active)
                throw new ArgumentException("Select an active render endpoint.", nameof(renderId));
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                acquiredBuffer = ReplayFileBuffer.Load(file);
            acquiredReader = new WaveFileReader(acquiredBuffer);
            if (acquiredReader.TotalTime > TimeSpan.FromSeconds(30.1)) throw new InvalidDataException("Only bounded microphone test recordings can be replayed.");
            ISampleProvider source = new VolumeSampleProvider(acquiredReader.ToSampleProvider()) { Volume = 0.25f };
            var destination = acquiredDevice.AudioClient.MixFormat;
            source = (source.WaveFormat.Channels, destination.Channels) switch
            {
                (1, 2) => new MonoToStereoSampleProvider(source),
                (2, 1) => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
                (1, 1) or (2, 2) => source,
                _ => throw new NotSupportedException("Test replay supports mono/stereo endpoints.")
            };
            if (source.WaveFormat.SampleRate != destination.SampleRate) source = new AdaptiveResamplingSampleProvider(source, destination.SampleRate);
            acquiredRender = new WasapiOut(acquiredDevice, AudioClientShareMode.Shared, true, 40);
            acquiredRender.Init(new RenderHeadSampleProvider(new OutputCeilingSampleProvider(source, 0.25)).ToWaveProvider());
            render = acquiredRender; reader = acquiredReader; replayBuffer = acquiredBuffer; device = acquiredDevice;
            render.PlaybackStopped += Stopped; AudioThreadScheduling.ReleaseOnStop(render);
        }
        catch { acquiredRender?.Dispose(); acquiredReader?.Dispose(); acquiredBuffer?.Dispose(); acquiredDevice?.Dispose(); enumerator.Dispose(); throw; }
    }
    public void Start() { ObjectDisposedException.ThrowIf(disposed, this); render.Play(); }
    private void Stopped(object? sender, StoppedEventArgs args)
    { complete = true; if (args.Exception is not null) Interlocked.CompareExchange(ref failure, args.Exception, null); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { render.Stop(); render.PlaybackStopped -= Stopped; render.Dispose(); }
        finally { reader.Dispose(); replayBuffer.Dispose(); device.Dispose(); enumerator.Dispose(); }
    }
}
