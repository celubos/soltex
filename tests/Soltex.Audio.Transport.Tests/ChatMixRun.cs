using System.Diagnostics;
using NAudio.CoreAudioApi;
using Soltex.Audio.Sonar;

namespace Soltex.Audio.Transport.Tests;

internal static class ChatMixRun
{
    private static readonly string[] Buses = ["game", "chat", "media", "aux"];
    private static readonly double[] Frequencies = [997, 1451, 1999, 2557];
    private static readonly double[] Positions = [0.5, 1, 0, -0.5, -1, 0];

    internal static async Task RunCommandAsync(string[] args)
    {
        if (!args.Contains("--allow-channel-state-changes", StringComparer.Ordinal)) throw new ArgumentException("ChatMix live mode requires --allow-channel-state-changes.", nameof(args));
        string Required(string key)
        {
            var index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {key}.", nameof(args));
        }
        var outputId = Required("--output-id");
        var journalPath = LocalOutputFile.Require(Required("--journal"), nameof(args));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
        await ExecuteAsync(outputId, journalPath, timeout.Token);
    }

    private static async Task ExecuteAsync(string outputId, string journalPath, CancellationToken token)
    {
        using var journal = new Journal(journalPath);
        using var client = await SonarClient.DiscoverAsync(token);
        using var enumerator = new MMDeviceEnumerator();
        var before = await client.GetSnapshotAsync(token);
        var chatMixBefore = await client.GetChatMixAsync(token);
        journal.Write("before", new { Snapshot = before, ChatMix = chatMixBefore, ProcessId = Environment.ProcessId, Amplitude = 0.003, Frequencies });
        SignalMeasurement.Check(chatMixBefore.CanAdjust, "Software ChatMix must already be enabled.");
        SignalMeasurement.Check(before.Devices.Any(d => d.Id == outputId && d.State == "active" && !d.IsVad && d.DataFlow == "render"), "Choose an active physical output.");
        SignalMeasurement.Check(before.Channels.Single(c => c.Id == "master") is { Muted: false, Volume: > 0.1 }, "Master must already be unmuted and above0.1.");
        foreach (var bus in Buses)
        {
            var channel = before.Channels.Single(c => c.Id == bus);
            SignalMeasurement.Check(channel.DeviceId == outputId && channel.IsRunning && !channel.Muted && channel.Volume > 0.1 && channel.VirtualDeviceId is not null,
                "All four playback buses must already target this output, be unmuted and have positive volume. ChatMix mode never changes their settings.");
        }
        var clock = Stopwatch.StartNew();
        using var probe = new AudioProbe(enumerator, outputId, Frequencies, clock);
        var tones = new List<AudioTone>();
        double? lastIntended = null;
        var restorationComplete = true;
        var passed = false;
        async Task SetAsync(double balance, CancellationToken writeToken)
        {
            lastIntended = balance;
            journal.Write("chatmix-intent", new { balance });
            journal.Write("chatmix-readback", await client.SetChatMixAsync(balance, writeToken));
        }
        async Task<double[]> MeasureAsync(string phase)
        {
            await Task.Delay(700, token);
            var start = clock.Elapsed.TotalSeconds;
            await Task.Delay(1000, token);
            foreach (var tone in tones) tone.Verify();
            var measured = probe.MedianSince(start);
            journal.Write("measurement", new { phase, Amplitudes = measured, Windows = probe.Since(start) });
            return measured;
        }
        try
        {
            var silence = new AudioTone(enumerator, outputId, 997, 0);
            tones.Add(silence);
            silence.Start();
            probe.Start();
            var noise = await MeasureAsync("tone-off");
            await SetAsync(0, token);
            for (var i = 0; i < Buses.Length; i++)
            {
                var tone = new AudioTone(enumerator, before.Channels.Single(c => c.Id == Buses[i]).VirtualDeviceId!, Frequencies[i], 0.003);
                tones.Add(tone);
                tone.Start();
            }
            var center = await MeasureAsync("center-reference");
            for (var i = 0; i < Buses.Length; i++)
                SignalMeasurement.Check(center[i] > Math.Max(1e-6, noise[i] * 10), $"{Buses[i]} tone is not distinguishable by20dB from baseline.");
            foreach (var position in Positions)
            {
                await SetAsync(position, token);
                var measured = await MeasureAsync("balance-" + position.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var scaled = position == 0 ? 0 : Math.Sign(position) * (2 * position / (position + Math.Sign(position)));
                double[] expected = [Math.Clamp(1 - scaled, 0, 1), Math.Clamp(1 + scaled, 0, 1), 1, 1];
                var ratios = measured.Select((value, index) => value / center[index]).ToArray();
                journal.Write("chatmix-signal-result", new { position, ExpectedLinearRatios = expected, MeasuredRatios = ratios });
                for (var i = 0; i < Buses.Length; i++)
                {
                    if (expected[i] == 0) SignalMeasurement.Check(ratios[i] < 0.05, $"{Buses[i]} extreme ChatMix did not suppress tone by26dB.");
                    else SignalMeasurement.Check(Math.Abs(SignalMeasurement.Decibels(ratios[i] / expected[i])) < 2,
                        $"{Buses[i]} ChatMix amplitude differs by more than2dB from the inspected transfer function.");
                }
            }
            passed = true;
            journal.Write("chatmix-pass", new { Positions, Proof = "Physical loopback amplitude: center, ±0.5, ±1; Media/Aux isolation; center return." });
        }
        catch (Exception error) { journal.Write("failure", new { Type = error.GetType().FullName, error.Message }); throw; }
        finally
        {
            foreach (var tone in tones.AsEnumerable().Reverse())
            {
                try { tone.Dispose(); }
                catch (Exception error) { journal.Write("tone-stop-error", error.Message); restorationComplete = false; }
            }
            using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var current = await client.GetChatMixAsync(restore.Token);
                if (Math.Abs(current.Balance - chatMixBefore.Balance) > 0.002)
                {
                    if (lastIntended.HasValue && Math.Abs(current.Balance - lastIntended.Value) < 0.002)
                        await SetAsync(chatMixBefore.Balance, restore.Token);
                    else { restorationComplete = false; journal.Write("restore-conflict", "External ChatMix change preserved; use before record for manual recovery."); }
                }
                var after = await client.GetSnapshotAsync(restore.Token);
                var chatMixAfter = await client.GetChatMixAsync(restore.Token);
                var channelsMatch = before.Channels.All(original =>
                {
                    var observed = after.Channels.Single(c => c.Id == original.Id);
                    return original.DeviceId == observed.DeviceId && original.Muted == observed.Muted && Math.Abs(original.Volume - observed.Volume) < 0.001;
                });
                restorationComplete &= channelsMatch && chatMixAfter.State == chatMixBefore.State && Math.Abs(chatMixAfter.Balance - chatMixBefore.Balance) < 0.002;
                journal.Write("after", new { Snapshot = after, ChatMix = chatMixAfter, channelsMatch, RestorationComplete = restorationComplete, TransportPassed = passed });
            }
            catch (Exception error) { restorationComplete = false; journal.Write("restore-error", error.Message); }
            if (!restorationComplete) Console.Error.WriteLine("ChatMix restoration incomplete; inspect the journal.");
        }
        if (!restorationComplete) throw new IOException("ChatMix restoration was not confirmed.");
        Console.WriteLine($"PASS: measured ChatMix and restoration. Journal: {journalPath}");
    }
}
