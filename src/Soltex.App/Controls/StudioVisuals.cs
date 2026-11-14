using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Soltex.Audio.Engine;
using Soltex.Audio.Host;
using Soltex.Audio.UI;

namespace Soltex.App.Controls;

/// <summary>
/// Vertical level meter drawn in one pass (no template, no animation timer). Level is a 0–1 meter position;
/// a negative level means "not measured" and draws only the track, never a fake signal.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(LevelMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(availableSize.Width, 8), 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double radius = Math.Min(width / 2, 4);
        drawingContext.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), radius, radius);
        double level = Level;
        if (level <= 0 || Fill is null) return;
        double filled = Math.Clamp(level, 0, 1) * height;
        // The gradient spans the whole meter, so green/amber/red stay tied to the level, not to the bar's length.
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, height - filled, width, filled), radius, radius));
        drawingContext.DrawRoundedRectangle(Fill, null, new Rect(0, 0, width, height), radius, radius);
        drawingContext.Pop();
    }
}

/// <summary>Small frequency-response preview of a channel's equalizer (±12 dB, 20 Hz–20 kHz).</summary>
public sealed class EqCurve : FrameworkElement
{
    private static readonly double[] Frequencies = Enumerable.Range(0, 64).Select(i => 20 * Math.Pow(1000, i / 63.0)).ToArray();
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(nameof(Settings), typeof(DspSettings), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GuideProperty = DependencyProperty.Register(nameof(Guide), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DspSettings? Settings { get => (DspSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Guide { get => (Brush?)GetValue(GuideProperty); set => SetValue(GuideProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= 2 || height <= 2) return;
        if (Guide is not null) drawingContext.DrawLine(new Pen(Guide, 1), new Point(0, height / 2), new Point(width, height / 2));
        if (Settings is not { } settings || Stroke is null) return;
        double[] decibels;
        try { decibels = settings.EqBypass ? new double[Frequencies.Length] : EqualizerResponse.Decibels(settings, 48000, Frequencies); }
        catch (ArgumentException) { return; }
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < decibels.Length; i++)
            {
                double x = i * width / (decibels.Length - 1);
                double y = height / 2 - Math.Clamp(decibels[i], -12, 12) / 12 * (height / 2 - 1.5);
                if (i == 0) context.BeginFigure(new Point(x, y), false, false); else context.LineTo(new Point(x, y), true, true);
            }
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, geometry);
    }
}

/// <summary>Usage ring (0–1) drawn as one arc over a track; a negative value draws only the track.</summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        double radius = (size - Thickness) / 2;
        Point center = new(ActualWidth / 2, ActualHeight / 2);
        if (Track is not null) drawingContext.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);
        double value = Value;
        if (value <= 0 || Stroke is null) return;
        double sweep = Math.Min(value, 0.9999) * 360;
        double end = (sweep - 90) * Math.PI / 180;
        Point start = new(center.X, center.Y - radius);
        Point stop = new(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));
        StreamGeometry arc = new();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(stop, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
}

/// <summary>Audio bus → its Studio channel brush (Studio.Channel.Game etc.).</summary>
public sealed class BusBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AudioBus bus ? Application.Current?.TryFindResource("Studio.Channel." + bus) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Audio bus → its icon glyph (Segoe Fluent Icons).</summary>
public sealed class BusGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AudioBus.Game => "",
        AudioBus.Chat => "",
        AudioBus.Media => "",
        AudioBus.Aux => "",
        AudioBus.Microphone => "",
        _ => ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Linear peak (−1 when unknown) → meter position, keeping "unknown" as −1 so the meter draws no signal.</summary>
public sealed class PeakLevelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double peak && peak >= 0 ? MicrophonePath.MeterPosition((float)peak) : -1d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Decibels → "+3.5" / "−12.0" / "0.0" (unit shown separately).</summary>
public sealed class DecibelTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double db
        ? (db <= -80 ? "−∞" : (db > 0.04 ? "+" : db < -0.04 ? "−" : "") + Math.Abs(db).ToString("0.0", CultureInfo.InvariantCulture))
        : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
