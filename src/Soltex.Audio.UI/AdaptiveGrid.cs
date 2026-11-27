using System.Windows;
using System.Windows.Controls;

namespace Soltex.Audio.UI;

/// <summary>
/// Equal columns that reflow with the width: as many columns of at least <see cref="MinColumnWidth"/> as fit (up to
/// <see cref="MaxColumns"/>), so cards and channel strips move to new rows instead of running past the edge or
/// squeezing into each other. With <see cref="Balanced"/> the rows are filled evenly (five strips become 3 + 2, not
/// 4 + 1). Every child in a row gets that row's tallest height, so cards line up; in a finite height with
/// <see cref="StretchRows"/> the rows share the spare height.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(240d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d > 0);
    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(nameof(MaxColumnWidth), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && d > 0);
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is int i && i > 0);
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d >= 0);
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure), v => v is double d && double.IsFinite(d) && d >= 0);
    public static readonly DependencyProperty BalancedProperty = DependencyProperty.Register(nameof(Balanced), typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty StretchRowsProperty = DependencyProperty.Register(nameof(StretchRows), typeof(bool), typeof(AdaptiveGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double MaxColumnWidth { get => (double)GetValue(MaxColumnWidthProperty); set => SetValue(MaxColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public bool Balanced { get => (bool)GetValue(BalancedProperty); set => SetValue(BalancedProperty, value); }
    public bool StretchRows { get => (bool)GetValue(StretchRowsProperty); set => SetValue(StretchRowsProperty, value); }

    /// <summary>The column count of the last layout.</summary>
    public int Columns { get; private set; }

    /// <summary>Columns and item width for <paramref name="count"/> items in <paramref name="width"/>.</summary>
    public static (int Columns, double ItemWidth) Plan(int count, double width, double minColumnWidth, double maxColumnWidth, int maxColumns, double spacing, bool balanced)
    {
        if (count <= 0) return (0, 0);
        int most = Math.Max(1, Math.Min(maxColumns, count));
        int columns = double.IsFinite(width)
            ? Math.Clamp((int)Math.Floor((width + spacing) / (minColumnWidth + spacing)), 1, most)
            : most;
        if (balanced)
        {
            int rows = (count + columns - 1) / columns;
            columns = (count + rows - 1) / rows;
        }
        double item = double.IsFinite(width) ? Math.Max(0, (width - (columns - 1) * spacing) / columns) : minColumnWidth;
        return (columns, Math.Min(item, maxColumnWidth));
    }

    private List<UIElement> Visible() => InternalChildren.Cast<UIElement>().Where(c => c is not null && c.Visibility != Visibility.Collapsed).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        List<UIElement> items = Visible();
        foreach (UIElement hidden in InternalChildren.Cast<UIElement>().Where(c => c is not null && c.Visibility == Visibility.Collapsed)) hidden.Measure(default);
        (int columns, double item) = Plan(items.Count, availableSize.Width, MinColumnWidth, MaxColumnWidth, MaxColumns, ColumnSpacing, Balanced);
        Columns = columns;
        if (columns == 0) return default;
        double height = 0;
        for (int start = 0; start < items.Count; start += columns)
        {
            double row = 0;
            for (int i = start; i < Math.Min(items.Count, start + columns); i++)
            {
                items[i].Measure(new Size(item, double.PositiveInfinity));
                row = Math.Max(row, items[i].DesiredSize.Height);
            }
            height += row + (start > 0 ? RowSpacing : 0);
        }
        double width = columns * item + (columns - 1) * ColumnSpacing;
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        List<UIElement> items = Visible();
        (int columns, double item) = Plan(items.Count, finalSize.Width, MinColumnWidth, MaxColumnWidth, MaxColumns, ColumnSpacing, Balanced);
        Columns = columns;
        if (columns == 0) return finalSize;
        List<double> rows = [];
        for (int start = 0; start < items.Count; start += columns)
            rows.Add(items.Skip(start).Take(columns).Max(c => c.DesiredSize.Height));
        double used = rows.Sum() + (rows.Count - 1) * RowSpacing;
        double extra = StretchRows && double.IsFinite(finalSize.Height) && finalSize.Height > used ? (finalSize.Height - used) / rows.Count : 0;
        double y = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            double height = rows[r] + extra;
            for (int c = 0; c < columns && r * columns + c < items.Count; c++)
                items[r * columns + c].Arrange(new Rect(c * (item + ColumnSpacing), y, item, height));
            y += height + RowSpacing;
        }
        return finalSize;
    }
}
