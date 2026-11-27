using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Soltex.Audio.UI;

namespace Soltex.Audio.Independent.UI.Tests;

/// <summary>The audio views' reflowing panels and the meter's ballistics, checked without any audio.</summary>
internal static class ResponsiveLayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Columns: as many as fit, rows balanced (five strips: 5, 3 + 2, 2 + 2 + 1, one per row).
        check(AdaptiveGrid.Plan(5, 900, 164, 210, int.MaxValue, 12, balanced: true).Columns == 5, "five strips sit in one row when they fit");
        check(AdaptiveGrid.Plan(5, 700, 164, 210, int.MaxValue, 12, balanced: true).Columns == 3, "five strips in a four-column width wrap as 3 + 2, never 4 + 1");
        check(AdaptiveGrid.Plan(6, 600, 132, 260, int.MaxValue, 10, balanced: true).Columns == 3, "six Sonar strips in a five-column width wrap as 3 + 3");
        check(AdaptiveGrid.Plan(5, 120, 164, 210, int.MaxValue, 12, balanced: true) is { Columns: 1, ItemWidth: 120 }, "a width below one column still gives one full-width column");
        check(AdaptiveGrid.Plan(3, 2000, 220, double.PositiveInfinity, 3, 28, balanced: false).Columns == 3, "the column count never passes MaxColumns");

        AdaptiveGrid grid = new() { MinColumnWidth = 100, ColumnSpacing = 10, RowSpacing = 10 };
        for (int i = 0; i < 5; i++) grid.Children.Add(new Border { Height = 40 + i * 5 });
        grid.Measure(new Size(330, double.PositiveInfinity)); grid.Arrange(new Rect(0, 0, 330, grid.DesiredSize.Height));
        Rect[] cells = grid.Children.Cast<FrameworkElement>().Select(c => LayoutInformation.GetLayoutSlot(c)).ToArray();
        check(grid.Columns == 3 && cells.All(c => c.Right <= 330.5) &&
            cells.Select((a, i) => cells.Skip(i + 1).All(b => !a.IntersectsWith(b) || Rect.Intersect(a, b).Width < 0.5 || Rect.Intersect(a, b).Height < 0.5)).All(x => x),
            "wrapped cells stay inside the width and never overlap");
        check(Math.Abs(cells[0].Height - cells[2].Height) < 0.5, "cells in one row share the row's tallest height");

        // A header row keeps its actions beside the title while the title has room, and moves them below otherwise.
        ResponsiveRow row = new() { MinPrimaryWidth = 200, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "Title", TextWrapping = TextWrapping.Wrap });
        row.Children.Add(new Border { Width = 120, Height = 30 });
        row.Children.Add(new Border { Width = 120, Height = 30 });
        row.Measure(new Size(600, double.PositiveInfinity)); row.Arrange(new Rect(0, 0, 600, row.DesiredSize.Height));
        check(!row.IsStacked, "a wide row keeps its trailing items inline");
        row.Measure(new Size(300, double.PositiveInfinity)); row.Arrange(new Rect(0, 0, 300, row.DesiredSize.Height));
        Rect first = LayoutInformation.GetLayoutSlot((FrameworkElement)row.Children[1]), second = LayoutInformation.GetLayoutSlot((FrameworkElement)row.Children[2]);
        check(row.IsStacked && first.Top > 0 && !first.IntersectsWith(second) && second.Right <= 300.5, "a narrow row moves its trailing items below and wraps them");

        // Meter ballistics: instant rise, smooth fall, a peak mark that holds and then falls.
        SignalMeter meter = new();
        meter.Level = 1;
        check(Math.Abs(meter.ShownDb) < 0.01 && Math.Abs(meter.HoldDb) < 0.01, "a new peak shows in full at once");
        meter.Level = 0.001;
        check(Math.Abs(meter.ShownDb) < 0.01, "a lower reading does not snap the bar down");
        DateTime now = DateTime.UtcNow;
        meter.Advance(TimeSpan.FromMilliseconds(100), now);
        check(meter.ShownDb is < -1 and > -4 && Math.Abs(meter.HoldDb) < 0.01, "the bar falls smoothly while the peak mark holds");
        for (int i = 0; i < 40; i++) meter.Advance(TimeSpan.FromMilliseconds(100), now + SignalMeter.HoldTime + TimeSpan.FromMilliseconds(100 * i));
        check(Math.Abs(meter.ShownDb + 60) < 0.01 && meter.HoldDb < -40, "after the hold time the peak mark falls too");
        meter.Level = -1;
        check(double.IsNaN(meter.ShownDb) && double.IsNaN(meter.HoldDb), "an unmeasured level shows no signal at all");
    }
}
