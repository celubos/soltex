using NAudio.Wave;
using Soltex.Audio.Engine;
using Soltex.Audio.Transport;

namespace Soltex.Audio.OwnedEndpoints;

/// <summary>A single transport reader fans identical PCM into every active handoff path. Only the newest microphone lease writes.</summary>
public sealed class OwnedAudioRouter : IDisposable
{
    private readonly OwnedAudioTransport transport;
    private readonly object sync = new();
    private readonly List<RenderSubscription> renders = [];
    private readonly List<MicrophoneSubscription> microphones = [];
    private readonly ManualResetEventSlim stop = new();
    private readonly bool automatic;
    private Thread? worker;
    private bool disposed;
    private string? failure;
    public string? Failure => Volatile.Read(ref failure);
    public OwnedAudioRouter(OwnedAudioTransport transport, bool automatic = true)
    { this.transport = transport ?? throw new ArgumentNullException(nameof(transport)); this.automatic = automatic; }
    public RenderSubscription Subscribe(OwnedAudioBus bus, BoundedFloatBus? target = null)
    {
        if ((uint)bus >= 4) throw new ArgumentOutOfRangeException(nameof(bus));
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); var item = new RenderSubscription(this, bus, target); renders.Add(item); StartWorker(); return item; }
    }
    public MicrophoneSubscription Inject(ISampleProvider source, OwnedAudioBus bus = OwnedAudioBus.Microphone)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (bus is not (OwnedAudioBus.Microphone or OwnedAudioBus.Stream)) throw new ArgumentOutOfRangeException(nameof(bus));
        if (source.WaveFormat.SampleRate != 48000 || source.WaveFormat.Channels != 2) throw new ArgumentException("Microphone injection requires 48 kHz stereo.", nameof(source));
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); var item = new MicrophoneSubscription(this, source, bus); microphones.Add(item); StartWorker(); return item; }
    }
    private void StartWorker()
    {
        if (!automatic || worker is not null) return;
        worker = new Thread(() =>
        {
            // This thread paces kernel refills; MMCSS keeps its 10 ms waits on time under CPU load.
            AudioThreadScheduling.RegisterCurrentThread();
            while (!stop.IsSet)
            {
                try { PumpOnce(); } catch (Exception error) { Volatile.Write(ref failure, error.Message); return; }
                stop.Wait(10);
            }
        }) { IsBackground = true, Name = "Soltex owned PCM transport" };
        worker.Start();
    }
    /// <summary>One bounded batch. Public for deterministic device-free verification; never call concurrently with automatic mode.</summary>
    public void PumpOnce()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var group in renders.GroupBy(item => item.Bus))
            {
                var packet = transport.ReadRender(group.Key, 4800);
                foreach (var target in group) { target.Buffer.Write(packet.Samples); target.DriverStatus = packet.Status; }
            }
            foreach (var writers in microphones.GroupBy(item => item.Bus))
            {
                var selected = writers.Last();
                var status = transport.Query(selected.Bus);
                // Refill against kernel consumption, not a nominal user-mode timer that can drift.
                int wanted = Math.Clamp(960 - status.BufferedFrames, 0, 960) * 2;
                int count = wanted == 0 ? 0 : selected.Source.Read(selected.Samples, 0, wanted);
                if (count > 0) selected.DriverStatus = transport.WriteCapture(selected.Bus, selected.Samples.AsSpan(0, count));
            }
        }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; stop.Set(); }
        if (worker is not null && !worker.Join(TimeSpan.FromSeconds(3))) throw new TimeoutException("Owned PCM transport worker did not finish its bounded I/O.");
        lock (sync) { renders.Clear(); microphones.Clear(); transport.Dispose(); }
        stop.Dispose();
    }
    public sealed class RenderSubscription : IDisposable
    {
        private readonly OwnedAudioRouter owner;
        internal OwnedAudioBus Bus { get; }
        public BoundedFloatBus Buffer { get; }
        public OwnedAudioStatus? DriverStatus { get; internal set; }
        internal RenderSubscription(OwnedAudioRouter owner, OwnedAudioBus bus, BoundedFloatBus? target) { this.owner = owner; Bus = bus; Buffer = target ?? new(48000, 2, 9600); if (Buffer.WaveFormat.SampleRate != 48000 || Buffer.WaveFormat.Channels != 2) throw new ArgumentException("Render target must be 48 kHz stereo.", nameof(target)); }
        public void Dispose() { lock (owner.sync) owner.renders.Remove(this); }
    }
    public sealed class MicrophoneSubscription : IDisposable
    {
        private readonly OwnedAudioRouter owner;
        internal ISampleProvider Source { get; }
        internal OwnedAudioBus Bus { get; }
        internal float[] Samples { get; } = new float[1920];
        public OwnedAudioStatus? DriverStatus { get; internal set; }
        internal MicrophoneSubscription(OwnedAudioRouter owner, ISampleProvider source, OwnedAudioBus bus) { this.owner = owner; Source = source; Bus = bus; }
        public void Dispose() { lock (owner.sync) owner.microphones.Remove(this); }
    }
}
