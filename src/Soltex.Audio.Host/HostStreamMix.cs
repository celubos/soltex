using Soltex.Audio.Engine;

namespace Soltex.Audio.Host;

public sealed record HostStreamSendProfile
{
    public AudioBus Bus { get; init; }
    public double PersonalGainDb { get; init; }
    public bool PersonalMute { get; init; }
    public double StreamGainDb { get; init; }
    public bool StreamMute { get; init; }
}
public sealed record HostStreamMixProfile
{
    public string? PersonalRenderEndpointId { get; init; }
    public string? StreamRenderEndpointId { get; init; }
    public IReadOnlyList<HostStreamSendProfile> Sends { get; init; } = Enum.GetValues<AudioBus>().Select(bus => new HostStreamSendProfile
    { Bus = bus, PersonalMute = bus == AudioBus.Microphone }).ToArray();
}
public interface IHostStreamMix : IDisposable
{
    WasapiStreamMixStatus Status { get; }
    void Start();
    void UpdateSource(string id, DspSettings dsp, StreamMixSendSettings personal, StreamMixSendSettings stream);
}

public sealed partial class AudioHostController
{
    private IHostStreamMix? streamMix;
    private AudioHostProfile? streamSettings;
    private WasapiStreamMixStatus? streamFailure;

    public Task<HostSnapshot> StartStreamMixAsync(CancellationToken cancellationToken = default) => Enqueue(() =>
    {
        if (running || active.Count > 0 || streamMix is not null || streamFailure is not null) throw new InvalidOperationException("Stop the current audio mode before starting the personal / stream mix.");
        if (failedRetirement.Count > 0) throw new InvalidOperationException("Audio resources did not confirm shutdown. Restart the audio host before opening another mode.");
        endpoints = backend!.Enumerate();
        var configuration = StreamConfiguration(profile);
        StopProbe();
        IHostStreamMix replacement;
        try { replacement = backend.CreateStreamMix(configuration); }
        catch (AggregateException) { failedRetirement.Add(AudioBus.Game); throw; }
        try
        {
            replacement.Start();
            if (!replacement.Status.Running || replacement.Status.Failure is not null) throw new IOException(replacement.Status.Failure ?? "Stream outputs did not start.");
            streamMix = replacement; streamSettings = profile;
        }
        catch
        {
            try { replacement.Dispose(); } catch (Exception error) { failedRetirement.Add(AudioBus.Game); lastError = "Stream startup cleanup failed: " + error.Message; }
            throw;
        }
        return Snapshot();
    }, cancellationToken);

    private WasapiStreamMixConfiguration StreamConfiguration(AudioHostProfile candidate)
    {
        var mix = candidate.StreamMix;
        var personal = endpoints.FirstOrDefault(e => e.Id == mix.PersonalRenderEndpointId && !e.IsCapture && e.IsActive);
        var stream = endpoints.FirstOrDefault(e => e.Id == mix.StreamRenderEndpointId && !e.IsCapture && e.IsActive);
        if (personal is null || stream is null) throw new ArgumentException("Choose two available render destinations for personal and stream output.");
        if (personal.Id == stream.Id || personal.TransportId is not null && personal.TransportId == stream.TransportId)
            throw new ArgumentException("Personal and stream outputs must be independent destinations, not two pins of one cable.");
        var channels = candidate.Channels.Where(c => c.Enabled).ToArray();
        if (channels.Length == 0) throw new ArgumentException("Enable at least one capture source in Soltex channels before starting stream mix.");
        List<StreamMixSourceConfiguration> sources = [];
        HashSet<string> transports = new(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            var capture = endpoints.FirstOrDefault(e => e.Id == channel.CaptureEndpointId && e.IsCapture && e.IsActive);
            if (capture is null) throw new ArgumentException(channel.Bus + ": choose an available capture input.");
            if (capture.TransportId is string transport && !transports.Add(transport)) throw new ArgumentException("Enabled sources must use separate virtual transports, not aliases of one cable.");
            if (capture.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase) || personal.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase) || stream.Name.Contains("Sonar", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Sonar endpoints are excluded from the independent stream mix.");
            if (channel.ExcludedOutputIds.Contains(personal.Id) || channel.ExcludedOutputIds.Contains(stream.Id))
                throw new ArgumentException(channel.Bus + ": a selected mix output is excluded in device preferences.");
            if (capture.TransportId is not null && (capture.TransportId == personal.TransportId || capture.TransportId == stream.TransportId))
                throw new ArgumentException(channel.Bus + ": a mix output feeds its own capture transport. Feedback route rejected.");
            var sends = StreamSends(candidate, channel);
            sources.Add(new(channel.Bus.ToString(), capture.Id, StreamDsp(channel), sends.Personal, sends.Stream));
        }
        return new(sources, personal.Id, stream.Id);
    }
    private static DspSettings StreamDsp(HostChannelProfile channel) => channel.Dsp with
    { GainDb = Math.Clamp(channel.Dsp.GainDb + channel.GainDb, -80, 24), Mute = channel.Dsp.Mute || channel.Mute };
    private static (StreamMixSendSettings Personal, StreamMixSendSettings Stream) StreamSends(AudioHostProfile profile, HostChannelProfile channel)
    {
        var send = profile.StreamMix.Sends.Single(s => s.Bus == channel.Bus);
        bool playback = channel.Bus != AudioBus.Microphone;
        double balance = 1;
        if (profile.Channels.Any(c => c.Enabled && c.Bus == AudioBus.Game) && profile.Channels.Any(c => c.Enabled && c.Bus == AudioBus.Chat))
        {
            var gains = ChatMix.Gains(profile.ChatMix);
            if (channel.Bus == AudioBus.Game) balance = gains.Game;
            else if (channel.Bus == AudioBus.Chat) balance = gains.Chat;
        }
        return (new(Math.Clamp(send.PersonalGainDb + (playback ? profile.MasterGainDb : 0) + (balance > 0 ? 20 * Math.Log10(balance) : -80), -80, 12),
                send.PersonalMute || playback && profile.MasterMute || balance < .0000001), new(send.StreamGainDb, send.StreamMute));
    }
    private void ValidateStreamChange(AudioHostProfile candidate)
    {
        if (streamMix is null) return;
        if (profile.StreamMix.PersonalRenderEndpointId != candidate.StreamMix.PersonalRenderEndpointId || profile.StreamMix.StreamRenderEndpointId != candidate.StreamMix.StreamRenderEndpointId ||
            profile.Channels.Any(c => { var changed = candidate.Channels.Single(n => n.Bus == c.Bus); return c.Enabled != changed.Enabled || c.Enabled && c.CaptureEndpointId != changed.CaptureEndpointId; }))
            throw new InvalidOperationException("Stop stream mix before changing its capture sources or output devices. Gain, mute and processing can be edited live.");
        _ = StreamConfiguration(candidate);
    }
    private void UpdateStreamMix()
    {
        if (streamMix?.Status is { Failure: not null } failure)
        {
            streamFailure = failure with { Running = false }; StopStreamMix(); return;
        }
        if (streamMix is null || ReferenceEquals(streamSettings, profile)) return;
        foreach (var channel in profile.Channels.Where(c => c.Enabled))
        {
            var sends = StreamSends(profile, channel);
            streamMix.UpdateSource(channel.Bus.ToString(), StreamDsp(channel), sends.Personal, sends.Stream);
        }
        streamSettings = profile;
    }
    private void StopStreamMix()
    {
        if (streamMix is not { } mix) return;
        streamMix = null; streamSettings = null;
        try { mix.Dispose(); }
        catch (Exception error) { failedRetirement.Add(AudioBus.Game); lastError = "Stream mix did not confirm shutdown: " + error.Message; }
    }
}
