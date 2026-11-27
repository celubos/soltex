using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Soltex.Audio.Engine;

namespace Soltex.App.Controls;

/// <summary>
/// The ten-band equalizer drawn as one curve, edited on the curve: drag a numbered point to move its band (left and
/// right is frequency, up and down is level), turn the wheel over it to make it wider or narrower, double-click to put
/// it back to flat, right-click for its shape. The curve is the exact response of the running filters (the same
/// coefficients as the engine). Keyboard: 1–9 and 0 pick a band, arrows move it (Shift for bigger steps), Page Up and
/// Page Down change its width, Space turns it on or off, Delete resets it.
/// </summary>
public sealed class EqEditor : FrameworkElement
{
    public const double MinimumFrequency = 20, MaximumFrequency = 20000, GainRange = 18, MinimumQ = 0.1, MaximumQ = 20;
    private const double DefaultQ = 0.7071067811865476;
    private const double LeftMargin = 34, BottomMargin = 20, TopMargin = 8, RightMargin = 10, HandleRadius = 9, HitRadius = 16;
    private static readonly double[] CurveFrequencies = Enumerable.Range(0, 241).Select(i => MinimumFrequency * Math.Pow(1000, i / 240.0)).ToArray();
    private static readonly double[] GridFrequencies = [30, 40, 50, 60, 70, 80, 90, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000,
        2000, 3000, 4000, 5000, 6000, 7000, 8000, 9000, 10000];
    private static readonly (double Hz, string Text)[] FrequencyLabels = [(50, "50"), (100, "100"), (200, "200"), (500, "500"),
        (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k")];

    public static readonly DependencyProperty CurveBrushProperty = Register(nameof(CurveBrush));
    public static readonly DependencyProperty GridBrushProperty = Register(nameof(GridBrush));
    public static readonly DependencyProperty LabelBrushProperty = Register(nameof(LabelBrush));
    public static readonly DependencyProperty PlotBrushProperty = Register(nameof(PlotBrush));
    public static readonly DependencyProperty HandleBrushProperty = Register(nameof(HandleBrush));
    public static readonly DependencyProperty HandleTextBrushProperty = Register(nameof(HandleTextBrush));
    public static readonly DependencyProperty SelectedTextBrushProperty = Register(nameof(SelectedTextBrush));
    public static readonly DependencyProperty FocusBrushProperty = Register(nameof(FocusBrush));

    private static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(EqEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? CurveBrush { get => (Brush?)GetValue(CurveBrushProperty); set => SetValue(CurveBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? LabelBrush { get => (Brush?)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public Brush? PlotBrush { get => (Brush?)GetValue(PlotBrushProperty); set => SetValue(PlotBrushProperty, value); }
    public Brush? HandleBrush { get => (Brush?)GetValue(HandleBrushProperty); set => SetValue(HandleBrushProperty, value); }
    public Brush? HandleTextBrush { get => (Brush?)GetValue(HandleTextBrushProperty); set => SetValue(HandleTextBrushProperty, value); }
    public Brush? SelectedTextBrush { get => (Brush?)GetValue(SelectedTextBrushProperty); set => SetValue(SelectedTextBrushProperty, value); }
    public Brush? FocusBrush { get => (Brush?)GetValue(FocusBrushProperty); set => SetValue(FocusBrushProperty, value); }

    private EqBand[] bands = DspSettings.FlatBands();
    private int selected, hovered = -1, dragging = -1;
    private Point dragStart;
    private EqBand dragOrigin = new(1000);

    public EqEditor()
    {
        Focusable = true;
        FocusVisualStyle = null; // the selected point draws its own focus ring
        MinHeight = 160;
        SnapsToDevicePixels = true;
        SetResourceReference(CurveBrushProperty, "Studio.AccentBrush");
        SetResourceReference(GridBrushProperty, "Studio.StrokeBrush");
        SetResourceReference(LabelBrushProperty, "Studio.TextTertiaryBrush");
        SetResourceReference(PlotBrushProperty, "Studio.BaseBrush");
        SetResourceReference(HandleBrushProperty, "Studio.RaisedBrush");
        SetResourceReference(HandleTextBrushProperty, "Studio.TextBrush");
        SetResourceReference(SelectedTextBrushProperty, "Studio.OnAccentBrush");
        SetResourceReference(FocusBrushProperty, "Studio.TextBrush");
        AutomationProperties.SetName(this, "Equalizer curve");
        AutomationProperties.SetHelpText(this, "Numbers 1 to 0 pick a band. Arrows move it, with Shift for bigger steps. Page Up and Page Down make it narrower or wider. Space turns it on or off. Delete resets it.");
        UpdateStatus();
    }

    /// <summary>Raised for every edit made on the curve (drag, wheel, keys, menu).</summary>
    public event EventHandler? BandsChanged;

    /// <summary>Raised when another band is picked.</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyList<EqBand> Bands => bands;

    public int SelectedIndex
    {
        get => selected;
        set
        {
            int next = Math.Clamp(value, 0, bands.Length - 1);
            if (next == selected) return;
            selected = next;
            UpdateStatus();
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows these bands (exactly ten). Does not raise <see cref="BandsChanged"/>.</summary>
    public void SetBands(IReadOnlyList<EqBand> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Count != 10) throw new ArgumentException("Exactly ten EQ bands are required.", nameof(value));
        bands = [.. value];
        UpdateStatus();
        InvalidateVisual();
    }

    /// <summary>Replaces one band from another control (the band details below the curve). Does not raise <see cref="BandsChanged"/>.</summary>
    public void SetBand(int index, EqBand band)
    {
        ArgumentNullException.ThrowIfNull(band);
        bands[index] = band;
        UpdateStatus();
        InvalidateVisual();
    }

    // ---- geometry ----

    private Rect PlotArea => new(LeftMargin, TopMargin, Math.Max(1, ActualWidth - LeftMargin - RightMargin), Math.Max(1, ActualHeight - TopMargin - BottomMargin));

    private static double Span => Math.Log10(MaximumFrequency / MinimumFrequency);

    private static double X(double frequency, Rect area) => area.Left + Math.Log10(Math.Clamp(frequency, MinimumFrequency, MaximumFrequency) / MinimumFrequency) / Span * area.Width;
    private static double FrequencyAt(double x, Rect area) => MinimumFrequency * Math.Pow(10, Math.Clamp((x - area.Left) / area.Width, 0, 1) * Span);
    private static double Y(double decibels, Rect area) => area.Top + (GainRange - Math.Clamp(decibels, -GainRange - 6, GainRange + 6)) / (2 * GainRange) * area.Height;
    private static double GainAt(double y, Rect area) => Math.Clamp(GainRange - (y - area.Top) / area.Height * 2 * GainRange, -GainRange, GainRange);

    /// <summary>True for shapes that have a level; cuts and notches only have a frequency and a width.</summary>
    internal static bool HasGain(EqFilterType type) => type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf;

    private Point HandlePoint(int index, Rect area)
    {
        EqBand band = bands[index];
        return new Point(X(band.FrequencyHz, area), Y(HasGain(band.Type) ? band.GainDb : 0, area));
    }

    private int HitTest(Point point)
    {
        Rect area = PlotArea;
        int best = -1;
        double bestDistance = HitRadius;
        // The selected point wins ties, then later bands (drawn on top).
        for (int i = bands.Length - 1; i >= 0; i--)
        {
            double distance = (HandlePoint(i, area) - point).Length - (i == selected ? 2 : 0);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    // ---- drawing ----

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 220 : Math.Min(availableSize.Height, 220));

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (ActualWidth < LeftMargin + RightMargin + 20 || ActualHeight < TopMargin + BottomMargin + 20) return;
        Rect area = PlotArea;
        double dim = IsEnabled ? 1 : 0.45;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Typeface typeface = new(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        drawingContext.DrawRoundedRectangle(PlotBrush, null, area, 6, 6);
        drawingContext.PushClip(new RectangleGeometry(area, 6, 6));
        Pen fine = new(GridBrush, 1);
        Pen major = new(GridBrush, 1);
        drawingContext.PushOpacity(0.55);
        foreach (double hz in GridFrequencies)
        {
            double x = Math.Round(X(hz, area)) + 0.5;
            bool decade = hz is 100 or 1000 or 10000;
            drawingContext.PushOpacity(decade ? 1 : 0.45);
            drawingContext.DrawLine(decade ? major : fine, new Point(x, area.Top), new Point(x, area.Bottom));
            drawingContext.Pop();
        }
        foreach (double db in new double[] { -12, -6, 6, 12 })
        {
            double y = Math.Round(Y(db, area)) + 0.5;
            drawingContext.DrawLine(fine, new Point(area.Left, y), new Point(area.Right, y));
        }
        drawingContext.Pop();
        double zero = Math.Round(Y(0, area)) + 0.5;
        drawingContext.DrawLine(new Pen(LabelBrush, 1), new Point(area.Left, zero), new Point(area.Right, zero));

        // The selected band on its own, faintly, so its share of the curve is visible.
        if (selected >= 0 && !bands[selected].Bypass && Response(SoloBands(selected)) is { } solo)
        {
            drawingContext.PushOpacity(0.5 * dim);
            drawingContext.DrawGeometry(null, new Pen(CurveBrush, 1.25) { DashStyle = DashStyles.Dash }, Line(solo, area));
            drawingContext.Pop();
        }

        if (Response(bands) is { } total)
        {
            Color accent = (CurveBrush as SolidColorBrush)?.Color ?? Colors.MediumPurple;
            StreamGeometry fill = new();
            using (StreamGeometryContext context = fill.Open())
            {
                context.BeginFigure(new Point(area.Left, zero), true, true);
                for (int i = 0; i < total.Length; i++) context.LineTo(new Point(X(CurveFrequencies[i], area), Y(total[i], area)), false, false);
                context.LineTo(new Point(area.Right, zero), false, false);
            }
            fill.Freeze();
            drawingContext.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(38 * dim), accent.R, accent.G, accent.B)), null, fill);
            drawingContext.PushOpacity(dim);
            drawingContext.DrawGeometry(null, new Pen(CurveBrush, 2.25) { LineJoin = PenLineJoin.Round }, Line(total, area));
            drawingContext.Pop();
        }
        drawingContext.Pop(); // clip

        // Scale labels outside the plot.
        foreach (double db in new double[] { -12, -6, 0, 6, 12 })
        {
            FormattedText text = Text(db == 0 ? "0" : (db > 0 ? "+" : "−") + Math.Abs(db).ToString("0", CultureInfo.InvariantCulture), typeface, pixelsPerDip, 11, LabelBrush);
            drawingContext.DrawText(text, new Point(area.Left - 6 - text.Width, Y(db, area) - text.Height / 2));
        }
        foreach ((double hz, string label) in FrequencyLabels)
        {
            FormattedText text = Text(label, typeface, pixelsPerDip, 11, LabelBrush);
            drawingContext.DrawText(text, new Point(X(hz, area) - text.Width / 2, area.Bottom + 3));
        }

        // Points, the selected one last (on top).
        for (int i = 0; i < bands.Length; i++) if (i != selected) DrawHandle(drawingContext, i, area, typeface, pixelsPerDip, dim);
        if (selected >= 0) DrawHandle(drawingContext, selected, area, typeface, pixelsPerDip, dim);
    }

    private void DrawHandle(DrawingContext drawingContext, int index, Rect area, Typeface typeface, double pixelsPerDip, double dim)
    {
        EqBand band = bands[index];
        Point center = HandlePoint(index, area);
        bool isSelected = index == selected;
        double radius = HandleRadius + (isSelected || index == hovered || index == dragging ? 2 : 0);
        drawingContext.PushOpacity(dim * (band.Bypass ? 0.55 : 1));
        if (isSelected && IsKeyboardFocused)
            drawingContext.DrawEllipse(null, new Pen(FocusBrush, 2), center, radius + 4, radius + 4);
        if (isSelected) drawingContext.DrawEllipse(CurveBrush, null, center, radius, radius);
        else drawingContext.DrawEllipse(HandleBrush, new Pen(CurveBrush, band.Bypass ? 1 : 1.75) { DashStyle = band.Bypass ? DashStyles.Dash : DashStyles.Solid }, center, radius, radius);
        string number = index == 9 ? "10" : (index + 1).ToString(CultureInfo.InvariantCulture);
        FormattedText text = Text(number, typeface, pixelsPerDip, index == 9 ? 9.5 : 11, isSelected ? SelectedTextBrush : HandleTextBrush, FontWeights.SemiBold);
        drawingContext.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        drawingContext.Pop();
    }

    private static FormattedText Text(string value, Typeface typeface, double pixelsPerDip, double size, Brush? brush, FontWeight? weight = null)
    {
        Typeface face = weight is { } w ? new Typeface(typeface.FontFamily, FontStyles.Normal, w, FontStretches.Normal) : typeface;
        return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.Gray, pixelsPerDip);
    }

    private static StreamGeometry Line(double[] decibels, Rect area)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < decibels.Length; i++)
            {
                Point point = new(X(CurveFrequencies[i], area), Y(decibels[i], area));
                if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private EqBand[] SoloBands(int index) => bands.Select((b, i) => i == index ? b : b with { Bypass = true }).ToArray();

    /// <summary>The exact response at 48 kHz, or null when a band is outside what the engine accepts.</summary>
    internal static double[]? Response(IReadOnlyList<EqBand> values)
    {
        try { return EqualizerResponse.Decibels(new DspSettings { Bands = values.ToArray() }, 48000, CurveFrequencies); }
        catch (ArgumentException) { return null; }
    }

    // ---- editing ----

    /// <summary>A frequency rounded to what a person would type: three significant digits.</summary>
    internal static double RoundFrequency(double hz)
    {
        hz = Math.Clamp(hz, MinimumFrequency, MaximumFrequency);
        double step = Math.Pow(10, Math.Floor(Math.Log10(hz)) - 2);
        return Math.Round(hz / step) * step;
    }

    private void Edit(int index, EqBand band)
    {
        // Only what this edit changed is rounded, so values nobody moved (and the default width) stay exact.
        EqBand current = bands[index];
        band = band with
        {
            FrequencyHz = band.FrequencyHz == current.FrequencyHz ? band.FrequencyHz : RoundFrequency(band.FrequencyHz),
            GainDb = band.GainDb == current.GainDb ? band.GainDb : Math.Round(Math.Clamp(band.GainDb, -GainRange, GainRange), 1),
            Q = band.Q == current.Q || band.Q == DefaultQ ? band.Q : Math.Round(Math.Clamp(band.Q, MinimumQ, MaximumQ), 2)
        };
        if (band == bands[index]) return;
        bands[index] = band;
        UpdateStatus();
        InvalidateVisual();
        BandsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Back to flat: no level, the default width, the band's own shape kept and turned on.</summary>
    internal static EqBand Reset(EqBand band) => band with { GainDb = 0, Q = DefaultQ, Bypass = false };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        SelectedIndex = hit;
        if (e.ClickCount == 2)
        {
            Edit(hit, Reset(bands[hit]));
            e.Handled = true;
            return;
        }
        dragging = hit;
        dragStart = e.GetPosition(this);
        dragOrigin = bands[hit];
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);
        if (dragging >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            Rect area = PlotArea;
            // Shift moves a quarter as far, for fine adjustments.
            double scale = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.25 : 1;
            Point origin = new(X(dragOrigin.FrequencyHz, area), Y(HasGain(dragOrigin.Type) ? dragOrigin.GainDb : 0, area));
            Point target = origin + (position - dragStart) * scale;
            EqBand moved = dragOrigin with { FrequencyHz = FrequencyAt(target.X, area) };
            if (HasGain(dragOrigin.Type)) moved = moved with { GainDb = GainAt(target.Y, area) };
            Edit(dragging, moved);
            return;
        }
        int hit = HitTest(position);
        if (hit != hovered)
        {
            hovered = hit;
            Cursor = hit >= 0 ? Cursors.SizeAll : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging < 0) return;
        dragging = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = -1;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (hovered < 0) return;
        hovered = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Only over a point: elsewhere the wheel scrolls the page as usual.
        int target = HitTest(e.GetPosition(this));
        if (target < 0) return;
        SelectedIndex = target;
        // Up makes the band narrower (higher Q), as on most equalizers.
        Edit(target, bands[target] with { Q = bands[target].Q * Math.Pow(1.12, e.Delta / 120.0) });
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        SelectedIndex = hit;
        ShowMenu(hit);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (selected < 0) return;
        bool big = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        EqBand band = bands[selected];
        switch (e.Key)
        {
            case >= Key.D1 and <= Key.D9: SelectedIndex = e.Key - Key.D1; break;
            case Key.D0: SelectedIndex = 9; break;
            case >= Key.NumPad1 and <= Key.NumPad9: SelectedIndex = e.Key - Key.NumPad1; break;
            case Key.NumPad0: SelectedIndex = 9; break;
            case Key.Left: Edit(selected, band with { FrequencyHz = band.FrequencyHz / Math.Pow(2, big ? 1 / 3.0 : 1 / 12.0) }); break;
            case Key.Right: Edit(selected, band with { FrequencyHz = band.FrequencyHz * Math.Pow(2, big ? 1 / 3.0 : 1 / 12.0) }); break;
            case Key.Up when HasGain(band.Type): Edit(selected, band with { GainDb = band.GainDb + (big ? 3 : 0.5) }); break;
            case Key.Down when HasGain(band.Type): Edit(selected, band with { GainDb = band.GainDb - (big ? 3 : 0.5) }); break;
            case Key.PageUp: Edit(selected, band with { Q = band.Q * 1.25 }); break;
            case Key.PageDown: Edit(selected, band with { Q = band.Q / 1.25 }); break;
            case Key.Space: Edit(selected, band with { Bypass = !band.Bypass }); break;
            case Key.Delete or Key.Back: Edit(selected, Reset(band)); break;
            case Key.Apps: ShowMenu(selected); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsEnabledProperty) InvalidateVisual();
    }

    private void ShowMenu(int index)
    {
        ContextMenu menu = new() { PlacementTarget = this };
        foreach (EqFilterType type in Enum.GetValues<EqFilterType>())
        {
            MenuItem item = new() { Header = TypeName(type), IsCheckable = true, IsChecked = bands[index].Type == type };
            EqFilterType chosen = type;
            item.Click += (_, _) => Edit(index, bands[index] with { Type = chosen });
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        MenuItem on = new() { Header = "Band on", IsCheckable = true, IsChecked = !bands[index].Bypass };
        on.Click += (_, _) => Edit(index, bands[index] with { Bypass = !bands[index].Bypass });
        menu.Items.Add(on);
        MenuItem reset = new() { Header = "Reset band" };
        reset.Click += (_, _) => Edit(index, Reset(bands[index]));
        menu.Items.Add(reset);
        menu.IsOpen = true;
    }

    internal static string TypeName(EqFilterType type) => type switch
    {
        EqFilterType.Peak => "Bell",
        EqFilterType.LowShelf => "Low shelf",
        EqFilterType.HighShelf => "High shelf",
        EqFilterType.HighPass => "Low cut",
        EqFilterType.LowPass => "High cut",
        EqFilterType.Notch => "Notch",
        _ => type.ToString()
    };

    internal static string FrequencyText(double hz) => hz >= 1000
        ? (hz / 1000).ToString(hz >= 10000 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " kHz"
        : hz.ToString("0", CultureInfo.InvariantCulture) + " Hz";

    internal static string Describe(int index, EqBand band)
    {
        string level = HasGain(band.Type)
            ? ", " + (band.GainDb > 0.04 ? "+" : band.GainDb < -0.04 ? "−" : "") + Math.Abs(band.GainDb).ToString("0.0", CultureInfo.InvariantCulture) + " dB"
            : "";
        return $"Band {index + 1}: {TypeName(band.Type)}, {FrequencyText(band.FrequencyHz)}{level}, Q {band.Q.ToString("0.00", CultureInfo.InvariantCulture)}{(band.Bypass ? ", off" : "")}";
    }

    private void UpdateStatus() => AutomationProperties.SetItemStatus(this, selected >= 0 ? Describe(selected, bands[selected]) : "");
}
