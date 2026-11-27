using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Soltex.App.Desktop;
using Soltex.App.Personalize;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.Audio.UI;

namespace Soltex.App.Pages;

/// <summary>
/// Desktop: groups (Fences-class: a folder's contents or your own shortcuts, on the desktop), widgets (Rainmeter-class:
/// clock, system, network), arranging them, and showing or hiding the desktop icons.
/// </summary>
public sealed class DesktopPage : UserControl, IShellPage
{
    internal static readonly IReadOnlyList<SearchEntry> Entries =
    [
        new("Desktop", "Desktop", Destination.Desktop, "top", "desktop"),
        new("Desktop groups", "Desktop", Destination.Desktop, "groups", ["Fences", "folder", "shortcuts", "portal", "organize icons"]),
        new("Desktop widgets", "Desktop", Destination.Desktop, "widgets", ["Rainmeter", "clock", "CPU", "memory", "network", "meter"]),
        new("Arrange groups and widgets", "Desktop", Destination.Desktop, "arrange", ["move", "resize", "layout"]),
        new("Desktop icons", "Desktop", Destination.Desktop, "icons", ["hide desktop icons", "show desktop icons"]),
        new("Live wallpaper", "Desktop", Destination.Desktop, "wallpaper", ["DeskScapes", "video wallpaper", "animated wallpaper", "background"])
    ];

    private static readonly (DesktopWidgetKind Kind, string Title)[] WidgetTitles =
        [(DesktopWidgetKind.Clock, "Clock"), (DesktopWidgetKind.System, "CPU and memory"), (DesktopWidgetKind.Network, "Network speed")];

    private readonly ShellContext context;
    private readonly PageAnchors anchors = new();
    private readonly StackPanel groupRows = new();
    private readonly List<GroupRowParts> rows = [];
    private readonly TextBlock groupsEmpty = new() { Text = "Show a folder's files, or your own shortcuts, in a box on the desktop.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Dictionary<DesktopWidgetKind, CheckBox> widgetSwitches = [];
    private readonly CheckBox arrangeSwitch, iconsSwitch;
    private readonly TextBlock wallpaperNote = new();
    private readonly Button removeWallpaper = new() { Content = "Remove" };
    private bool loading;

    /// <summary>One group's row, updated in place while the list of groups stays the same.</summary>
    private sealed record GroupRowParts(string Id, Border Row, TextBox Name, TextBlock Detail, Button Remove);

    internal DesktopPage(ShellContext context)
    {
        this.context = context;
        StackPanel page = new();
        TextBlock title = new() { Text = "Desktop", Style = (Style)FindResource("Shell.PageTitle"), Margin = new Thickness(0, 0, 0, 20) };
        page.Children.Add(title);
        anchors.Add("top", title);

        page.Children.Add(Section("Groups", "Inspired by Fences", first: true));
        page.Children.Add(groupRows);
        // Adding a group is a row like the page's other rows: what a group is (until there is one) and the two ways to add
        // one, which move below the text when the page is narrow.
        ResponsiveRow add = new() { MinPrimaryWidth = 200, Spacing = 8 };
        StackPanel addText = new() { VerticalAlignment = VerticalAlignment.Center };
        addText.Children.Add(new TextBlock { Text = "Add a group", Style = (Style)FindResource("Shell.Body") });
        groupsEmpty.Style = (Style)FindResource("Shell.Secondary");
        groupsEmpty.FontSize = 12;
        addText.Children.Add(groupsEmpty);
        add.Children.Add(addText);
        Button addFolder = new() { Content = "Show a folder…", Style = (Style)FindResource("Studio.PrimaryButton"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHelpText(addFolder, "Shows a folder's files on the desktop and keeps them up to date");
        addFolder.Click += (_, _) => AddFolderGroup();
        Button addList = new() { Content = "New empty group", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHelpText(addList, "An empty group; drop files, folders or shortcuts on it");
        addList.Click += (_, _) => AddGroup(null, "New group");
        add.Children.Add(addFolder);
        add.Children.Add(addList);
        page.Children.Add(new Border { Style = (Style)FindResource("Studio.Row"), Child = add });
        anchors.Add("groups", addFolder);

        page.Children.Add(Section("Widgets", "Inspired by Rainmeter"));
        foreach ((DesktopWidgetKind kind, string name) in WidgetTitles)
        {
            CheckBox toggle = Switch(name);
            toggle.Checked += (_, _) => SetWidget(kind, true);
            toggle.Unchecked += (_, _) => SetWidget(kind, false);
            widgetSwitches[kind] = toggle;
            page.Children.Add(Row(name, toggle));
        }
        anchors.Add("widgets", widgetSwitches[DesktopWidgetKind.Clock]);

        page.Children.Add(Section("Layout", null));
        arrangeSwitch = Switch("Move and resize groups and widgets");
        arrangeSwitch.Checked += (_, _) => Update(s => s with { DesktopArrange = true });
        arrangeSwitch.Unchecked += (_, _) => Update(s => s with { DesktopArrange = false });
        page.Children.Add(Row("Move and resize groups and widgets", arrangeSwitch));
        anchors.Add("arrange", arrangeSwitch);
        iconsSwitch = Switch("Show desktop icons");
        iconsSwitch.Checked += (_, _) => SetIcons(true);
        iconsSwitch.Unchecked += (_, _) => SetIcons(false);
        page.Children.Add(Row("Show desktop icons", iconsSwitch));
        anchors.Add("icons", iconsSwitch);

        page.Children.Add(Section("Live wallpaper", "Inspired by DeskScapes"));
        Button chooseVideo = new() { Content = "Choose video…" };
        AutomationProperties.SetName(chooseVideo, "Choose a video wallpaper");
        chooseVideo.Click += (_, _) => ChooseVideo();
        AutomationProperties.SetName(removeWallpaper, "Remove the video wallpaper");
        removeWallpaper.Style = (Style)FindResource("Studio.GhostButton");
        removeWallpaper.Click += (_, _) => Update(s => s with { LiveWallpaper = null });
        StackPanel wallpaperButtons = new() { Orientation = Orientation.Horizontal };
        wallpaperButtons.Children.Add(removeWallpaper);
        wallpaperButtons.Children.Add(chooseVideo);
        Grid wallpaperGrid = new();
        wallpaperGrid.ColumnDefinitions.Add(new ColumnDefinition());
        wallpaperGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel wallpaperText = new() { VerticalAlignment = VerticalAlignment.Center };
        wallpaperText.Children.Add(new TextBlock { Text = "Video background", Style = (Style)FindResource("Shell.Body") });
        wallpaperNote.Style = (Style)FindResource("Shell.Secondary");
        wallpaperNote.FontSize = 12;
        wallpaperNote.Margin = new Thickness(0, 2, 0, 0);
        wallpaperText.Children.Add(wallpaperNote);
        wallpaperGrid.Children.Add(wallpaperText);
        Grid.SetColumn(wallpaperButtons, 1);
        wallpaperButtons.VerticalAlignment = VerticalAlignment.Center;
        wallpaperGrid.Children.Add(wallpaperButtons);
        page.Children.Add(new Border { Style = (Style)FindResource("Studio.Row"), Child = wallpaperGrid });
        anchors.Add("wallpaper", chooseVideo);

        Grid frame = new();
        // Followed like the XAML pages' {DynamicResource}: the window narrows the padding on a small page and density changes it.
        frame.SetResourceReference(MarginProperty, "Shell.PagePadding");
        // The same content column as every other page (Shell.PageMaxWidth), so pages start and end in the same place.
        ColumnDefinition column = new() { Width = new GridLength(1, GridUnitType.Star) };
        column.SetResourceReference(ColumnDefinition.MaxWidthProperty, "Shell.PageMaxWidth");
        frame.ColumnDefinitions.Add(column);
        frame.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        frame.Children.Add(page);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = frame };
        context.Services.Settings.Changed += _ => Dispatcher.InvokeAsync(Refresh);
        // Moves, roll-ups and drops on the desktop save through the desktop service; say so when that fails.
        context.Services.Desktop.SaveFailed += message => Dispatcher.InvokeAsync(() => context.ShowNotice(NoticeSeverity.Caution, message));
        context.Services.Wallpaper.StatusChanged += status => Dispatcher.InvokeAsync(() =>
        {
            Refresh();
            if (status == LiveWallpaperStatus.CannotPlay) context.ShowNotice(NoticeSeverity.Caution, "That video can't be played. Try an MP4 file.");
        });
        Refresh();
    }

    public Destination Destination => Destination.Desktop;
    public string Title => "Desktop";
    public FrameworkElement View => this;
    public IEnumerable<SearchEntry> SearchEntries() => Entries;
    public bool Reveal(string anchor) => anchors.Reveal(anchor);
    public void OnShown() => Refresh();
    public void OnHidden() { }

    internal CheckBox WidgetSwitch(DesktopWidgetKind kind) => widgetSwitches[kind];
    internal CheckBox ArrangeSwitch => arrangeSwitch;

    internal void Refresh()
    {
        loading = true;
        try
        {
            AppSettings s = context.Services.Settings.Current;
            RefreshGroups(s.DesktopGroups);
            foreach ((DesktopWidgetKind kind, CheckBox toggle) in widgetSwitches) toggle.IsChecked = s.DesktopWidgets.Any(w => w.Kind == kind);
            arrangeSwitch.IsChecked = s.DesktopArrange;
            iconsSwitch.IsChecked = !DesktopService.DesktopIconsHidden();
            wallpaperNote.Text = WallpaperNote(s.LiveWallpaper, context.Services.Wallpaper.Status);
            removeWallpaper.Visibility = s.LiveWallpaper is null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { loading = false; }
    }

    internal static string WallpaperNote(string? video, LiveWallpaperStatus status) => video is null ? "Off" : Path.GetFileName(video) + " · " + status switch
    {
        LiveWallpaperStatus.FileMissing => "file not found",
        LiveWallpaperStatus.CannotPlay => "this video can't be played",
        LiveWallpaperStatus.NoDesktop => "the desktop wasn't found",
        _ => "muted, paused while a window is maximized"
    };

    /// <summary>
    /// Rebuilds the group rows only when groups were added, removed or reordered; otherwise updates them in place, so a
    /// click that ends a rename (Remove, say) still lands on its button.
    /// </summary>
    private void RefreshGroups(IReadOnlyList<DesktopGroup> groups)
    {
        groupsEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!groups.Select(g => g.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            rows.Clear();
            groupRows.Children.Clear();
            foreach (DesktopGroup group in groups)
            {
                GroupRowParts row = GroupRow(group.Id);
                rows.Add(row);
                groupRows.Children.Add(row.Row);
            }
        }
        for (int i = 0; i < groups.Count; i++)
        {
            DesktopGroup group = groups[i];
            GroupRowParts row = rows[i];
            if (!row.Name.IsKeyboardFocusWithin) row.Name.Text = group.Title;
            AutomationProperties.SetName(row.Name, "Group name: " + group.Title);
            AutomationProperties.SetName(row.Remove, "Remove group " + group.Title);
            row.Detail.Text = group.Folder is { } folder ? folder : group.Items.Count == 1 ? "1 item" : group.Items.Count + " items, drop more on the group";
        }
    }

    /// <summary>Adds a group; a folder group shows that folder's contents. Placed below the existing groups.</summary>
    internal void AddGroup(string? folder, string title)
    {
        Update(s =>
        {
            int index = s.DesktopGroups.Count;
            Rect area = SystemParameters.WorkArea;
            double left = area.Left + 24 + index % 4 * 300, top = area.Top + 24 + index / 4 % 3 * 260;
            DesktopGroup group = new(Guid.NewGuid().ToString("N"), title, folder, [], left, top, 280, 220);
            return s with { DesktopGroups = [.. s.DesktopGroups, group] };
        });
    }

    private void AddFolderGroup()
    {
        using System.Windows.Forms.FolderBrowserDialog dialog = new() { Description = "Folder to show on the desktop", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || !Directory.Exists(dialog.SelectedPath)) return;
        AddGroup(dialog.SelectedPath, new DirectoryInfo(dialog.SelectedPath).Name);
    }

    /// <summary>A group's row: its name (saved on Enter or when focus leaves) and Remove. Texts are filled by <see cref="RefreshGroups"/>.</summary>
    private GroupRowParts GroupRow(string id)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBox name = new() { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        DesktopGroup? Saved() => context.Services.Settings.Current.DesktopGroups.FirstOrDefault(g => g.Id == id);
        void Commit()
        {
            if (Saved() is not { } saved) return;
            string title = name.Text.Trim();
            if (title.Length == 0) name.Text = saved.Title;
            else if (title != saved.Title) Update(s => s with { DesktopGroups = [.. s.DesktopGroups.Select(g => g.Id == id ? g with { Title = title } : g)] });
        }
        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && Saved() is { } saved)
            {
                name.Text = saved.Title;
                name.SelectAll();
                e.Handled = true;
            }
        };
        text.Children.Add(name);
        TextBlock detail = new() { Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(detail);
        grid.Children.Add(text);
        Button remove = new() { Content = "Remove", Style = (Style)FindResource("Studio.GhostButton"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        remove.Click += (_, _) => Update(s => s with { DesktopGroups = [.. s.DesktopGroups.Where(g => g.Id != id)] });
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);
        return new GroupRowParts(id, new Border { Style = (Style)FindResource("Studio.Row"), Child = grid }, name, detail, remove);
    }

    private void ChooseVideo()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Title = "Video for the desktop", Filter = "Videos|*.mp4;*.wmv;*.mov;*.m4v;*.avi|All files|*.*" };
        if (dialog.ShowDialog(context.Owner) == true) Update(s => s with { LiveWallpaper = dialog.FileName });
    }

    private void SetWidget(DesktopWidgetKind kind, bool on) => Update(s =>
    {
        if (!on) return s with { DesktopWidgets = [.. s.DesktopWidgets.Where(w => w.Kind != kind)] };
        if (s.DesktopWidgets.Any(w => w.Kind == kind)) return s;
        Rect area = SystemParameters.WorkArea;
        double top = area.Top + 24 + s.DesktopWidgets.Count * 150;
        return s with { DesktopWidgets = [.. s.DesktopWidgets, new DesktopWidget(kind, area.Right - 260, top)] };
    });

    private void SetIcons(bool show)
    {
        if (loading) return;
        if (!DesktopService.SetDesktopIconsHidden(!show)) context.ShowNotice(NoticeSeverity.Caution, "The desktop was not found, so the icons were not changed.");
    }

    /// <summary>Saves a change; when the settings file can't be written, says so and shows the saved state again.</summary>
    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (loading) return;
        if (!PageSettings.TrySave(context, change)) Refresh();
    }

    private StackPanel Section(string title, string? credit, bool first = false)
    {
        // The first heading sits right under the page title, as on the other pages.
        StackPanel heading = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, first ? 0 : 18, 0, 8) };
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.CardTitle"), VerticalAlignment = VerticalAlignment.Bottom });
        if (credit is not null)
            heading.Children.Add(new TextBlock { Text = credit, Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(10, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom });
        return heading;
    }

    private CheckBox Switch(string name)
    {
        CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    private Border Row(string title, Control control)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.Body"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid };
    }
}
