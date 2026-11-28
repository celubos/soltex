using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Soltex.App.Controls;

/// <summary>
/// Rows that follow each other in a panel read as one card, as in Windows Settings: the first row keeps the top corners,
/// the last the bottom corners, and every row after the first draws only the line that divides it from the one above.
/// Anything else in the panel (a heading, a button) ends the card. It is set by the Studio.Row style, so every page's
/// lists group themselves, and a row that is added, removed, shown or hidden later regroups its panel.
/// </summary>
public static class RowGrouping
{
    /// <summary>The rows' corner radius and the space below a card, as in the Studio.Row style.</summary>
    private const double Radius = 8, CardGap = 4;

    public static readonly DependencyProperty IsGroupedProperty = DependencyProperty.RegisterAttached(
        "IsGrouped", typeof(bool), typeof(RowGrouping), new PropertyMetadata(false, IsGroupedChanged));

    /// <summary>The panel a row was last grouped in, so its neighbours regroup when it leaves.</summary>
    private static readonly DependencyProperty OwnerProperty = DependencyProperty.RegisterAttached(
        "Owner", typeof(Panel), typeof(RowGrouping), new PropertyMetadata(null));

    /// <summary>The panel waiting to be regrouped (one pass per panel, however many rows changed).</summary>
    private static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached(
        "Pending", typeof(bool), typeof(RowGrouping), new PropertyMetadata(false));

    public static bool GetIsGrouped(DependencyObject element) => (bool)element.GetValue(IsGroupedProperty);
    public static void SetIsGrouped(DependencyObject element, bool value) => element.SetValue(IsGroupedProperty, value);

    private static void IsGroupedChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Border row) return;
        if ((bool)e.NewValue)
        {
            row.Loaded += Row_Changed;
            row.Unloaded += Row_Changed;
            row.IsVisibleChanged += Row_VisibleChanged;
        }
        else
        {
            row.Loaded -= Row_Changed;
            row.Unloaded -= Row_Changed;
            row.IsVisibleChanged -= Row_VisibleChanged;
        }
    }

    private static void Row_Changed(object sender, RoutedEventArgs e) => Schedule((Border)sender);
    private static void Row_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Schedule((Border)sender);

    private static void Schedule(Border row)
    {
        Panel? panel = row.Parent as Panel ?? row.GetValue(OwnerProperty) as Panel;
        if (panel is null) return;
        if (row.Parent is Panel parent) row.SetValue(OwnerProperty, parent);
        if ((bool)panel.GetValue(PendingProperty)) return;
        panel.SetValue(PendingProperty, true);
        // After the current change (a row may be added and removed in one go), before the next frame is drawn.
        panel.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            panel.SetValue(PendingProperty, false);
            Apply(panel);
        });
    }

    /// <summary>Groups the rows of a panel now. Rows hidden with Visibility.Collapsed are skipped, as are other collapsed elements.</summary>
    public static void Apply(Panel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        List<Border> run = [];
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (child is Border row && GetIsGrouped(row)) { run.Add(row); continue; }
            Shape(run);
            run.Clear();
        }
        Shape(run);
    }

    /// <summary>A row's own margin and padding, from before it was first grouped (grouping changes both).</summary>
    private static readonly DependencyProperty OriginalProperty = DependencyProperty.RegisterAttached(
        "Original", typeof(Original), typeof(RowGrouping), new PropertyMetadata(null));

    private sealed record Original(Thickness Margin, Thickness Padding);

    private static void Shape(List<Border> run)
    {
        for (int i = 0; i < run.Count; i++)
        {
            bool first = i == 0, last = i == run.Count - 1;
            Border row = run[i];
            if (row.GetValue(OriginalProperty) is not Original original)
            {
                original = new Original(row.Margin, row.Padding);
                row.SetValue(OriginalProperty, original);
            }
            row.CornerRadius = new CornerRadius(first ? Radius : 0, first ? Radius : 0, last ? Radius : 0, last ? Radius : 0);
            row.BorderThickness = new Thickness(1, first ? 1 : 0, 1, 1);
            if (run.Count == 1)
            {
                row.Margin = original.Margin;
                row.Padding = original.Padding;
                continue;
            }
            // Inside a card every row spans its full width; a row that was indented (a setting that belongs to the one
            // above it) keeps its indent inside the card instead of leaving a notch in the card's edge.
            Thickness margin = original.Margin, padding = original.Padding;
            row.Margin = new Thickness(0, first ? margin.Top : 0, 0, last ? CardGap : 0);
            row.Padding = new Thickness(padding.Left + Math.Max(0, margin.Left), padding.Top, padding.Right + Math.Max(0, margin.Right), padding.Bottom);
        }
    }
}
