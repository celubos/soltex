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
            new("monitor", "Gaming Monitor (High Definition Audio Device)", false, true)
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
            IndependentMixerChannel[] rows = view.PlaybackChannels.ToArray();
            Program.True(rows.Select(r => r.Bus).SequenceEqual([AudioBus.Game, AudioBus.Chat, AudioBus.Media, AudioBus.Aux]),
                "Playback must list Game, Chat, Media and Aux and leave the microphone to its own card.");
            Program.True(((IEnumerable<IndependentMixerChannel>)view.ChannelList.ItemsSource).Select(r => r.Bus)
                .SequenceEqual([AudioBus.Microphone, AudioBus.Game, AudioBus.Chat, AudioBus.Media, AudioBus.Aux]),
                "The strips must show the microphone first, then the four playback channels, in one set that wraps together.");
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
            IndependentMixerChannel[] rows = view.PlaybackChannels.ToArray();
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

    /// <summary>A helper that cannot be reached until <see cref="Available"/> is set.</summary>
    private sealed class OfflineConnection : IIndependentMixerConnection
    {
        private readonly ScriptedConnection inner = new();
        public bool Available { get; set; }
        public int Connects { get; private set; }
        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Connects++;
            return Available ? inner.ConnectAsync(cancellationToken) : Task.FromException<HostSnapshot>(new IOException("the audio engine did not answer."));
        }
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) => inner.ReadAsync(cancellationToken);
        public Task<HostSnapshot> ApplyAsync(AudioHostProfile profile, CancellationToken cancellationToken = default) => inner.ApplyAsync(profile, cancellationToken);
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public static void PlaybackExplainsNoConnectionAndRetries()
    {
        OfflineConnection connection = new();
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
            Program.True(view.OfflineBar.Visibility == Visibility.Visible && view.OfflineReason.Text.Contains("did not answer", StringComparison.Ordinal),
                "Without a connection the Mixer must say why; got: '" + view.OfflineReason.Text + "'");
            Program.True(view.StateText.Text == "Not connected" && !view.StartStopButton.IsEnabled, "Without a connection the state must say so and Start must wait.");
            Program.True(string.IsNullOrEmpty(view.StatusLine.Text), "The reason must be shown once, not again on the status line.");
            SaveRender(window, "playback-offline-dark");

            connection.Available = true;
            view.RetryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (view.OfflineBar.Visibility == Visibility.Visible && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
            Program.True(connection.Connects >= 2 && view.OfflineBar.Visibility == Visibility.Collapsed && view.StateText.Text == "Stopped",
                "Try again must reconnect and then show the normal stopped state.");
        }
        finally
        {
            window.Close();
            Wait(workspace.ShutdownAsync());
        }
    }

    /// <summary>A running helper with measured levels that stops answering reads while <see cref="Fail"/> is set.</summary>
    private sealed class FlakyConnection : IIndependentMixerConnection
    {
        private static readonly HostEndpoint[] Endpoints =
        [
            new("cable-out", "CABLE Output (VB-Audio Virtual Cable)", true, true) { TransportId = "vb-audio-base-cable" },
            new("headset", "Speakers (USB Audio Interface)", false, true)
        ];
        private readonly AudioHostProfile profile = AudioHostProfile.CreateDefault() with
        {
            Channels = AudioHostProfile.CreateDefault().Channels.Select(c => c.Bus == AudioBus.Game ? c with { Enabled = true, CaptureEndpointId = "cable-out", RenderEndpointId = "headset" } : c).ToArray()
        };
        public bool Fail { get; set; }
        public int Starts { get; private set; }
        private HostSnapshot Snapshot() => new(true, profile,
            [new HostChannelStatus(AudioBus.Game, "Running", "cable-out", "headset", "Playing.", .4f, new BridgeStatus(true, 480, 0, 0, 0, null) { OutputPeak = .5f })],
            Endpoints, null) { StartRequested = true };
        public Task<HostSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<HostSnapshot>(new IOException("the audio engine stopped answering.")) : Task.FromResult(Snapshot());
        public Task<HostSnapshot> ApplyAsync(AudioHostProfile next, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<HostSnapshot> StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.FromResult(Snapshot()); }
        public Task<HostSnapshot> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// When the engine stops answering, the Mixer says "Not connected" with the reason and a retry, its meters go back to
    /// "not measured" and the microphone strip stops saying Running; it recovers on the next answer. While running, the
    /// status line doesn't repeat "Audio is running." under the Running badge, and state and status are live regions.
    /// </summary>
    public static void PlaybackShowsLostConnection()
    {
        FlakyConnection connection = new();
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
            IndependentMixerChannel game = workspace.Channels.Single(c => c.Bus == AudioBus.Game);
            Program.True(view.StateText.Text == "Running" && game.Peak > 0, "The fixture must start running with a measured level.");
            Program.True(workspace.StatusText == IndependentMixerWorkspace.RunningStatus && view.StatusLine.Text.Length == 0,
                "'Audio is running.' must not repeat the Running badge; got: '" + view.StatusLine.Text + "'");
            Program.True(global::Soltex.App.Controls.LiveRegion.GetIsLive(view.StateText) && global::Soltex.App.Controls.LiveRegion.GetIsLive(view.StatusLine) &&
                global::Soltex.App.Controls.LiveRegion.GetIsLive(view.OfflineReason), "The Mixer's state, status and connection reason must be announced to screen readers.");

            connection.Fail = true;
            Wait(workspace.RefreshStateAsync());
            Pump();
            Program.True(workspace.ConnectionLost && view.StateText.Text == "Not connected",
                "A lost connection must not keep showing Running; got: " + view.StateText.Text);
            Program.True(workspace.Channels.All(c => c.Peak < 0), "Meters must go back to 'not measured' when the engine stops answering.");
            Program.True(view.OfflineBar.Visibility == Visibility.Visible && view.OfflineReason.Text.Contains("stopped answering", StringComparison.Ordinal),
                "The Mixer must say why and offer Try again; got: '" + view.OfflineReason.Text + "'");
            Program.True(view.Mic.State == "Not connected", "The microphone strip must not keep its last state; got: " + view.Mic.State);
            SaveRender(window, "playback-connection-lost-dark");

            connection.Fail = false;
            view.RetryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (workspace.ConnectionLost && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
            Pump();
            Program.True(!workspace.ConnectionLost && view.StateText.Text == "Running" && view.OfflineBar.Visibility == Visibility.Collapsed && game.Peak > 0,
                "Try again must show the confirmed state and levels once the engine answers.");
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
