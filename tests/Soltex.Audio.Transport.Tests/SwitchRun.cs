using System.Diagnostics;
using NAudio.CoreAudioApi;
using Soltex.Audio.Sonar;

namespace Soltex.Audio.Transport.Tests;

internal static class SwitchRun
{
    private static readonly string[] Buses = ["game", "chat", "media", "aux"];
    private static readonly double[] Frequencies = [997, 1451, 1999, 2557];
    internal static async Task RunCommandAsync(string[] args)
    {
        if (!args.Contains("--allow-channel-state-changes", StringComparer.Ordinal)) throw new ArgumentException("Live switching requires --allow-channel-state-changes.", nameof(args));
        string Required(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {key}.", nameof(args));
        }
        var journalPath = LocalOutputFile.Require(Required("--journal"), nameof(args));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
        await ExecuteAsync(Required("--output-id"), Required("--alternate-id"), journalPath, timeout.Token);
    }

    private static async Task ExecuteAsync(string outputId, string alternateId, string journalPath, CancellationToken token)
    {
        using var journal = new Journal(journalPath);
        using var client = await SonarClient.DiscoverAsync(token);
        using var enumerator = new MMDeviceEnumerator();
        var before = await client.GetSnapshotAsync(token);
        var chatMix = await client.GetChatMixAsync(token);
        var defaults = GetDefaults(enumerator);
        journal.Write("before", new { Snapshot = before, ChatMix = chatMix, Defaults = defaults, ProcessId = Environment.ProcessId, Amplitude = 0.003, Frequencies, SwitchCount = 20 });
        SignalMeasurement.Check(outputId != alternateId, "Two distinct physical outputs required.");
        foreach (var id in new[] { outputId, alternateId })
            SignalMeasurement.Check(before.Devices.Any(d => d.Id == id && d.State == "active" && !d.IsVad && d.DataFlow == "render"), "Both outputs must be active physical render endpoints.");
        SignalMeasurement.Check(chatMix.CanAdjust && Math.Abs(chatMix.Balance) < 0.002, "ChatMix must already be centered to avoid implicit balance reset during route switching.");
        SignalMeasurement.Check(before.Channels.Single(c => c.Id == "master") is { Muted: false, Volume: > 0.1 }, "Master must already be unmuted and above0.1.");
        foreach (var bus in Buses)
            SignalMeasurement.Check(before.Channels.Single(c => c.Id == bus) is { IsRunning: true, Muted: false, Volume: > 0.1, VirtualDeviceId: not null } channel && channel.DeviceId == outputId,
                "All four buses must already be active, unmuted and on the first output.");
        var clock = Stopwatch.StartNew();
        using var probeA = new AudioProbe(enumerator, outputId, Frequencies, clock);
        using var probeB = new AudioProbe(enumerator, alternateId, Frequencies, clock);
        var tones = new List<AudioTone>();
        string? lastIntended = null;
        var restored = true;
        var passed = false;
        async Task<double[]> MeasureAsync(AudioProbe probe)
        {
            await Task.Delay(500, token);
            var start = clock.Elapsed.TotalSeconds;
            await Task.Delay(1000, token);
            foreach (var tone in tones) tone.Verify();
            return probe.MedianSince(start);
        }
        try
        {
            foreach (var id in new[] { outputId, alternateId })
            {
                var silence = new AudioTone(enumerator, id, 997, 0);
                tones.Add(silence); silence.Start();
            }
            probeA.Start(); probeB.Start();
            var noiseA = await MeasureAsync(probeA);
            var noiseB = probeB.MedianSince(clock.Elapsed.TotalSeconds - 1);
            for (var i = 0; i < Buses.Length; i++)
            {
                var tone = new AudioTone(enumerator, before.Channels.Single(c => c.Id == Buses[i]).VirtualDeviceId!, Frequencies[i], 0.003);
                tones.Add(tone); tone.Start();
            }
            var referenceA = await MeasureAsync(probeA);
            double[]? referenceB = null;
            for (var i = 0; i < Buses.Length; i++) SignalMeasurement.Check(referenceA[i] > Math.Max(1e-6, noiseA[i] * 10), "Initial tone cannot be distinguished from baseline by20dB.");
            for (var cycle = 0; cycle < 20; cycle++)
            {
                var toB = cycle % 2 == 0;
                lastIntended = toB ? alternateId : outputId;
                var destination = toB ? probeB : probeA;
                var source = toB ? probeA : probeB;
                var noise = toB ? noiseB : noiseA;
                var start = clock.Elapsed.TotalSeconds;
                journal.Write("switch-intent", new { Cycle = cycle + 1, Destination = lastIntended, start });
                var state = await client.SetOutputAsync("game", lastIntended, true, token);
                var readback = clock.Elapsed.TotalSeconds;
                var measured = await MeasureAsync(destination);
                var residual = source.MedianSince(clock.Elapsed.TotalSeconds - 1);
                if (toB && referenceB is null) referenceB = measured;
                var reference = toB ? referenceB! : referenceA;
                var oldReference = toB ? referenceA : referenceB!;
                var destinationTimeline = destination.Since(start - 0.3);
                var sourceTimeline = source.Since(start - 0.3);
                var firstDetectedMs = new double[Buses.Length];
                for (var i = 0; i < Buses.Length; i++)
                {
                    SignalMeasurement.Check(measured[i] > Math.Max(1e-6, noise[i] * 10), $"{Buses[i]} missing from output on switch{cycle + 1}.");
                    SignalMeasurement.Check(Math.Abs(SignalMeasurement.Decibels(measured[i] / reference[i])) < 2, $"{Buses[i]} steady amplitude drift exceeded2dB.");
                    SignalMeasurement.Check(residual[i] < oldReference[i] * 0.05, $"{Buses[i]} remained on previous output.");
                    var first = destinationTimeline.FirstOrDefault(w => w.EndSeconds >= start && w.Amplitudes[i] > reference[i] * 0.5)
                        ?? throw new IOException("New destination never acquired tone.");
                    firstDetectedMs[i] = (first.EndSeconds - start) * 1000;
                    SignalMeasurement.Check(firstDetectedMs[i] <= 1500, $"{Buses[i]} reacquisition exceeded1500ms.");
                }
                journal.Write("switch-measured", new { Cycle = cycle + 1, start, readback, Channels = state.Channels, Amplitudes = measured, Residual = residual,
                    FirstDetectedMs = firstDetectedMs, DestinationTimeline = destinationTimeline, SourceTimeline = sourceTimeline });
            }
            passed = true;
            journal.Write("sustained-pass", new { Switches = 20, DurationSeconds = clock.Elapsed.TotalSeconds, MeasurementResolutionMs = 100 });
        }
        catch (Exception error) { journal.Write("failure", new { Type = error.GetType().FullName, error.Message }); throw; }
        finally
        {
            foreach (var tone in tones.AsEnumerable().Reverse())
            {
                try { tone.Dispose(); }
                catch (Exception error) { restored = false; journal.Write("tone-stop-error", error.Message); }
            }
            using var restoreToken = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var bus in Buses)
            {
                try
                {
                    var current = (await client.GetSnapshotAsync(restoreToken.Token)).Channels.Single(c => c.Id == bus);
                    if (current.DeviceId == outputId) continue;
                    if (lastIntended is not null && current.DeviceId == lastIntended)
                    {
                        journal.Write("restore-intent", new { bus, outputId });
                        await client.SetOutputAsync(bus, outputId, false, restoreToken.Token);
                    }
                    else { restored = false; journal.Write("restore-conflict", new { bus, Message = "External output change preserved." }); }
                }
                catch (Exception error) { restored = false; journal.Write("restore-error", new { bus, error.Message }); }
            }
            try
            {
                var after = await client.GetSnapshotAsync(restoreToken.Token);
                var afterMix = await client.GetChatMixAsync(restoreToken.Token);
                var afterDefaults = GetDefaults(enumerator);
                var settingsMatch = before.Channels.All(original =>
                {
                    var current = after.Channels.Single(c => c.Id == original.Id);
                    return original.DeviceId == current.DeviceId && original.Muted == current.Muted && Math.Abs(original.Volume - current.Volume) < 0.001;
                });
                restored &= settingsMatch && defaults.SequenceEqual(afterDefaults) && Math.Abs(afterMix.Balance - chatMix.Balance) < 0.002;
                journal.Write("after", new { Snapshot = after, ChatMix = afterMix, Defaults = afterDefaults, settingsMatch, RestorationComplete = restored, TransportPassed = passed });
            }
            catch (Exception error) { restored = false; journal.Write("restore-verification-error", error.Message); }
        }
        if (!restored) throw new IOException("Sustained switching restoration could not be confirmed.");
        Console.WriteLine($"PASS:20 measured switches and restoration. Journal: {journalPath}");
    }

    private static string[] GetDefaults(MMDeviceEnumerator enumerator)
    {
        var result = new List<string>();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
            result.Add($"{flow}/{role}:{device.ID}");
        }
        return result.ToArray();
    }
}
