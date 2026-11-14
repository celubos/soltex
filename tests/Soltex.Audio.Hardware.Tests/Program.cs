using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using Soltex.Audio.Engine;
using Soltex.Audio.Transport.Tests;

namespace Soltex.Audio.Hardware.Tests;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] != "--allow-microphone-test")
        {
            Console.WriteLine("Explicit opt-in: --allow-microphone-test <capture-ID> <render-ID> <new-receipt.json on a local drive>. Captures 5s, replays attenuated audio, opens a quiet bridge for 10s. No defaults or endpoint levels changed. Temporary WAV is deleted after the test.");
            return args.Length == 0 ? 0 : 2;
        }
        string receipt = LocalOutputFile.Require(args[3], nameof(args));
        string folder = Path.GetDirectoryName(receipt)!;
        string recording = Path.Combine(folder, "owned-mic-test-" + Guid.NewGuid().ToString("N") + ".wav");
        var observations = new List<object>();
        int exit = 0;
        try
        {
            var quiet = new DspSettings { GainDb = -30, HeadroomDb = -6 };
            using (var recorder = new MicrophoneTestRecorder(args[1], recording, quiet, 5))
            {
                Console.WriteLine("MICROPHONE CAPTURE ACTIVE for at most 5 seconds; no live monitoring.");
                recorder.Start(); var timer = Stopwatch.StartNew();
                while (!recorder.Status.Complete && timer.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
                observations.Add(new { Phase = "record", recorder.Status });
                if (!recorder.Status.Complete || recorder.Status.Failure is not null) throw new IOException("Recording did not complete successfully.");
            }
            using (var reader = new WaveFileReader(recording))
            {
                var source = reader.ToSampleProvider(); float[] samples = new float[4096]; double sum = 0, peak = 0; long total = 0;
                int count;
                while ((count = source.Read(samples, 0, samples.Length)) > 0)
                    for (int i = 0; i < count; i++)
                    {
                        if (!float.IsFinite(samples[i])) throw new IOException("Nonfinite captured sample.");
                        peak = Math.Max(peak, Math.Abs(samples[i])); sum += (double)samples[i] * samples[i]; total++;
                    }
                observations.Add(new { Phase = "recorded-file", Seconds = reader.TotalTime.TotalSeconds, Samples = total, Peak = peak, Rms = total > 0 ? Math.Sqrt(sum / total) : 0, Format = reader.WaveFormat.ToString() });
                if (total == 0 || Math.Abs(reader.TotalTime.TotalSeconds - 5) > 0.05) throw new IOException("Bounded recording frame count failed.");
            }
            using (var replay = new MicrophoneTestReplay(args[2], recording))
            {
                Console.WriteLine("Replaying the quiet captured test to the explicitly selected output.");
                replay.Start(); var timer = Stopwatch.StartNew();
                while (!replay.Complete && timer.Elapsed < TimeSpan.FromSeconds(8)) Thread.Sleep(100);
                observations.Add(new { Phase = "replay", replay.Complete, replay.Failure });
                if (!replay.Complete || replay.Failure is not null) throw new IOException("Replay did not complete successfully.");
            }
            using (var bridge = new WasapiBridge(new(args[1], args[2]) { Dsp = quiet with { GainDb = -60 } }))
            {
                Console.WriteLine("MICROPHONE CAPTURE ACTIVE: quiet ten-second independent bridge.");
                bridge.Start();
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(1000); observations.Add(new { Phase = "bridge", Second = i + 1, bridge.Status });
                    if (bridge.Status.Failure is not null) throw new IOException(bridge.Status.Failure);
                }
                bridge.Stop();
            }
        }
        catch (Exception error) { observations.Add(new { Phase = "failure", error.Message }); Console.Error.WriteLine(error.Message); exit = 1; }
        finally
        {
            if (File.Exists(recording)) File.Delete(recording); // Exact new random file owned by this invocation only.
            observations.Add(new { Phase = "cleanup", TemporaryRecordingRemoved = !File.Exists(recording), CaptureEndpoint = args[1], RenderEndpoint = args[2], Passed = exit == 0, Limitation = "Stream lifecycle and finite frames, not known speech, acoustic output or virtual microphone topology proof." });
            using var stream = new FileStream(receipt, FileMode.CreateNew);
            JsonSerializer.Serialize(stream, observations);
        }
        return exit;
    }
}
