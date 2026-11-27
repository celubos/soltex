using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.Audio.Engine;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

/// <summary>The channel processing window keeps the safety limiter and the smooth background through every edit.</summary>
internal static class DspWindowChecks
{
    public static void Run(Action<bool, string> check)
    {
        DspSettings saved = new()
        {
            OutputCeiling = 0.89,
            NoiseSuppression = new SpectralNoiseSuppressionSettings(Enabled: true, NoiseFloorDb: -55, ReductionDb: 12, SmoothResidual: true),
            // Fields the window does not show must survive too.
            Limiter = new SafetyLimiterSettings(Enabled: true, ReleaseMs: 300, TruePeak: false)
        };
        var window = new IndependentMixerDspWindow("Game", saved);
        try
        {
            DspSettings untouched = window.Read();
            check(untouched == saved with { Bands = untouched.Bands } && untouched.Bands.SequenceEqual(saved.Bands),
                "opening and reading the processing window without edits keeps every setting exactly");
            check(Switch(window, "Safety limiter").IsChecked == true && Switch(window, "Smooth background").IsChecked == true,
                "the processing window shows the saved limiter and smooth background switches");
            check(AutomationProperties.GetHelpText(Switch(window, "Safety limiter")).Contains("1.6 ms", StringComparison.Ordinal) &&
                AutomationProperties.GetHelpText(Switch(window, "Smooth background")).Contains("slightly different sound", StringComparison.Ordinal),
                "both switches carry their short note for screen readers");

            Field(window, "Reduction dB").Text = "20";
            Field(window, "Threshold dB").Text = "-30";
            DspSettings edited = window.Read();
            check(edited.NoiseSuppression is { ReductionDb: 20, SmoothResidual: true, NoiseFloorDb: -55 } && edited.Limiter == saved.Limiter,
                "editing other values keeps smooth background and the limiter (with its unseen release and true-peak settings)");

            Switch(window, "Smooth background").IsChecked = false;
            Switch(window, "Safety limiter").IsChecked = false;
            DspSettings switchedOff = window.Read();
            check(!switchedOff.NoiseSuppression.SmoothResidual && switchedOff.NoiseSuppression.ReductionDb == 20 &&
                switchedOff.Limiter is { Enabled: false, ReleaseMs: 300, TruePeak: false },
                "turning both switches off changes only those two settings");
            _ = new PcmDspProcessor(48000, 2, switchedOff);
            check(true, "the edited settings pass the engine's own validation");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject below in Logical(node)) yield return below;
        }
    }

    private static CheckBox Switch(Window window, string label) =>
        Logical(window).OfType<CheckBox>().Single(box => Equals(box.Content, label));

    private static TextBox Field(Window window, string name) =>
        Logical(window).OfType<TextBox>().First(box => AutomationProperties.GetName(box) == name);
}
