using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Cable.Tests;

/// <summary>Bounded RAM-only ordinary capture; stores mono samples and timing, never audio files.</summary>
internal sealed class CaptureProbe : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly Stopwatch _clock;
    private readonly object _gate = new();
    private readonly float[] _samples;
    private readonly WaveFormat _format;
    private int _used;
    private double? _origin;
    private volatile Exception? _failure;
    internal int SampleRate => _format.SampleRate;
    internal string Format => _format.ToString();

    internal CaptureProbe(MMDeviceEnumerator enumerator, string endpoint, Stopwatch clock)
    {
        _device = enumerator.GetDevice(endpoint); _clock = clock;
        try
        {
            if (_device.DataFlow != NAudio.CoreAudioApi.DataFlow.Capture || _device.State != DeviceState.Active)
                throw new InvalidOperationException("A live capture endpoint is required.");
            _capture = new(_device, true, 40);
            using AudioClient formatClient = _device.AudioClient;
            _format = formatClient.MixFormat;
            if (_format.Channels is < 1 or > 2 || _format.SampleRate is < 8000 or > 192000)
                throw new NotSupportedException("The focused acceptance supports mono/stereo 8–192 kHz capture without reconfiguring devices.");
            _capture.WaveFormat = _format;
            _samples = new float[_format.SampleRate * 16];
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, args) => { if (args.Exception is not null) _failure = args.Exception; };
        }
        catch { _capture?.Dispose(); _device.Dispose(); throw; }
    }

    internal void Start() => _capture.StartRecording();

    internal float[] Window(double start, double end)
    {
        lock (_gate)
        {
            if (_failure is not null) throw new IOException("Capture failed.", _failure);
            if (_origin is null) throw new InconclusiveException("No capture frames arrived; missing frames are not silence.");
            int first = (int)Math.Round((start - _origin.Value) * SampleRate);
            int last = (int)Math.Round((end - _origin.Value) * SampleRate);
            if (first < 0 || last > _used || last <= first)
                throw new InconclusiveException("A complete bounded capture window is unavailable.");
            return _samples.AsSpan(first, last - first).ToArray();
        }
    }

    internal double ToneAmplitude(double start, double end, double frequency)
    {
        float[] samples = Window(start, end);
        try { return SignalMeasurement.Amplitude(samples, SampleRate, frequency); }
        finally { Array.Clear(samples); }
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        try
        {
            int frames = args.BytesRecorded / _format.BlockAlign;
            if (args.BytesRecorded % _format.BlockAlign != 0) throw new IOException("Partial capture frame.");
            lock (_gate)
            {
                _origin ??= _clock.Elapsed.TotalSeconds - (double)frames / SampleRate;
                if (frames > _samples.Length - _used) throw new IOException("Capture exceeded its fixed 16-second RAM bound.");
                int width = _format.BitsPerSample / 8;
                for (int frame = 0; frame < frames; frame++)
                {
                    float sum = 0;
                    for (int channel = 0; channel < _format.Channels; channel++)
                        sum += SignalMeasurement.DecodeSample(args.Buffer.AsSpan(frame * _format.BlockAlign + channel * width, width), _format) / _format.Channels;
                    if (!float.IsFinite(sum)) throw new IOException("Nonfinite input sample.");
                    _samples[_used++] = sum;
                }
            }
        }
        catch (Exception exception) { _failure = exception; }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); }
        finally
        {
            _capture.Dispose(); _device.Dispose();
            lock (_gate) { Array.Clear(_samples); _used = 0; }
        }
    }
}

internal sealed class InconclusiveException(string message) : Exception(message);
