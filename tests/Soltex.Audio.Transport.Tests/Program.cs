using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.CoreAudioApi;
using Soltex.Audio.Sonar;
using Soltex.Audio.Transport.Tests;

if (args.Length == 0 || args[0] == "--self-test")
{
    SignalMeasurement.SelfTest();
    return;
}
if (args[0] == "--inspect")
{
    using var client = await SonarClient.DiscoverAsync();
    var state = await client.GetSnapshotAsync();
    Console.WriteLine(JsonSerializer.Serialize(new { state.IsReady, state.Channels, Devices = state.Devices.Where(d => !d.IsVad && d.DataFlow == "render") }));
    return;
}
if (args[0] == "--inspect-formats")
{
    using var enumerator = new MMDeviceEnumerator();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        using (device)
        {
            var format = device.AudioClient.MixFormat;
            using var data = new MemoryStream();
            using var writer = new BinaryWriter(data);
            format.Serialize(writer);
            Console.WriteLine(JsonSerializer.Serialize(new { device.ID, device.FriendlyName, Format = format.ToString(), format.SampleRate,
                format.Channels, format.BitsPerSample, Encoding = format.Encoding.ToString(), SerializedWaveFormat = Convert.ToHexString(data.ToArray()) }));
        }
    }
    return;
}
if (args[0] == "--run-chatmix")
{
    await ChatMixRun.RunCommandAsync(args);
    return;
}
if (args[0] == "--run-switches")
{
    await SwitchRun.RunCommandAsync(args);
    return;
}
if (args[0] != "--run-live" || !args.Contains("--allow-channel-state-changes", StringComparer.Ordinal))
    throw new ArgumentException("Use --self-test or --inspect. Live requires --run-live --allow-channel-state-changes --output-id ID --alternate-id ID --journal NEW_D_OR_E_PATH.");
string Required(string key)
{
    var index = Array.IndexOf(args, key);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {key}.");
}
var outputId = Required("--output-id");
var alternateId = Required("--alternate-id");
var journalPath = LocalOutputFile.Require(Required("--journal"), "--journal");
var amplitudeIndex = Array.IndexOf(args, "--amplitude");
var amplitude = amplitudeIndex < 0 ? 0.003 : double.Parse(args[amplitudeIndex + 1], CultureInfo.InvariantCulture);
if (!double.IsFinite(amplitude) || amplitude is < 0.0001 or > 0.01) throw new ArgumentException("Peak amplitude must be 0.0001..0.01 (default0.003).", nameof(args));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
await TransportRun.ExecuteAsync(outputId, alternateId, amplitude, journalPath, cancellation.Token);

internal sealed class Journal : IDisposable
{
    private readonly StreamWriter writer;
    internal Journal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough)) { AutoFlush = true };
    }
    internal void Write(string kind, object value) => writer.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.UtcNow, Kind = kind, Value = value }));
    public void Dispose() => writer.Dispose();
}

internal static class TransportRun
{
    private static readonly string[] Buses = ["game", "chat", "media", "aux"];
    private static readonly double[] Frequencies = [997, 1451, 1999, 2557];

    internal static async Task ExecuteAsync(string outputId, string alternateId, double amplitude, string journalPath, CancellationToken token)
    {
        using var journal = new Journal(journalPath);
        using var client = await SonarClient.DiscoverAsync(token);
        using var enumerator = new MMDeviceEnumerator();
        var before = await client.GetSnapshotAsync(token);
        var defaults = ReadDefaults(enumerator);
        var master = before.Channels.Single(c => c.Id == "master");
        journal.Write("before", new { ProcessId = Environment.ProcessId, Executable = Environment.ProcessPath, Snapshot = before, Defaults = defaults, Frequencies, Amplitude = amplitude });
        SignalMeasurement.Check(outputId != alternateId, "Select two different physical output endpoints.");
        foreach (var id in new[] { outputId, alternateId })
            SignalMeasurement.Check(before.Devices.Any(d => d.Id == id && !d.IsVad && d.DataFlow == "render" && d.State == "active"), "Both selected outputs must be active physical render endpoints.");
        SignalMeasurement.Check(!master.Muted && master.Volume >= 0.1, "Master must already be unmuted and at least0.1; harness never changes master.");
        foreach (var bus in Buses)
        {
            var channel = before.Channels.Single(c => c.Id == bus);
            SignalMeasurement.Check(channel.IsRunning && channel.DeviceId is not null && before.Devices.Any(d => d.Id == channel.VirtualDeviceId && d.State == "active"), $"{bus} requires an active virtual endpoint and existing running output route.");
        }
        var level = Math.Min(master.Volume, 0.5);
        var expected = new Dictionary<(string Bus, string Field), object>();
        var tones = new List<AudioTone>();
        var clock = Stopwatch.StartNew();
        using var probeA = new AudioProbe(enumerator, outputId, Frequencies, clock);
        using var probeB = new AudioProbe(enumerator, alternateId, Frequencies, clock);
        var passed = false;
        var restorationFailed = false;

        async Task SetAsync(string bus, string field, object value, CancellationToken commandToken)
        {
            expected[(bus, field)] = value; // Persist intent before a possibly ambiguous network write.
            journal.Write("intent", new { bus, field, value });
            var role = before.Channels.Single(c => c.Id == bus).Role;
            var state = field switch
            {
                "volume" => await client.SetVolumeAsync(role, (double)value, commandToken),
                "mute" => await client.SetMuteAsync(role, (bool)value, commandToken),
                "output" => await client.SetOutputAsync(bus, (string)value, false, commandToken),
                _ => throw new ArgumentException("Unknown field.")
            };
            journal.Write("readback", new { bus, field, Channels = state.Channels });
        }

        async Task<double[]> MeasureAsync(string phase, AudioProbe probe, int settleMs = 600)
        {
            await Task.Delay(settleMs, token);
            var start = clock.Elapsed.TotalSeconds;
            await Task.Delay(1000, token);
            foreach (var tone in tones) tone.Verify();
            var result = probe.MedianSince(start);
            journal.Write("measurement", new { phase, start, End = clock.Elapsed.TotalSeconds, Amplitudes = result, Windows = probe.Since(start) });
            return result;
        }

        try
        {
            foreach (var bus in Buses)
            {
                await SetAsync(bus, "output", outputId, token);
                await SetAsync(bus, "volume", level, token);
                await SetAsync(bus, "mute", false, token);
            }
            // Silent owned streams keep both physical engines clocking, so no-data is not mistaken for digital silence.
            foreach (var id in new[] { outputId, alternateId })
            {
                var keepAlive = new AudioTone(enumerator, id, 997, 0);
                tones.Add(keepAlive);
                keepAlive.Start();
            }
            probeA.Start();
            probeB.Start();
            journal.Write("capture-formats", new { A = probeA.Format.ToString(), B = probeB.Format.ToString(), WindowMs = 100 });
            var noiseA = await MeasureAsync("tone-off-a", probeA);
            var noiseB = await MeasureAsync("tone-off-b", probeB, 0);
            for (var i = 0; i < Buses.Length; i++)
            {
                var tone = new AudioTone(enumerator, before.Channels.Single(c => c.Id == Buses[i]).VirtualDeviceId!, Frequencies[i], amplitude);
                tones.Add(tone);
                tone.Start();
            }
            var baseline = await MeasureAsync("all-buses-baseline", probeA, 1000);
            for (var i = 0; i < Buses.Length; i++)
                SignalMeasurement.Check(baseline[i] > Math.Max(1e-6, noiseA[i] * 10), $"{Buses[i]} tone cannot be distinguished from baseline noise by20dB.");

            for (var i = 0; i < Buses.Length; i++)
            {
                var bus = Buses[i];
                await SetAsync(bus, "volume", level * 0.5, token);
                var lowered = await MeasureAsync(bus + "-half-scalar", probeA);
                var gainDb = SignalMeasurement.Decibels(lowered[i] / baseline[i]);
                journal.Write("gain-result", new { bus, ScalarBefore = level, ScalarAfter = level * 0.5, MeasuredDb = gainDb });
                SignalMeasurement.Check(gainDb < -1 && gainDb > -80, $"{bus} gain did not measurably attenuate a still-present tone.");
                CheckIsolation(lowered, baseline, i);
                await SetAsync(bus, "volume", level, token);
                await SetAsync(bus, "mute", true, token);
                var muted = await MeasureAsync(bus + "-mute", probeA);
                SignalMeasurement.Check(muted[i] < baseline[i] * 0.05, $"{bus} mute did not suppress its tone by26dB.");
                CheckIsolation(muted, baseline, i);
                await SetAsync(bus, "mute", false, token);
            }
            var steadyA = await MeasureAsync("pre-switch", probeA);
            foreach (var bus in Buses) expected[(bus, "output")] = alternateId;
            var switchStart = clock.Elapsed.TotalSeconds;
            journal.Write("intent-linked-output", new { From = outputId, To = alternateId, switchStart });
            var switched = await client.SetOutputAsync("game", alternateId, true, token);
            var switchReadback = clock.Elapsed.TotalSeconds;
            journal.Write("linked-output-readback", new { switchReadback, switched.Channels });
            var steadyB = await MeasureAsync("post-switch-b", probeB, 1500);
            var residualA = probeA.MedianSince(clock.Elapsed.TotalSeconds - 1);
            for (var i = 0; i < Buses.Length; i++)
            {
                SignalMeasurement.Check(steadyB[i] > Math.Max(1e-6, noiseB[i] * 10), $"{Buses[i]} failed to appear at second physical output.");
                SignalMeasurement.Check(residualA[i] < baseline[i] * 0.05, $"{Buses[i]} remained audible at old physical output.");
            }
            var traceA = probeA.Since(switchStart - 0.3);
            var traceB = probeB.Since(switchStart - 0.3);
            var gaps = Buses.Select((bus, index) => MeasureGap(bus, index, traceA, traceB, steadyA, steadyB, switchStart)).ToArray();
            journal.Write("switch-timeline", new { switchStart, switchReadback, WindowMs = 100, TraceA = traceA, TraceB = traceB, Gaps = gaps });
            foreach (var gap in gaps) SignalMeasurement.Check(gap.LongestGapMs <= 1500, $"{gap.Bus} output switch interruption exceeded1500ms.");
            passed = true;
            journal.Write("transport-pass", new { Tests = "four playback buses, level change, mute isolation, linked output switch", Limitation = "Direct endpoint rendering only; no app policy route, microphone, acoustic output or latency proof." });
        }
        catch (Exception error)
        {
            journal.Write("failure", new { Type = error.GetType().FullName, error.Message });
            throw;
        }
        finally
        {
            foreach (var tone in tones.AsEnumerable().Reverse())
            {
                try { tone.Dispose(); }
                catch (Exception error) { journal.Write("tone-stop-error", error.Message); restorationFailed = true; }
            }
            using var restoreToken = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            // Restore only exact fields owned by this run; do not overwrite independently changed state.
            foreach (var field in new[] { "output", "volume", "mute" })
            foreach (var bus in Buses)
            {
                if (!expected.TryGetValue((bus, field), out var written)) continue;
                var original = before.Channels.Single(c => c.Id == bus);
                object wanted = Value(original, field);
                try
                {
                    var current = (await client.GetSnapshotAsync(restoreToken.Token)).Channels.Single(c => c.Id == bus);
                    if (Equal(Value(current, field), wanted)) continue;
                    if (!Equal(Value(current, field), written))
                    {
                        restorationFailed = true;
                        journal.Write("restore-conflict", new { bus, field, Message = "Concurrent change preserved. Manual recovery from before record required." });
                        continue;
                    }
                    await SetAsync(bus, field, wanted, restoreToken.Token);
                }
                catch (Exception error) { restorationFailed = true; journal.Write("restore-error", new { bus, field, error.Message }); }
            }
            try
            {
                var after = await client.GetSnapshotAsync(restoreToken.Token);
                var afterDefaults = ReadDefaults(enumerator);
                var defaultsMatch = defaults.SequenceEqual(afterDefaults);
                var channelsMatch = Buses.All(bus =>
                {
                    var original = before.Channels.Single(c => c.Id == bus);
                    var restored = after.Channels.Single(c => c.Id == bus);
                    return original.DeviceId == restored.DeviceId && original.Muted == restored.Muted && Math.Abs(original.Volume - restored.Volume) < 0.001;
                });
                var afterMaster = after.Channels.Single(c => c.Id == "master");
                var masterMatch = master.Muted == afterMaster.Muted && Math.Abs(master.Volume - afterMaster.Volume) < 0.001;
                restorationFailed |= !defaultsMatch || !channelsMatch || !masterMatch;
                journal.Write("after", new { Snapshot = after, Defaults = afterDefaults, defaultsMatch, channelsMatch, masterMatch, RestorationComplete = !restorationFailed, TransportPassed = passed });
            }
            catch (Exception error) { restorationFailed = true; journal.Write("restore-verification-error", error.Message); }
            if (restorationFailed) Console.Error.WriteLine("RESTORATION INCOMPLETE: use the journal before record; current unrelated changes were preserved.");
        }
        if (restorationFailed) throw new IOException("Transport finished but restoration could not be fully confirmed.");
        Console.WriteLine($"PASS: measured four-bus transport and restoration. Journal: {journalPath}");
    }

    private static object Value(SonarChannel channel, string field) => field switch
    {
        "output" => channel.DeviceId!, "volume" => channel.Volume, "mute" => channel.Muted,
        _ => throw new ArgumentException("Unknown field.", nameof(field))
    };
    private static bool Equal(object left, object right) => left is double a && right is double b ? Math.Abs(a - b) < 0.001 : Equals(left, right);
    private static string[] ReadDefaults(MMDeviceEnumerator enumerator) =>
        (from flow in new[] { DataFlow.Render, DataFlow.Capture } from role in new[] { Role.Console, Role.Multimedia, Role.Communications }
         select ReadDefault(enumerator, flow, role)).ToArray();
    private static string ReadDefault(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        using var device = enumerator.GetDefaultAudioEndpoint(flow, role);
        return $"{flow}/{role}:{device.ID}";
    }
    private static void CheckIsolation(double[] measured, double[] baseline, int changed)
    {
        for (var i = 0; i < measured.Length; i++)
            if (i != changed) SignalMeasurement.Check(Math.Abs(SignalMeasurement.Decibels(measured[i] / baseline[i])) < 2,
                $"Unrelated {Buses[i]} tone changed by more than2dB.");
    }

    private sealed record SwitchGap(string Bus, double FirstNewOutputMs, double LongestGapMs);
    private static SwitchGap MeasureGap(string bus, int index, ToneWindow[] a, ToneWindow[] b, double[] referenceA, double[] referenceB, double start)
    {
        var first = b.FirstOrDefault(w => w.EndSeconds >= start && w.Amplitudes[index] >= referenceB[index] * 0.5)
            ?? throw new IOException("New output never reached half its steady-state tone amplitude.");
        double gap = 0, longest = 0;
        for (var time = start; time <= first.EndSeconds + 0.2; time += 0.1)
        {
            var old = a.MinBy(w => Math.Abs(w.EndSeconds - time))!;
            var next = b.MinBy(w => Math.Abs(w.EndSeconds - time))!;
            if (Math.Abs(old.EndSeconds - time) > 0.25 || Math.Abs(next.EndSeconds - time) > 0.25)
                throw new IOException("Missing loopback windows prevent switch-gap measurement.");
            if (old.Amplitudes[index] < referenceA[index] * 0.1 && next.Amplitudes[index] < referenceB[index] * 0.1) gap += 100;
            else gap = 0;
            longest = Math.Max(longest, gap);
        }
        return new(bus, (first.EndSeconds - start) * 1000, longest);
    }
}
