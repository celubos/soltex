using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soltex.Audio.Engine;

public sealed record StreamMixSourceConfiguration(string Id, string CaptureEndpointId, DspSettings Dsp,
    StreamMixSendSettings Personal, StreamMixSendSettings Stream);
public sealed record WasapiStreamMixConfiguration(IReadOnlyList<StreamMixSourceConfiguration> Sources,
    string PersonalRenderEndpointId, string StreamRenderEndpointId)
{
    public int LatencyMs { get; init; } = 40;
    public int BufferMs { get; init; } = 200;
    public double OutputCeiling { get; init; } = .98;
}
public sealed record StreamMixSourceStatus(string Id, long ProcessedFrames, StreamMixQueueStatus Personal, StreamMixQueueStatus Stream)
{
    public string CaptureEndpointId { get; init; } = "";
}
public sealed record WasapiStreamMixStatus(bool Running, string? Failure, float PersonalPeak, float StreamPeak,
    long PersonalLimitedSamples, long StreamLimitedSamples, IReadOnlyList<StreamMixSourceStatus> Sources)
{
    public string PersonalRenderEndpointId { get; init; } = "";
    public string StreamRenderEndpointId { get; init; } = "";
}

/// <summary>Explicit independent personal/stream mixes. Each source opens exactly one capture and executes its DSP once.
/// Construct/start/update/stop/dispose on one MTA control owner without a synchronization context.</summary>
public sealed class WasapiStreamMixer : IDisposable
{
    private MMDeviceEnumerator? enumerator;
    private readonly List<CapturePath> captures = [];
    private readonly List<RenderPath> renders = [];
    private Exception? failure;
    private volatile bool started, disposed;
    private bool hasStarted;
    public WasapiStreamMixStatus Status => new(started && !disposed && Volatile.Read(ref failure) is null,
        Volatile.Read(ref failure)?.Message, renders[0].Guard.Peak, renders[1].Guard.Peak,
        renders[0].Sum.LimitedSamples, renders[1].Sum.LimitedSamples,
        captures.Select(c => new StreamMixSourceStatus(c.Id, c.Source.ProcessedFrames, c.Source.PersonalQueue, c.Source.StreamQueue) { CaptureEndpointId = c.EndpointId }).ToArray())
        { PersonalRenderEndpointId = renders[0].EndpointId, StreamRenderEndpointId = renders[1].EndpointId };

    public WasapiStreamMixer(WasapiStreamMixConfiguration configuration) : this(configuration, null, null) { }

    /// <summary>Optional owned-transport adapters. Return null to use ordinary WASAPI. Render adapters expose
    /// their fixed OutputWaveFormat before Init. The mixer owns every returned adapter exactly once.</summary>
    public WasapiStreamMixer(WasapiStreamMixConfiguration configuration,
        Func<string, int, IWaveIn?>? captureFactory, Func<string, int, IWavePlayer?>? renderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(configuration.Sources);
        if (configuration.Sources.Count is < 1 or > 5) throw new ArgumentException("One to five capture sources are required.", nameof(configuration));
        configuration = configuration with { Sources = Array.AsReadOnly(configuration.Sources.ToArray()) };
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.PersonalRenderEndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.StreamRenderEndpointId);
        DspSettings.Range(configuration.LatencyMs, 10, 500, nameof(configuration.LatencyMs));
        DspSettings.Range(configuration.BufferMs, configuration.LatencyMs * 2, 2000, nameof(configuration.BufferMs));
        DspSettings.Range(configuration.OutputCeiling, .01, 1, nameof(configuration.OutputCeiling));
        HashSet<string> ids = new(StringComparer.Ordinal), endpoints = new(StringComparer.Ordinal);
        foreach (var source in configuration.Sources)
        {
            ArgumentNullException.ThrowIfNull(source); ArgumentException.ThrowIfNullOrWhiteSpace(source.Id); ArgumentException.ThrowIfNullOrWhiteSpace(source.CaptureEndpointId);
            if (!ids.Add(source.Id) || !endpoints.Add(source.CaptureEndpointId)) throw new ArgumentException("Source identities and capture endpoints must be unique; fanout shares a single capture.", nameof(configuration));
            ArgumentNullException.ThrowIfNull(source.Dsp); ArgumentNullException.ThrowIfNull(source.Personal); ArgumentNullException.ThrowIfNull(source.Stream);
            _ = source.Personal.Target(); _ = source.Stream.Target();
        }
        MMDeviceEnumerator GetEnumerator() => enumerator ??= new();
        try
        {
            foreach (var source in configuration.Sources) captures.Add(new CapturePath(GetEnumerator, source, configuration, RecordFailure, captureFactory));
            renders.Add(new RenderPath(GetEnumerator, configuration.PersonalRenderEndpointId, captures.Select(c => c.Source.Personal).ToArray(), configuration, OnStopped, renderFactory));
            renders.Add(new RenderPath(GetEnumerator, configuration.StreamRenderEndpointId, captures.Select(c => c.Source.Stream).ToArray(), configuration, OnStopped, renderFactory));
        }
        catch (Exception error)
        {
            var errors = DisposeResources();
            if (errors.Count != 0) { errors.Insert(0, error); throw new AggregateException("Stream mixer construction and cleanup failed.", errors); }
            throw;
        }
    }

    public void UpdateSource(string id, DspSettings dsp, StreamMixSendSettings personal, StreamMixSendSettings stream)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var capture = captures.Find(c => StringComparer.Ordinal.Equals(c.Id, id)) ?? throw new ArgumentException("Unknown source.", nameof(id));
        capture.Source.Update(dsp, personal, stream);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (hasStarted) throw new InvalidOperationException("Create a new stream mixer for another session.");
        hasStarted = true; started = true;
        try { foreach (var render in renders) render.Output.Play(); foreach (var capture in captures) capture.Input.StartRecording(); }
        catch (Exception error)
        {
            try { Stop(); } catch (Exception cleanup) { throw new AggregateException("Stream mixer start and stop failed.", error, cleanup); }
            throw;
        }
    }

    public void Stop()
    {
        if (disposed) return; started = false;
        List<Exception> errors = [];
        foreach (var capture in captures) Attempt(capture.Stop, errors);
        foreach (var render in renders) Attempt(render.Output.Stop, errors);
        if (errors.Count != 0) throw new AggregateException("Stream mixer stop did not close every stream cleanly.", errors);
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    { if (args.Exception is not null) RecordFailure(args.Exception); else if (started) RecordFailure(new IOException("An owned stream-mix output stopped unexpectedly.")); }
    private void RecordFailure(Exception error) => Interlocked.CompareExchange(ref failure, error, null);
    private static void Attempt(Action action, List<Exception> errors) { try { action(); } catch (Exception error) { errors.Add(error); } }
    private List<Exception> DisposeResources()
    {
        List<Exception> errors = [];
        foreach (var render in renders) Attempt(render.Dispose, errors);
        foreach (var capture in captures) Attempt(capture.Dispose, errors);
        if (enumerator is not null) Attempt(enumerator.Dispose, errors);
        return errors;
    }
    public void Dispose()
    {
        if (disposed) return;
        Exception? stopError = null; try { Stop(); } catch (Exception error) { stopError = error; }
        disposed = true; var errors = DisposeResources();
        if (stopError is not null) errors.Insert(0, stopError);
        if (errors.Count != 0) throw new AggregateException("Stream mixer resources did not all close cleanly.", errors);
    }

    private sealed class CapturePath : IDisposable
    {
        private readonly MMDevice? device;
        private readonly WaveFormat format;
        private readonly float[] decoded;
        private readonly Action<Exception> fail;
        private bool disposing;
        private volatile bool stopping;
        public string Id { get; }
        public string EndpointId { get; }
        public IWaveIn Input { get; }
        public StreamMixPcmSource Source { get; }
        internal CapturePath(Func<MMDeviceEnumerator> getEnumerator, StreamMixSourceConfiguration source, WasapiStreamMixConfiguration config,
            Action<Exception> fail, Func<string, int, IWaveIn?>? factory)
        {
            this.fail = fail; Id = source.Id; EndpointId = source.CaptureEndpointId;
            IWaveIn? input = null;
            try
            {
                input = factory?.Invoke(source.CaptureEndpointId, config.LatencyMs);
                if (input is null)
                {
                    device = getEnumerator().GetDevice(source.CaptureEndpointId);
                    if (device.DataFlow != DataFlow.Capture || device.State != DeviceState.Active) throw new InvalidOperationException("Every selected source must be an active capture endpoint.");
                    input = new WasapiCapture(device, true, config.LatencyMs);
                }
                format = input.WaveFormat;
                if (format.Channels is < 1 or > 2) throw new NotSupportedException("Stream-mix sources currently support mono or stereo.");
                PcmDecoder.Decode([], [], format);
                decoded = new float[Math.Max(1, format.SampleRate / 50) * format.Channels];
                Source = new(format.SampleRate, format.Channels, format.SampleRate * config.BufferMs / 1000, source.Dsp, source.Personal, source.Stream);
                Input = input; Input.DataAvailable += Data; Input.RecordingStopped += Stopped;
            }
            catch { try { input?.Dispose(); } finally { device?.Dispose(); } throw; }
        }
        private void Data(object? sender, WaveInEventArgs args)
        {
            try
            {
                int bytes = decoded.Length / format.Channels * format.BlockAlign;
                for (int offset = 0; offset < args.BytesRecorded; offset += bytes)
                {
                    int count = PcmDecoder.Decode(args.Buffer.AsSpan(offset, Math.Min(bytes, args.BytesRecorded - offset)), decoded, format);
                    Source.ProcessAndPublish(decoded.AsSpan(0, count));
                }
            }
            catch (Exception error) { fail(error); }
        }
        private void Stopped(object? sender, StoppedEventArgs args)
        { if (args.Exception is not null) fail(args.Exception); else if (!disposing && !stopping) fail(new IOException("An owned stream-mix capture stopped.")); }
        internal void Stop() { stopping = true; Input.StopRecording(); }
        public void Dispose()
        {
            disposing = true; Input.DataAvailable -= Data; Input.RecordingStopped -= Stopped;
            try { Input.Dispose(); } finally { device?.Dispose(); }
        }
    }

    private sealed class RenderPath : IDisposable
    {
        private readonly MMDevice? device;
        public string EndpointId { get; }
        public IWavePlayer Output { get; }
        public StreamMixSumProvider Sum { get; }
        public OutputCeilingSampleProvider Guard { get; }
        internal RenderPath(Func<MMDeviceEnumerator> getEnumerator, string endpointId, ISampleProvider[] sources, WasapiStreamMixConfiguration config,
            EventHandler<StoppedEventArgs> stopped, Func<string, int, IWavePlayer?>? factory)
        {
            EndpointId = endpointId; IWavePlayer? output = null;
            try
            {
                output = factory?.Invoke(endpointId, config.LatencyMs);
                WaveFormat format;
                if (output is null)
                {
                    device = getEnumerator().GetDevice(endpointId);
                    if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active) throw new InvalidOperationException("Both destinations must be active render endpoints.");
                    format = device.AudioClient.MixFormat;
                    output = new WasapiOut(device, AudioClientShareMode.Shared, true, config.LatencyMs);
                }
                else format = output.OutputWaveFormat ?? throw new InvalidOperationException("An owned output must declare its format before initialization.");
                for (int i = 0; i < sources.Length; i++)
                {
                    sources[i] = (sources[i].WaveFormat.Channels, format.Channels) switch
                    { (1, 2) => new MonoToStereoSampleProvider(sources[i]), (2, 1) => new StereoToMonoSampleProvider(sources[i]) { LeftVolume = .5f, RightVolume = .5f },
                        (1, 1) or (2, 2) => sources[i], _ => throw new NotSupportedException("Stream-mix outputs currently support mono or stereo.") };
                    if (sources[i].WaveFormat.SampleRate != format.SampleRate) sources[i] = new WdlResamplingSampleProvider(sources[i], format.SampleRate);
                }
                Sum = new(sources, config.OutputCeiling); Guard = new(Sum, config.OutputCeiling);
                output.Init(Guard.ToWaveProvider());
                Output = output; Output.PlaybackStopped += stopped;
            }
            catch { try { output?.Dispose(); } finally { device?.Dispose(); } throw; }
        }
        public void Dispose() { try { Output.Dispose(); } finally { device?.Dispose(); } }
    }
}
