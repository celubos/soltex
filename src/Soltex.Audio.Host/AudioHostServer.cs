using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soltex.Audio.Host;

public static class AudioHostServer
{
    /// <summary>Reads exactly one bounded secret line; a missing, slow or oversized secret fails closed.</summary>
    public static async Task<string> ReadTokenAsync(TextReader input, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        char[] buffer = new char[66];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(length, 1), deadline.Token).ConfigureAwait(false);
            if (read == 0 || buffer[length] == '\n') break;
            if (buffer[length] != '\r') length++;
        }
        string token = new(buffer, 0, length);
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("The audio helper did not receive a valid session secret from its owner.");
        return token;
    }

    /// <summary>Value of <c>--token</c> meaning "read the 64-character secret from standard input".</summary>
    public const string TokenFromStandardInput = "-";

    public static async Task<int> RunAsync(string[] args, Func<IHostAudioBackend>? backendFactory = null, CancellationToken cancellationToken = default)
    {
        if (args.Length != 10 || args[0] != "--pipe" || args[2] != "--token" || args[4] != "--profile" || args[6] != "--owner-pid" || args[8] != "--owner-start" ||
            !args[1].StartsWith("Soltex.Audio.Host.", StringComparison.Ordinal) || args[1].Length > 80 ||
            !int.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out int ownerPid) ||
            !long.TryParse(args[9], NumberStyles.None, CultureInfo.InvariantCulture, out long ownerStart))
            throw new ArgumentException("The audio helper must be launched by its Soltex client with a valid owner and protocol identity.");
        string token = args[3] == TokenFromStandardInput ? await ReadTokenAsync(Console.In, cancellationToken).ConfigureAwait(false) : args[3];
        if (token.Length != 64)
            throw new ArgumentException("The audio helper must be launched by its Soltex client with a valid owner and protocol identity.");
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A process-lifetime named object closes the gap between successive pipe instances.
        // Unlike Mutex ownership it is not tied to the async continuation's managed thread.
        using Semaphore ownership = new(0, 1, "Local\\" + args[1] + ".Owner", out bool ownsProfile);
        if (!ownsProfile) throw new IOException("Another audio host already owns this user/profile.");
        byte[] expectedSecret = Encoding.UTF8.GetBytes(token);
        await using AudioHostController controller = new(Path.GetFullPath(args[5]), backendFactory);
        Task ownerWatch = WatchOwnerAsync(ownerPid, ownerStart, shutdown);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = new(args[1], PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 65536, 65536);
                try { await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false); }
                catch (IOException) when (!shutdown.IsCancellationRequested)
                {
                    // The client hung up before the accept completed; audio keeps running and the next client is served.
                    continue;
                }
                while (pipe.IsConnected && !shutdown.IsCancellationRequested)
                {
                    HostRequest request;
                    try
                    {
                        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                        requestDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                        request = await HostProtocol.ReadAsync<HostRequest>(pipe, requestDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is IOException or JsonException or OperationCanceledException) { break; }
                    if (request.Version != 1 || request.Id == Guid.Empty || request.Token is null ||
                        !CryptographicOperations.FixedTimeEquals(expectedSecret, Encoding.UTF8.GetBytes(request.Token))) break;
                    HostReply response;
                    bool exit = false;
                    try
                    {
                        HostSnapshot state = request.Operation switch
                        {
                            "stream-start" => await controller.StartStreamMixAsync(shutdown.Token).ConfigureAwait(false),
                            "mic-test" when request.CaptureEndpointId is not null => await controller.StartMicrophoneTestAsync(request.CaptureEndpointId, shutdown.Token).ConfigureAwait(false),
                            "mic-test-stop" => await controller.StopMicrophoneTestAsync(shutdown.Token).ConfigureAwait(false),
                            "read" => await controller.ReadAsync(shutdown.Token).ConfigureAwait(false),
                            "start" => await controller.StartAsync(shutdown.Token).ConfigureAwait(false),
                            "stop" => await controller.StopAsync(shutdown.Token).ConfigureAwait(false),
                            "apply" when request.Profile is not null => await controller.ApplyProfileAsync(request.Profile, shutdown.Token).ConfigureAwait(false),
                            "shutdown" => await controller.StopAsync(shutdown.Token).ConfigureAwait(false),
                            _ => throw new ArgumentException("Unknown or incomplete audio host operation.")
                        };
                        exit = request.Operation == "shutdown"; response = new(1, request.Id, state, null);
                    }
                    catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or NotSupportedException or AggregateException or JsonException)
                    { response = new(1, request.Id, null, error.Message); }
                    try
                    {
                        using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                        responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                        await HostProtocol.WriteAsync(pipe, response, responseDeadline.Token).ConfigureAwait(false);
                    }
                    catch (IOException) { break; }
                    if (exit) { shutdown.Cancel(); break; }
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
        finally { shutdown.Cancel(); await ownerWatch.ConfigureAwait(false); CryptographicOperations.ZeroMemory(expectedSecret); }
    }

    private static async Task WatchOwnerAsync(int pid, long startedTicks, CancellationTokenSource shutdown)
    {
        try
        {
            using Process owner = Process.GetProcessById(pid);
            if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != startedTicks) { shutdown.Cancel(); return; }
            // Wait on the parent app's process handle instead of checking every second (that check alone cost 1.55 CPU-ms/s).
            // Holding the handle pins this exact process, so a reused process id cannot be mistaken for the parent app.
            await owner.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            shutdown.Cancel();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { shutdown.Cancel(); }
    }
}
