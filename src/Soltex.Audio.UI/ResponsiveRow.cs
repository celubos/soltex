using System.Windows;
using System.Windows.Controls;

namespace Soltex.Audio.UI;

/// <summary>
/// A row with one main child (the first) and trailing children (the rest) such as a badge, buttons or a list box.
/// While the main child keeps at least <see cref="MinPrimaryWidth"/>, the trailing children sit on its right;
/// otherwise they move below it and wrap, so a narrow window stacks the row instead of squeezing or overlapping it.
/// Each child aligns itself within its slot with its own vertical alignment.
/// </summary>
public sealed class ResponsiveRow : Panel
{
    public static readonly DependencyProperty MinPrimaryWidthProperty = DependencyProperty.Register(nameof(MinPrimaryWidth), typeof(double), typeof(ResponsiveRow),
        new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d >= 0);
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(ResponsiveRow),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d >= 0);
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(ResponsiveRow),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d >= 0);

    /// <summary>The narrowest the main child may get before the trailing children move below it.</summary>
    public double MinPrimaryWidth { get => (double)GetValue(MinPrimaryWidthProperty); set => SetValue(MinPrimaryWidthProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    /// <summary>True when the last layout put the trailing children below the main child.</summary>
    public bool IsStacked { get; private set; }

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c is not null && c.Visibility != Visibility.Collapsed).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        List<UIElement> items = Visible();
        foreach (UIElement hidden in InternalChildren.Cast<UIElement>().Where(c => c is not null && c.Visibility == Visibility.Collapsed)) hidden.Measure(default);
        if (items.Count == 0) return default;
        UIElement primary = items[0];
        List<UIElement> trailing = items.Skip(1).ToList();
        foreach (UIElement item in trailing) item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double trailingWidth = trailing.Sum(t => t.DesiredSize.Width) + Spacing * trailing.Count;
        double width = availableSize.Width;
        IsStacked = trailing.Count > 0 && double.IsFinite(width) && width - trailingWidth < MinPrimaryWidth;
        if (!IsStacked)
        {
            primary.Measure(new Size(double.IsFinite(width) ? Math.Max(0, width - trailingWidth) : double.PositiveInfinity, double.PositiveInfinity));
            double height = Math.Max(primary.DesiredSize.Height, trailing.Count == 0 ? 0 : trailing.Max(t => t.DesiredSize.Height));
            double used = primary.DesiredSize.Width + trailingWidth;
            return new Size(double.IsFinite(width) ? Math.Min(width, used) : used, height);
        }
        primary.Measure(new Size(width, double.PositiveInfinity));
        foreach (UIElement item in trailing.Where(t => t.DesiredSize.Width > width)) item.Measure(new Size(width, double.PositiveInfinity));
        double lines = Lines(trailing, width).Sum(line => line.Height) + RowSpacing * Lines(trailing, width).Count;
        return new Size(width, primary.DesiredSize.Height + lines);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<UIElement> items = Visible();
        if (items.Count == 0) return finalSize;
        UIElement primary = items[0];
        List<UIElement> trailing = items.Skip(1).ToList();
        if (!IsStacked)
        {
            double trailingWidth = trailing.Sum(t => t.DesiredSize.Width) + Spacing * trailing.Count;
            double main = Math.Max(0, finalSize.Width - trailingWidth);
            primary.Arrange(new Rect(0, 0, main, finalSize.Height));
            double x = main;
            foreach (UIElement item in trailing)
            {
                x += Spacing;
                item.Arrange(new Rect(x, 0, item.DesiredSize.Width, finalSize.Height));
                x += item.DesiredSize.Width;
            }
            return finalSize;
        }
        primary.Arrange(new Rect(0, 0, finalSize.Width, primary.DesiredSize.Height));
        double y = primary.DesiredSize.Height;
        foreach ((List<UIElement> line, double height) in Lines(trailing, finalSize.Width))
        {
            y += RowSpacing;
            double x = 0;
            foreach (UIElement item in line)
            {
                double itemWidth = Math.Min(item.DesiredSize.Width, finalSize.Width);
                item.Arrange(new Rect(x, y, itemWidth, height));
                x += itemWidth + Spacing;
            }
            y += height;
        }
        return finalSize;
    }

    /// <summary>The trailing children flowed into lines no wider than <paramref name="width"/>.</summary>
    private List<(List<UIElement> Items, double Height)> Lines(List<UIElement> trailing, double width)
    {
        List<(List<UIElement>, double)> lines = [];
        List<UIElement> current = [];
        double x = 0, height = 0;
        foreach (UIElement item in trailing)
        {
            double itemWidth = Math.Min(item.DesiredSize.Width, width);
            if (current.Count > 0 && x + Spacing + itemWidth > width)
            {
                lines.Add((current, height));
                current = []; x = 0; height = 0;
            }
            x += (current.Count > 0 ? Spacing : 0) + itemWidth;
            height = Math.Max(height, item.DesiredSize.Height);
            current.Add(item);
        }
        if (current.Count > 0) lines.Add((current, height));
        return lines;
    }
}
