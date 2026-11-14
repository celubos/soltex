using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Soltex.Audio.Engine;
using EngineWindow = Soltex.Audio.UI.EngineControlWindow;

namespace Soltex.Audio.App.Tests;

internal static class EngineWorkspaceChecks
{
    public static async Task ControlsAndLifecycleAsync(string workingDirectory, string? renderDirectory)
    {
        string preferences = Path.Combine(workingDirectory, "valid-controls.json");
        File.WriteAllText(preferences, JsonSerializer.Serialize(new { SchemaVersion = 1, Settings = new DspSettings
        { InputChannel = 1, OutputCeiling = .7, NoiseSuppression = new(true, -60, 12), Stereo = new(true, .8, .1, .2) } }));
        var window = Create(preferences);
        var worker = Checks.Field<Thread>(window, "worker");
        try
        {
            window.Show();
            await PreviewReadyAsync(window);
            Checks.That(worker.IsAlive && worker.GetApartmentState() == ApartmentState.MTA, "Independent controls must own a live MTA worker.");
            NoAudio(window);
            Checks.That(Checks.Named<ComboBox>(window, "CaptureSelector").SelectedIndex == -1
                && Checks.Named<ComboBox>(window, "RenderSelector").SelectedIndex == -1, "Endpoints must never be selected implicitly.");
            Checks.That(!Checks.Named<Button>(window, "StopButton").IsEnabled, "A stopped workspace must not offer an active Stop action.");
            Checks.That(Checks.Named<DataGrid>(window, "BandsGrid").Items.Count == 10, "The ten-band editor must expose every band.");
            Checks.Named<Button>(window, "StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Checks.That(Feedback(window).Contains("Choose a capture input", StringComparison.Ordinal), "Starting without explicit endpoints must be rejected.");
            NoAudio(window);

            var headroom = Checks.Named<TextBox>(window, "Headroom");
            FocusManager.SetFocusedElement(window, headroom); // Logical focus only; never activate the hidden HWND or send input.
            Checks.That(ReferenceEquals(FocusManager.GetFocusedElement(window), headroom), "Numeric field must accept logical focus.");
            headroom.Text = Checks.Number(-12);
            Checks.Named<Slider>(window, "Gain").Value = -9;
            Checks.Named<CheckBox>(window, "Mute").IsChecked = true;
            var band = Checks.Named<DataGrid>(window, "BandsGrid").Items[5];
            band.GetType().GetProperty("Gain")!.SetValue(band, Checks.Number(6));
            Checks.Named<CheckBox>(window, "ExpanderEnabled").IsChecked = true;
            await Checks.UntilAsync(() => StoredGain(preferences) == -9 && Feedback(window).StartsWith("DSP preview ready", StringComparison.Ordinal), "Controls did not reach the persisted preview.");
            var stored = ReadPreset(preferences);
            Checks.That(stored.HeadroomDb == -12 && stored.Mute && stored.Bands[5].GainDb == 6 && stored.NoiseAttenuation.Enabled,
                "A valid preset must preserve independent gain, mute, EQ and dynamics controls.");
            Checks.That(stored.InputChannel == 1 && stored.OutputCeiling == .7 && stored.NoiseSuppression.Enabled
                && stored.NoiseSuppression.NoiseFloorDb == -60 && stored.Stereo.Width == .8 && stored.Stereo.Crossfeed == .2,
                "Editing basic controls must preserve advanced input, ceiling, noise and stereo settings.");
            byte[] lastValid = File.ReadAllBytes(preferences);

            foreach (string invalid in new[] { "not-a-number", "NaN", "100" })
            {
                headroom.Text = invalid;
                await Checks.UntilAsync(() => Feedback(window).StartsWith("Controls not applied:", StringComparison.Ordinal), "Invalid numeric controls were accepted.");
                Checks.That(File.ReadAllBytes(preferences).SequenceEqual(lastValid), "Rejected numeric edits must not overwrite the last valid preset.");
                headroom.Text = Checks.Number(-12);
                await PreviewReadyAsync(window);
            }
            Checks.Render(window, renderDirectory, "engine-valid-controls.png");
            Checks.Named<ScrollViewer>(window, "ControlsScroll").ScrollToEnd(); window.UpdateLayout();
            Checks.Render(window, renderDirectory, "engine-dynamics.png");
            NoAudio(window);
        }
        finally
        {
            await Checks.CloseAsync(window);
            Checks.That(!worker.IsAlive, "Closing must join the owned MTA worker.");
        }

        // Real constructor/load path, not only the serialization helper.
        var reopened = Create(preferences);
        var reopenedWorker = Checks.Field<Thread>(reopened, "worker");
        try
        {
            reopened.Show(); await PreviewReadyAsync(reopened);
            Checks.That(Checks.Named<Slider>(reopened, "Gain").Value == -9 && Checks.Named<TextBox>(reopened, "Headroom").Text == Checks.Number(-12)
                && Checks.Named<CheckBox>(reopened, "Mute").IsChecked == true && Checks.Named<CheckBox>(reopened, "ExpanderEnabled").IsChecked == true,
                "A reopened workspace must load nondefault controls instead of silently falling back.");
            var restoredBand = Checks.Named<DataGrid>(reopened, "BandsGrid").Items[5];
            Checks.That((string?)restoredBand.GetType().GetProperty("Gain")!.GetValue(restoredBand) == Checks.Number(6), "A reopened workspace must restore the edited EQ band.");
            Checks.Click(reopened, "Reset DSP");
            await Checks.UntilAsync(() => StoredGain(preferences) == 0 && !Checks.Named<CheckBox>(reopened, "Mute").IsChecked.GetValueOrDefault(), "Explicit reset did not restore defaults.",
                () => FormattableString.Invariant($"storedGain={(StoredGain(preferences) is double g ? g : double.NaN)}, gainSlider={Checks.Named<Slider>(reopened, "Gain").Value}") + $", mute={Checks.Named<CheckBox>(reopened, "Mute").IsChecked}, feedback='{Feedback(reopened)}'");
            var reset = ReadPreset(preferences);
            Checks.That(reset.HeadroomDb == -6 && reset.Bands.All(b => b.GainDb == 0) && !reset.NoiseAttenuation.Enabled
                && reset.InputChannel is null && !reset.NoiseSuppression.Enabled && !reset.Stereo.Enabled && reset.OutputCeiling == .98,
                "Reset must restore all processing defaults, not only the visible gain slider.");
            NoAudio(reopened);
        }
        finally { await Checks.CloseAsync(reopened); Checks.That(!reopenedWorker.IsAlive, "Reopened worker leaked after close."); }
    }

    public static async Task RejectedPreferencesAsync(string workingDirectory, bool malformed)
    {
        string path = Path.Combine(workingDirectory, malformed ? "malformed.json" : "future-version.json");
        byte[] original = Encoding.UTF8.GetBytes(malformed ? "{ malformed-user-file\r\n" : "{\"SchemaVersion\":99,\"Settings\":{}}\r\n");
        File.WriteAllBytes(path, original);
        var window = Create(path);
        var worker = Checks.Field<Thread>(window, "worker");
        try
        {
            window.Show();
            await Checks.UntilAsync(() => Feedback(window).Contains("rejected and preserved", StringComparison.Ordinal), "Rejected saved preferences were not clearly reported.");
            Checks.That(File.ReadAllBytes(path).SequenceEqual(original), "Loading rejected preferences must preserve their exact bytes.");
            Checks.Named<Slider>(window, "Gain").Value = -3;
            await Checks.UntilAsync(() => Feedback(window).Contains("Rejected saved preferences were preserved", StringComparison.Ordinal), "Preview edits lost the preservation notice.");
            Checks.That(File.ReadAllBytes(path).SequenceEqual(original), "Previewing fallback controls must not overwrite rejected preferences.");
            NoAudio(window);
            if (!malformed)
            {
                Checks.Click(window, "Reset DSP");
                await Checks.UntilAsync(() => StoredGain(path) == 0, "Explicit reset must be able to replace rejected preferences.");
                Checks.That(ReadPreset(path).Bands.Count == 10, "Explicit replacement must write a complete current-version preset.");
            }
        }
        finally { await Checks.CloseAsync(window); Checks.That(!worker.IsAlive, "Rejected-preference workspace leaked its worker."); }
        if (malformed) Checks.That(File.ReadAllBytes(path).SequenceEqual(original), "Closing must not alter a rejected file.");
    }

    private static EngineWindow Create(string preferences)
    {
        var window = new EngineWindow(preferences);
        Checks.PrepareOffscreen(window);
        return window;
    }

    private static string Feedback(EngineWindow window) => Checks.Named<TextBlock>(window, "Feedback").Text;
    private static Task PreviewReadyAsync(EngineWindow window) => Checks.UntilAsync(
        () => Feedback(window).StartsWith("DSP preview ready", StringComparison.Ordinal), "Independent controls did not become ready.");

    private static void NoAudio(EngineWindow window)
    {
        Checks.That(Checks.Named<TextBlock>(window, "RunState").Text == "STOPPED", "The fixture must remain stopped.");
        foreach (string field in new[] { "bridge", "recorder", "replay" })
            Checks.That(Checks.FieldIsNull(window, field), $"The read-only fixture unexpectedly created '{field}'.");
    }

    private static DspSettings ReadPreset(string path)
    {
        // File dialogs are not driven by automation. This invokes the exact production decoder,
        // while all autosave/reopen/reset checks above go through the real window behavior.
        var method = typeof(EngineWindow).GetMethod("ReadPreset", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Production preset decoder was renamed; update the documented test seam.");
        try { return (DspSettings)method.Invoke(null, [path])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static double? StoredGain(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.GetProperty("SchemaVersion").GetInt32() != 1) return null;
            return document.RootElement.GetProperty("Settings").GetProperty("GainDb").GetDouble();
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException) { return null; }
    }
}
