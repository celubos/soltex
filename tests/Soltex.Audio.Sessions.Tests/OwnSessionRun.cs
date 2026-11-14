using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Sessions.Tests;

internal static class OwnSessionRun
{
    private const double Frequency = 1619, Amplitude = 0.002;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || args[1] != "--endpoint" || args[3] != "--receipt")
            throw new ArgumentException("--live-own-session --endpoint <physical endpoint ID> --receipt <new JSON file on a local drive>");
        string endpointId = args[2], receiptPath = LocalOutputFile.Require(args[4], nameof(args));
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        if (!string.Equals(Path.GetFileName(executable), "Soltex.Audio.Sessions.Tests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the unique Soltex.Audio.Sessions.Tests.exe apphost directly; dotnet.exe is not an owned application identity.");
        using Process ownProcess = Process.GetCurrentProcess();
        DateTimeOffset processStarted = ownProcess.StartTime.ToUniversalTime();
        Receipt receipt = new()
        {
            CreatedAtUtc = DateTimeOffset.UtcNow, EndpointId = endpointId, ProcessId = Environment.ProcessId,
            ProcessStartTimeUtc = processStarted, ExecutablePath = executable,
            ExecutableSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable))),
            FrequencyHz = Frequency, SourceAmplitude = Amplitude
        };
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
        using (FileStream reserve = new(receiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        void Save()
        {
            string temporary = receiptPath + ".checkpoint-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, JsonOptions));
            File.Move(temporary, receiptPath, overwrite: true);
        }
        Save();

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        await using AudioSessionService service = new();
        await using AudioDeviceService devices = new();
        MMDeviceEnumerator? enumerator = null;
        AudioTone? keepAlive = null, tone = null;
        RecorderClient? probe = null;
        AudioSessionInfo? before = null;
        try
        {
            receipt.BeforeDevices = await devices.GetSnapshotAsync();
            enumerator = new();
            AudioSessionSnapshot initial = await service.GetSnapshotAsync(cancellation.Token);
            AudioSessionEndpoint endpoint = initial.Endpoints.Single(endpoint => endpoint.Id == endpointId);
            Require(!endpoint.IsVirtual && !endpoint.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase), "A physical output must be explicitly selected.");
            Require(endpoint.Muted == false && endpoint.Volume >= .05, "The physical endpoint must already be unmuted and above 5%; this harness will not change it.");
            receipt.EndpointName = endpoint.Name;
            keepAlive = new(enumerator, endpointId, Frequency, 0);
            keepAlive.Start();
            probe = await RecorderClient.StartAsync(executable, endpointId, receipt.ExecutableSha256, cancellation.Token);
            receipt.Recorder = probe.Identity;
            before = await FindOwnSessionAsync(service, endpointId, processStarted, cancellation.Token);
            receipt.BeforeSession = before; Save();
            Require(before.CanControl && !before.IsSystemSounds && before.Key.ProcessId == Environment.ProcessId &&
                before.Key.ProcessStartTimeUtc == processStarted, "Only this exact executable process/session can be controlled.");

            double background = await MeasureAsync("background", probe, receipt, Save, cancellation.Token);
            Require(background < 0.000005, "The selected tone frequency already has interfering signal; no session controls changed.");
            await SetAsync("unmute own session", null, false);
            await SetAsync("own session volume .5", .5, null);
            tone = new(enumerator, endpointId, Frequency, Amplitude); tone.Start();
            double half = await MeasureAsync("volume .5", probe, receipt, Save, cancellation.Token);
            tone.Verify();
            Require(half > Math.Max(background * 10, .00002), "The owned tone must be positively detected on the physical loopback.");
            await SetAsync("own session volume .25", .25, null);
            double quarter = await MeasureAsync("volume .25", probe, receipt, Save, cancellation.Token);
            receipt.GainChangeDb = SignalMeasurement.Decibels(quarter / half);
            Require(Math.Abs(receipt.GainChangeDb.Value + 6.020599913) <= 1, "Physical tone gain must fall by 6.02 dB within 1 dB.");
            await SetAsync("mute own session", null, true);
            double muted = await MeasureAsync("muted", probe, receipt, Save, cancellation.Token);
            Require(muted <= Math.Max(background * 3, Math.Max(quarter * .01, .000001)), "Mute must suppress the measured own tone by at least 40 dB or to the measured background floor.");
            receipt.SignalChecksPassed = true;

            async Task SetAsync(string label, double? volume, bool? mute)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                // Receipt is saved before issuing the request, so crashes do not hide an attempted write.
                receipt.WriteAttempts++; receipt.LastAction = label; Save();
                AudioSessionControlResult result = volume is { } value
                    ? await service.SetSessionVolumeAsync(before.Key, value, cancellation.Token)
                    : await service.SetSessionMuteAsync(before.Key, mute!.Value, cancellation.Token);
                receipt.Controls.Add(new(label, result)); Save();
                Require(result.Applied && result.WriteIssued, $"Own-session control failed: {result.Error}");
            }
        }
        catch (Exception exception)
        {
            receipt.Error = exception.ToString(); Console.Error.WriteLine(exception.Message);
        }
        finally
        {
            // The tone/session remains alive through restoration. Cancellation cannot skip recovery.
            try
            {
                if (receipt.WriteAttempts > 0 && before is not null)
                {
                    AudioSessionControlResult? volume = null, mute = null;
                    try
                    {
                        volume = await service.SetSessionVolumeAsync(before.Key, before.Volume!.Value);
                        receipt.Controls.Add(new("restore original volume", volume));
                    }
                    catch (Exception exception) { receipt.RestoreError += "\nSession volume: " + exception.Message; }
                    try
                    {
                        mute = await service.SetSessionMuteAsync(before.Key, before.Muted!.Value);
                        receipt.Controls.Add(new("restore original mute", mute));
                    }
                    catch (Exception exception) { receipt.RestoreError += "\nSession mute: " + exception.Message; }
                    AudioSessionSnapshot after = await service.GetSnapshotAsync();
                    receipt.AfterSession = after.Endpoints.SelectMany(endpoint => endpoint.Sessions).SingleOrDefault(session => session.Key == before.Key);
                    receipt.SessionRestored = volume?.Applied == true && mute?.Applied == true && receipt.AfterSession is { } restored &&
                        Math.Abs(restored.Volume!.Value - before.Volume!.Value) <= .0001 && restored.Muted == before.Muted;
                }
                else receipt.SessionRestored = true;
            }
            catch (Exception exception) { receipt.RestoreError = exception.ToString(); }
            try { tone?.Dispose(); } catch (Exception exception) { receipt.RestoreError += "\nTone stop: " + exception.Message; }
            try { if (probe is not null) await probe.DisposeAsync(); } catch (Exception exception) { receipt.RestoreError += "\nCapture stop: " + exception.Message; }
            try { keepAlive?.Dispose(); } catch (Exception exception) { receipt.RestoreError += "\nKeepalive stop: " + exception.Message; }
            try { enumerator?.Dispose(); } catch (Exception exception) { receipt.RestoreError += "\nEnumerator release: " + exception.Message; }
            try
            {
                receipt.AfterDevices = await devices.GetSnapshotAsync();
                receipt.DefaultsUnchanged = receipt.BeforeDevices is { } original &&
                    original.Defaults.SequenceEqual(receipt.AfterDevices.Defaults);
                var originalEndpoint = receipt.BeforeDevices?.Endpoints.SingleOrDefault(endpoint => endpoint.EndpointId == endpointId);
                var currentEndpoint = receipt.AfterDevices.Endpoints.SingleOrDefault(endpoint => endpoint.EndpointId == endpointId);
                receipt.EndpointSettingsUnchanged = originalEndpoint is not null && currentEndpoint is not null &&
                    originalEndpoint.VolumeScalar == currentEndpoint.VolumeScalar && originalEndpoint.IsMuted == currentEndpoint.IsMuted;
            }
            catch (Exception exception) { receipt.RestoreError += "\nDevice verification: " + exception.Message; }
            receipt.RestorationComplete = receipt.SessionRestored && receipt.DefaultsUnchanged &&
                receipt.EndpointSettingsUnchanged && string.IsNullOrEmpty(receipt.RestoreError);
            receipt.Passed = receipt.SignalChecksPassed && receipt.RestorationComplete && receipt.Error is null;
            receipt.FinishedAtUtc = DateTimeOffset.UtcNow; Save();
            Console.CancelKeyPress -= handler;
        }
        Console.WriteLine($"Own session proof: passed={receipt.Passed}, restoration={receipt.RestorationComplete}, gainDb={receipt.GainChangeDb:F3}; receipt={receiptPath}");
        return receipt.Passed ? 0 : 1;
    }

    private static async Task<AudioSessionInfo> FindOwnSessionAsync(AudioSessionService service, string endpointId,
        DateTimeOffset processStarted, CancellationToken token)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            AudioSessionSnapshot snapshot = await service.GetSnapshotAsync(token);
            AudioSessionInfo[] matches = snapshot.Endpoints.Where(endpoint => endpoint.Id == endpointId)
                .SelectMany(endpoint => endpoint.Sessions).Where(session => session.Key.ProcessId == Environment.ProcessId &&
                    session.Key.ProcessStartTimeUtc == processStarted && session.State != AudioSessionState.Expired).ToArray();
            if (matches.Length == 1) return matches[0];
            Require(matches.Length <= 1, "Multiple owned sessions exist; refusing ambiguous session selection.");
            await Task.Delay(200, token);
        }
        throw new InvalidOperationException("The exact owned session was not discovered within four seconds.");
    }

    private static async Task<double> MeasureAsync(string label, RecorderClient probe, Receipt receipt,
        Action save, CancellationToken token)
    {
        RecorderReply measurement = await probe.MeasureAsync(token);
        receipt.Measurements.Add(new(label, measurement.Amplitude!.Value, measurement.Volume, measurement.Muted)); save();
        return measurement.Amplitude.Value;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Measurement(string Stage, double Amplitude, double? RecorderVolume, bool? RecorderMuted);
    private sealed record Control(string Stage, AudioSessionControlResult Result);
    private sealed class Receipt
    {
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public string EndpointId { get; init; } = "";
        public string? EndpointName { get; set; }
        public int ProcessId { get; init; }
        public DateTimeOffset ProcessStartTimeUtc { get; init; }
        public string ExecutablePath { get; init; } = "";
        public string ExecutableSha256 { get; init; } = "";
        public double FrequencyHz { get; init; }
        public double SourceAmplitude { get; init; }
        public AudioDeviceSnapshot? BeforeDevices { get; set; }
        public AudioDeviceSnapshot? AfterDevices { get; set; }
        public AudioSessionInfo? BeforeSession { get; set; }
        public AudioSessionInfo? AfterSession { get; set; }
        public RecorderReply? Recorder { get; set; }
        public List<Measurement> Measurements { get; } = [];
        public List<Control> Controls { get; } = [];
        public string? LastAction { get; set; }
        public int WriteAttempts { get; set; }
        public bool NoOwnedWrites => WriteAttempts == 0;
        public double? GainChangeDb { get; set; }
        public bool SignalChecksPassed { get; set; }
        public bool SessionRestored { get; set; }
        public bool DefaultsUnchanged { get; set; }
        public bool EndpointSettingsUnchanged { get; set; }
        public bool RestorationComplete { get; set; }
        public bool Passed { get; set; }
        public string? Error { get; set; }
        public string? RestoreError { get; set; }
    }
}
