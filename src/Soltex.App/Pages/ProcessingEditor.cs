using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Soltex.Audio.Engine;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>
/// The one processing editor for every channel (microphone and playback). Edits the helper's DspSettings in plain
/// language. Values the person does not touch are kept exactly as saved, so presets stay recognizable.
/// </summary>
public sealed class ProcessingEditor : UserControl
{
    private readonly bool microphone;
    private readonly StackPanel sections = new();
    private readonly List<(Slider Slider, double Initial)> sliders = [];
    private DspSettings original = new();
    private ComboBox? preset;
    private Slider gain = null!, gateThreshold = null!, expanderThreshold = null!, expanderDepth = null!, suppressionStrength = null!, suppressionFloor = null!;
    private Slider compressorThreshold = null!, compressorRatio = null!, compressorMakeup = null!, width = null!, pan = null!, crossfeed = null!, ceiling = null!;
    private Slider[] bands = [];
    private CheckBox gateOn = null!, expanderOn = null!, suppressionOn = null!, eqOn = null!, compressorOn = null!, stereoOn = null!, mono = null!, swap = null!;
    private CheckBox smoothResidual = null!, limiter = null!;
    private bool loading;

    public ProcessingEditor(DspSettings settings, bool microphone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.microphone = microphone;
        Resources.Add(typeof(CheckBox), SquareCheckBoxStyle(TryFindResource(typeof(CheckBox)) as Style));
        Content = sections;
        Load(settings);
    }

    /// <summary>
    /// The app's 8 px control radius turns a check box into a circle that reads as a radio button. Check boxes keep
    /// the app's style with a 4 px box.
    /// </summary>
    internal static Style SquareCheckBoxStyle(Style? basedOn)
    {
        Style style = new(typeof(CheckBox), basedOn);
        style.Resources.Add("ControlCornerRadius", new CornerRadius(4));
        style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(4)));
        return style;
    }

    /// <summary>Raised when any value changes.</summary>
    public event EventHandler? Changed;

    public bool IsMicrophone => microphone;
    public ComboBox? PresetChoice => preset;

    public void Load(DspSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        loading = true;
        try
        {
            original = settings;
            sliders.Clear();
            sections.Children.Clear();
            if (microphone) AddPresetRow();
            Build();
        }
        finally { loading = false; }
    }

    private void AddPresetRow()
    {
        StackPanel row = Section("Start from a preset", null, "Replaces everything below. Your input choice and level stay.", out _);
        preset = new ComboBox { DisplayMemberPath = nameof(MicrophoneSoundChoice.Title), MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(preset, "Start from a preset");
        MicrophoneSound current = MicrophoneSetup.Identify(original);
        List<MicrophoneSoundChoice> choices = [.. MicrophoneSetup.Sounds];
        if (current == MicrophoneSound.Custom) choices.Add(MicrophoneSetup.Custom);
        preset.ItemsSource = choices;
        preset.SelectedItem = choices.First(c => c.Sound == current);
        preset.SelectionChanged += (_, _) =>
        {
            if (loading || preset.SelectedItem is not MicrophoneSoundChoice { Sound: not MicrophoneSound.Custom } chosen) return;
            bool hadFocus = preset.IsKeyboardFocusWithin;
            Load(MicrophoneSetup.ApplySound(chosen.Sound, Result()));
            Changed?.Invoke(this, EventArgs.Empty);
            // Load rebuilds every control, including this list. Keep keyboard focus on the new list so arrow keys
            // can keep stepping through presets.
            if (hadFocus) Dispatcher.BeginInvoke(() => preset?.Focus(), DispatcherPriority.Input);
        };
        row.Children.Add(preset);
    }

    private void Build()
    {
        DspSettings s = original;
        StackPanel level = Section("Level", null, "Raises or lowers this channel before any other processing.", out _);
        gain = AddSlider(level, "Level", s.GainDb, -24, 24, 0.5, Db);

        StackPanel gate = Section("Noise gate", s.Gate.Enabled, "Closes between words so breathing and keyboard noise drop away. Too high a threshold cuts off quiet syllables.", out gateOn);
        gateThreshold = AddSlider(gate, "Opens above", s.Gate.ThresholdDb, -90, -10, 1, Dbfs);

        StackPanel expander = Section("Quiet-noise reduction", s.NoiseAttenuation.Enabled, "Turns down everything quieter than the threshold, more gently than a gate.", out expanderOn);
        expanderThreshold = AddSlider(expander, "Below", s.NoiseAttenuation.ThresholdDb, -90, -10, 1, Dbfs);
        expanderDepth = AddSlider(expander, "At most", s.NoiseAttenuation.MaximumAttenuationDb, 0, 60, 1, v => "−" + Db(v).TrimStart('+'));

        StackPanel suppression = Section("Background noise suppression", s.NoiseSuppression.Enabled,
            "Reduces steady noise such as fans and hum against a noise level you set. It can't pick out your voice, so it won't remove talking or music.", out suppressionOn);
        suppressionStrength = AddSlider(suppression, "Strength", s.NoiseSuppression.ReductionDb, 0, 36, 1, v => v.ToString("0", CultureInfo.InvariantCulture) + " dB less noise");
        suppressionFloor = AddSlider(suppression, "Noise level", s.NoiseSuppression.NoiseFloorDb, -90, -20, 1, Dbfs);
        smoothResidual = AddCheck(suppression, "Smooth background", s.NoiseSuppression.SmoothResidual, "Smoother background, slightly different sound.");

        StackPanel eq = Section("Equalizer", !s.EqBypass, "Shapes the tone. Small changes go a long way.", out eqOn);
        bands = new Slider[s.Bands.Count];
        for (int i = 0; i < s.Bands.Count; i++)
        {
            EqBand band = s.Bands[i];
            bool shapes = band.Type is EqFilterType.HighPass or EqFilterType.LowPass or EqFilterType.Notch;
            bands[i] = AddSlider(eq, BandName(band), band.GainDb, -12, 12, 0.5, Db);
            if (shapes) { bands[i].IsEnabled = false; AutomationProperties.SetHelpText(bands[i], "This band is a filter with no level to adjust."); }
        }

        StackPanel compressor = Section("Compressor", s.Compressor.Enabled, "Evens out loud and soft words so you sit at a steady level.", out compressorOn);
        compressorThreshold = AddSlider(compressor, "Starts at", s.Compressor.ThresholdDb, -60, 0, 1, Dbfs);
        compressorRatio = AddSlider(compressor, "Ratio", s.Compressor.Ratio, 1, 20, 0.5, v => v.ToString("0.#", CultureInfo.InvariantCulture) + " : 1");
        compressorMakeup = AddSlider(compressor, "Make-up", s.Compressor.MakeupDb, 0, 24, 0.5, Db);

        if (!microphone)
        {
            StackPanel stereo = Section("Stereo", s.Stereo.Enabled, "Width, balance and headphone crossfeed. Not surround or spatial audio.", out stereoOn);
            width = AddSlider(stereo, "Width", s.Stereo.Width * 100, 0, 200, 5, v => v.ToString("0", CultureInfo.InvariantCulture) + " %");
            pan = AddSlider(stereo, "Balance", s.Stereo.Pan * 100, -100, 100, 5, v => v == 0 ? "Center" : (v < 0 ? "Left " : "Right ") + Math.Abs(v).ToString("0", CultureInfo.InvariantCulture));
            crossfeed = AddSlider(stereo, "Crossfeed", s.Stereo.Crossfeed * 100, 0, 100, 5, v => v.ToString("0", CultureInfo.InvariantCulture) + " %");
            mono = AddCheck(stereo, "Mono", s.Stereo.Mono);
            swap = AddCheck(stereo, "Swap left and right", s.Stereo.SwapChannels);
        }

        StackPanel output = Section("Clipping guard", null, "The last stage. Nothing leaves above this level, so the output never distorts digitally.", out _);
        ceiling = AddSlider(output, "Ceiling", 20 * Math.Log10(Math.Clamp(s.OutputCeiling, 0.01, 1)), -12, 0, 0.1, Dbfs);
        // Not part of a preset's sound: switching it does not turn the preset into Custom.
        limiter = AddCheck(output, "Safety limiter", s.Limiter.Enabled, "Catches peaks above the ceiling. Adds 1.6 ms.", changesSound: false);
    }

    /// <summary>The edited settings. Untouched controls keep their saved values exactly.</summary>
    public DspSettings Result()
    {
        DspSettings s = original;
        DspSettings result = s with
        {
            GainDb = Pick(gain, s.GainDb, v => v),
            Gate = s.Gate with { Enabled = gateOn.IsChecked == true, ThresholdDb = Pick(gateThreshold, s.Gate.ThresholdDb, v => v) },
            NoiseAttenuation = s.NoiseAttenuation with
            {
                Enabled = expanderOn.IsChecked == true,
                ThresholdDb = Pick(expanderThreshold, s.NoiseAttenuation.ThresholdDb, v => v),
                MaximumAttenuationDb = Pick(expanderDepth, s.NoiseAttenuation.MaximumAttenuationDb, v => v)
            },
            NoiseSuppression = s.NoiseSuppression with
            {
                Enabled = suppressionOn.IsChecked == true,
                ReductionDb = Pick(suppressionStrength, s.NoiseSuppression.ReductionDb, v => v),
                NoiseFloorDb = Pick(suppressionFloor, s.NoiseSuppression.NoiseFloorDb, v => v),
                SmoothResidual = smoothResidual.IsChecked == true
            },
            Limiter = s.Limiter with { Enabled = limiter.IsChecked == true },
            EqBypass = eqOn.IsChecked != true,
            Bands = s.Bands.Select((band, i) => band with { GainDb = Pick(bands[i], band.GainDb, v => v) }).ToArray(),
            Compressor = s.Compressor with
            {
                Enabled = compressorOn.IsChecked == true,
                ThresholdDb = Pick(compressorThreshold, s.Compressor.ThresholdDb, v => v),
                Ratio = Pick(compressorRatio, s.Compressor.Ratio, v => v),
                MakeupDb = Pick(compressorMakeup, s.Compressor.MakeupDb, v => v)
            },
            OutputCeiling = Pick(ceiling, s.OutputCeiling, v => Math.Round(Math.Pow(10, v / 20), 4))
        };
        if (!microphone)
        {
            result = result with
            {
                Stereo = s.Stereo with
                {
                    Enabled = stereoOn.IsChecked == true,
                    Width = Pick(width, s.Stereo.Width, v => v / 100),
                    Pan = Pick(pan, s.Stereo.Pan, v => v / 100),
                    Crossfeed = Pick(crossfeed, s.Stereo.Crossfeed, v => v / 100),
                    Mono = mono.IsChecked == true,
                    SwapChannels = swap.IsChecked == true
                }
            };
        }
        return result;
    }

    private double Pick(Slider slider, double saved, Func<double, double> convert)
    {
        (Slider _, double initial) = sliders.First(s => ReferenceEquals(s.Slider, slider));
        return Math.Abs(slider.Value - initial) < 1e-9 ? saved : convert(slider.Value);
    }

    private StackPanel Section(string title, bool? enabled, string description, out CheckBox toggle)
    {
        Border card = new() { Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(StyleProperty, "Shell.Card");
        AutomationProperties.SetName(card, title);
        StackPanel panel = new();
        card.Child = panel;
        DockPanel header = new() { Margin = new Thickness(0, 0, 0, 2) };
        toggle = new CheckBox { Content = "On", IsChecked = enabled == true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, title + " on");
        if (enabled is not null) { DockPanel.SetDock(toggle, Dock.Right); header.Children.Add(toggle); }
        TextBlock heading = new() { Text = title };
        heading.SetResourceReference(StyleProperty, "Shell.CardTitle");
        header.Children.Add(heading);
        panel.Children.Add(header);
        TextBlock text = new() { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        text.SetResourceReference(StyleProperty, "Shell.Secondary");
        panel.Children.Add(text);
        StackPanel body = new();
        panel.Children.Add(body);
        if (enabled is not null)
        {
            CheckBox owner = toggle;
            body.IsEnabled = enabled.Value;
            owner.Checked += (_, _) => { body.IsEnabled = true; Raise(); };
            owner.Unchecked += (_, _) => { body.IsEnabled = false; Raise(); };
        }
        sections.Children.Add(card);
        return body;
    }

    private Slider AddSlider(Panel parent, string label, double value, double minimum, double maximum, double step, Func<double, string> format)
    {
        Grid row = new() { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Shell.Body");
        double shown = Math.Clamp(Math.Round(value / step) * step, minimum, maximum);
        Slider slider = new()
        {
            Minimum = minimum, Maximum = maximum, Value = shown, SmallChange = step, LargeChange = step * 4,
            TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
        };
        AutomationProperties.SetName(slider, label);
        TextBlock readout = new() { Text = format(shown), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        readout.SetResourceReference(StyleProperty, "Shell.Secondary");
        slider.ValueChanged += (_, e) => { readout.Text = format(e.NewValue); AutomationProperties.SetItemStatus(slider, readout.Text); Raise(); };
        AutomationProperties.SetItemStatus(slider, readout.Text);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(readout);
        parent.Children.Add(row);
        sliders.Add((slider, shown));
        return slider;
    }

    private CheckBox AddCheck(Panel parent, string label, bool value, string? note = null, bool changesSound = true)
    {
        CheckBox box = new() { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetName(box, label);
        Action changed = changesSound ? Raise : () => { if (!loading) Changed?.Invoke(this, EventArgs.Empty); };
        box.Checked += (_, _) => changed();
        box.Unchecked += (_, _) => changed();
        parent.Children.Add(box);
        if (note is not null)
        {
            // A short, honest note under the switch, also read by screen readers.
            AutomationProperties.SetHelpText(box, note);
            TextBlock text = new() { Text = note, Margin = new Thickness(28, 0, 0, 2) };
            text.SetResourceReference(StyleProperty, "Shell.Secondary");
            text.FontSize = 12;
            parent.Children.Add(text);
        }
        return box;
    }

    private void Raise()
    {
        if (loading) return;
        if (preset is not null && preset.SelectedItem is MicrophoneSoundChoice { Sound: not MicrophoneSound.Custom })
        {
            // A hand edit leaves the preset; show Custom so the label never claims a preset that no longer applies.
            loading = true;
            try
            {
                if (preset.ItemsSource is List<MicrophoneSoundChoice> choices && !choices.Contains(MicrophoneSetup.Custom)) { choices.Add(MicrophoneSetup.Custom); preset.Items.Refresh(); }
                preset.SelectedItem = MicrophoneSetup.Custom;
            }
            finally { loading = false; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string BandName(EqBand band)
    {
        string frequency = band.FrequencyHz >= 1000
            ? (band.FrequencyHz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz"
            : band.FrequencyHz.ToString("0", CultureInfo.InvariantCulture) + " Hz";
        return band.Type switch
        {
            EqFilterType.HighPass => "Low cut " + frequency,
            EqFilterType.LowPass => "High cut " + frequency,
            EqFilterType.Notch => "Notch " + frequency,
            EqFilterType.LowShelf => "Bass " + frequency,
            EqFilterType.HighShelf => "Treble " + frequency,
            _ => frequency
        };
    }

    private static string Db(double value) => MicrophonePath.Decibels(value);
    private static string Dbfs(double value) => value.ToString("0.#", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal) + " dBFS";
}

/// <summary>A themed dialog around the processing editor. Nothing is applied until Save.</summary>
public sealed class ProcessingWindow : Window
{
    public ProcessingWindow(string title, DspSettings settings, bool microphone)
    {
        Title = title;
        Width = 720; Height = 780; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Editor = new ProcessingEditor(settings, microphone);
        DockPanel root = new() { Margin = new Thickness(24, 20, 24, 20) };
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        Button save = new() { Content = "Save", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        save.SetResourceReference(StyleProperty, "AccentButtonStyle");
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 110 };
        save.Click += (_, _) => { Result = Editor.Result(); DialogResult = true; };
        footer.Children.Add(save);
        footer.Children.Add(cancel);
        root.Children.Add(footer);
        TextBlock heading = new() { Text = title, Margin = new Thickness(0, 0, 0, 12) };
        heading.SetResourceReference(StyleProperty, "Shell.SectionTitle");
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        root.Children.Add(new ScrollViewer { Content = Editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        Content = root;
    }

    public ProcessingEditor Editor { get; }
    public DspSettings? Result { get; private set; }
}
