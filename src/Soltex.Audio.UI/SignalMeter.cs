using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soltex.Audio.UI;

/// <summary>
/// Level meter with studio ballistics: it rises at once, falls back smoothly (no flicker between polls) and keeps a
/// thin peak-hold mark for a moment before it drops. <see cref="Level"/> is a linear peak (1 = full scale); a negative
/// level means "not measured" and draws only the track, never a made-up signal. The scale runs from
/// <see cref="MinimumDb"/> to 0 dBFS. <see cref="Fill"/> spans the whole meter, so green, amber and red stay tied to
/// the level rather than to the bar's length. Animates only while something moves and while it is shown.
/// </summary>
public sealed class SignalMeter : FrameworkElement
{
    /// <summary>How fast the bar falls, in decibels per second.</summary>
    internal const double ReleaseDbPerSecond = 26;
    /// <summary>How fast the peak mark falls once its hold time is over.</summary>
    internal const double HoldReleaseDbPerSecond = 18;
    internal static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.2);

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(SignalMeter),
        new FrameworkPropertyMetadata(-1d, (d, _) => ((SignalMeter)d).LevelChanged()));
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(SignalMeter),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SignalMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(SignalMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumDbProperty = DependencyProperty.Register(nameof(MinimumDb), typeof(double), typeof(SignalMeter),
        new FrameworkPropertyMetadata(-60d, FrameworkPropertyMetadataOptions.AffectsRender), v => v is double d && double.IsFinite(d) && d < 0);
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(SignalMeter),
        new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d > 0);

    private double shownDb = double.NaN, holdDb = double.NaN, targetDb = double.NaN;
    private DateTime holdUntil, lastFrame;
    private bool animating;

    public SignalMeter()
    {
        // Shown again (another tab, a restored window): start from the current level. A peak held from before the meter
        // was hidden would otherwise show for a moment as if it were happening now.
        IsVisibleChanged += (_, _) => { Settle(); InvalidateVisual(); };
        Unloaded += (_, _) => Settle();
    }

    /// <summary>Linear peak: 0 is silence, 1 is full scale; negative when not measured.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    /// <summary>The meter colours from silence to full scale; defaults to the theme's MeterBrush.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double MinimumDb { get => (double)GetValue(MinimumDbProperty); set => SetValue(MinimumDbProperty, value); }
    /// <summary>The meter's width when vertical, or height when horizontal.</summary>
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>The level the bar shows now, in dBFS (NaN when not measured).</summary>
    internal double ShownDb => shownDb;
    /// <summary>The peak-hold mark, in dBFS (NaN when not measured).</summary>
    internal double HoldDb => holdDb;

    protected override Size MeasureOverride(Size availableSize) => Orientation == Orientation.Vertical
        ? new Size(Math.Min(Thickness, availableSize.Width), 0)
        : new Size(0, Math.Min(Thickness, availableSize.Height));

    private void LevelChanged()
    {
        double level = Level;
        if (!double.IsFinite(level) || level < 0)
        {
            targetDb = shownDb = holdDb = double.NaN;
            UpdateAnimation();
            InvalidateVisual();
            return;
        }
        targetDb = Math.Clamp(20 * Math.Log10(Math.Max(level, 1e-6)), MinimumDb, 0);
        // Attack is instant: a new peak always shows in full.
        if (double.IsNaN(shownDb) || targetDb >= shownDb) shownDb = targetDb;
        if (double.IsNaN(holdDb) || targetDb >= holdDb) { holdDb = targetDb; holdUntil = DateTime.UtcNow + HoldTime; }
        UpdateAnimation();
        InvalidateVisual();
    }

    /// <summary>Advances the fall by <paramref name="elapsed"/>; returns true while something still moves.</summary>
    internal bool Advance(TimeSpan elapsed, DateTime now)
    {
        if (double.IsNaN(targetDb)) return false;
        double seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.25);
        if (shownDb > targetDb) shownDb = Math.Max(targetDb, shownDb - ReleaseDbPerSecond * seconds);
        if (now >= holdUntil && holdDb > shownDb) holdDb = Math.Max(shownDb, holdDb - HoldReleaseDbPerSecond * seconds);
        return shownDb > targetDb || holdDb > shownDb;
    }

    private void UpdateAnimation()
    {
        bool wanted = IsVisible && IsLoaded && !double.IsNaN(targetDb) && (shownDb > targetDb || holdDb > shownDb);
        if (wanted == animating) return;
        animating = wanted;
        if (wanted) { lastFrame = DateTime.UtcNow; CompositionTarget.Rendering += OnFrame; }
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        DateTime now = DateTime.UtcNow;
        bool moving = Advance(now - lastFrame, now);
        lastFrame = now;
        InvalidateVisual();
        if (!moving) UpdateAnimation();
    }

    /// <summary>Hidden or unloaded: stop animating and show the current level as it is.</summary>
    private void Settle()
    {
        shownDb = targetDb;
        holdDb = targetDb;
        UpdateAnimation();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth, height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        bool vertical = Orientation == Orientation.Vertical;
        double radius = Math.Min(vertical ? width : height, 6) / 2;
        Rect whole = new(0, 0, width, height);
        Brush? track = Track ?? TryFindResource("TrackBrush") as Brush;
        Brush? fill = Fill ?? TryFindResource(vertical ? "MeterBrush" : "MeterHorizontalBrush") as Brush ?? TryFindResource("SignalBrush") as Brush;
        if (track is not null) drawingContext.DrawRoundedRectangle(track, null, whole, radius, radius);
        if (double.IsNaN(shownDb) || fill is null) return;
        double length = vertical ? height : width;
        double Position(double db) => Math.Clamp((db - MinimumDb) / -MinimumDb, 0, 1) * length;
        double shown = Position(shownDb);
        if (shown > 0.5)
        {
            Rect bar = vertical ? new Rect(0, height - shown, width, shown) : new Rect(0, 0, shown, height);
            drawingContext.PushClip(new RectangleGeometry(bar, radius, radius));
            drawingContext.DrawRoundedRectangle(fill, null, whole, radius, radius);
            drawingContext.Pop();
        }
        if (!double.IsNaN(holdDb) && holdDb > shownDb + 0.5 && holdDb > MinimumDb)
        {
            double hold = Position(holdDb);
            Rect mark = vertical ? new Rect(0, Math.Max(0, height - hold - 1), width, 2) : new Rect(Math.Max(0, Math.Min(width - 2, hold - 1)), 0, 2, height);
            drawingContext.PushClip(new RectangleGeometry(mark));
            drawingContext.DrawRoundedRectangle(fill, null, whole, radius, radius);
            drawingContext.Pop();
        }
    }
}
