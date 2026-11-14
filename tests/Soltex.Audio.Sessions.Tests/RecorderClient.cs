using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Sessions.Tests;

internal sealed record RecorderReply(string Kind, int ProcessId, DateTimeOffset StartTimeUtc,
    string ExecutableSha256, AudioSessionKey? SessionKey, double? Volume, bool? Muted,
    double? Amplitude, string? Error);

/// <summary>Measurement has a different process-local audio session from the controlled renderer.</summary>
internal sealed class RecorderClient : IAsyncDisposable
{
    private readonly Process _child;
    private readonly Task<string> _errors;
    internal RecorderReply Identity { get; private set; } = null!;

    private RecorderClient(Process child) { _child = child; _errors = child.StandardError.ReadToEndAsync(); }

    internal static async Task<RecorderClient> StartAsync(string executable, string endpointId, string expectedHash, CancellationToken token)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--record-child"); start.ArgumentList.Add("--endpoint"); start.ArgumentList.Add(endpointId);
        RecorderClient client = new(Process.Start(start) ?? throw new InvalidOperationException("Owned recorder failed to start."));
        try
        {
            RecorderReply ready = await client.ReadAsync(token);
            if (ready.Kind != "ready" || ready.ProcessId != client._child.Id ||
                ready.StartTimeUtc != new DateTimeOffset(client._child.StartTime.ToUniversalTime()) ||
                !string.Equals(ready.ExecutableSha256, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                ready.SessionKey?.EndpointId != endpointId || ready.SessionKey.ProcessId != client._child.Id ||
                ready.SessionKey.ProcessStartTimeUtc != ready.StartTimeUtc || ready.Volume != 1 || ready.Muted != false)
                throw new InvalidOperationException("Owned recorder identity or independent unity session was not verified.");
            client.Identity = ready;
            return client;
        }
        catch
        {
            try { await client.DisposeAsync(); } catch (IOException) { }
            throw;
        }
    }

    internal async Task<RecorderReply> MeasureAsync(CancellationToken token)
    {
        await _child.StandardInput.WriteLineAsync("measure".AsMemory(), token);
        await _child.StandardInput.FlushAsync(token);
        RecorderReply reply = await ReadAsync(token);
        if (!IsIndependentMeasurement(Identity, reply))
            throw new InvalidOperationException("Recorder session changed, was attenuated, or returned an invalid signal measurement.");
        return reply;
    }

    internal static bool IsIndependentMeasurement(RecorderReply identity, RecorderReply reply) =>
        reply.Kind == "measurement" && reply.ProcessId == identity.ProcessId && reply.StartTimeUtc == identity.StartTimeUtc &&
        reply.SessionKey == identity.SessionKey && reply.Volume == 1 && reply.Muted == false &&
        reply.Amplitude is { } value && double.IsFinite(value) && value >= 0;

    private async Task<RecorderReply> ReadAsync(CancellationToken token)
    {
        string line = await _child.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)
            ?? throw new IOException("Recorder exited before its reply: " + await _errors);
        RecorderReply reply = JsonSerializer.Deserialize<RecorderReply>(line) ?? throw new IOException("Recorder returned no result.");
        if (reply.Error is not null) throw new IOException(reply.Error);
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_child.HasExited)
            {
                try { await _child.StandardInput.WriteLineAsync("stop"); await _child.StandardInput.FlushAsync(); }
                catch (IOException) { }
                try { await _child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException)
                {
                    // This retained process handle belongs to the child we just spawned, never an arbitrary PID.
                    _child.Kill(entireProcessTree: false);
                    await _child.WaitForExitAsync();
                    throw new IOException("Owned recorder did not stop normally; its exact spawned process was terminated.");
                }
            }
            string error = await _errors;
            if (_child.ExitCode != 0) throw new IOException($"Recorder exit {_child.ExitCode}: {error}");
        }
        finally { _child.Dispose(); }
    }

    internal static async Task<int> RunChildAsync(string[] args)
    {
        if (args.Length != 3 || args[1] != "--endpoint") throw new ArgumentException("Recorder child requires one explicit endpoint.");
        using Process process = Process.GetCurrentProcess();
        DateTimeOffset start = process.StartTime.ToUniversalTime();
        string digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Environment.ProcessPath!)));
        AudioSessionInfo? session = null;
        void Reply(string kind, double? amplitude = null, string? error = null) => Console.WriteLine(JsonSerializer.Serialize(
            new RecorderReply(kind, Environment.ProcessId, start, digest, session?.Key, session?.Volume, session?.Muted, amplitude, error)));
        try
        {
            using MMDeviceEnumerator enumerator = new();
            Stopwatch clock = Stopwatch.StartNew();
            using AudioProbe probe = new(enumerator, args[2], [1619], clock);
            probe.Start();
            await using AudioSessionService service = new();
            async Task<AudioSessionInfo> ReadSessionAsync()
            {
                AudioSessionSnapshot snapshot = await service.GetSnapshotAsync();
                return snapshot.Endpoints.Where(endpoint => endpoint.Id == args[2]).SelectMany(endpoint => endpoint.Sessions)
                    .Single(item => item.Key.ProcessId == Environment.ProcessId && item.Key.ProcessStartTimeUtc == start && item.State != AudioSessionState.Expired);
            }
            session = await ReadSessionAsync();
            if (session.Volume != 1 || session.Muted != false) throw new IOException("New recorder session is not at unity and unmuted; no measurement is valid.");
            Reply("ready");
            while (await Console.In.ReadLineAsync() is { } command && command != "stop")
            {
                if (command != "measure") throw new IOException("Unknown recorder operation.");
                await Task.Delay(350);
                double from = clock.Elapsed.TotalSeconds;
                await Task.Delay(900);
                session = await ReadSessionAsync();
                Reply("measurement", probe.MedianSince(from)[0]);
            }
            return 0;
        }
        catch (Exception exception) { Reply("error", error: exception.ToString()); return 1; }
    }
}
