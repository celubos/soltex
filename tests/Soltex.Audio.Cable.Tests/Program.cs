using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Soltex.Audio.Engine;
using Soltex.Audio.Sessions;
using Soltex.Audio.Sessions.Tests;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Cable.Tests;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--record-child") return RecorderClient.RunChildAsync(args).GetAwaiter().GetResult();
        if (args.Length == 0 || args is ["--self-test"]) { PureChecks.Run(); return 0; }
        if (args is ["--inspect", var receipt]) return Inspect(receipt);
        if (args.Length > 0 && args[0] == "--allow-microphone-return") return MicrophoneReturn.Run(args);
        Options options = Options.Parse(args);
        return Run(options);
    }

    private static int Inspect(string receipt)
    {
        using Journal journal = new(receipt);
        AudioDeviceService devices = new();
        AudioSessionService sessions = new();
        try
        {
            journal.Set("devices", devices.GetSnapshotAsync().GetAwaiter().GetResult());
            journal.Set("sessions", sessions.GetSnapshotAsync().GetAwaiter().GetResult());
            using MMDeviceEnumerator enumerator = new();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                using (device)
                using (AudioClient client = device.AudioClient)
                    journal.Append("formats", new { device.ID, device.FriendlyName, Format = client.MixFormat.ToString() });
            }
            journal.Set("readOnly", true);
            return 0;
        }
        finally
        {
            sessions.DisposeAsync().AsTask().GetAwaiter().GetResult();
            devices.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int Run(Options options)
    {
        using Journal journal = new(options.Receipt);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        AudioDeviceService? devices = null;
        AudioSessionService? sessions = null;
        MMDeviceEnumerator? enumerator = null;
        WasapiBridge? bridge = null;
        AudioTone? cableClock = null, outputClock = null, source = null;
        CaptureProbe? cableProbe = null;
        RecorderClient? recorder = null;
        AudioDeviceSnapshot? before = null;
        List<string> cleanupErrors = [];
        bool passed = false, inconclusive = false;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException("Executable identity unavailable.");
            if (Path.GetFileName(executable) != "Soltex.Audio.Cable.Tests.exe")
                throw new InvalidOperationException("Run the unique built Soltex.Audio.Cable.Tests.exe apphost directly, not dotnet.exe.");
            string digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable)));
            journal.Set("identity", new { Executable = executable, SHA256 = digest, ProcessId = Environment.ProcessId,
                EngineSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WasapiBridge).Assembly.Location))),
                TestAssemblySHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Program).Assembly.Location))) });
            Process[] sonar = Process.GetProcessesByName("SteelSeriesSonar");
            try { if (sonar.Length != 0) throw new InconclusiveException("Sonar is running. Exit SteelSeries Sonar before this run."); }
            finally { foreach (Process process in sonar) process.Dispose(); }
            devices = new(); sessions = new(); enumerator = new();
            before = devices.GetSnapshotAsync().GetAwaiter().GetResult(); journal.Set("before", before);
            Preflight(before, options);
            AudioSessionSnapshot sessionSnapshot = sessions.GetSnapshotAsync().GetAwaiter().GetResult();
            journal.Set("sessionsBefore", sessionSnapshot);
            if (sessionSnapshot.Errors.Count != 0) throw new InconclusiveException("Cannot safely inspect current cable sessions.");
            // Both base-cable render pins feed the same transport; an unused selected pin
            // does not isolate the capture stream if another application owns the other pin.
            if (sessionSnapshot.Endpoints.Where(endpoint => endpoint.Id == options.CableRender ||
                    endpoint.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)).SelectMany(endpoint => endpoint.Sessions)
                .Any(session => session.State == AudioSessionState.Active && !session.IsSystemSounds && session.Key.ProcessId != Environment.ProcessId))
                throw new InconclusiveException("Another application is already rendering to the cable; its stream will not be bridged by this test.");
            int captureRate;
            using (MMDevice input = enumerator.GetDevice(options.CableCapture))
            using (MMDevice output = enumerator.GetDevice(options.PhysicalOutput))
            using (AudioClient inputClient = input.AudioClient)
            using (AudioClient outputClient = output.AudioClient)
            {
                var inputFormat = inputClient.MixFormat;
                var outputFormat = outputClient.MixFormat;
                if (inputFormat.Channels is < 1 or > 2 || outputFormat.Channels is < 1 or > 2)
                    throw new InconclusiveException("The existing bridge supports mono/stereo. Device formats will not be changed.");
                captureRate = inputFormat.SampleRate;
                journal.Set("formats", new { Capture = inputFormat.ToString(), Output = outputFormat.ToString() });
            }

            cableClock = new(enumerator, options.CableRender, 1619, 0); cableClock.Start();
            outputClock = new(enumerator, options.PhysicalOutput, 1619, 0); outputClock.Start();
            cableProbe = new(enumerator, options.CableCapture, clock); cableProbe.Start();
            recorder = RecorderClient.StartAsync(executable, options.PhysicalOutput, digest, cancellation.Token).GetAwaiter().GetResult();
            journal.Set("recorder", recorder.Identity);
            DspSettings settings = new() { GainDb = 0, HeadroomDb = 0, EqBypass = true,
                Bands = DspSettings.FlatBands().Select(band => band with { FrequencyHz = Math.Min(band.FrequencyHz, captureRate * .45) }).ToArray() };
            bridge = new(new(options.CableCapture, options.PhysicalOutput) { Dsp = settings });
            bridge.Start();
            var background = Measure("background");
            if (background.Physical > .000005 || background.Cable > .000005)
                throw new InconclusiveException("Background signal at the test frequency prevents an isolated source proof.");
            source = new(enumerator, options.CableRender, 1619, .002); source.Start();
            var unity = Measure("DSP unity");
            Require(unity.Cable > Math.Max(.00001, background.Cable * 10), "Known tone did not reach CABLE capture.");
            Require(unity.Physical > Math.Max(.00001, background.Physical * 10), "Known tone did not reach physical loopback through Soltex.");
            bridge.UpdateSettings(settings with { GainDb = -6.020599913279624 });
            var half = Measure("DSP half gain");
            double db = SignalMeasurement.Decibels(half.Physical / unity.Physical);
            journal.Set("measuredGainDb", db);
            Require(Math.Abs(db + 6.020599913279624) <= 1, "Physical output must change by -6.02dB ±1dB for the owned DSP gain change.");
            StableCable(unity.Cable, half.Cable);
            bridge.UpdateSettings(settings with { Mute = true });
            var muted = Measure("DSP mute");
            Require(muted.Physical <= Math.Max(.000001, Math.Max(background.Physical * 3, unity.Physical * .01)), "DSP mute did not suppress the tone by40dB/background floor.");
            StableCable(unity.Cable, muted.Cable);
            bridge.UpdateSettings(settings);
            var recovered = Measure("DSP recovered");
            Require(Math.Abs(SignalMeasurement.Decibels(recovered.Physical / unity.Physical)) <= 1, "Owned DSP did not recover its initial signal level.");
            StableCable(unity.Cable, recovered.Cable);
            passed = true;

            (double Physical, double Cable) Measure(string stage)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                RecorderReply result = recorder.MeasureAsync(cancellation.Token).GetAwaiter().GetResult();
                double end = clock.Elapsed.TotalSeconds - .2;
                double raw = cableProbe.ToneAmplitude(end - .5, end, 1619);
                BridgeStatus status = bridge.Status;
                journal.Append("measurements", new { Stage = stage, PhysicalAmplitude = result.Amplitude, CableAmplitude = raw,
                    result.Volume, result.Muted, Bridge = status });
                source?.Verify(); cableClock.Verify(); outputClock.Verify();
                Require(status.Running && status.Failure is null && status.DroppedFrames == 0, "The bridge failed or dropped capture frames.");
                return (result.Amplitude!.Value, raw);
            }
        }
        catch (Exception error)
        {
            inconclusive = error is InconclusiveException;
            journal.Set("error", new { Kind = error.GetType().Name, error.Message });
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            void Close(string name, Action action) { try { action(); } catch (Exception error) { cleanupErrors.Add(name + ": " + error.Message); } }
            if (source is not null) Close("source", source.Dispose);
            if (bridge is not null) Close("bridge", bridge.Dispose);
            if (cableProbe is not null) Close("cable capture", cableProbe.Dispose);
            if (recorder is not null)
            {
                int childPid = recorder.Identity.ProcessId;
                Close("recorder", () => recorder.DisposeAsync().AsTask().GetAwaiter().GetResult());
                Close("recorder identity verification", () =>
                {
                    using Process? remaining = GetProcess(childPid);
                    bool same = remaining is not null && new DateTimeOffset(remaining.StartTime.ToUniversalTime()) == recorder.Identity.StartTimeUtc;
                    journal.Set("recorderClosed", !same);
                    if (same) cleanupErrors.Add("Owned recorder remains alive.");
                });
            }
            if (outputClock is not null) Close("physical clock", outputClock.Dispose);
            if (cableClock is not null) Close("cable clock", cableClock.Dispose);
            if (enumerator is not null) Close("enumerator", enumerator.Dispose);
            bool stateMatches = false;
            if (devices is not null)
            {
                Close("state comparison", () =>
                {
                    AudioDeviceSnapshot after = devices.GetSnapshotAsync().GetAwaiter().GetResult(); journal.Set("after", after);
                    stateMatches = before is not null && Unchanged(before, after);
                });
                Close("devices service", () => devices.DisposeAsync().AsTask().GetAwaiter().GetResult());
            }
            if (sessions is not null) Close("sessions service", () => sessions.DisposeAsync().AsTask().GetAwaiter().GetResult());
            inconclusive &= cleanupErrors.Count == 0 && (before is null || stateMatches);
            journal.Set("final", new { Status = passed && stateMatches && cleanupErrors.Count == 0 ? "Passed" : inconclusive ? "Inconclusive" : "Failed",
                SignalPassed = passed, WindowsSettingsWrites = 0, StateUnchanged = stateMatches, CleanupErrors = cleanupErrors,
                CleanupComplete = cleanupErrors.Count == 0, FinishedAtUtc = DateTimeOffset.UtcNow });
            passed &= stateMatches && cleanupErrors.Count == 0;
            Console.CancelKeyPress -= cancel;
        }
        Console.WriteLine($"Cable transport passed={passed}; receipt={options.Receipt}");
        return passed ? 0 : inconclusive ? 3 : 1;
    }

    private static void StableCable(double baseline, double current) => Require(Math.Abs(SignalMeasurement.Decibels(current / baseline)) <= 1,
        "Cable source changed during a downstream DSP check.");
    private static void Require(bool value, string message) { if (!value) throw new IOException(message); }
    private static Process? GetProcess(int id) { try { return Process.GetProcessById(id); } catch (ArgumentException) { return null; } }

    private static void Preflight(AudioDeviceSnapshot snapshot, Options options)
    {
        if (snapshot.State == AudioObservationState.Unavailable) throw new InconclusiveException("Core Audio state is unavailable.");
        AudioEndpoint Find(string id, AudioEndpointDirection direction) => snapshot.Endpoints.Single(endpoint =>
            endpoint.EndpointId == id && endpoint.Direction == direction && endpoint.State == AudioEndpointState.Active);
        AudioEndpoint render = Find(options.CableRender, AudioEndpointDirection.Render);
        AudioEndpoint capture = Find(options.CableCapture, AudioEndpointDirection.Capture);
        AudioEndpoint physical = Find(options.PhysicalOutput, AudioEndpointDirection.Render);
        if (!render.FullName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) || !capture.FullName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
            throw new InconclusiveException("Explicit cable endpoints must identify the separately installed VB-Audio transport.");
        if (physical.FullName.Contains("Sonar", StringComparison.OrdinalIgnoreCase) || physical.FullName.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
            physical.FullName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || physical.IsMuted != false || physical.VolumeScalar is not >= .05)
            throw new InconclusiveException("Select an already active, unmuted physical output above5%; the harness does not change endpoint settings.");
    }

    internal static bool Unchanged(AudioDeviceSnapshot before, AudioDeviceSnapshot after) =>
        before.Defaults.SequenceEqual(after.Defaults) && before.Endpoints.Count == after.Endpoints.Count &&
        before.Endpoints.All(a => after.Endpoints.Any(b => a.EndpointId == b.EndpointId && a.State == b.State && a.VolumeScalar == b.VolumeScalar && a.IsMuted == b.IsMuted));
}

internal sealed record Options(string CableRender, string CableCapture, string PhysicalOutput, string Receipt)
{
    internal static Options Parse(string[] args)
    {
        if (args.Length != 9 || args[0] != "--allow-cable-transport" || args[1] != "--cable-render" ||
            args[3] != "--cable-capture" || args[5] != "--physical-output" || args[7] != "--receipt")
            throw new ArgumentException("Use --allow-cable-transport --cable-render <ID> --cable-capture <ID> --physical-output <ID> --receipt <new JSON file on a local drive>.");
        if (new[] { args[2], args[4], args[6] }.Any(string.IsNullOrWhiteSpace) || new[] { args[2], args[4], args[6] }.Distinct().Count() != 3)
            throw new ArgumentException("Three distinct explicit endpoint IDs are required.");
        return new(args[2], args[4], args[6], args[8]);
    }
}

internal sealed class Journal : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Dictionary<string, object?> _data = new() { ["createdAtUtc"] = DateTimeOffset.UtcNow };
    internal Journal(string path)
    {
        _path = LocalOutputFile.Require(path, nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using FileStream reservation = new(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
    internal void Set(string key, object? value) { _data[key] = value; Save(); }
    internal void Append(string key, object value)
    {
        if (!_data.TryGetValue(key, out object? current)) _data[key] = current = new List<object>();
        ((List<object>)current!).Add(value); Save();
    }
    private void Save()
    {
        string temp = _path + ".checkpoint-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions)); File.Move(temp, _path, true);
    }
    public void Dispose() => Save();
}
