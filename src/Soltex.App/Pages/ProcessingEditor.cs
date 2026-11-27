using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Soltex.App.Controls;
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
    private EqEditor curve = null!;
    private ComboBox bandChoice = null!, bandType = null!;
    private CheckBox bandOn = null!;
    private Slider bandFrequency = null!, bandGain = null!, bandQ = null!;
    private TextBlock bandFrequencyText = null!, bandGainText = null!, bandQText = null!;
    private bool syncingBand;
    private CheckBox gateOn = null!, expanderOn = null!, suppressionOn = null!, eqOn = null!, compressorOn = null!, stereoOn = null!, mono = null!, swap = null!;
    private CheckBox smoothResidual = null!, limiter = null!, neuralOn = null!;
    private Slider neuralStrength = null!;
    /// <summary>The Strength slider's last notch: the network's full result (no cap on how far noise is turned down).</summary>
    private const double FullStrength = 48;
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

        // The sections follow the signal: noise removal, then tone, then dynamics, then the output.
        StackPanel neural = Section("AI noise removal", s.NeuralNoiseRemoval.Enabled, microphone
            ? "Removes what isn't your voice (keyboards, mouse clicks, fans, hum, traffic) as you talk, using RNNoise, a neural network trained on speech by Xiph. Adds about 30 ms."
            : "Removes what isn't a voice from this channel, using RNNoise, a neural network trained on speech by Xiph. Made for voices, such as Chat; it would strip music and game sound. Adds about 30 ms.",
            out neuralOn);
        neuralStrength = AddSlider(neural, "Strength", Math.Min(s.NeuralNoiseRemoval.MaximumReductionDb, FullStrength), 6, FullStrength, 1,
            v => v >= FullStrength ? "Full" : v.ToString("0", CultureInfo.InvariantCulture) + " dB less noise");
        AutomationProperties.SetName(neuralStrength, "Noise removal strength");
        AutomationProperties.SetHelpText(neuralStrength, "How far noise is turned down. Full is the network's own result; lower keeps a little of the room for a more natural sound.");
        if (!PcmDspProcessor.NeuralNoiseRemovalAvailable)
        {
            // Off and greyed out, unless it was saved on (so it can still be turned off).
            TextBlock missing = new() { Text = "Not available: the noise removal library is missing from this installation. Reinstall Soltex to restore it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            missing.SetResourceReference(TextBlock.ForegroundProperty, "Studio.CautionBrush");
            neural.Children.Add(missing);
            if (!s.NeuralNoiseRemoval.Enabled) neuralOn.IsEnabled = false;
        }

        StackPanel suppression = Section("Background noise suppression", s.NoiseSuppression.Enabled,
            "Reduces steady noise such as fans and hum against a noise level you set. It can't pick out your voice, so it won't remove talking or music.", out suppressionOn);
        suppressionStrength = AddSlider(suppression, "Strength", s.NoiseSuppression.ReductionDb, 0, 36, 1, v => v.ToString("0", CultureInfo.InvariantCulture) + " dB less noise");
        suppressionFloor = AddSlider(suppression, "Noise level", s.NoiseSuppression.NoiseFloorDb, -90, -20, 1, Dbfs);
        smoothResidual = AddCheck(suppression, "Smooth background", s.NoiseSuppression.SmoothResidual, "Smoother background, slightly different sound.");

        StackPanel eq = Section("Equalizer", !s.EqBypass,
            "Drag a point to shape the tone: left and right is pitch, up and down is level. The mouse wheel makes a band wider or narrower, a double-click resets it and a right-click changes its shape.", out eqOn);
        BuildEqualizer(eq, s.Bands);

        StackPanel gate = Section("Noise gate", s.Gate.Enabled, "Closes between words so breathing and keyboard noise drop away. Too high a threshold cuts off quiet syllables.", out gateOn);
        gateThreshold = AddSlider(gate, "Opens above", s.Gate.ThresholdDb, -90, -10, 1, Dbfs);

        StackPanel expander = Section("Quiet-noise reduction", s.NoiseAttenuation.Enabled, "Turns down everything quieter than the threshold, more gently than a gate.", out expanderOn);
        expanderThreshold = AddSlider(expander, "Below", s.NoiseAttenuation.ThresholdDb, -90, -10, 1, Dbfs);
        expanderDepth = AddSlider(expander, "At most", s.NoiseAttenuation.MaximumAttenuationDb, 0, 60, 1, v => "−" + Db(v).TrimStart('+'));

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
            NeuralNoiseRemoval = s.NeuralNoiseRemoval with
            {
                Enabled = neuralOn.IsChecked == true,
                MaximumReductionDb = Pick(neuralStrength, s.NeuralNoiseRemoval.MaximumReductionDb, v => v >= FullStrength ? 100 : v)
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
            // Bands nobody touched are the saved records themselves, so they stay exactly as saved.
            Bands = curve.Bands.ToArray(),
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

    /// <summary>
    /// The curve plus the selected band's details. The curve is for the mouse; the details below are ordinary controls,
    /// so every band can also be set exactly, from the keyboard and with a screen reader.
    /// </summary>
    private void BuildEqualizer(Panel parent, IReadOnlyList<EqBand> saved)
    {
        curve = new EqEditor { Height = 220, Margin = new Thickness(0, 4, 0, 10) };
        curve.SetBands(saved);
        parent.Children.Add(curve);

        WrapPanel choices = new() { Margin = new Thickness(0, 0, 0, 4) };
        bandChoice = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetName(bandChoice, "Band");
        bandChoice.ItemsSource = Enumerable.Range(1, saved.Count).Select(i => "Band " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        bandType = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetName(bandType, "Shape");
        bandType.ItemsSource = Enum.GetValues<EqFilterType>().Select(t => new KeyValuePair<EqFilterType, string>(t, EqEditor.TypeName(t))).ToArray();
        bandType.DisplayMemberPath = "Value";
        bandType.SelectedValuePath = "Key";
        bandOn = new CheckBox { Content = "Band on", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 6) };
        AutomationProperties.SetName(bandOn, "Band on");
        Button reset = new() { Content = "Reset band", Margin = new Thickness(0, 0, 8, 6) };
        Button flat = new() { Content = "Flat", Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetHelpText(flat, "Every band back to no level change. Frequencies and shapes stay.");
        choices.Children.Add(bandChoice);
        choices.Children.Add(bandType);
        choices.Children.Add(bandOn);
        choices.Children.Add(reset);
        choices.Children.Add(flat);
        parent.Children.Add(choices);

        double octave = Math.Log10(2);
        bandFrequency = DetailSlider(parent, "Frequency", "Band frequency", Math.Log10(EqEditor.MinimumFrequency), Math.Log10(EqEditor.MaximumFrequency), octave / 12, octave / 3, out bandFrequencyText);
        bandGain = DetailSlider(parent, "Level", "Band level", -EqEditor.GainRange, EqEditor.GainRange, 0.5, 3, out bandGainText);
        bandQ = DetailSlider(parent, "Width (Q)", "Band width (Q)", Math.Log10(EqEditor.MinimumQ), Math.Log10(EqEditor.MaximumQ), Math.Log10(1.1), Math.Log10(1.5), out bandQText);
        AutomationProperties.SetHelpText(bandQ, "Higher Q is narrower.");

        curve.SelectionChanged += (_, _) => ShowBand();
        curve.BandsChanged += (_, _) => { ShowBand(); Raise(); };
        bandChoice.SelectionChanged += (_, _) => { if (!syncingBand && bandChoice.SelectedIndex >= 0) curve.SelectedIndex = bandChoice.SelectedIndex; };
        bandType.SelectionChanged += (_, _) => EditBand(b => bandType.SelectedValue is EqFilterType type ? b with { Type = type } : b);
        bandOn.Checked += (_, _) => EditBand(b => b with { Bypass = false });
        bandOn.Unchecked += (_, _) => EditBand(b => b with { Bypass = true });
        bandFrequency.ValueChanged += (_, e) => EditBand(b => b with { FrequencyHz = EqEditor.RoundFrequency(Math.Pow(10, e.NewValue)) });
        bandGain.ValueChanged += (_, e) => EditBand(b => b with { GainDb = Math.Round(e.NewValue, 1) });
        bandQ.ValueChanged += (_, e) => EditBand(b => b with { Q = Math.Round(Math.Pow(10, e.NewValue), 2) });
        reset.Click += (_, _) => EditBand(EqEditor.Reset);
        flat.Click += (_, _) =>
        {
            curve.SetBands(curve.Bands.Select(b => b.GainDb != 0 ? b with { GainDb = 0 } : b).ToArray());
            ShowBand();
            Raise();
        };
        // Start on the first band that does something, so the details show what this sound is about.
        int first = Math.Max(0, saved.ToList().FindIndex(b => !b.Bypass && (Math.Abs(b.GainDb) > 0.04 || !EqEditor.HasGain(b.Type))));
        curve.SelectedIndex = first;
        ShowBand();
    }

    private static Slider DetailSlider(Panel parent, string label, string automationName, double minimum, double maximum, double small, double large, out TextBlock readout)
    {
        Grid row = new() { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        TextBlock name = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(StyleProperty, "Shell.Body");
        Slider slider = new()
        {
            Minimum = minimum, Maximum = maximum, SmallChange = small, LargeChange = large,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
        };
        AutomationProperties.SetName(slider, automationName);
        readout = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        readout.SetResourceReference(StyleProperty, "Shell.Secondary");
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(readout, 2);
        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(readout);
        parent.Children.Add(row);
        return slider;
    }

    /// <summary>Shows the selected band in the detail controls without editing it.</summary>
    private void ShowBand()
    {
        int index = curve.SelectedIndex;
        EqBand band = curve.Bands[index];
        syncingBand = true;
        try
        {
            bandChoice.SelectedIndex = index;
            bandType.SelectedValue = band.Type;
            bandOn.IsChecked = !band.Bypass;
            bandFrequency.Value = Math.Log10(Math.Clamp(band.FrequencyHz, EqEditor.MinimumFrequency, EqEditor.MaximumFrequency));
            bandGain.Value = Math.Clamp(band.GainDb, -EqEditor.GainRange, EqEditor.GainRange);
            bandQ.Value = Math.Log10(Math.Clamp(band.Q, EqEditor.MinimumQ, EqEditor.MaximumQ));
            bool hasGain = EqEditor.HasGain(band.Type);
            bandGain.IsEnabled = hasGain;
            AutomationProperties.SetHelpText(bandGain, hasGain ? "" : "A " + EqEditor.TypeName(band.Type).ToLowerInvariant() + " has no level to adjust.");
            bandFrequencyText.Text = EqEditor.FrequencyText(band.FrequencyHz);
            bandGainText.Text = hasGain ? Db(band.GainDb) : "—";
            bandQText.Text = band.Q.ToString("0.00", CultureInfo.InvariantCulture);
            AutomationProperties.SetItemStatus(bandFrequency, bandFrequencyText.Text);
            AutomationProperties.SetItemStatus(bandGain, bandGainText.Text);
            AutomationProperties.SetItemStatus(bandQ, bandQText.Text);
        }
        finally { syncingBand = false; }
    }

    private void EditBand(Func<EqBand, EqBand> change)
    {
        if (syncingBand || loading) return;
        int index = curve.SelectedIndex;
        EqBand before = curve.Bands[index], after = change(before);
        if (after == before) return;
        curve.SetBand(index, after);
        ShowBand();
        Raise();
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

    private static string Db(double value) => MicrophonePath.Decibels(value);
    private static string Dbfs(double value) => value.ToString("0.#", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal) + " dBFS";
}

/// <summary>
/// A themed dialog around the processing editor. With a preview, every change is heard as it is made (the latest one
/// at most every 80 ms) and Cancel or closing puts the sound it opened with back; without one, nothing changes until Save.
/// </summary>
public sealed class ProcessingWindow : Window
{
    private readonly Func<DspSettings, Task>? preview;
    private readonly DspSettings opened;
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly TextBlock? previewNote;
    private Task previewing = Task.CompletedTask;
    private bool previewed, previewQueued;

    public ProcessingWindow(string title, DspSettings settings, bool microphone, Func<DspSettings, Task>? preview = null)
    {
        this.preview = preview;
        opened = settings;
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
        TextBlock heading = new() { Text = title, Margin = new Thickness(0, 0, 0, preview is null ? 12 : 2) };
        heading.SetResourceReference(StyleProperty, "Shell.SectionTitle");
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        if (preview is not null)
        {
            previewNote = new TextBlock { Text = LiveText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            previewNote.SetResourceReference(StyleProperty, "Shell.Secondary");
            AutomationProperties.SetLiveSetting(previewNote, AutomationLiveSetting.Polite);
            DockPanel.SetDock(previewNote, Dock.Top);
            root.Children.Add(previewNote);
            Editor.Changed += (_, _) => { previewTimer.Stop(); previewTimer.Start(); };
            previewTimer.Tick += (_, _) => { previewTimer.Stop(); QueuePreview(); };
            Closed += (_, _) => { previewTimer.Stop(); if (DialogResult != true && previewed) _ = RevertAsync(); };
        }
        root.Children.Add(new ScrollViewer { Content = Editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
        Content = root;
    }

    private const string LiveText = "You hear each change as you make it while audio is on. Cancel puts the old sound back.";

    public ProcessingEditor Editor { get; }
    public DspSettings? Result { get; private set; }

    /// <summary>The preview that is running now; completes when the helper has the latest change.</summary>
    internal Task Previewing => previewing;

    /// <summary>Sends the editor's current sound; while one is on its way, only the newest waiting change follows it.</summary>
    private void QueuePreview()
    {
        if (preview is null) return;
        if (!previewing.IsCompleted) { previewQueued = true; return; }
        previewing = SendPreviewsAsync();
    }

    private async Task SendPreviewsAsync()
    {
        do
        {
            previewQueued = false;
            previewed = true;
            try
            {
                await preview!(Editor.Result());
                if (previewNote is not null) previewNote.Text = LiveText;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException)
            {
                if (previewNote is not null) previewNote.Text = "Not playing live right now (" + error.Message.TrimEnd('.') + "). Save still keeps your changes.";
            }
        }
        while (previewQueued);
    }

    /// <summary>After Cancel: waits for any preview on its way, then puts the opening sound back.</summary>
    private async Task RevertAsync()
    {
        previewQueued = false;
        try { await previewing; } catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException) { }
        try { await preview!(opened); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or OperationCanceledException or TimeoutException) { }
    }
}
