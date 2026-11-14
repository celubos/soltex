using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soltex.Audio.Transport.Tests;

internal sealed record ToneWindow(double EndSeconds, double[] Amplitudes, double Peak);

internal sealed class AudioProbe : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiLoopbackCapture capture;
    private readonly Stopwatch clock;
    private readonly double[] frequencies;
    private readonly float[] window;
    private readonly List<ToneWindow> measurements = [];
    private readonly object gate = new();
    private int used;
    private Exception? failure;
    internal WaveFormat Format => capture.WaveFormat;

    internal AudioProbe(MMDeviceEnumerator enumerator, string id, double[] frequencies, Stopwatch clock)
    {
        this.clock = clock;
        this.frequencies = frequencies;
        device = enumerator.GetDevice(id);
        try
        {
            capture = new WasapiLoopbackCapture(device);
            capture.WaveFormat = device.AudioClient.MixFormat;
            window = new float[capture.WaveFormat.SampleRate / 10];
            capture.DataAvailable += DataAvailable;
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null) failure = e.Exception; };
        }
        catch { capture?.Dispose(); device.Dispose(); throw; }
    }

    internal void Start() => capture.StartRecording();
    internal ToneWindow[] Since(double time)
    {
        if (failure is not null) throw new IOException("Loopback capture failed.", failure);
        lock (gate) return measurements.Where(m => m.EndSeconds >= time).ToArray();
    }

    internal double[] MedianSince(double time)
    {
        var values = Since(time);
        if (values.Length < 5) throw new IOException("Insufficient physical loopback frames; silence is not assumed from missing capture data.");
        return frequencies.Select((_, i) => values.Select(v => v.Amplitudes[i]).Order().ElementAt(values.Length / 2)).ToArray();
    }

    private void DataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            var format = capture.WaveFormat;
            var bytes = format.BitsPerSample / 8;
            if (args.BytesRecorded % format.BlockAlign != 0) throw new IOException("Partial loopback frame.");
            for (var offset = 0; offset < args.BytesRecorded; offset += format.BlockAlign)
            {
                float sample = 0;
                for (var channel = 0; channel < format.Channels; channel++)
                    sample += SignalMeasurement.DecodeSample(args.Buffer.AsSpan(offset + channel * bytes, bytes), format) / format.Channels;
                if (!float.IsFinite(sample)) throw new IOException("Non-finite loopback sample.");
                window[used++] = sample;
                if (used != window.Length) continue;
                var amplitudes = frequencies.Select(frequency => SignalMeasurement.Amplitude(window, format.SampleRate, frequency)).ToArray();
                var remainingFrames = (args.BytesRecorded - offset - format.BlockAlign) / format.BlockAlign;
                var measurement = new ToneWindow(clock.Elapsed.TotalSeconds - (double)remainingFrames / format.SampleRate,
                    amplitudes, window.Max(v => Math.Abs(v)));
                lock (gate)
                {
                    if (measurements.Count >= 12000) throw new IOException("Measurement timeline exceeded 20 minute bound.");
                    measurements.Add(measurement);
                }
                used = 0;
            }
        }
        catch (Exception error) { failure = error; }
    }

    public void Dispose()
    {
        capture.StopRecording();
        capture.Dispose();
        device.Dispose();
    }
}

internal sealed class AudioTone : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiOut output;
    private Exception? failure;
    internal AudioTone(MMDeviceEnumerator enumerator, string id, double frequency, double amplitude)
    {
        device = enumerator.GetDevice(id);
        try
        {
            var format = device.AudioClient.MixFormat;
            output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
            // Preserve WAVEFORMATEXTENSIBLE, including its native channel mask; generic multichannel float is rejected by some VADs.
            output.Init(new EndpointToneSource(format, frequency, amplitude));
            output.PlaybackStopped += (_, e) => { if (e.Exception is not null) failure = e.Exception; };
        }
        catch { output?.Dispose(); device.Dispose(); throw; }
    }
    internal void Start() => output.Play();
    internal void Verify() { if (failure is not null) throw new IOException("Test tone render failed.", failure); }
    public void Dispose() { output.Stop(); output.Dispose(); device.Dispose(); }
}
