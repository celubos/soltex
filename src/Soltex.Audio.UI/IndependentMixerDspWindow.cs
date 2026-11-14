using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Soltex.Audio.Engine;

namespace Soltex.Audio.UI;

internal sealed class IndependentMixerDspWindow : Window
{
    private readonly ObservableCollection<BandEdit> bands;
    private readonly Dictionary<string, TextBox> fields = [];
    private readonly Dictionary<string, CheckBox> switches = [];
    private readonly EngineResponseGraph graph = new() { Height = 135, Margin = new Thickness(0, 10, 0, 12) };
    private readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid grid;
    public DspSettings Settings { get; private set; }
    internal IndependentMixerDspWindow(string title, DspSettings settings)
    {
        Settings = settings; Title = title + " · processing"; Width = 850; Height = 830; MinWidth = 650; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AudioTheme.Apply(this); SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        DockPanel root = new() { Margin = new Thickness(20) }; root.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush"); Content = root;
        StackPanel footer = new() { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(feedback);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        Button preview = new() { Content = "Preview response", Margin = new Thickness(0, 0, 8, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        Button apply = new() { Content = "Apply processing", IsDefault = true };
        buttons.Children.Add(preview); buttons.Children.Add(cancel); buttons.Children.Add(apply); footer.Children.Add(buttons);
        StackPanel body = new(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        body.Children.Add(new TextBlock { Text = title + " processing", FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "Ten-band EQ, gate, compressor, downward expansion and headroom. Starting audio validates the actual device format.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) });
        WrapPanel basic = new(); body.Children.Add(basic);
        AddField(basic, "Headroom", "Headroom dB", settings.HeadroomDb);
        AddField(basic, "Gain", "Processing gain dB", settings.GainDb);
        AddField(basic, "Ceiling", "Output ceiling", settings.OutputCeiling);
        AddSwitch(basic, "EqBypass", "Bypass equalizer", settings.EqBypass);
        AddSwitch(basic, "Mute", "Mute processing", settings.Mute);
        body.Children.Add(graph);
        bands = new(settings.Bands.Select((band, index) => new BandEdit(index + 1, band)));
        grid = new() { ItemsSource = bands, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 250, RowHeight = 27, HeadersVisibility = DataGridHeadersVisibility.Column };
        grid.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush"); grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        Style rowStyle = new(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SurfaceBrush")));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TextBrush"))); grid.RowStyle = rowStyle;
        Style cellStyle = new(typeof(DataGridCell)); cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TextBrush")));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent)); grid.CellStyle = cellStyle;
        Style headerStyle = new(typeof(DataGridColumnHeader)); headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("RaisedBrush")));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, FindResource("TextBrush"))); headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5))); grid.ColumnHeaderStyle = headerStyle;
        grid.Columns.Add(new DataGridTextColumn { Header = "Band", Binding = new Binding(nameof(BandEdit.Number)), IsReadOnly = true, Width = 45 });
        foreach ((string name, string label) in new[] { (nameof(BandEdit.Frequency), "Frequency Hz"), (nameof(BandEdit.Gain), "Gain dB"), (nameof(BandEdit.Q), "Q") })
            grid.Columns.Add(new DataGridTextColumn { Header = label, Binding = new Binding(name) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, MinWidth = 100, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridComboBoxColumn { Header = "Filter", SelectedItemBinding = new Binding(nameof(BandEdit.Type)), ItemsSource = Enum.GetValues<EqFilterType>(), Width = 130 });
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Bypass", Binding = new Binding(nameof(BandEdit.Bypass)), Width = 65 });
        body.Children.Add(grid);
        WrapPanel gate = Section(body, "Gate", settings.Gate.Enabled);
        AddField(gate, "GateThreshold", "Threshold dB", settings.Gate.ThresholdDb); AddField(gate, "GateHysteresis", "Hysteresis dB", settings.Gate.HysteresisDb);
        AddField(gate, "GateAttack", "Attack ms", settings.Gate.AttackMs); AddField(gate, "GateHold", "Hold ms", settings.Gate.HoldMs);
        AddField(gate, "GateRelease", "Release ms", settings.Gate.ReleaseMs); AddField(gate, "GateFloor", "Closed gain dB", settings.Gate.ClosedGainDb);
        WrapPanel compressor = Section(body, "Compressor", settings.Compressor.Enabled);
        AddField(compressor, "CompThreshold", "Threshold dB", settings.Compressor.ThresholdDb); AddField(compressor, "CompRatio", "Ratio", settings.Compressor.Ratio);
        AddField(compressor, "CompAttack", "Attack ms", settings.Compressor.AttackMs); AddField(compressor, "CompRelease", "Release ms", settings.Compressor.ReleaseMs); AddField(compressor, "CompMakeup", "Makeup dB", settings.Compressor.MakeupDb);
        WrapPanel expander = Section(body, "Downward expander", settings.NoiseAttenuation.Enabled);
        AddField(expander, "ExpThreshold", "Noise floor dB", settings.NoiseAttenuation.ThresholdDb); AddField(expander, "ExpRatio", "Ratio", settings.NoiseAttenuation.Ratio);
        AddField(expander, "ExpMaximum", "Max reduction dB", settings.NoiseAttenuation.MaximumAttenuationDb); AddField(expander, "ExpAttack", "Attack ms", settings.NoiseAttenuation.AttackMs); AddField(expander, "ExpRelease", "Release ms", settings.NoiseAttenuation.ReleaseMs);
        body.Children.Add(new TextBlock { Text = "The expander attenuates quiet passages; it does not identify or remove noise from speech.", Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        WrapPanel suppression = Section(body, "Spectral noise suppression", settings.NoiseSuppression.Enabled);
        AddField(suppression, "NoiseFloor", "Noise floor dB", settings.NoiseSuppression.NoiseFloorDb);
        AddField(suppression, "NoiseReduction", "Reduction dB", settings.NoiseSuppression.ReductionDb);
        body.Children.Add(new TextBlock { Text = "Stationary noise reduction with a manual noise floor; adds about 20–40 ms of algorithm delay when enabled.", TextWrapping = TextWrapping.Wrap });
        switches["Spectral noise suppression"].ToolTip = "Enabling or disabling changes algorithm delay and may cause a short transition. Noise floor/reduction edits preserve buffered audio.";
        WrapPanel stereo = Section(body, "Stereo", settings.Stereo.Enabled);
        switches["Stereo"].Content = "Stereo (two-channel sources only)";
        switches["Stereo"].ToolTip = "No effect on mono input. Settings remain saved when reusing this profile on a stereo source.";
        AddField(stereo, "Width", "Width (0–2)", settings.Stereo.Width); AddField(stereo, "Pan", "Pan (−1 to 1)", settings.Stereo.Pan);
        AddField(stereo, "Crossfeed", "Crossfeed (0–1)", settings.Stereo.Crossfeed); AddSwitch(stereo, "Swap", "Swap channels", settings.Stereo.SwapChannels); AddSwitch(stereo, "Mono", "Mono", settings.Stereo.Mono);
        preview.Click += (_, _) => Validate(false); apply.Click += (_, _) => Validate(true);
        graph.SetResponse(settings, PreviewRate(settings));
    }
    private WrapPanel Section(StackPanel body, string name, bool enabled)
    {
        WrapPanel panel = new() { Margin = new Thickness(0, 15, 0, 0) }; body.Children.Add(panel);
        AddSwitch(panel, name, name, enabled); return panel;
    }
    private void AddSwitch(Panel parent, string key, string label, bool value)
    {
        CheckBox box = new() { Content = label, IsChecked = value, MinWidth = 140, Margin = new Thickness(0, 0, 14, 7) };
        switches.Add(key, box); parent.Children.Add(box);
    }
    private void AddField(Panel parent, string key, string label, double value)
    {
        StackPanel panel = new() { Width = 112, Margin = new Thickness(0, 0, 12, 7) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush") });
        TextBox box = new() { Text = value.ToString("G", CultureInfo.CurrentCulture) }; System.Windows.Automation.AutomationProperties.SetName(box, label);
        fields.Add(key, box); panel.Children.Add(box); parent.Children.Add(panel);
    }
    private double Number(string name) => Parse(fields[name].Text, name);
    private static double Parse(string text, string name) => double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) && double.IsFinite(number)
        ? number : throw new ArgumentException(name + " must be a finite number.");
    private bool Enabled(string key) => switches[key].IsChecked == true;
    private void Validate(bool apply)
    {
        try
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            var candidate = Settings with
            {
                HeadroomDb = Number("Headroom"), GainDb = Number("Gain"), Mute = Enabled("Mute"), OutputCeiling = Number("Ceiling"), EqBypass = Enabled("EqBypass"),
                Bands = bands.Select(b => new EqBand(Parse(b.Frequency, "Frequency"), Parse(b.Gain, "Gain"), b.QValue(), b.Type, b.Bypass)).ToArray(),
                Gate = new(Enabled("Gate"), Number("GateThreshold"), Number("GateHysteresis"), Number("GateAttack"), Number("GateHold"), Number("GateRelease"), Number("GateFloor")),
                Compressor = new(Enabled("Compressor"), Number("CompThreshold"), Number("CompRatio"), Number("CompAttack"), Number("CompRelease"), Number("CompMakeup")),
                NoiseSuppression = new(Enabled("Spectral noise suppression"), Number("NoiseFloor"), Number("NoiseReduction")),
                Stereo = new(Enabled("Stereo"), Number("Width"), Number("Pan"), Number("Crossfeed"), Enabled("Swap"), Enabled("Mono")),
                NoiseAttenuation = new(Enabled("Downward expander"), Number("ExpThreshold"), Number("ExpRatio"), Number("ExpMaximum"), Number("ExpAttack"), Number("ExpRelease"))
            };
            _ = new PcmDspProcessor(192000, 2, candidate); int rate = PreviewRate(candidate); graph.SetResponse(candidate, rate);
            feedback.Text = $"Settings validated. Response preview at {rate / 1000.0:0.#} kHz; actual stream format is checked by the host.";
            if (apply) { Settings = candidate; DialogResult = true; }
        }
        catch (ArgumentException error) { feedback.Text = error.Message; }
    }
    private static int PreviewRate(DspSettings settings) => Math.Clamp((int)Math.Ceiling(settings.Bands.Max(b => b.FrequencyHz) / .49), 48000, 192000);
    private sealed class BandEdit(int number, EqBand band)
    {
        private readonly double originalQ = band.Q;
        private readonly string initialQ = band.Q.ToString("0.###", CultureInfo.CurrentCulture);
        public int Number { get; } = number;
        public string Frequency { get; set; } = band.FrequencyHz.ToString("G", CultureInfo.CurrentCulture);
        public string Gain { get; set; } = band.GainDb.ToString("G", CultureInfo.CurrentCulture);
        public string Q { get; set; } = band.Q.ToString("0.###", CultureInfo.CurrentCulture);
        public EqFilterType Type { get; set; } = band.Type;
        public bool Bypass { get; set; } = band.Bypass;
        public double QValue() => Q == initialQ ? originalQ : Parse(Q, "Q");
    }
}
