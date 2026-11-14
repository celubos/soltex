using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Soltex.Audio.Host;

/// <summary>Owns one unelevated helper. Connecting never starts streams; failures never replay mutations.</summary>
public sealed class AudioHostClient : IAsyncDisposable
{
    private readonly string profileDirectory, hostExecutablePath, pipeName, secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? child;
    private NamedPipeClientStream? pipe;
    private bool disposed;

    public AudioHostClient(string profileDirectory, string hostExecutablePath)
    {
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        this.hostExecutablePath = Path.GetFullPath(hostExecutablePath);
        using var identity = WindowsIdentity.GetCurrent();
        string key = (identity.User?.Value ?? throw new IOException("Current Windows user identity unavailable.")) + "|" + this.profileDirectory.ToUpperInvariant();
        pipeName = "Soltex.Audio.Host." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }
    public Task<HostSnapshot> EnsureConnectedAsync(CancellationToken cancellationToken = default) => RequestAsync("read", null, cancellationToken);
    public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => RequestAsync("read", null, cancellationToken);
    public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) => RequestAsync("start", null, cancellationToken);
    public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => RequestAsync("stop", null, cancellationToken);
    public Task<HostSnapshot> ApplyProfileAsync(AudioHostProfile profile, CancellationToken cancellationToken = default) =>
        RequestAsync("apply", HostProfileStore.Validate(profile), cancellationToken);

    public Task<HostSnapshot> StartStreamMixAsync(CancellationToken cancellationToken = default) => RequestAsync("stream-start", null, cancellationToken);
    public Task<HostSnapshot> StartMicrophoneTestAsync(string endpointId, CancellationToken cancellationToken = default) => RequestAsync("mic-test", null, cancellationToken, endpointId);
    public Task<HostSnapshot> StopMicrophoneTestAsync(CancellationToken cancellationToken = default) => RequestAsync("mic-test-stop", null, cancellationToken);

    private async Task<HostSnapshot> RequestAsync(string operation, AudioHostProfile? profile, CancellationToken cancellationToken, string? captureEndpointId = null)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(12));
            await ConnectAsync(deadline.Token).ConfigureAwait(false);
            return (await SendAsync(operation, profile, deadline.Token, captureEndpointId).ConfigureAwait(false)).Snapshot ?? throw new IOException("Host response contains no state.");
        }
        // A missing engine stays a FileNotFoundException so callers can tell a broken install from a lost connection.
        catch (Exception error) when (error is (IOException and not FileNotFoundException) or OperationCanceledException)
        {
            pipe?.Dispose(); pipe = null;
            throw new IOException("Lost the connection to the audio engine. Check the current state before you try the change again.", error);
        }
        finally { gate.Release(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        if (pipe?.IsConnected == true) return;
        if (child is null || child.HasExited)
        {
            child?.Dispose(); child = null;
            if (!File.Exists(hostExecutablePath)) throw new FileNotFoundException("The audio engine is missing from this installation. Reinstall Soltex to restore it.", hostExecutablePath);
            Directory.CreateDirectory(profileDirectory);
            using Process parent = Process.GetCurrentProcess();
            // The session secret travels over the child's private stdin pipe, never the command line,
            // because command lines are readable by every process of the same user.
            ProcessStartInfo start = new(hostExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(hostExecutablePath)!
            };
            foreach (string value in new[] { "--pipe", pipeName, "--token", AudioHostServer.TokenFromStandardInput, "--profile", Path.Combine(profileDirectory, "independent-audio-v1.json"),
                         "--owner-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--owner-start", parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(value);
            child = Process.Start(start) ?? throw new IOException("Audio host process did not start.");
            try
            {
                await child.StandardInput.WriteLineAsync(secret.AsMemory(), token).ConfigureAwait(false);
                await child.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
            finally { child.StandardInput.Close(); }
        }
        pipe?.Dispose();
        pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(token).ConfigureAwait(false);
    }

    private async Task<HostReply> SendAsync(string operation, AudioHostProfile? profile, CancellationToken token, string? captureEndpointId = null)
    {
        HostRequest request = new(1, Guid.NewGuid(), secret, operation, profile) { CaptureEndpointId = captureEndpointId };
        await HostProtocol.WriteAsync(pipe!, request, token).ConfigureAwait(false);
        HostReply response = await HostProtocol.ReadAsync<HostReply>(pipe!, token).ConfigureAwait(false);
        if (response.Version != 1 || response.Id != request.Id) throw new IOException("Audio host response identity mismatch.");
        if (response.Error is not null) throw new InvalidOperationException(response.Error);
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed && child is null) return;
            disposed = true;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Exception? gracefulFailure = null;
            bool forced = false;
            try
            {
                if (child is { HasExited: false })
                {
                    await ConnectAsync(deadline.Token).ConfigureAwait(false);
                    await SendAsync("shutdown", null, deadline.Token).ConfigureAwait(false);
                    await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
            { gracefulFailure = error; }
            finally { pipe?.Dispose(); pipe = null; }
            if (child is { HasExited: false })
            {
                // This retained Process handle belongs only to the helper started above, never a name/PID scan.
                // Releasing it while the parent lives would orphan an uncontrollable audio graph.
                child.Kill(entireProcessTree: false);
                forced = true;
                using var forcedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await child.WaitForExitAsync(forcedDeadline.Token).ConfigureAwait(false);
                gracefulFailure ??= new IOException("Audio host did not exit after shutdown.");
            }
            if (child is { HasExited: true } && child.ExitCode != 0)
                gracefulFailure ??= new IOException("Audio host exited with code " + child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
            child?.Dispose(); child = null;
            if (gracefulFailure is not null)
                throw new IOException(forced ? "The owned audio helper required a forced stop after graceful shutdown failed. No other process was terminated." :
                    "The owned audio helper exited, but did not confirm a clean shutdown.", gracefulFailure);
        }
        finally { gate.Release(); }
        GC.SuppressFinalize(this);
    }
}
