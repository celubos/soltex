using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Soltex.Audio.Engine;

namespace Soltex.Audio.UI;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Window lifetime is closed asynchronously: Closing drains the MTA worker, joins it and disposes its command queue before allowing Close.")]
public partial class EngineControlWindow : Window
{
    public static IReadOnlyList<EqFilterType> FilterTypes { get; } = Enum.GetValues<EqFilterType>();
    private sealed record EndpointChoice(string Id, string Name);
    private readonly ObservableCollection<BandEditor> bands = [];
    private readonly BlockingCollection<Action> commands = new();
    private readonly Thread worker;
    private readonly DispatcherTimer changes = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer telemetry = new() { Interval = TimeSpan.FromSeconds(1) };
    private WasapiBridge? bridge; // Access only on the owned MTA worker.
    private MicrophoneTestRecorder? recorder;
    private MicrophoneTestReplay? replay;
    private string? lastRecording;
    private string? recordingDigest;
    private DateTimeOffset recordingDeadline;
    private bool ready, busy, running, polling, closing, allowClose;
    private bool preserveRejectedPreferences;
    private int sampleRate = 48000;
    private DspSettings retainedSettings = new();
    private static readonly string DefaultPreferencePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoltexAudio", "engine-dsp-v1.json");
    private readonly string preferencePath;
    private readonly TaskCompletionSource windowClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly JsonSerializerOptions PresetJson = new()
    { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private sealed record DspPreset(int SchemaVersion, DspSettings Settings);

    public EngineControlWindow() : this(DefaultPreferencePath) { }

    public EngineControlWindow(string preferencePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferencePath);
        this.preferencePath = Path.GetFullPath(preferencePath);
        InitializeComponent();
        worker = new Thread(() => { foreach (var command in commands.GetConsumingEnumerable()) command(); })
        { IsBackground = true, Name = "Soltex independent audio control" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        foreach (var (band, index) in DspSettings.FlatBands().Select((band, index) => (band, index)))
        {
            var editor = new BandEditor(index + 1, band);
            editor.PropertyChanged += (_, _) => ScheduleControls();
            bands.Add(editor);
        }
        BandsGrid.ItemsSource = bands;
        changes.Tick += async (_, _) => { changes.Stop(); await ApplyControlsAsync(); };
        telemetry.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) =>
        {
            string? loadError = null;
            try { if (File.Exists(this.preferencePath)) PopulateControls(ReadPreset(this.preferencePath)); }
            catch (Exception error)
            {
                preserveRejectedPreferences = true;
                loadError = $"Saved DSP preferences were rejected and preserved: {error.Message} Reset DSP to replace them.";
            }
            ready = true; await RefreshAsync(); await ApplyControlsAsync();
            if (loadError is not null) Feedback.Text = loadError;
        };
        Closing += ClosingAsync;
        Closed += (_, _) => windowClosed.TrySetResult();
    }

    public Task ShutdownAsync()
    {
        Dispatcher.VerifyAccess();
        if (!windowClosed.Task.IsCompleted && !closing) Close();
        return windowClosed.Task;
    }

    private Task<T> OnWorker<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (commands.IsAddingCompleted) return Task.FromException<T>(new ObjectDisposedException(nameof(EngineControlWindow)));
        commands.Add(() =>
        {
            try { completion.SetResult(operation()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task;
    }

    private async Task RefreshAsync()
    {
        if (running || busy || closing) return;
        SetBusy(true);
        try
        {
            var devices = await OnWorker(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                var captures = new List<EndpointChoice>(); var renders = new List<EndpointChoice>();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                {
                    using (device)
                    {
                        var choice = new EndpointChoice(device.ID, device.FriendlyName);
                        if (device.DataFlow == DataFlow.Capture) captures.Add(choice); else renders.Add(choice);
                    }
                }
                return (Captures: captures, Renders: renders);
            });
            var input = (CaptureSelector.SelectedItem as EndpointChoice)?.Id;
            var output = (RenderSelector.SelectedItem as EndpointChoice)?.Id;
            CaptureSelector.ItemsSource = devices.Captures;
            RenderSelector.ItemsSource = devices.Renders;
            CaptureSelector.SelectedItem = devices.Captures.Find(d => d.Id == input);
            RenderSelector.SelectedItem = devices.Renders.Find(d => d.Id == output);
            Feedback.Text = $"{devices.Captures.Count} capture inputs · {devices.Renders.Count} render outputs. Choose an input and an output.";
        }
        catch (Exception error) { Feedback.Text = $"Devices could not be listed: {error.Message}"; }
        finally { SetBusy(false); }
    }

    private async void AdvancedProcessingClicked(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        try
        {
            var dialog = new IndependentMixerDspWindow("Input, noise and stereo", ReadControls()) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            PopulateControls(dialog.Settings);
            await ApplyControlsAsync();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        { Feedback.Text = "Processing was not applied: " + error.Message; }
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (running || busy || closing) return;
        if (CaptureSelector.SelectedItem is not EndpointChoice input || RenderSelector.SelectedItem is not EndpointChoice output)
        { Feedback.Text = "Choose a capture input and a render output before starting."; return; }
        SetBusy(true); changes.Stop();
        try
        {
            var settings = ReadControls();
            var format = await OnWorker(() =>
            {
                if (bridge is not null) throw new InvalidOperationException("A bridge already owns this workspace.");
                var created = new WasapiBridge(new(input.Id, output.Id) { Dsp = settings });
                try
                {
                    created.Start(); bridge = created;
                    return (created.Processor.SampleRate, created.Processor.Channels, Output: created.RenderFormat.ToString());
                }
                catch { created.Dispose(); throw; }
            });
            sampleRate = format.SampleRate;
            running = true;
            RunState.Text = MicrophonePath.IsChecked == true ? "MICROPHONE CAPTURE ACTIVE" : "CAPTURE → RENDER ACTIVE";
            Feedback.Text = $"{input.Name} → {output.Name}. Soltex DSP is processing this path.";
            Telemetry.Text = $"{sampleRate:N0} Hz · {format.Channels} channels → {format.Output}";
            Response.SetResponse(settings, sampleRate);
            telemetry.Start();
        }
        catch (Exception error) { Feedback.Text = $"Could not start: {error.Message}"; }
        finally { SetBusy(false); }
    }

    private async void StopClicked(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        if (busy) return;
        SetBusy(true); telemetry.Stop();
        try
        {
            await OnWorker(CloseOwnedStreams);
            Feedback.Text = "Stopped. Windows defaults are unchanged.";
        }
        catch (Exception error) { Feedback.Text = $"Stream close reported: {error.Message}"; }
        finally { running = false; RunState.Text = "STOPPED"; Telemetry.Text = "No stream active"; SetBusy(false); }
    }

    private void ControlChanged(object sender, RoutedEventArgs e) => ScheduleControls();
    private void BandEditEnding(object sender, DataGridCellEditEndingEventArgs e) => ScheduleControls();
    private void ScheduleControls()
    {
        if (!ready || closing) return;
        GainValue.Text = $"{Gain.Value:F1} dB";
        changes.Stop(); changes.Start();
    }

    private DspSettings ReadControls() => retainedSettings with
    {
        GainDb = Gain.Value, Mute = Mute.IsChecked == true, HeadroomDb = Number(Headroom),
        EqBypass = EqBypass.IsChecked == true, Bands = bands.Select(b => b.Value()).ToArray(),
        Gate = new(GateEnabled.IsChecked == true, Number(GateThreshold), Number(GateHysteresis),
            Number(GateAttack), Number(GateHold), Number(GateRelease), Number(GateFloor)),
        Compressor = new(CompressorEnabled.IsChecked == true, Number(CompressorThreshold), Number(CompressorRatio),
            Number(CompressorAttack), Number(CompressorRelease), Number(CompressorMakeup)),
        NoiseAttenuation = new(ExpanderEnabled.IsChecked == true, Number(ExpanderThreshold), Number(ExpanderRatio),
            Number(ExpanderDepth), Number(ExpanderAttack), Number(ExpanderRelease))
    };

    private async Task ApplyControlsAsync()
    {
        if (closing || busy) return;
        try
        {
            var settings = ReadControls();
            // The response API validates exactly the same controls as the processor.
            Response.SetResponse(settings, sampleRate);
            string? preferenceNote = await OnWorker(() =>
            {
                bridge?.UpdateSettings(settings); recorder?.Processor.UpdateSettings(settings);
                if (preserveRejectedPreferences) return "Rejected saved preferences were preserved. Reset DSP to replace them.";
                try { WritePreset(preferencePath, settings); return (string?)null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return $"Preferences could not be saved: {error.Message}"; }
            });
            Feedback.Text = running ? "DSP controls applied to the active path." : "DSP preview ready. Choose an input and an output, then start.";
            if (preferenceNote is not null) Feedback.Text += " " + preferenceNote;
        }
        catch (Exception error) { Feedback.Text = $"Controls not applied: {error.Message}"; }
    }

    private async Task PollAsync()
    {
        if (polling || closing || !running) return;
        polling = true;
        try
        {
            var state = await OnWorker(() => (Bridge: bridge?.Status, Record: recorder?.Status,
                ReplayComplete: replay?.Complete ?? false, ReplayFailure: replay?.Failure));
            if (state.Record is not null)
            {
                Telemetry.Text = $"RECORDING selected input · {state.Record.SecondsRecorded:F1} / 10.0 seconds · {lastRecording}";
                if (state.Record.Complete || state.Record.Failure is not null || DateTimeOffset.UtcNow >= recordingDeadline)
                {
                    await StopAsync();
                    Feedback.Text = state.Record.Failure is null ? $"Recording saved locally: {lastRecording}. Replay uses the explicitly selected output at −12 dB."
                        : $"Recording stopped: {state.Record.Failure}";
                }
                return;
            }
            if (state.ReplayComplete || state.ReplayFailure is not null)
            {
                await StopAsync(); Feedback.Text = state.ReplayFailure is null ? "Test replay finished. No capture is active." : $"Replay stopped: {state.ReplayFailure}";
                return;
            }
            var status = state.Bridge;
            if (status is null) return;
            Telemetry.Text = $"Queued {status.BufferedFrames:N0} frames · dropped {status.DroppedFrames:N0} · silent {status.SilentFrames:N0} · clipped {status.LimitedSamples:N0}";
            if (status.Failure is not null)
            {
                var message = status.Failure;
                await StopAsync(); Feedback.Text = $"Audio path stopped after a stream failure: {message}";
            }
        }
        catch (Exception error) { Feedback.Text = $"Engine status unavailable: {error.Message}"; }
        finally { polling = false; }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        StartButton.IsEnabled = !busy && !running && !closing;
        StopButton.IsEnabled = !busy && running && !closing;
        RefreshButton.IsEnabled = !busy && !running && !closing;
        RecordButton.IsEnabled = !busy && !running && !closing;
        ReplayButton.IsEnabled = !busy && !running && !closing && lastRecording is not null;
        DiscardButton.IsEnabled = !busy && !running && !closing && lastRecording is not null && recordingDigest is not null;
        CaptureSelector.IsEnabled = RenderSelector.IsEnabled = MicrophonePath.IsEnabled = !busy && !running && !closing;
    }

    private async void ClosingAsync(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; changes.Stop(); telemetry.Stop(); SetBusy(true);
        try { await OnWorker(CloseOwnedStreams); }
        catch (Exception error) { Feedback.Text = $"Shutdown reported: {error.Message}"; }
        finally
        {
            commands.CompleteAdding();
            await Task.Run(worker.Join);
            commands.Dispose(); allowClose = true; Close();
        }
    }

    private static double Number(TextBox field) => Parse(field.Text, field.Name);
    private static double Parse(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) || !double.IsFinite(value))
            throw new ArgumentException($"{name} needs a finite number.");
        return value;
    }

    private bool CloseOwnedStreams()
    {
        List<Exception> errors = [];
        void Close(IDisposable? owned) { try { owned?.Dispose(); } catch (Exception error) { errors.Add(error); } }
        var ownedBridge = bridge; bridge = null;
        var ownedRecorder = recorder; recorder = null;
        var ownedReplay = replay; replay = null;
        Close(ownedBridge); Close(ownedRecorder); Close(ownedReplay);
        if (ownedRecorder is not null && errors.Count == 0 && lastRecording is not null)
        {
            using var file = File.OpenRead(lastRecording);
            recordingDigest = Convert.ToHexString(SHA256.HashData(file));
        }
        if (errors.Count != 0) throw new AggregateException("Owned stream cleanup reported an error.", errors);
        return true;
    }

    private async void RecordClicked(object sender, RoutedEventArgs e)
    {
        if (busy || running || closing) return;
        if (CaptureSelector.SelectedItem is not EndpointChoice input)
        { Feedback.Text = "Select the microphone/capture input to record. Recording never starts automatically."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Record selected input for at most 10 seconds — choose a new WAV file",
            Filter = "Local microphone test (*.wav)|*.wav", AddExtension = true,
            FileName = $"Soltex-mic-{DateTime.Now:yyyyMMdd-HHmmss}.wav",
            InitialDirectory = Environment.GetEnvironmentVariable("SOLTEX_AUDIO_RECORDINGS") ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            ValidateRecordingDestination(dialog.FileName);
            var settings = ReadControls();
            int rate = await OnWorker(() =>
            {
                if (bridge is not null || recorder is not null || replay is not null) throw new InvalidOperationException("Stop the current path first.");
                var created = new MicrophoneTestRecorder(input.Id, dialog.FileName, settings, 10);
                try { created.Start(); recorder = created; return created.Processor.SampleRate; }
                catch { created.Dispose(); throw; }
            });
            lastRecording = dialog.FileName; recordingDigest = null; sampleRate = rate; recordingDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            RecordingLocation.Text = $"Retained on close · Discard deletes this test: {lastRecording}"; RecordingLocation.ToolTip = lastRecording;
            running = true; RunState.Text = "RECORDING SELECTED INPUT";
            Feedback.Text = $"Recording {input.Name} through Soltex DSP. No live monitoring. Stop ends the recording early.";
            telemetry.Start();
        }
        catch (Exception error) { Feedback.Text = $"Recording not started: {error.Message}"; }
        finally { SetBusy(false); }
    }

    private async void ReplayClicked(object sender, RoutedEventArgs e)
    {
        if (busy || running || closing || lastRecording is null) return;
        if (RenderSelector.SelectedItem is not EndpointChoice output)
        { Feedback.Text = "Select an explicit render output for test replay."; return; }
        SetBusy(true);
        try
        {
            await OnWorker(() =>
            {
                if (bridge is not null || recorder is not null || replay is not null) throw new InvalidOperationException("Stop the current path first.");
                var created = new MicrophoneTestReplay(output.Id, lastRecording);
                try { created.Start(); replay = created; return true; } catch { created.Dispose(); throw; }
            });
            running = true; RunState.Text = "REPLAYING TEST · −12 dB";
            Feedback.Text = $"Replaying to {output.Name} at a fixed −12 dB attenuation. Stop ends playback.";
            Telemetry.Text = "No capture active during replay"; telemetry.Start();
        }
        catch (Exception error) { Feedback.Text = $"Replay not started: {error.Message}"; }
        finally { SetBusy(false); }
    }

    private async void DiscardClicked(object sender, RoutedEventArgs e)
    {
        if (busy || running || closing || lastRecording is null || recordingDigest is null) return;
        SetBusy(true);
        try
        {
            await OnWorker(() =>
            {
                ValidateRecordingDestination(lastRecording, false);
                if ((File.GetAttributes(lastRecording) & FileAttributes.ReparsePoint) != 0) throw new IOException("The recording path has changed; it was not deleted.");
                string actual;
                using (var file = File.OpenRead(lastRecording)) actual = Convert.ToHexString(SHA256.HashData(file));
                if (!string.Equals(actual, recordingDigest, StringComparison.Ordinal)) throw new IOException("The recording changed after this session; it was not deleted.");
                File.Delete(lastRecording); return true;
            });
            lastRecording = null; recordingDigest = null; RecordingLocation.Text = "Test recording discarded. No audio file is retained by this workspace."; RecordingLocation.ToolTip = null;
            Feedback.Text = "Deleted only the unchanged WAV created by this workspace.";
        }
        catch (Exception error) { Feedback.Text = $"Recording not discarded: {error.Message}"; }
        finally { SetBusy(false); }
    }

    private static void ValidateRecordingDestination(string path, bool requireNew = true)
    {
        string absolute = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(absolute);
        // Any local fixed drive; never a network share, and (below) never through a junction or symbolic link.
        if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new IOException("Choose a recording destination on a local drive.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(absolute)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Recording destinations must not traverse junctions or symbolic links.");
        if (requireNew && File.Exists(absolute)) throw new IOException("Choose a new filename; existing recordings are preserved.");
    }

    private async void SavePresetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        try
        {
            var settings = ReadControls(); _ = EqualizerResponse.Decibels(settings, sampleRate, [1000]);
            var dialog = new Microsoft.Win32.SaveFileDialog
            { Title = "Save independent DSP preset", Filter = "Soltex DSP preset (*.json)|*.json", FileName = "Soltex-audio.dsp.json", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            await OnWorker(() => { WritePreset(dialog.FileName, settings); return true; });
            if (string.Equals(Path.GetFullPath(dialog.FileName), preferencePath, StringComparison.OrdinalIgnoreCase))
                preserveRejectedPreferences = false;
            Feedback.Text = "DSP preset saved. Presets contain processing controls, with no automatic stream start.";
        }
        catch (Exception error) { Feedback.Text = $"Preset not saved: {error.Message}"; }
    }

    private async void LoadPresetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        { Title = "Load independent DSP preset", Filter = "Soltex DSP preset (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var settings = await OnWorker(() => ReadPreset(dialog.FileName));
            _ = EqualizerResponse.Decibels(settings, sampleRate, [1000]);
            PopulateControls(settings); await ApplyControlsAsync();
        }
        catch (Exception error) { Feedback.Text = $"Preset not loaded: {error.Message}"; }
    }

    private async void ResetPresetClicked(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        preserveRejectedPreferences = false;
        PopulateControls(new DspSettings()); await ApplyControlsAsync();
    }

    private static DspSettings ReadPreset(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Preset exceeds the 1 MiB limit.");
        var preset = JsonSerializer.Deserialize<DspPreset>(File.ReadAllText(path), PresetJson)
            ?? throw new InvalidDataException("The preset is empty.");
        if (preset.SchemaVersion != 1) throw new InvalidDataException($"Unsupported preset version {preset.SchemaVersion}.");
        // Validate values independently of the eventual device; its actual Nyquist bound is checked before applying/starting.
        _ = EqualizerResponse.Decibels(preset.Settings, 192000, [1000]);
        return preset.Settings;
    }

    private static void WritePreset(string path, DspSettings settings)
    {
        string absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        string temporary = absolute + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new DspPreset(1, settings), PresetJson)); Soltex.Shared.AtomicFileReplace.Move(temporary, absolute); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void PopulateControls(DspSettings settings)
    {
        bool wasReady = ready; ready = false;
        try
        {
            retainedSettings = settings;
            Gain.Value = settings.GainDb; Mute.IsChecked = settings.Mute; Headroom.Text = Format(settings.HeadroomDb); EqBypass.IsChecked = settings.EqBypass;
            for (int i = 0; i < bands.Count; i++)
            {
                var band = settings.Bands[i]; bands[i].Frequency = Format(band.FrequencyHz); bands[i].Gain = Format(band.GainDb);
                bands[i].Q = Format(band.Q); bands[i].Type = band.Type; bands[i].Bypass = band.Bypass;
            }
            GateEnabled.IsChecked = settings.Gate.Enabled; GateThreshold.Text = Format(settings.Gate.ThresholdDb);
            GateHysteresis.Text = Format(settings.Gate.HysteresisDb); GateAttack.Text = Format(settings.Gate.AttackMs);
            GateHold.Text = Format(settings.Gate.HoldMs); GateRelease.Text = Format(settings.Gate.ReleaseMs); GateFloor.Text = Format(settings.Gate.ClosedGainDb);
            CompressorEnabled.IsChecked = settings.Compressor.Enabled; CompressorThreshold.Text = Format(settings.Compressor.ThresholdDb);
            CompressorRatio.Text = Format(settings.Compressor.Ratio); CompressorAttack.Text = Format(settings.Compressor.AttackMs);
            CompressorRelease.Text = Format(settings.Compressor.ReleaseMs); CompressorMakeup.Text = Format(settings.Compressor.MakeupDb);
            ExpanderEnabled.IsChecked = settings.NoiseAttenuation.Enabled; ExpanderThreshold.Text = Format(settings.NoiseAttenuation.ThresholdDb);
            ExpanderRatio.Text = Format(settings.NoiseAttenuation.Ratio); ExpanderDepth.Text = Format(settings.NoiseAttenuation.MaximumAttenuationDb);
            ExpanderAttack.Text = Format(settings.NoiseAttenuation.AttackMs); ExpanderRelease.Text = Format(settings.NoiseAttenuation.ReleaseMs);
            GainValue.Text = $"{Gain.Value:F1} dB";
        }
        finally { ready = wasReady; }
    }
    private static string Format(double value) => value.ToString("G", CultureInfo.CurrentCulture);

    private sealed class BandEditor : INotifyPropertyChanged
    {
        private string frequency, gain, q;
        private EqFilterType type;
        private bool bypass;
        public int Number { get; }
        public string Frequency { get => frequency; set { frequency = value; Changed(); } }
        public string Gain { get => gain; set { gain = value; Changed(); } }
        public string Q { get => q; set { q = value; Changed(); } }
        public EqFilterType Type { get => type; set { type = value; Changed(); } }
        public bool Bypass { get => bypass; set { bypass = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public BandEditor(int number, EqBand band)
        {
            Number = number; frequency = band.FrequencyHz.ToString(CultureInfo.CurrentCulture);
            gain = band.GainDb.ToString(CultureInfo.CurrentCulture); q = band.Q.ToString("F3", CultureInfo.CurrentCulture);
            type = band.Type; bypass = band.Bypass;
        }
        public EqBand Value() => new(Parse(frequency, $"Band {Number} frequency"), Parse(gain, $"Band {Number} gain"),
            Parse(q, $"Band {Number} Q"), type, bypass);
        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
