using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Soltex.Audio.Host;

namespace Soltex.Audio.UI;

internal sealed class IndependentMixerPolicyWindow : Window
{
    private readonly ObservableCollection<IndependentEndpointChoice> priority = [];
    private readonly ListBox order;
    private readonly ListBox excluded;
    internal IndependentMixerPolicyWindow(IndependentMixerChannel channel, IReadOnlyList<HostEndpoint> endpoints)
    {
        Title = channel.Title + " · device order"; Width = 650; Height = 530; MinWidth = 530; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; AudioTheme.Apply(this);
        SetResourceReference(BackgroundProperty, "CanvasBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        HostEndpoint[] outputs = endpoints.Where(e => !e.IsCapture).ToArray();
        List<IndependentEndpointChoice> choices = outputs
            .Select(e => new IndependentEndpointChoice(e.Id, e.Name + IndependentMixerWorkspace.DuplicateSuffix(outputs, e) + (e.IsActive ? "" : " (unavailable)"))).ToList();
        int missing = 0;
        foreach (string id in channel.OutputPriorityIds.Concat(channel.ExcludedOutputIds).Distinct())
            if (choices.All(c => c.Id != id)) choices.Add(new(id, "Saved device (not connected)" + (++missing > 1 ? " (" + missing.ToString(System.Globalization.CultureInfo.CurrentCulture) + ")" : "")));
        foreach (string id in channel.OutputPriorityIds) priority.Add(choices.Single(c => c.Id == id));
        foreach (var choice in choices.Where(c => priority.All(p => p.Id != c.Id))) priority.Add(choice);
        DockPanel root = new() { Margin = new Thickness(20) }; Content = root;
        TextBlock guidance = new() { Text = "Put the outputs in the order you prefer. Excluded devices are never picked automatically. Choose Apply changes in the mixer to use this order.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(guidance, Dock.Top); root.Children.Add(guidance);
        StackPanel footer = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        Button save = new() { Content = "Use this order", IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) =>
        {
            channel.OutputPriorityIds = priority.Select(c => c.Id).ToArray();
                channel.ExcludedOutputIds = excluded!.SelectedItems.Cast<IndependentEndpointChoice>().Select(c => c.Id).ToArray();
            DialogResult = true;
        };
        footer.Children.Add(cancel); footer.Children.Add(save); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Grid columns = new(); columns.ColumnDefinitions.Add(new()); columns.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); columns.ColumnDefinitions.Add(new()); root.Children.Add(columns);
        DockPanel first = new(); columns.Children.Add(first);
        TextBlock orderLabel = new() { Text = "Priority order", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(orderLabel, Dock.Top); first.Children.Add(orderLabel);
        StackPanel move = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Button up = new() { Content = "Move up", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 6, 0) }, down = new() { Content = "Move down", Padding = new Thickness(10, 6, 10, 6) };
        move.Children.Add(up); move.Children.Add(down); DockPanel.SetDock(move, Dock.Bottom); first.Children.Add(move);
        order = new() { ItemsSource = priority, DisplayMemberPath = nameof(IndependentEndpointChoice.Label) }; first.Children.Add(order);
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        DockPanel second = new(); Grid.SetColumn(second, 2); columns.Children.Add(second);
        TextBlock excludeLabel = new() { Text = "Exclude from automatic selection", Margin = new Thickness(0, 0, 0, 8), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(excludeLabel, Dock.Top); second.Children.Add(excludeLabel);
        excluded = new() { ItemsSource = choices, DisplayMemberPath = nameof(IndependentEndpointChoice.Label), SelectionMode = SelectionMode.Multiple };
        foreach (var item in choices.Where(c => channel.ExcludedOutputIds.Contains(c.Id))) excluded.SelectedItems.Add(item);
        second.Children.Add(excluded);
        System.Windows.Automation.AutomationProperties.SetName(order, "Fallback output priority order");
        System.Windows.Automation.AutomationProperties.SetName(excluded, "Excluded outputs; select to exclude");
    }
    private void Move(int delta)
    {
        int index = order.SelectedIndex;
        if (index < 0 || index + delta < 0 || index + delta >= priority.Count) return;
        priority.Move(index, index + delta); order.SelectedIndex = index + delta;
    }
}
