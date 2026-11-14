using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

public sealed record MicrophoneProbeStatus(string EndpointId, bool Running, int SampleRate, int Channels,
    long Frames, IReadOnlyList<float> Peaks, IReadOnlyList<float> Rms, string? Error);

public interface IMicrophoneProbe : IDisposable
{
    MicrophoneProbeStatus Status { get; }
    void Start();
}

/// <summary>Capture only. Retains numeric measurements, never PCM or a playback endpoint.</summary>
public sealed class WindowsMicrophoneProbe : IMicrophoneProbe
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device;
    private readonly WasapiCapture capture;
    private readonly object sync = new();
    private readonly string endpoint;
    private readonly float[] samples;
    private MicrophoneProbeStatus status;
    private bool disposed;
    public WindowsMicrophoneProbe(string endpointId)
    {
        endpoint = endpointId;
        MMDevice? opened = null;
        WasapiCapture? stream = null;
        try
        {
            opened = enumerator.GetDevice(endpointId);
            if (opened.DataFlow != DataFlow.Capture || opened.State != DeviceState.Active)
                throw new ArgumentException("Choose an active capture input, not a playback speaker.", nameof(endpointId));
            stream = new WasapiCapture(opened, true, 40);
            if (stream.WaveFormat.Channels is < 1 or > 8) throw new NotSupportedException("Input test supports one to eight hardware inputs.");
            PcmDecoder.Decode([], [], stream.WaveFormat);
            samples = new float[stream.WaveFormat.SampleRate / 10 * stream.WaveFormat.Channels];
            device = opened; capture = stream;
            status = new(endpoint, false, capture.WaveFormat.SampleRate, capture.WaveFormat.Channels, 0, [], [], null);
            capture.DataAvailable += Data;
            capture.RecordingStopped += Stopped;
        }
        catch { stream?.Dispose(); opened?.Dispose(); enumerator.Dispose(); throw; }
    }
    public MicrophoneProbeStatus Status { get { lock (sync) return status with { Peaks = status.Peaks.ToArray(), Rms = status.Rms.ToArray() }; } }
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (sync) status = status with { Running = true };
        try { capture.StartRecording(); }
        catch { lock (sync) status = status with { Running = false }; throw; }
    }
    private void Data(object? sender, WaveInEventArgs args)
    {
        try
        {
            int channels = capture.WaveFormat.Channels;
            float[] peaks = new float[channels]; double[] energy = new double[channels]; long frames = 0;
            int blockBytes = samples.Length / channels * capture.WaveFormat.BlockAlign;
            for (int offset = 0; offset < args.BytesRecorded; offset += blockBytes)
            {
                int count = PcmDecoder.Decode(args.Buffer.AsSpan(offset, Math.Min(blockBytes, args.BytesRecorded - offset)), samples, capture.WaveFormat);
                for (int i = 0; i < count; i++) { float value = float.IsFinite(samples[i]) ? samples[i] : 0; int ch = i % channels; peaks[ch] = Math.Max(peaks[ch], Math.Abs(value)); energy[ch] += value * (double)value; }
                frames += count / channels;
            }
            lock (sync) status = status with { Frames = status.Frames + frames, Peaks = peaks, Rms = energy.Select(x => frames > 0 ? (float)Math.Sqrt(x / frames) : 0).ToArray() };
        }
        catch (Exception error) { lock (sync) status = status with { Running = false, Error = error.Message }; }
    }
    private void Stopped(object? sender, StoppedEventArgs args) { lock (sync) status = status with { Running = false, Error = args.Exception?.Message ?? status.Error }; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { capture.StopRecording(); }
        finally { capture.DataAvailable -= Data; capture.RecordingStopped -= Stopped; capture.Dispose(); device.Dispose(); enumerator.Dispose(); lock (sync) status = status with { Running = false }; }
    }
}
