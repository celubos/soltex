using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Pages;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.App.Tests;

/// <summary>The Fluent Playback view over the real mixer workspace and a scripted helper connection (no device).</summary>
internal static class PlaybackChecks
{
    private sealed class ScriptedConnection : IIndependentMixerConnection
    {
        private AudioHostProfile profile = AudioHostProfile.CreateDefault();
        private bool started;
        public List<AudioHostProfile> Applied { get; } = [];
        public int Starts { get; private set; }

        private static readonly HostEndpoint[] Endpoints =
        [
            new("sonar-game", "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", true, true),
            new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" },
            new("headset", "Speakers (USB Audio Interface)", false, true),
            new("monitor", "LG ULTRAGEAR+ (NVIDIA High Definition Audio)", false, true)
        ];

        private HostSnapshot Snapshot() => new(false, profile, [], Endpoints, null) { StartRequested = started };
        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> ApplyAsync(AudioHostProfile next, CancellationToken cancellationToken = default)
        {
            Applied.Add(next);
            profile = HostProfileStore.Validate(next);
            return Task.FromResult(Snapshot());
        }
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) { Starts++; started = true; return Task.FromResult(Snapshot()); }
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) { started = false; return Task.FromResult(Snapshot()); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public static void PlaybackStagesAppliesAndEditsSound()
    {
        ScriptedConnection connection = new();
        IndependentMixerWorkspace workspace = new();
        Wait(workspace.InitializeAsync(connection));
        PlaybackView view = new(workspace);
        Window window = new()
        {
            Content = view, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush")
        };
        RenderSmokeCapture.ConfigureWindow(window);
        window.Show();
        try
        {
            Pump();
            IndependentMixerChannel[] rows = ((IEnumerable<IndependentMixerChannel>)view.ChannelList.ItemsSource).ToArray();
            Program.True(rows.Select(r => r.Bus).SequenceEqual([AudioBus.Game, AudioBus.Chat, AudioBus.Media, AudioBus.Aux]),
                "Playback must list Game, Chat, Media and Aux and leave the microphone to its own card.");
            Program.True(view.StagedBar.Visibility == Visibility.Collapsed, "Nothing is staged before an edit.");

            IndependentMixerChannel game = rows[0];
            game.Enabled = true;
            game.CaptureEndpointId = "cable-out";
            game.RenderEndpointId = "headset";
            Pump();
            Program.True(view.StagedBar.Visibility == Visibility.Visible, "Changing a source or output must show that it is not applied yet.");
            Program.True(connection.Applied.Count == 0, "Routing edits must stay staged until Apply.");
            SaveRender(window, "playback-staged-dark");

            Wait(workspace.ApplyRoutingAsync());
            Pump();
            HostChannelProfile applied = connection.Applied[^1].Channels.Single(c => c.Bus == AudioBus.Game);
            Program.True(applied is { Enabled: true, CaptureEndpointId: "cable-out", RenderEndpointId: "headset" }, "Apply did not send the staged channel.");
            Program.True(view.StagedBar.Visibility == Visibility.Collapsed, "After Apply nothing is staged.");

            view.EditSound = (_, dsp) => dsp with { Stereo = new StereoSettings(Enabled: true, Width: 1.5) };
            Program.True(Wait(view.AdjustSoundAsync(AudioBus.Game)), "Saving Game's sound failed.");
            AudioHostProfile afterSound = connection.Applied[^1];
            Program.True(afterSound.Channels.Single(c => c.Bus == AudioBus.Game).Dsp.Stereo is { Enabled: true, Width: 1.5 } &&
                afterSound.Channels.Single(c => c.Bus == AudioBus.Game).CaptureEndpointId == "cable-out",
                "Sound edits must change only that channel's processing.");
            Program.True(afterSound.Channels.Single(c => c.Bus == AudioBus.Chat).Dsp.Stereo.Enabled == false, "Other channels must be untouched.");

            int before = connection.Applied.Count;
            view.MasterSlider.Value = -12;
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (connection.Applied.Count == before && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
            Program.True(connection.Applied.Count > before && connection.Applied[^1].MasterGainDb == -12, "The overall level must reach the helper.");
            Program.True(connection.Starts == 0, "No playback action in this test may start audio.");
        }
        finally
        {
            window.Close();
            Wait(workspace.ShutdownAsync());
        }
    }

    public static void PlaybackShowsRefusalsAndSharedOutput()
    {
        ScriptedConnection connection = new();
        IndependentMixerWorkspace workspace = new();
        Wait(workspace.InitializeAsync(connection));
        PlaybackView view = new(workspace);
        Window window = new()
        {
            Content = view, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("SolidBackgroundFillColorBaseBrush")
        };
        RenderSmokeCapture.ConfigureWindow(window);
        window.Show();
        try
        {
            Pump();
            Program.True(view.OutputChoice.SelectedItem is null && !view.SwitchOutput.IsEnabled,
                "With no channel playing there is no shared output to show, and Switch has nothing to do.");
            IndependentMixerChannel[] rows = ((IEnumerable<IndependentMixerChannel>)view.ChannelList.ItemsSource).ToArray();
            IndependentMixerChannel game = rows[0], chat = rows[1];
            game.Enabled = true;
            game.CaptureEndpointId = "cable-out";
            game.RenderEndpointId = "headset";
            Wait(workspace.ApplyRoutingAsync());
            Pump();
            Program.True(view.OutputChoice.SelectedItem is IndependentEndpointChoice { Id: "headset" } && view.SwitchOutput.IsEnabled,
                "When every playing channel uses one output, the box must show it instead of staying blank.");

            // Game and Chat on the same source: the helper refuses the profile.
            chat.Enabled = true;
            chat.CaptureEndpointId = "cable-out";
            chat.RenderEndpointId = "monitor";
            Pump();
            Wait(workspace.ApplyRoutingAsync());
            Pump();
            Program.True(view.StagedBar.Visibility == Visibility.Visible && view.StatusLine.Text.Contains("not confirmed", StringComparison.Ordinal),
                "A refused Apply must say why while the edits stay staged; got: '" + view.StatusLine.Text + "'");
            Program.True(workspace.LastOperationFailed, "The workspace must report the refused change.");
            Program.True(connection.Starts == 0, "No playback action in this test may start audio.");
        }
        finally
        {
            window.Close();
            Wait(workspace.ShutdownAsync());
        }
    }

    private static void SaveRender(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("SOLTEX_RENDER_DIR") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        RenderTargetBitmap bitmap = RenderSmokeCapture.Capture(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static void Wait(Task task) => WaitCore(task);

    private static bool WaitCore(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("A playback operation did not finish.");
            Pump();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        return true;
    }

    private static T Wait<T>(Task<T> task)
    {
        WaitCore(task);
        return task.Result;
    }

    private static void Pump()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
