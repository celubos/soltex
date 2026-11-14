using System.Diagnostics;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using Soltex.Audio.Engine;
using Soltex.Audio.Sessions;

namespace Soltex.Audio.Cable.Tests;

internal static class MicrophoneReturn
{
    internal static int Run(string[] args)
    {
        if (args.Length != 9 || args[1] != "--microphone" || args[3] != "--cable-render" ||
            args[5] != "--cable-capture" || args[7] != "--receipt" ||
            new[] { args[2], args[4], args[6] }.Distinct().Count() != 3)
            throw new ArgumentException("Use --allow-microphone-return --microphone <ID> --cable-render <ID> --cable-capture <ID> --receipt <new JSON file on a local drive>.");
        using Journal journal = new(args[8]);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        AudioDeviceService devices = new();
        AudioSessionService sessions = new();
        MMDeviceEnumerator? enumerator = null;
        WasapiBridge? bridge = null;
        CaptureProbe? reference = null, returned = null;
        AudioDeviceSnapshot? before = null;
        List<string> cleanupErrors = [];
        bool passed = false, inconclusive = false, unchanged = false;
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            journal.Set("identity", new { ProcessId = Environment.ProcessId, Executable = Environment.ProcessPath,
                TestSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MicrophoneReturn).Assembly.Location))),
                EngineSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WasapiBridge).Assembly.Location))) });
            Process[] sonar = Process.GetProcessesByName("SteelSeriesSonar");
            try { if (sonar.Length != 0) throw new InconclusiveException("Sonar must be stopped for independent microphone-return acceptance."); }
            finally { foreach (Process process in sonar) process.Dispose(); }
            before = devices.GetSnapshotAsync().GetAwaiter().GetResult(); journal.Set("before", before);
            AudioEndpoint Endpoint(string id, AudioEndpointDirection direction) => before.Endpoints.Single(e =>
                e.EndpointId == id && e.Direction == direction && e.State == AudioEndpointState.Active);
            AudioEndpoint mic = Endpoint(args[2], AudioEndpointDirection.Capture);
            AudioEndpoint cableRender = Endpoint(args[4], AudioEndpointDirection.Render);
            AudioEndpoint cableCapture = Endpoint(args[6], AudioEndpointDirection.Capture);
            if (!cableRender.FullName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
                !cableCapture.FullName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
                mic.FullName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                mic.FullName.Contains("Cable", StringComparison.OrdinalIgnoreCase) ||
                mic.FullName.Contains("Sonar", StringComparison.OrdinalIgnoreCase) || mic.IsMuted != false)
                throw new InconclusiveException("Explicit active physical microphone and base-cable endpoints are required.");
            if (before.Defaults.Any(d => d.EndpointId == args[4] || d.EndpointId == args[6]))
                throw new InconclusiveException("Cable must not be a default device during microphone-return isolation.");
            var currentSessions = sessions.GetSnapshotAsync().GetAwaiter().GetResult(); journal.Set("sessionsBefore", currentSessions);
            if (currentSessions.Errors.Count != 0 || currentSessions.Endpoints
                .Where(e => e.Name.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => e.Sessions).Any(s => s.State == AudioSessionState.Active && !s.IsSystemSounds))
                throw new InconclusiveException("Another application owns the cable or its sessions could not be inspected.");
            enumerator = new();
            reference = new(enumerator, args[2], clock); returned = new(enumerator, args[6], clock);
            using (MMDevice renderDevice = enumerator.GetDevice(args[4]))
            using (AudioClient client = renderDevice.AudioClient)
            {
                if (reference.SampleRate != returned.SampleRate || client.MixFormat.SampleRate != reference.SampleRate ||
                    client.MixFormat.Channels is < 1 or > 2)
                    throw new InconclusiveException("This correlation test requires compatible equal-rate mono/stereo endpoints; no device format is changed.");
                journal.Set("formats", new { Microphone = reference.Format, Return = returned.Format, Render = client.MixFormat.ToString() });
            }
            DspSettings settings = new() { GainDb = -6.020599913279624, HeadroomDb = 0, EqBypass = true,
                Bands = DspSettings.FlatBands().Select(b => b with { FrequencyHz = Math.Min(b.FrequencyHz, reference.SampleRate * .45) }).ToArray() };
            reference.Start(); returned.Start();
            bridge = new(new(args[2], args[4]) { Dsp = settings }); bridge.Start();
            Console.WriteLine("Microphone return capture: approximately 9 seconds, RAM only; no physical monitoring.");
            CaptureGain("half gain", .5);
            bridge.UpdateSettings(settings with { GainDb = -12.041199826559248 });
            CaptureGain("quarter gain", .25);
            bridge.UpdateSettings(settings with { Mute = true });
            Delay(1500);
            double end = clock.Elapsed.TotalSeconds - .2;
            float[] direct = [], result = [];
            try
            {
                direct = reference.Window(end - .5, end); result = returned.Window(end - .5, end);
                double inputRms = SignalAlignment.Rms(direct), outputRms = SignalAlignment.Rms(result);
                journal.Set("mute", new { InputRms = inputRms, OutputRms = outputRms });
                if (inputRms < SignalAlignment.MinimumRms) throw new InconclusiveException("Microphone was silent during mute verification.");
                if (outputRms > Math.Max(.000001, inputRms * .005)) throw new IOException("Muted return did not suppress the reference by at least 40dB relative to half gain.");
            }
            finally { Array.Clear(direct); Array.Clear(result); }
            VerifyBridge(); passed = true;

            void CaptureGain(string phase, double gain)
            {
                Delay(3500);
                double end = clock.Elapsed.TotalSeconds - .2;
                float[] direct = [], result = [];
                try
                {
                    direct = reference.Window(end - 2.5, end); result = returned.Window(end - 2.5, end);
                    journal.Append("alignment", new { Phase = phase, ExpectedGain = gain,
                        Result = SignalAlignment.Analyze(direct, result, reference.SampleRate, gain), Bridge = bridge.Status });
                }
                finally { Array.Clear(direct); Array.Clear(result); }
                VerifyBridge();
            }
            void Delay(int milliseconds)
            {
                if (cancellation.Token.WaitHandle.WaitOne(milliseconds)) cancellation.Token.ThrowIfCancellationRequested();
            }
            void VerifyBridge()
            {
                var status = bridge.Status;
                journal.Set("bridge", status);
                if (!status.Running || status.Failure is not null || status.DroppedFrames != 0)
                    throw new IOException("Microphone bridge failed or dropped capture frames.");
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
            if (bridge is not null) Close("bridge", bridge.Dispose);
            if (reference is not null) Close("reference", reference.Dispose);
            if (returned is not null) Close("return", returned.Dispose);
            if (enumerator is not null) Close("enumerator", enumerator.Dispose);
            Close("state comparison", () => { var after = devices.GetSnapshotAsync().GetAwaiter().GetResult(); journal.Set("after", after);
                unchanged = before is not null && Program.Unchanged(before, after); });
            Close("sessions", () => sessions.DisposeAsync().AsTask().GetAwaiter().GetResult());
            Close("devices", () => devices.DisposeAsync().AsTask().GetAwaiter().GetResult());
            passed &= unchanged && cleanupErrors.Count == 0;
            inconclusive &= cleanupErrors.Count == 0 && (before is null || unchanged);
            journal.Set("final", new { Status = passed ? "Passed" : inconclusive ? "Inconclusive" : "Failed",
                StateUnchanged = unchanged, WindowsSettingsWrites = 0, AudioFilesWritten = 0,
                CleanupErrors = cleanupErrors, CleanupComplete = cleanupErrors.Count == 0, FinishedAtUtc = DateTimeOffset.UtcNow });
            Console.CancelKeyPress -= cancel;
        }
        return passed ? 0 : inconclusive ? 3 : 1;
    }
}
