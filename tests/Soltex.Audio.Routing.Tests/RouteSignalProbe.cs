using System.Diagnostics;
using NAudio.CoreAudioApi;
using Soltex.Audio.Transport.Tests;

internal sealed class RouteSignalProbe : IDisposable
{
    private static readonly double[] Frequencies = [1877];
    private readonly List<AudioTone> silentStreams = [];
    private readonly Dictionary<string, AudioProbe> probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> noise = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly string game, chat;
    private readonly string? physical;
    private readonly RouteJournal journal;
    private bool disposed;

    internal RouteSignalProbe(MMDeviceEnumerator enumerator, string game, string chat, string? physical, RouteJournal journal)
    {
        this.game = game; this.chat = chat; this.physical = physical; this.journal = journal;
        ClearProbe.Check(game != chat && physical != game && physical != chat, "Signal probes require distinct Game, Chat and optional physical endpoints.");
        try
        {
            var ids = physical is null ? new[] { game, chat } : new[] { game, chat, physical };
            foreach (var id in ids)
            {
                using var device = enumerator.GetDevice(id);
                ClearProbe.Check(device.State == DeviceState.Active && device.DataFlow == DataFlow.Render, "Every signal probe must be an active render endpoint.");
                var silence = new AudioTone(enumerator, id, Frequencies[0], 0);
                silentStreams.Add(silence); silence.Start();
                var probe = new AudioProbe(enumerator, id, Frequencies, clock);
                probes.Add(id, probe); probe.Start();
                journal.Write("signal-probe-format", new { Endpoint = id, device.FriendlyName, Format = probe.Format.ToString(), ToneHz = Frequencies[0], WindowMs = 100 });
            }
            Thread.Sleep(400);
            var start = clock.Elapsed.TotalSeconds;
            Thread.Sleep(1200);
            foreach (var (id, probe) in probes)
            {
                var amplitude = probe.MedianSince(start)[0];
                noise.Add(id, amplitude);
                journal.Write("route-tone-off-baseline", new { Endpoint = id, Amplitude = amplitude, Windows = probe.Since(start) });
            }
        }
        catch { Dispose(); throw; }
    }

    internal void Verify(string selected, string phase)
    {
        ClearProbe.Check(selected == game || selected == chat, "Unknown selected test bus.");
        Thread.Sleep(400);
        var start = clock.Elapsed.TotalSeconds;
        Thread.Sleep(1200);
        foreach (var silent in silentStreams) silent.Verify();
        var measured = probes.ToDictionary(p => p.Key, p => p.Value.MedianSince(start)[0], StringComparer.OrdinalIgnoreCase);
        foreach (var (id, probe) in probes)
            journal.Write("route-signal-measurement", new { phase, Selected = selected, Endpoint = id, Amplitude = measured[id], Baseline = noise[id], Windows = probe.Since(start) });
        var selectedAmplitude = measured[selected];
        var other = selected == game ? chat : game;
        ClearProbe.Check(selectedAmplitude > Math.Max(1e-6, noise[selected] * 10), "Selected virtual bus lacks the child tone at20dB above its tone-off baseline.");
        ClearProbe.Check(measured[other] < selectedAmplitude * 0.05, "The other virtual bus is not at least26dB below the selected bus.");
        if (physical is not null)
            ClearProbe.Check(measured[physical] > Math.Max(1e-6, noise[physical] * 10), "Explicit physical loopback lacks the child tone at20dB above its tone-off baseline.");
        journal.Write("route-signal-pass", new { phase, Selected = selected, ToneHz = Frequencies[0], SelectedAmplitude = selectedAmplitude,
            OtherAmplitude = measured[other], OtherRelativeDb = SignalMeasurement.Decibels(measured[other] / selectedAmplitude),
            PhysicalEndpoint = physical, PhysicalAmplitude = physical is null ? (double?)null : measured[physical] });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var tone in silentStreams.AsEnumerable().Reverse())
        {
            try { tone.Dispose(); }
            catch (Exception error) { journal.Write("silent-stream-stop-error", error.Message); }
        }
        foreach (var probe in probes.Values)
        {
            try { probe.Dispose(); }
            catch (Exception error) { journal.Write("loopback-stop-error", error.Message); }
        }
    }
}
