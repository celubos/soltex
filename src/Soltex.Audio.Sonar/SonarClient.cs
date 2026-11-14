using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Soltex.Audio.Sonar;

/// <summary>
/// Optional controller for installed Sonar 1.106. Requires GG, Sonar and proprietary drivers.
/// It does not implement an independent audio graph or signal processing and never starts or installs GG.
/// </summary>
public sealed partial class SonarClient : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly string[] SnapshotPaths = ["AudioDevices", "ClassicRedirections", "VolumeSettings/classic", "AudioDeviceRouting"];
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    public Uri Endpoint { get; }

    private SonarClient(Uri endpoint, HttpClient http)
    {
        if (endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.AbsolutePath != "/")
            throw new ArgumentException("Sonar endpoint must be a discovered IPv4 loopback root.", nameof(endpoint));
        Endpoint = endpoint;
        _http = http;
    }

    public static async Task<SonarClient> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var endpoint in SonarDiscovery.GetCandidates())
        {
            var client = new SonarClient(endpoint, new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(4) });
            try
            {
                await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                client.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { client.Dispose(); }
            catch { client.Dispose(); throw; }
        }
        throw new SonarUnavailableException("The installed Sonar engine is unavailable. Open SteelSeries GG, enable Sonar, and retry.");
    }

    public async Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var requests = SnapshotPaths
            .Select(path => ReadAsync(path, cancellationToken)).ToArray();
        var data = await Task.WhenAll(requests).ConfigureAwait(false);
        return SonarProtocol.ParseSnapshot(data[0], data[1], data[2], data[3]) with { Endpoint = Endpoint };
    }

    public Task<SonarSnapshot> SetVolumeAsync(string role, double volume, CancellationToken cancellationToken = default)
    {
        role = ValidateRole(role);
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        var requestedRole = role;
        return CommandAsync($"VolumeSettings/classic/{role}/Volume/{volume.ToString("R", CultureInfo.InvariantCulture)}",
            snapshot => Math.Abs(snapshot.Channels.Single(c => c.Role == requestedRole).Volume - volume) < 0.001,
            "Sonar did not confirm the requested level.", cancellationToken);
    }

    public Task<SonarSnapshot> SetMuteAsync(string role, bool muted, CancellationToken cancellationToken = default)
    {
        role = ValidateRole(role);
        var requestedRole = role;
        return CommandAsync($"VolumeSettings/classic/{role}/Mute/{muted.ToString().ToLowerInvariant()}",
            snapshot => snapshot.Channels.Single(c => c.Role == requestedRole).Muted == muted,
            "Sonar did not confirm the requested mute state.", cancellationToken);
    }

    public async Task<SonarSnapshot> SetOutputAsync(string bus, string endpointId, bool linked = false, CancellationToken cancellationToken = default)
    {
        if (bus is not ("game" or "chat" or "media" or "aux" or "mic")) throw new ArgumentException("Unknown audio bus.", nameof(bus));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (bus == "mic" && linked) throw new ArgumentException("Microphone input cannot be linked to playback outputs.", nameof(linked));
        var before = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var target = before.Devices.SingleOrDefault(d => d.Id == endpointId);
        if (target is null || target.IsVad || target.State != "active" || target.DataFlow != (bus == "mic" ? "capture" : "render"))
            throw new SonarCommandException("Choose an available physical device with the correct audio direction.");
        return await CommandAsync($"ClassicRedirections/{(linked ? "render" : bus)}/deviceId/{Uri.EscapeDataString(endpointId)}",
            snapshot => snapshot.Channels.Where(c => linked ? c.Id is "game" or "chat" or "media" or "aux" : c.Id == bus)
                .All(c => c.DeviceId == endpointId && c.IsRunning),
            "Sonar has not confirmed an active route to the requested device.", cancellationToken).ConfigureAwait(false);
    }

    public async Task<SonarRoutingResult> RouteApplicationAsync(string dataFlow, string endpointId, uint processId,
        CancellationToken cancellationToken = default)
    {
        if (dataFlow is not ("render" or "capture")) throw new ArgumentException("Unknown audio direction.", nameof(dataFlow));
        if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId), "System sounds cannot be assigned as an application.");
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!before.Devices.Any(d => d.Id == endpointId && d.DataFlow == dataFlow && d.State == "active"))
                throw new SonarCommandException("The selected application destination is unavailable.");
            if (!before.Applications.Any(a => a.ProcessId == processId && a.DataFlow == dataFlow))
                throw new SonarCommandException("The application's audio session has ended; refresh before routing.");
            await PutAsync($"AudioDeviceRouting/{dataFlow}/{Uri.EscapeDataString(endpointId)}/{processId}", cancellationToken).ConfigureAwait(false);
            var policyJson = await ReadAsync($"AudioDeviceRouting/{dataFlow}/{processId}", cancellationToken).ConfigureAwait(false);
            var policy = SonarProtocol.ParsePolicyDeviceId(policyJson);
            var after = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var sessions = after.Applications.Where(a => a.ProcessId == processId && a.DataFlow == dataFlow && a.State == "active").ToArray();
            return new(endpointId, policy, string.Equals(policy, endpointId, StringComparison.OrdinalIgnoreCase),
                sessions.Length > 0 && sessions.All(a => a.DeviceId == endpointId), after);
        }
        finally { _commands.Release(); }
    }

    /// <summary>Receives bounded state notifications and reconnects. No peak values are synthesized from volume events.</summary>
    public async IAsyncEnumerable<SonarEvent> WatchEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        var attempt = 0;
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var connected = false;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                await socket.ConnectAsync(new UriBuilder(Endpoint) { Scheme = "ws", Path = "sock" }.Uri, connectTimeout.Token).ConfigureAwait(false);
                connected = true;
                attempt = 0;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException && !token.IsCancellationRequested) { }
            if (connected)
            {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    JsonElement? payload = null;
                    try { payload = await ReceiveEventAsync(socket, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is WebSocketException or IOException or JsonException) { break; }
                    if (payload is null) break;
                    yield return new SonarEvent(DateTimeOffset.UtcNow, payload.Value);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempt++, 3))), token).ConfigureAwait(false);
        }
    }

    private static async Task<JsonElement?> ReceiveEventAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaximumResponseBytes)
                throw new IOException("Sonar returned an unsupported or oversized event.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private async Task<SonarSnapshot> CommandAsync(string path, Func<SonarSnapshot, bool> confirmed, string failure, CancellationToken token)
    {
        ThrowIfDisposed();
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PutAsync(path, token).ConfigureAwait(false);
            var after = await GetSnapshotAsync(token).ConfigureAwait(false);
            if (!confirmed(after)) throw new SonarCommandException(failure);
            return after;
        }
        finally { _commands.Release(); }
    }

    private async Task PutAsync(string path, CancellationToken token)
    {
        using var response = await _http.PutAsync(new Uri(Endpoint, path), null, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new SonarCommandException($"Sonar rejected the command (HTTP {(int)response.StatusCode}). Refresh the mixer and retry.");
    }

    private async Task<string> ReadAsync(string path, CancellationToken token)
    {
        using var response = await _http.GetAsync(new Uri(Endpoint, path), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new SonarUnavailableException($"Sonar state is unavailable (HTTP {(int)response.StatusCode}).");
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, token).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    private static string ValidateRole(string role) => role switch
    {
        "game" or "chatRender" or "chatCapture" or "media" or "aux" or "Master" => role,
        _ => throw new ArgumentException("Unknown Sonar channel role.", nameof(role))
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _http.Dispose();
        _lifetime.Dispose();
        // Waiters may still release this managed semaphore; do not invalidate it during command cancellation.
    }
}
