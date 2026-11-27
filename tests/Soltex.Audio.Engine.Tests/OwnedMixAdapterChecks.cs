using NAudio.Wave;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Engine.Tests;

internal static class OwnedMixAdapterChecks
{
    public static void PcmAndLifetime()
    {
        var capture = new FakeCapture(); var personal = new FakeOutput(); var stream = new FakeOutput(); int opens = 0;
        var config = Configuration();
        var mixer = new WasapiStreamMixer(config, (id, _) => { Check(id == "owned:Game", "Capture identity changed"); opens++; return capture; },
            (id, _) => id == "physical:test" ? personal : stream);
        mixer.Start();
        // The drift-compensating converters keep one read plus one packet and a fade reserve queued, so the producer runs
        // one 50 ms packet ahead (a pull of exactly what was just written would, correctly, fade out as an underrun).
        // Packets are separate deliveries, so leave a few milliseconds between them.
        capture.Emit(2400, .1f, .2f); Thread.Sleep(5); capture.Emit(2400, .1f, .2f);
        float[] own = personal.Pull(2400), broadcast = stream.Pull(2400);
        Check(opens == 1 && capture.Starts == 1, "Owned input opened more than once");
        Check(mixer.Status.Sources.Single().ProcessedFrames == 4800, "DSP producer frame count duplicated");
        Check(Math.Abs(own[^1] - .2) < .00001 && Math.Abs(own[^2] - .2) < .00001, "Input2 did not reach both personal channels");
        Check(Math.Abs(broadcast[^1] - .2 * Math.Pow(10, -6.0/20)) < .0001, "Owned stream output lost its independent send gain");
        mixer.UpdateSource("Game", config.Sources[0].Dsp, new(), new(0, true));
        // Feed and drain 341 ms so the documented 20 ms gain smoother has settled.
        for (int block = 0; block < 4; block++)
        { capture.Emit(4096, .1f, .2f); own = personal.Pull(4096); broadcast = stream.Pull(4096); }
        Check(Math.Abs(own[^1] - .2) < .00001 && Math.Abs(broadcast[^1]) < .000001, "Stream mute leaked into personal output");
        capture.Fail(); Check(mixer.Status.Failure == "owned capture lost" && !mixer.Status.Running, "Owned capture failure was hidden");
        mixer.Dispose(); mixer.Dispose();
        Check(capture.Disposals == 1 && personal.Disposals == 1 && stream.Disposals == 1, "Adapters were not disposed exactly once");
        Check(capture.Stops == 1 && personal.Stops == 1 && stream.Stops == 1, "Adapter stop ownership was lost");
    }
    public static void PartialConstruction()
    {
        var capture = new FakeCapture(); var personal = new FakeOutput(); var stream = new FakeOutput { RejectInit = true };
        try
        {
            using var mixer = new WasapiStreamMixer(Configuration(), (_, _) => capture, (id, _) => id == "physical:test" ? personal : stream);
            throw new InvalidOperationException("Rejected output initialization was accepted");
        }
        catch (IOException error) when (error.Message == "owned output rejected") { }
        Check(capture.Disposals == 1 && personal.Disposals == 1 && stream.Disposals == 1, "Partial construction leaked an owned adapter");
        Check(capture.Starts == 0 && personal.Starts == 0 && stream.Starts == 0, "Construction started audio");
    }
    private static WasapiStreamMixConfiguration Configuration() => new(
        [new("Game", "owned:Game", new() { HeadroomDb = 0, InputChannel = 1 }, new(), new(-6))], "physical:test", "owned:Stream");
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private sealed class FakeCapture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public int Starts, Stops, Disposals;
        public void StartRecording() => Starts++;
        public void StopRecording() { Stops++; RecordingStopped?.Invoke(this, new()); }
        public void Dispose() => Disposals++;
        internal void Emit(int frames, float left, float right)
        {
            float[] pcm = new float[frames * 2]; for(int i=0;i<pcm.Length;i+=2) { pcm[i]=left; pcm[i+1]=right; }
            byte[] bytes = new byte[pcm.Length * 4]; Buffer.BlockCopy(pcm,0,bytes,0,bytes.Length);
            DataAvailable?.Invoke(this, new(bytes,bytes.Length));
        }
        internal void Fail() => RecordingStopped?.Invoke(this, new(new IOException("owned capture lost")));
    }
    private sealed class FakeOutput : IWavePlayer
    {
        private IWaveProvider? source;
        public bool RejectInit { get; init; }
        public int Starts, Stops, Disposals;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) { source = waveProvider; if (RejectInit) throw new IOException("owned output rejected"); }
        public void Play() { Starts++; PlaybackState = PlaybackState.Playing; }
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { Stops++; PlaybackState = PlaybackState.Stopped; PlaybackStopped?.Invoke(this,new()); }
        public void Dispose() => Disposals++;
        internal float[] Pull(int frames)
        {
            var bytes = new byte[frames*8]; int read = source!.Read(bytes,0,bytes.Length); Check(read == bytes.Length,"Output did not fill complete frames");
            var samples = new float[read/4]; Buffer.BlockCopy(bytes,0,samples,0,read); return samples;
        }
    }
}
