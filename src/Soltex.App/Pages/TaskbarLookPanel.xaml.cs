using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Soltex.App.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

/// <summary>
/// Taskbar look (TranslucentTB-class): one row per taskbar state with a short summary of its look and a switch (the
/// Desktop look always applies). Opening a row shows its editor, one at a time: style, colour or Windows' accent colour,
/// opacity, and the line above the taskbar. Changes save to app settings; the taskbar service applies them.
/// </summary>
public partial class TaskbarLookPanel : UserControl
{
    /// <summary>A state as the page shows it: row title, search title, when it is used, and its switch's name.</summary>
    internal sealed record StateInfo(TaskbarState State, string Title, string SearchTitle, string Description, string? SwitchName, string[] Synonyms);

    /// <summary>The rows, in page order.</summary>
    internal static readonly StateInfo[] States =
    [
        new(TaskbarState.Desktop, "Desktop", "Taskbar look on the desktop", "Used when no other look applies.", null,
            ["background", "desktop", "transparent", "translucent"]),
        new(TaskbarState.VisibleWindow, "Window visible", "Taskbar when a window is visible", "Used while a window is open on the taskbar's display.",
            "Change the look when a window is visible", ["open window", "visible", "dynamic"]),
        new(TaskbarState.MaximizedWindow, "Window maximized", "Taskbar when a window is maximized", "Used while a window is maximized on the taskbar's display.",
            "Change the look when a window is maximized", ["maximised", "full screen", "dynamic"]),
        new(TaskbarState.StartOpen, "Start open", "Taskbar when Start is open", "Used while Start is open on the taskbar's display.",
            "Change the look when Start is open", ["start menu", "dynamic"]),
        new(TaskbarState.SearchOpen, "Search open", "Taskbar when Search is open", "Used while Search is open on the taskbar's display.",
            "Change the look when Search is open", ["search box", "windows search", "dynamic"]),
        new(TaskbarState.TaskViewOpen, "Task View open", "Taskbar when Task View is open", "Used on every display while Task View is open.",
            "Change the look when Task View is open", ["task view", "virtual desktops", "win+tab"]),
        new(TaskbarState.BatterySaver, "Battery saver", "Taskbar in battery saver",
            "Used on every display while battery saver is on, unless Start, Search or Task View is open.",
            "Change the look in battery saver", ["battery saver", "energy saver", "power saving"])
    ];

    private static readonly (string Label, TaskbarLookKind Kind)[] Styles =
    [
        ("Windows default", TaskbarLookKind.Default),
        ("Opaque", TaskbarLookKind.Opaque),
        ("Clear", TaskbarLookKind.Clear),
        ("Blur", TaskbarLookKind.Blur),
        ("Acrylic", TaskbarLookKind.Acrylic)
    ];

    private static readonly (string Name, uint Rgb)[] Palette =
    [
        ("Black", 0x000000), ("Graphite", 0x202020), ("White", 0xFFFFFF), ("Blue", 0x0067C0),
        ("Violet", 0x6B4FBB), ("Rose", 0xC42B5C), ("Green", 0x107C41)
    ];

    /// <summary>Windows 11 draws its taskbar with XAML; earlier versions are not supported.</summary>
    private static readonly bool Supported = Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Width of a Studio.Switch without a label: the space the Desktop row keeps so its chevron lines up.</summary>
    private const double SwitchWidth = 44;

    /// <summary>One state's row.</summary>
    private sealed record Row(StateInfo Info, ExpandButton Header, TextBlock Summary, CheckBox? Switch, Border Host);

    private readonly Dictionary<TaskbarState, Row> rows = [];
    private readonly DispatcherTimer opacityDelay;
    private readonly Border editorFrame;
    private readonly TextBlock description, opacityText, note;
    private readonly ComboBox styleList;
    private readonly Grid colorRow, opacityRow;
    private readonly StackPanel swatches;
    private readonly Button accentSwatch, customButton;
    private readonly Slider opacitySlider;
    private readonly CheckBox lineSwitch;
    private TaskbarState? open = TaskbarState.Desktop;
    private (TaskbarState State, double Percent)? pendingOpacity;
    private ShellContext? context;
    private bool loading;

    public TaskbarLookPanel()
    {
        InitializeComponent();
        note = new TextBlock { Style = Resource<Style>("Shell.Secondary"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
        LiveRegion.SetIsLive(note, true); // screen readers read a new problem as it shows

        description = new TextBlock { Style = Resource<Style>("Shell.Secondary"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) };
        styleList = new ComboBox { MinWidth = 220, ItemsSource = Styles.Select(s => s.Label).ToArray() };
        styleList.SelectionChanged += Style_Changed;

        swatches = new StackPanel { Orientation = Orientation.Horizontal };
        foreach ((string name, uint rgb) in Palette)
        {
            Button swatch = new() { Style = Resource<Style>("Swatch"), Tag = rgb, ToolTip = name, Background = new SolidColorBrush(ToColor(rgb)) };
            swatch.Click += Swatch_Click;
            swatches.Children.Add(swatch);
        }
        accentSwatch = new Button { Style = Resource<Style>("Swatch"), ToolTip = "Windows accent color", Margin = new Thickness(8, 0, 4, 0) };
        accentSwatch.Click += Accent_Click;
        customButton = new Button { Style = Resource<Style>("Studio.GhostButton"), Content = "Custom", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        customButton.Click += Custom_Click;
        StackPanel colors = new() { Orientation = Orientation.Horizontal, Children = { swatches, accentSwatch, customButton } };

        opacitySlider = new Slider { Width = 200, Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 5 };
        opacitySlider.ValueChanged += Opacity_Changed;
        opacityText = new TextBlock { Style = Resource<Style>("Shell.Secondary"), Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        lineSwitch = new CheckBox { Style = Resource<Style>("Studio.Switch") };
        lineSwitch.Checked += Line_Changed;
        lineSwitch.Unchecked += Line_Changed;

        colorRow = EditorRow("Color", colors);
        opacityRow = EditorRow("Opacity", new StackPanel { Orientation = Orientation.Horizontal, Children = { opacitySlider, opacityText } });
        StackPanel editor = new() { Children = { description, EditorRow("Style", styleList), colorRow, opacityRow, EditorRow("Line above the taskbar", lineSwitch) } };
        // Editor labels line up with the row titles (and every other row's label on the page).
        editorFrame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0, 8, 0, 2), Child = editor };
        editorFrame.SetResourceReference(Border.BorderBrushProperty, "Studio.StrokeBrush");

        foreach (StateInfo info in States) Rows.Children.Add(BuildRow(info));
        opacityDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => SaveOpacity(), Dispatcher);
        opacityDelay.Stop();
    }

    internal void Attach(ShellContext shell)
    {
        context = shell;
        shell.Services.TaskbarLook.Changed += () => Dispatcher.InvokeAsync(Refresh);
        Refresh();
    }

    /// <summary>The search anchor of a state's row.</summary>
    internal static string AnchorFor(TaskbarState state) => "look." + state;

    /// <summary>The header that opens and closes a state's editor.</summary>
    internal ToggleButton HeaderFor(TaskbarState state) => rows[state].Header;

    /// <summary>The switch that turns a state on (none for Desktop, which always applies).</summary>
    internal CheckBox? SwitchFor(TaskbarState state) => rows[state].Switch;

    internal TextBlock SummaryFor(TaskbarState state) => rows[state].Summary;

    /// <summary>The state whose editor is open, or null.</summary>
    internal TaskbarState? OpenState => open;

    internal ComboBox StyleList => styleList;
    internal Grid ColorRow => colorRow;
    internal Grid OpacityRow => opacityRow;
    internal Slider OpacitySlider => opacitySlider;
    internal TextBlock OpacityText => opacityText;
    internal CheckBox LineSwitch => lineSwitch;
    internal Button AccentSwatch => accentSwatch;
    internal Button CustomButton => customButton;
    internal StackPanel Swatches => swatches;

    /// <summary>Why the look cannot be changed or did not take effect (empty and collapsed when it did).</summary>
    internal TextBlock Note => note;

    private AppSettings Current => context!.Services.Settings.Current;

    /// <summary>A short description of a look, such as "Acrylic, 60%".</summary>
    internal static string Summary(TaskbarStateLook look)
    {
        ArgumentNullException.ThrowIfNull(look);
        string text = look.Kind switch
        {
            TaskbarLookKind.Opaque => "Opaque",
            TaskbarLookKind.Clear => "Clear",
            TaskbarLookKind.Blur => "Blur",
            TaskbarLookKind.Acrylic => "Acrylic",
            _ => "Windows default"
        };
        if (look.UseAccent && look.Kind is TaskbarLookKind.Opaque or TaskbarLookKind.Clear or TaskbarLookKind.Acrylic) text += ", accent color";
        if (look.Kind is TaskbarLookKind.Clear or TaskbarLookKind.Acrylic) text += ", " + Percent(look.Color) + "%";
        if (!look.Line) text += ", no line";
        return text;
    }

    internal void Refresh()
    {
        if (context is null) return;
        loading = true;
        try
        {
            AppSettings s = Current;
            TaskbarLookService service = context.Services.TaskbarLook;
            bool usable = Supported && service.Available;
            // The editor moves to the open row: first out of every other row.
            foreach (Row row in rows.Values)
            {
                TaskbarStateLook look = s.LookFor(row.Info.State);
                string summary = Summary(look);
                row.Summary.Text = summary;
                AutomationProperties.SetHelpText(row.Header, summary);
                bool here = open == row.Info.State;
                row.Header.IsChecked = here;
                if (row.Switch is { } toggle)
                {
                    toggle.IsChecked = look.Enabled;
                    toggle.IsEnabled = usable;
                }
                if (!here) row.Host.Child = null;
                row.Host.Visibility = here ? Visibility.Visible : Visibility.Collapsed;
            }
            if (open is { } state)
            {
                Border host = rows[state].Host;
                if (!ReferenceEquals(host.Child, editorFrame)) host.Child = editorFrame;
                ShowEditor(rows[state].Info, s.LookFor(state));
            }
            editorFrame.IsEnabled = usable;

            // When only the details are in use, the details section shows the helper's problems instead.
            string? problem = !Supported ? "Needs Windows 11." : service.Unavailable ?? (TaskbarLookService.OnlyDetails(s) ? null : service.Problem);
            note.Text = problem ?? "";
            note.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { loading = false; }
    }

    private void ShowEditor(StateInfo info, TaskbarStateLook look)
    {
        string title = info.Title;
        description.Text = info.Description;
        styleList.SelectedIndex = Array.FindIndex(Styles, x => x.Kind == look.Kind);
        AutomationProperties.SetName(styleList, title + " style");
        colorRow.Visibility = look.Kind is TaskbarLookKind.Clear or TaskbarLookKind.Opaque or TaskbarLookKind.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        // Opaque ignores opacity, and Windows' own look and Blur have no colour of their own.
        opacityRow.Visibility = look.Kind is TaskbarLookKind.Clear or TaskbarLookKind.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        int percent = Percent(look.Color);
        opacitySlider.Value = percent;
        opacityText.Text = percent + "%";
        AutomationProperties.SetName(opacitySlider, title + " opacity");
        lineSwitch.IsChecked = look.Line;
        AutomationProperties.SetName(lineSwitch, title + ": line above the taskbar");

        // The chosen colour is marked by its ring and, for screen readers, by ", selected" in its name.
        Brush selected = Resource<Brush>("Studio.AccentBrush");
        uint rgb = look.Color & 0xFFFFFF;
        foreach (Button swatch in swatches.Children.OfType<Button>())
        {
            bool chosen = !look.UseAccent && (uint)swatch.Tag == rgb;
            swatch.BorderBrush = chosen ? selected : Brushes.Transparent;
            AutomationProperties.SetName(swatch, title + " color: " + swatch.ToolTip + (chosen ? ", selected" : ""));
        }
        if (colorRow.Visibility == Visibility.Visible) accentSwatch.Background = new SolidColorBrush(ToColor(TaskbarStateProbe.AccentColor()));
        accentSwatch.BorderBrush = look.UseAccent ? selected : Brushes.Transparent;
        AutomationProperties.SetName(accentSwatch, title + " color: Windows accent color" + (look.UseAccent ? ", selected" : ""));
        AutomationProperties.SetName(customButton, "Choose a custom color: " + title);
    }

    private Border BuildRow(StateInfo info)
    {
        TextBlock title = new() { Text = info.Title, Style = Resource<Style>("Shell.Body") };
        TextBlock summary = new() { Style = Resource<Style>("Shell.Secondary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        ExpandButton header = new() { Style = Resource<Style>("StateHeader"), Tag = info.State, ToolTip = info.Description, Content = new StackPanel { Children = { title, summary } } };
        AutomationProperties.SetName(header, info.Title);
        header.Checked += Header_Changed;
        header.Unchecked += Header_Changed;

        // The header fills the first column (title at the row's left edge, its chevron at the column's right edge); the
        // switch has the second column to itself, so the two never overlap at any width or text size. Desktop has no
        // switch: an empty space of the same width keeps its chevron in line with the other rows'.
        Grid top = new();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(header);
        CheckBox? toggle = null;
        FrameworkElement end;
        if (info.SwitchName is { } switchName)
        {
            toggle = new CheckBox { Style = Resource<Style>("Studio.Switch"), Tag = info.State, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, switchName);
            toggle.Checked += Switch_Changed;
            toggle.Unchecked += Switch_Changed;
            end = toggle;
        }
        else end = new Border { Width = SwitchWidth };
        end.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(end, 1);
        top.Children.Add(end);

        StackPanel body = new() { Children = { top } };
        if (info.State == TaskbarState.Desktop) body.Children.Add(note); // problems show under the first row, as the details' do
        Border host = new() { Visibility = Visibility.Collapsed };
        body.Children.Add(host);
        rows[info.State] = new Row(info, header, summary, toggle, host);
        return new Border { Style = Resource<Style>("Studio.Row"), Child = body };
    }

    private Grid EditorRow(string label, FrameworkElement control)
    {
        Grid grid = new() { Margin = new Thickness(0, 6, 0, 0), MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, Style = Resource<Style>("Shell.Body"), VerticalAlignment = VerticalAlignment.Center });
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (context is null || loading) return;
        context.Services.TryUpdateSettings(change, "Taskbar");
        Refresh();
    }

    /// <summary>Changes the look of the state whose editor is open.</summary>
    private void UpdateOpen(Func<TaskbarStateLook, TaskbarStateLook> change)
    {
        if (open is { } state) Update(s => s.WithTaskbarState(state, change));
    }

    private void Header_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not ExpandButton { Tag: TaskbarState state } header) return;
        open = header.IsChecked == true ? state : open == state ? null : open;
        Refresh();
    }

    private void Switch_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || sender is not CheckBox { Tag: TaskbarState state } toggle) return;
        bool on = toggle.IsChecked == true;
        if (on) open = state; // show the look that now applies
        Update(s => s.WithTaskbarState(state, l => l with { Enabled = on }));
    }

    private void Style_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || styleList.SelectedIndex < 0) return;
        TaskbarLookKind kind = Styles[styleList.SelectedIndex].Kind;
        // Acrylic without any tint looks like Blur; start it at a visible tint.
        UpdateOpen(l => l with { Kind = kind, Color = kind == TaskbarLookKind.Acrylic && l.Color >> 24 == 0 ? 0x66000000 | (l.Color & 0xFFFFFF) : l.Color });
    }

    private void Line_Changed(object sender, RoutedEventArgs e)
    {
        bool line = lineSwitch.IsChecked == true;
        if (!loading) UpdateOpen(l => l with { Line = line });
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: uint rgb }) UpdateOpen(l => l with { Color = (l.Color & 0xFF000000) | rgb, UseAccent = false });
    }

    private void Accent_Click(object sender, RoutedEventArgs e) => UpdateOpen(l => l with { UseAccent = true });

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        if (context is null || open is not { } state) return;
        TaskbarStateLook look = Current.LookFor(state);
        uint current = look.UseAccent ? TaskbarStateProbe.AccentColor() : look.Color & 0xFFFFFF;
        using System.Windows.Forms.ColorDialog dialog = new()
        {
            FullOpen = true,
            AnyColor = true,
            Color = System.Drawing.Color.FromArgb(unchecked((int)(current | 0xFF000000)))
        };
        System.Windows.Forms.NativeWindow owner = new();
        owner.AssignHandle(new WindowInteropHelper(Window.GetWindow(this)).Handle);
        try
        {
            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        }
        finally { owner.ReleaseHandle(); }
        uint rgb = unchecked((uint)dialog.Color.ToArgb()) & 0xFFFFFF;
        Update(s => s.WithTaskbarState(state, l => l with { Color = (l.Color & 0xFF000000) | rgb, UseAccent = false }));
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading || open is not { } state) return;
        opacityText.Text = (int)opacitySlider.Value + "%";
        // Save once the slider settles, not on every step of a drag.
        pendingOpacity = (state, opacitySlider.Value);
        opacityDelay.Stop();
        opacityDelay.Start();
    }

    private void SaveOpacity()
    {
        opacityDelay.Stop();
        if (pendingOpacity is not { } pending) return;
        pendingOpacity = null;
        uint alpha = (uint)Math.Round(pending.Percent * 255 / 100);
        Update(s => s.WithTaskbarState(pending.State, l => l with { Color = (alpha << 24) | (l.Color & 0xFFFFFF) }));
    }

    private static int Percent(uint color) => (int)Math.Round((color >> 24) * 100 / 255.0);

    private T Resource<T>(string key) => (T)FindResource(key);

    private static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>A row header that opens and closes its row: a toggle button that screen readers also hear as expandable.</summary>
internal sealed class ExpandButton : ToggleButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ExpandButtonPeer(this);

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        (UIElementAutomationPeer.FromElement(this) as ExpandButtonPeer)?.RaiseExpanded(true);
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        (UIElementAutomationPeer.FromElement(this) as ExpandButtonPeer)?.RaiseExpanded(false);
    }

    private sealed class ExpandButtonPeer(ExpandButton owner) : ToggleButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.IsChecked == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

        public void Expand()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.IsChecked = true;
        }

        public void Collapse()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.IsChecked = false;
        }

        internal void RaiseExpanded(bool expanded) => RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            expanded ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
}
