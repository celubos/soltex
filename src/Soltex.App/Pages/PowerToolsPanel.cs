using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.App.Tools;

namespace Soltex.App.Pages;

/// <summary>PowerToys-class tools on the Tools page: text extractor, window zones, batch rename and key remapping.</summary>
public sealed class PowerToolsPanel : UserControl
{
    private const string TextExtractorNote = "Win+Shift+T copies the text in any part of the screen";
    private const string ChooseKey = "Choose a key";

    private static readonly (WindowZonesLayout Layout, string Label)[] Layouts =
        [(WindowZonesLayout.Off, "Off"), (WindowZonesLayout.TwoColumns, "Two columns"), (WindowZonesLayout.ThreeColumns, "Three columns"),
         (WindowZonesLayout.Grid, "Four quarters"), (WindowZonesLayout.Focus, "Wide center")];

    private readonly ShellContext context;
    private readonly CheckBox textExtractor;
    private readonly TextBlock textExtractorNote;
    private readonly ComboBox zones;
    private readonly StackPanel remaps = new();
    private bool loading;

    internal PowerToolsPanel(ShellContext context)
    {
        this.context = context;
        StackPanel body = new();
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 8) };
        heading.Children.Add(new TextBlock { Text = "More tools", Style = (Style)FindResource("Shell.CardTitle"), VerticalAlignment = VerticalAlignment.Bottom });
        heading.Children.Add(new TextBlock { Text = "Inspired by PowerToys", Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom });
        body.Children.Add(heading);

        textExtractor = new CheckBox { Style = (Style)FindResource("Studio.Switch") };
        AutomationProperties.SetName(textExtractor, "Text extractor shortcut");
        textExtractor.Checked += (_, _) => Update(s => s with { TextExtractorShortcut = true });
        textExtractor.Unchecked += (_, _) => Update(s => s with { TextExtractorShortcut = false });
        body.Children.Add(Row("Text extractor", TextExtractorNote, textExtractor, out textExtractorNote));

        zones = new ComboBox { MinWidth = 200, ItemsSource = Layouts.Select(l => l.Label).ToArray() };
        AutomationProperties.SetName(zones, "Window zones");
        zones.SelectionChanged += (_, _) => { if (zones.SelectedIndex >= 0) Update(s => s with { WindowZones = Layouts[zones.SelectedIndex].Layout }); };
        body.Children.Add(Row("Window zones", "Hold Shift while dragging a window, then drop it in a zone", zones, out _));

        Button rename = new() { Content = "Open" };
        AutomationProperties.SetName(rename, "Open batch rename");
        rename.Click += (_, _) => new BatchRenameWindow { Owner = context.Owner }.Show();
        body.Children.Add(Row("Batch rename", "Find and replace in many file names at once, with a preview and undo", rename, out _));

        // A new row does nothing until both keys are chosen: From == To is never remapped.
        Button add = new() { Content = "Add a remap" };
        AutomationProperties.SetName(add, "Add a key remap");
        add.Click += (_, _) => Update(s => { int key = FirstFree(s); return s with { KeyRemaps = [.. s.KeyRemaps, new KeyRemap(key, key)] }; });
        body.Children.Add(Row("Key remapping", "Make a key act as another key, or turn it off", add, out _));
        body.Children.Add(remaps);
        Content = body;
        context.Services.Settings.Changed += _ => Dispatcher.InvokeAsync(Refresh);
        context.Services.Utilities.Changed += () => Dispatcher.InvokeAsync(ShowTextExtractorState);
        Refresh();
        ShowTextExtractorState();
    }

    internal CheckBox TextExtractorSwitch => textExtractor;
    internal ComboBox ZonesList => zones;

    internal void Refresh()
    {
        loading = true;
        try
        {
            AppSettings s = context.Services.Settings.Current;
            textExtractor.IsChecked = s.TextExtractorShortcut;
            zones.SelectedIndex = Math.Max(0, Array.FindIndex(Layouts, l => l.Layout == s.WindowZones));
            remaps.Children.Clear();
            foreach (KeyRemap remap in s.KeyRemaps) remaps.Children.Add(RemapRow(remap));
        }
        finally { loading = false; }
    }

    /// <summary>Shows why the shortcut does nothing (for example, another app already uses it).</summary>
    private void ShowTextExtractorState() => textExtractorNote.Text = context.Services.Utilities.TextExtractorProblem ?? TextExtractorNote;

    /// <summary>The first key no row uses yet, leaving out Ctrl, Alt and Windows so a new row never starts on a shortcut key.</summary>
    private static int FirstFree(AppSettings s) =>
        KeyRemapper.Keys.Select(k => k.Key).Where(k => !KeyRemapper.IsModifier(k)).FirstOrDefault(k => s.KeyRemaps.All(r => r.From != k), 0x14);

    private Border RemapRow(KeyRemap remap)
    {
        // Until a target is chosen the row is inactive (From == To) and "Acts as" starts with a "Choose a key" item.
        bool unset = remap.From == remap.To;
        int offset = unset ? 2 : 1;
        string[] names = [.. KeyRemapper.Keys.Select(k => k.Name)];
        string[] targets = unset ? [ChooseKey, "Turned off", .. names] : ["Turned off", .. names];
        ComboBox from = new() { MinWidth = 150, ItemsSource = names, SelectedIndex = KeyIndex(remap.From) };
        ComboBox to = new()
        {
            MinWidth = 150, ItemsSource = targets, Margin = new Thickness(8, 0, 0, 0),
            SelectedIndex = unset ? 0 : remap.To == 0 ? 0 : KeyIndex(remap.To) + offset
        };
        AutomationProperties.SetName(from, "Key to remap");
        AutomationProperties.SetName(to, "Acts as");
        void Save()
        {
            if (loading || from.SelectedIndex < 0 || to.SelectedIndex < 0) return;
            (int fromKey, string fromName) = KeyRemapper.Keys[from.SelectedIndex];
            int toKey = unset && to.SelectedIndex == 0 ? fromKey
                : to.SelectedIndex == offset - 1 ? 0
                : KeyRemapper.Keys[to.SelectedIndex - offset].Key;
            if (fromKey != remap.From && context.Services.Settings.Current.KeyRemaps.Any(r => r != remap && r.From == fromKey))
            {
                // Two rows for one key would be merged when saved, silently dropping one of them.
                context.ShowNotice(NoticeSeverity.Caution, fromName + " is already remapped.");
                _ = Dispatcher.InvokeAsync(Refresh);
                return;
            }
            KeyRemap next = new(fromKey, toKey);
            if (next != remap) Update(s => s with { KeyRemaps = [.. s.KeyRemaps.Select(r => r == remap ? next : r)] });
        }
        from.SelectionChanged += (_, _) => Save();
        to.SelectionChanged += (_, _) => Save();
        Button remove = new() { Content = "Remove", Style = (Style)FindResource("Studio.GhostButton"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(remove, "Remove key remap");
        remove.Click += (_, _) => Update(s => s with { KeyRemaps = [.. s.KeyRemaps.Where(r => r != remap)] });
        StackPanel line = new() { Orientation = Orientation.Horizontal };
        line.Children.Add(from);
        line.Children.Add(new TextBlock { Text = "→", Style = (Style)FindResource("Shell.Body"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        line.Children.Add(to);
        line.Children.Add(remove);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = line, Margin = new Thickness(24, 0, 0, 0) };
    }

    private static int KeyIndex(int key) => Math.Max(0, KeyRemapper.Keys.ToList().FindIndex(k => k.Key == key));

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (!loading) context.Services.Settings.Update(change);
    }

    private Border Row(string title, string note, Control control, out TextBlock noteText)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.Body") });
        noteText = new TextBlock { Text = note, Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        text.Children.Add(noteText);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid };
    }
}
