using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

/// <summary>
/// Taskbar details (Windhawk-class): running app indicator, clock, app icon size, button corners, and scrolling over the
/// taskbar to change the volume. Plain rows; choices save to app settings and the taskbar service applies them.
/// </summary>
public sealed class TaskbarDetailsPanel : UserControl
{
    private readonly Dictionary<string, Control> controls = [];
    private readonly CheckBox scrollVolume;
    private readonly TextBlock detailsNote, scrollVolumeNote;
    private ShellContext? context;
    private bool loading;

    public TaskbarDetailsPanel()
    {
        StackPanel rows = new();
        detailsNote = Note();
        scrollVolumeNote = Note();
        foreach (TaskbarTweak tweak in TaskbarTweaks.All)
        {
            Control control;
            if (tweak.Options.Count == 2 && tweak.Id == "indicator")
            {
                CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), Tag = tweak };
                toggle.Checked += (_, _) => Choose(tweak, 0);
                toggle.Unchecked += (_, _) => Choose(tweak, 1);
                control = toggle;
            }
            else
            {
                ComboBox list = new() { MinWidth = 220, Tag = tweak, ItemsSource = tweak.Options.Select(o => o.Label).ToArray() };
                list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) Choose(tweak, list.SelectedIndex); };
                control = list;
            }
            AutomationProperties.SetName(control, tweak.Title);
            controls[tweak.Id] = control;
            // Problems with the details show under the first one, like the background's under "Background".
            rows.Children.Add(Row(tweak.Title, control, controls.Count == 1 ? detailsNote : null));
        }
        scrollVolume = new CheckBox { Style = (Style)FindResource("Studio.Switch") };
        AutomationProperties.SetName(scrollVolume, "Scroll over the taskbar to change volume");
        scrollVolume.Checked += (_, _) => Update(s => s with { TaskbarScrollVolume = true });
        scrollVolume.Unchecked += (_, _) => Update(s => s with { TaskbarScrollVolume = false });
        rows.Children.Add(Row("Scroll over the taskbar to change volume", scrollVolume, scrollVolumeNote));
        Content = rows;
    }

    internal Control ControlFor(string id) => controls[id];

    internal CheckBox ScrollVolumeSwitch => scrollVolume;

    /// <summary>Why some details could not be applied (empty and collapsed when they were).</summary>
    internal TextBlock DetailsNote => detailsNote;

    /// <summary>Why scrolling over the taskbar does not change the volume (empty and collapsed when it does).</summary>
    internal TextBlock ScrollVolumeNote => scrollVolumeNote;

    internal void Attach(ShellContext shell)
    {
        context = shell;
        shell.Services.TaskbarLook.Changed += () => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    internal void Refresh()
    {
        if (context is null) return;
        loading = true;
        try
        {
            AppSettings s = context.Services.Settings.Current;
            TaskbarLookService service = context.Services.TaskbarLook;
            bool usable = Environment.OSVersion.Version.Build >= 22000 && service.Available;
            foreach (TaskbarTweak tweak in TaskbarTweaks.All)
            {
                int index = TaskbarTweaks.ChoiceIndex(tweak, s.TaskbarTweaks);
                switch (controls[tweak.Id])
                {
                    case CheckBox toggle: toggle.IsChecked = index == 0; break;
                    case ComboBox list: list.SelectedIndex = index; break;
                }
                controls[tweak.Id].IsEnabled = usable;
            }
            scrollVolume.IsChecked = s.TaskbarScrollVolume;

            // The background section shows why the details are unavailable and, while a look is in use, the helper's
            // own problems; here go the details' rule failures, and the helper's problems when only details are in use.
            string? general = usable && TaskbarLookService.OnlyDetails(s) ? service.Problem : null;
            string? details = usable && s.TaskbarTweaks.Count > 0 ? service.DetailsProblem : null;
            Show(detailsNote, general is not null && details is not null && general != details ? general + " " + details : general ?? details);
            Show(scrollVolumeNote, s.TaskbarScrollVolume ? service.ScrollVolumeProblem : null);
        }
        finally { loading = false; }
    }

    private static void Show(TextBlock note, string? text)
    {
        note.Text = text ?? "";
        note.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Choose(TaskbarTweak tweak, int index)
    {
        string key = tweak.Options[index].Key;
        Update(s =>
        {
            Dictionary<string, string> tweaks = new(s.TaskbarTweaks);
            if (index == 0) tweaks.Remove(tweak.Id);
            else tweaks[tweak.Id] = key;
            return s with { TaskbarTweaks = tweaks };
        });
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (context is null || loading) return;
        context.Services.Settings.Update(change);
    }

    private TextBlock Note()
    {
        TextBlock note = new()
        {
            Style = (Style)FindResource("Shell.Secondary"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        return note;
    }

    private Border Row(string title, Control control, TextBlock? note = null)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock label = new() { Text = title, Style = (Style)FindResource("Shell.Body"), VerticalAlignment = VerticalAlignment.Center };
        if (note is null) grid.Children.Add(label);
        else grid.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, note } });
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid };
    }
}
