using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Soltex.Audio.Engine;

namespace Soltex.Audio.UI;

public sealed class EngineResponseGraph : FrameworkElement
{
    private double[] response = [];
    private double maximumFrequency = 20000;
    public void SetResponse(DspSettings settings, int sampleRate)
    {
        double maximum = Math.Min(20000, sampleRate * 0.49);
        maximumFrequency = maximum;
        var frequencies = Enumerable.Range(0, 240).Select(i => 20 * Math.Pow(maximum / 20, i / 239.0)).ToArray();
        response = EqualizerResponse.Decibels(settings, sampleRate, frequencies);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double width = Math.Max(1, ActualWidth - 52), height = Math.Max(1, ActualHeight - 28);
        var background = (Brush?)TryFindResource("CanvasBrush") ?? Brushes.Black;
        var muted = (Brush?)TryFindResource("MutedBrush") ?? Brushes.Gray;
        var line = (Brush?)TryFindResource("BorderBrush") ?? Brushes.DimGray;
        var accent = (Brush?)TryFindResource("AccentBrush") ?? Brushes.Coral;
        drawingContext.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (int db = -24; db <= 24; db += 12)
        {
            double y = (24 - db) / 48.0 * height + 5;
            drawingContext.DrawLine(new Pen(line, db == 0 ? 1.3 : 0.6), new Point(38, y), new Point(width + 38, y));
            DrawLabel(drawingContext, db > 0 ? $"+{db}" : db.ToString(CultureInfo.InvariantCulture), muted, 2, y - 7);
        }
        foreach (double frequency in new double[] { 20, 100, 1000, 10000 })
        {
            if (frequency > maximumFrequency * 0.85) continue;
            double fraction = Math.Log(frequency / 20) / Math.Log(maximumFrequency / 20);
            string label = frequency >= 1000 ? $"{frequency / 1000:0}k" : frequency.ToString(CultureInfo.InvariantCulture);
            DrawLabel(drawingContext, label, muted, 38 + fraction * width, height + 10);
        }
        DrawLabel(drawingContext, $"{maximumFrequency / 1000:0.#}k Hz", muted, width + 5, height + 10);
        if (response.Length < 2) return;
        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            for (int i = 0; i < response.Length; i++)
            {
                var point = new Point(38 + width * i / (response.Length - 1), 5 + (24 - Math.Clamp(response[i], -24, 24)) / 48 * height);
                if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
            }
        }
        curve.Freeze(); drawingContext.DrawGeometry(null, new Pen(accent, 2), curve);
    }

    private void DrawLabel(DrawingContext context, string text, Brush brush, double x, double y) => context.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
}
