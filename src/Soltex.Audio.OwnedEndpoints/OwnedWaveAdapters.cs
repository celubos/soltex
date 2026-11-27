using NAudio.Wave;
using Soltex.Audio.Transport;

namespace Soltex.Audio.OwnedEndpoints;

/// <summary>Shared owned render-bus input for the existing stream mixer.</summary>
public sealed class OwnedWaveIn : IWaveIn
{
    private readonly OwnedAudioRouter router;
    private readonly OwnedAudioBus bus;
    private readonly ManualResetEventSlim stop = new();
    private OwnedAudioRouter.RenderSubscription? subscription;
    private Thread? thread;
    private bool disposed;
    public WaveFormat WaveFormat
    {
        get => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        set { if (value.SampleRate != 48000 || value.Channels != 2 || value.Encoding != WaveFormatEncoding.IeeeFloat || value.BitsPerSample != 32) throw new NotSupportedException("Owned inputs use 48 kHz stereo float PCM."); }
    }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public OwnedWaveIn(OwnedAudioRouter router, OwnedAudioBus bus)
    { this.router = router ?? throw new ArgumentNullException(nameof(router)); if ((uint)bus >= 4) throw new ArgumentOutOfRangeException(nameof(bus)); this.bus = bus; }
    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (thread is not null) throw new InvalidOperationException("Owned input is single-use.");
        subscription = router.Subscribe(bus);
        thread = new Thread(() =>
        {
            Exception? failure = null; float[] samples = new float[9600]; byte[] bytes = new byte[samples.Length * sizeof(float)];
            try
            {
                Soltex.Audio.Engine.AudioThreadScheduling.RegisterCurrentThread();
                while (!stop.IsSet)
                {
                    if (router.Failure is { } message) throw new IOException(message);
                    int count = Math.Min(4800, subscription.Buffer.BufferedFrames) * 2;
                    if (count != 0) { subscription.Buffer.Read(samples, 0, count); Buffer.BlockCopy(samples, 0, bytes, 0, count * sizeof(float)); DataAvailable?.Invoke(this, new(bytes, count * sizeof(float))); }
                    stop.Wait(5);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                try { RecordingStopped?.Invoke(this, new(failure)); }
                finally { Soltex.Audio.Engine.AudioThreadScheduling.ReleaseCurrentThread(); }
            }
        }) { IsBackground = true, Name = "Soltex owned stream input" };
        thread.Start();
    }
    public void StopRecording()
    {
        if (disposed) return; stop.Set();
        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(TimeSpan.FromSeconds(1))) throw new TimeoutException("Owned stream input did not stop.");
        subscription?.Dispose(); subscription = null;
    }
    public void Dispose() { if (disposed) return; StopRecording(); disposed = true; stop.Dispose(); }
}

/// <summary>Capture-return output for the stream mixer; router grants one writer per Microphone/Stream bus.</summary>
public sealed class OwnedWavePlayer : IWavePlayer
{
    private readonly OwnedAudioRouter router;
    private readonly OwnedAudioBus bus;
    private OwnedAudioRouter.MicrophoneSubscription? injection;
    private ISampleProvider? source;
    private bool disposed;
    public WaveFormat OutputWaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public PlaybackState PlaybackState { get; private set; }
    public float Volume { get => 1; set { if (value != 1) throw new NotSupportedException("Set gain in the Soltex mixer DSP."); } }
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public OwnedWavePlayer(OwnedAudioRouter router, OwnedAudioBus bus)
    { this.router = router ?? throw new ArgumentNullException(nameof(router)); if (bus is not (OwnedAudioBus.Microphone or OwnedAudioBus.Stream)) throw new ArgumentOutOfRangeException(nameof(bus)); this.bus = bus; }
    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider); ObjectDisposedException.ThrowIf(disposed, this);
        if (source is not null) throw new InvalidOperationException("Owned output has already been initialized.");
        if (waveProvider.WaveFormat.SampleRate != 48000 || waveProvider.WaveFormat.Channels != 2) throw new ArgumentException("Owned output needs converted 48 kHz stereo PCM.", nameof(waveProvider));
        source = waveProvider.ToSampleProvider();
    }
    public void Play()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (source is null) throw new InvalidOperationException("Initialize owned output first.");
        if (PlaybackState == PlaybackState.Playing) return;
        injection = router.Inject(source, bus); PlaybackState = PlaybackState.Playing;
    }
    public void Pause() { injection?.Dispose(); injection = null; PlaybackState = PlaybackState.Paused; }
    public void Stop()
    {
        if (PlaybackState == PlaybackState.Stopped) return;
        injection?.Dispose(); injection = null; PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new(null));
    }
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}
