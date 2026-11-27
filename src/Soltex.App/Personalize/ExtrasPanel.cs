using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Personalize;

/// <summary>
/// Extra sections for the Start page (folders next to the power button) and the Appearance page (automatic dark mode,
/// pointer scheme). Plain rows like the rest of the page.
/// </summary>
public sealed class ExtrasPanel : UserControl
{
    /// <summary>One pointer scheme in the list: Windows' name for it and how it is shown (also what screen readers say).</summary>
    private sealed record PointerChoice(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly TimeSpan[] Times = [.. Enumerable.Range(0, 48).Select(i => TimeSpan.FromMinutes(30 * i))];
    private readonly ShellContext context;
    private readonly Destination destination;
    private readonly Dictionary<Guid, CheckBox> folders = [];
    private readonly CheckBox? schedule;
    private readonly ComboBox? lightFrom, darkFrom, pointer;
    private readonly TextBlock? sameTimes;
    private bool loading;

    internal ExtrasPanel(ShellContext context, Destination destination)
    {
        this.context = context;
        this.destination = destination;
        StackPanel body = new();
        if (destination == Destination.Start)
        {
            body.Children.Add(Section("Folders next to the power button", "Inspired by Start11"));
            foreach ((string name, Guid id) in StartFolders.All)
            {
                CheckBox toggle = Switch(name);
                toggle.Checked += (_, _) => SetFolder(id, true);
                toggle.Unchecked += (_, _) => SetFolder(id, false);
                folders[id] = toggle;
                body.Children.Add(Row(name, toggle));
            }
        }
        else
        {
            body.Children.Add(Section("Automatic dark mode", null));
            schedule = Switch("Switch between light and dark by time of day");
            schedule.Checked += (_, _) => Update(s => s with { DarkModeSchedule = true });
            schedule.Unchecked += (_, _) => Update(s => s with { DarkModeSchedule = false });
            body.Children.Add(Row("Switch between light and dark by time of day", schedule));
            string[] labels = [.. Times.Select(t => DateTime.Today.Add(t).ToString("t", CultureInfo.CurrentCulture))];
            // Lists keep the width of the page's other lists, so they line up row under row.
            lightFrom = new ComboBox { Width = Pages.CustomizePage.ChoiceWidth, ItemsSource = labels };
            darkFrom = new ComboBox { Width = Pages.CustomizePage.ChoiceWidth, ItemsSource = labels };
            AutomationProperties.SetName(lightFrom, "Light from");
            AutomationProperties.SetName(darkFrom, "Dark from");
            lightFrom.SelectionChanged += (_, _) => SetTime(lightFrom, darkFrom, i => s => s with { LightFrom = Times[i] });
            darkFrom.SelectionChanged += (_, _) => SetTime(darkFrom, lightFrom, i => s => s with { DarkFrom = Times[i] });
            body.Children.Add(Row("Light from", lightFrom));
            body.Children.Add(Row("Dark from", darkFrom));
            sameTimes = new TextBlock { Text = "Light and dark need different times.", Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(2, 4, 0, 0), Visibility = Visibility.Collapsed };
            body.Children.Add(sameTimes);

            body.Children.Add(Section("Pointer", null));
            // A long scheme name is trimmed in the box; the open list and the tooltip show all of it.
            pointer = new ComboBox
            {
                Width = Pages.CustomizePage.ChoiceWidth,
                ItemsSource = PointerSchemes.Names().Select(name => new PointerChoice(name, PointerSchemes.Label(name))).ToArray(),
                ItemTemplate = Pages.CustomizePage.TrimmedChoice(nameof(PointerChoice.Label))
            };
            AutomationProperties.SetName(pointer, "Pointer scheme");
            pointer.SelectionChanged += (_, _) =>
            {
                pointer.ToolTip = (pointer.SelectedItem as PointerChoice)?.Label;
                if (loading || pointer.SelectedItem is not PointerChoice choice) return;
                if (!PointerSchemes.Apply(choice.Name)) context.ShowNotice(NoticeSeverity.Caution, "That pointer scheme could not be applied.");
            };
            body.Children.Add(Row("Pointer scheme", pointer));
        }
        Content = body;
        Refresh();
    }

    /// <summary>Search anchors this panel offers on its page.</summary>
    internal IEnumerable<(string Anchor, FrameworkElement Target)> Anchors =>
        destination == Destination.Start
            ? [("startfolders", folders.Values.First())]
            : [("darkmode", schedule!), ("pointer", pointer!)];

    internal static IEnumerable<SearchEntry> EntriesFor(Destination destination) => destination switch
    {
        Destination.Start => [new SearchEntry("Folders next to the power button", "Start", destination, "startfolders", ["Start11", "Settings", "Documents", "Downloads", "File Explorer", "Start menu folders", "user folder", "personal folder"])],
        Destination.Appearance =>
        [
            new SearchEntry("Automatic dark mode", "Appearance", destination, "darkmode", ["schedule", "night", "sunset", "light mode", "theme"]),
            new SearchEntry("Pointer scheme", "Appearance", destination, "pointer", ["cursor", "mouse pointer", "black cursor", "large cursor"])
        ],
        _ => []
    };

    internal void Refresh()
    {
        loading = true;
        try
        {
            if (destination == Destination.Start)
            {
                HashSet<Guid> visible = [.. StartFolders.Read()];
                foreach ((Guid id, CheckBox toggle) in folders) toggle.IsChecked = visible.Contains(id);
                return;
            }
            AppSettings s = context.Services.Settings.Current;
            schedule!.IsChecked = s.DarkModeSchedule;
            lightFrom!.SelectedIndex = Slot(s.LightFrom);
            darkFrom!.SelectedIndex = Slot(s.DarkFrom);
            lightFrom.IsEnabled = darkFrom.IsEnabled = s.DarkModeSchedule;
            // Equal times would mean dark all day (a saved file can still hold them).
            sameTimes!.Visibility = s.DarkModeSchedule && lightFrom.SelectedIndex == darkFrom.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
            string current = PointerSchemes.Current();
            pointer!.SelectedItem = pointer.Items.OfType<PointerChoice>().FirstOrDefault(choice => choice.Name == current);
            pointer.ToolTip = (pointer.SelectedItem as PointerChoice)?.Label;
        }
        finally { loading = false; }
    }

    private static int Slot(TimeSpan time) => Array.IndexOf(Times, TimeSpan.FromMinutes(Math.Round(time.TotalMinutes / 30) * 30 % 1440));

    /// <summary>Saves a schedule time, or refuses one equal to the other time and says why.</summary>
    private void SetTime(ComboBox changed, ComboBox other, Func<int, Func<AppSettings, AppSettings>> change)
    {
        if (loading || changed.SelectedIndex < 0) return;
        if (changed.SelectedIndex == other.SelectedIndex)
        {
            // Put the saved time back once this selection change has finished.
            Dispatcher.InvokeAsync(() =>
            {
                Refresh();
                sameTimes!.Visibility = Visibility.Visible;
            });
            return;
        }
        sameTimes!.Visibility = Visibility.Collapsed;
        Update(change(changed.SelectedIndex));
    }

    private void SetFolder(Guid id, bool shown)
    {
        if (loading) return;
        try { StartFolders.Set(id, shown); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "Start folders could not be changed: " + error.Message);
        }
    }

    /// <summary>Saves a change and shows the saved state; a settings file that can't be written is reported, not thrown.</summary>
    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (loading) return;
        Pages.PageSettings.TrySave(context, change);
        Refresh();
    }

    private StackPanel Section(string title, string? credit)
    {
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 18, 0, 8) };
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.CardTitle"), VerticalAlignment = VerticalAlignment.Bottom });
        if (credit is not null)
            heading.Children.Add(new TextBlock { Text = credit, Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom });
        return heading;
    }

    private CheckBox Switch(string name)
    {
        CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch") };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    private Border Row(string title, Control control)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.Body"), VerticalAlignment = VerticalAlignment.Center });
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid };
    }
}
