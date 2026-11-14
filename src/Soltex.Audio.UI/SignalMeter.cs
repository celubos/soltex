using System.Windows;
using System.Windows.Media;

namespace Soltex.Audio.UI;

public sealed class SignalMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(SignalMeter), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);
        var background = (Brush)FindResource("FieldBrush");
        drawingContext.DrawRoundedRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight), 2, 2);
        if (!double.IsFinite(Level) || Level < 0) return;
        double db = 20 * Math.Log10(Math.Max(Level, 0.001));
        double filled = Math.Clamp((db + 60) / 60, 0, 1) * ActualHeight;
        var color = (Brush)FindResource(Level >= 0.999 ? "DangerBrush" : Level > 0.8 ? "WarningBrush" : "SignalBrush");
        drawingContext.DrawRoundedRectangle(color, null, new Rect(0, ActualHeight - filled, ActualWidth, filled), 2, 2);
        var divider = new Pen(background, 2);
        for (double y = 8; y < ActualHeight; y += 8) drawingContext.DrawLine(divider, new Point(0, y), new Point(ActualWidth, y));
    }
}
