using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Controls;
using Soltex.App.Pages;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

internal sealed record NavItem(Destination Destination, string Label, string Glyph, string Description)
{
    /// <summary>The tooltip on the icon rail, where the label itself is hidden.</summary>
    public string CompactTip => Label + ": " + Description;
}

/// <summary>What pages may ask of the shell.</summary>
internal sealed class ShellContext(ShellServices services, ShellWindow window)
{
    public ShellServices Services { get; } = services;
    public Window Owner => window;
    public void Navigate(Destination destination, string? anchor = null) => window.Navigate(destination, anchor);
    public void ShowNotice(NoticeSeverity severity, string message, string? actionLabel = null, Action? action = null) =>
        window.ShowNotice(severity, message, actionLabel, action);
    public void RefreshSearch() => window.RebuildSearchIndex();
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The tray icon is disposed in ShutdownAsync, the window's only exit path.")]
public partial class ShellWindow : Window, ISuiteWindow
{
    private static readonly NavItem[] Primary =
    [
        new(Destination.MyPc, "Home", "\uE80F", "De-Windows, protection and performance"),
        new(Destination.Taskbar, "Taskbar", "\uE7F4", "Taskbar look, buttons and behavior"),
        new(Destination.Start, "Start", "\uE8A9", "Start layout, recommendations and search"),
        new(Destination.Explorer, "File Explorer", "\uE8B7", "Extensions, menus and navigation pane"),
        new(Destination.Appearance, "Appearance", "\uE790", "Dark mode, transparency and accent color"),
        new(Destination.Multitasking, "Multitasking", "\uE7C4", "Snap, Alt+Tab and mouse"),
        new(Destination.Desktop, "Desktop", "\uE80A", "Groups, widgets and desktop icons"),
        new(Destination.Customize, "Privacy", "\uE72E", "Ads, tips and tracking"),
        new(Destination.Audio, "Audio", "\uE767", "Mixer, microphone and app volume"),
        new(Destination.Tools, "Tools", "\uE90F", "Remote help, scans and utilities")
    ];
    /// <summary>The pages in the rail, in order. Settings uses the same names for the start page and for hiding pages.</summary>
    internal static IReadOnlyList<NavItem> PrimaryPages => Primary;

    private static readonly NavItem SettingsItem = new(Destination.Settings, "Settings", "\uE713", "How this app looks and starts");
    private static readonly NavItem HistoryItem = new(Destination.History, "History", "\uE81C", "Windows settings this app changed, with undo");

    private readonly ShellServices services;
    private readonly bool renderSmoke;
    private readonly Dictionary<Destination, IShellPage> pages = [];
    private readonly CommandIndex search = new();
    private readonly ShellContext context;
    private IShellPage? current;
    private TrayIcon? tray;
    private Action? noticeAction;
    private AppSettings applied;
    private int themeRefreshQueued;
    private bool syncingSelection, quitRequested, shutdownStarted, shutdownComplete, followingSystem;
    /// <summary>A drift offer the user has not answered (Put back or closed), and whether the notice bar shows it now.</summary>
    private DriftReport? pendingDrift;
    private bool driftOnShow;
    private string? driftBalloonShown;

    internal ShellWindow(ShellServices services, bool renderSmoke)
    {
        this.services = services;
        this.renderSmoke = renderSmoke;
        context = new ShellContext(services, this);
        InitializeComponent();
        applied = services.Settings.Current;
        ApplyNavigationVisibility();
        FooterNav.ItemsSource = new[] { HistoryItem, SettingsItem };
        ThemeService.ApplyScale(ScaleRoot, services.Settings.Current);
        services.Settings.Changed += OnSettingsChanged;
        // The drift check runs only in the real session; the offer itself needs nothing from it.
        services.Drift.Found += OfferDrift;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += (_, _) => UpdatePageActivity();
        IsVisibleChanged += (_, _) => UpdatePageActivity();
        Closing += OnClosing;
        if (!renderSmoke)
        {
            try { AttachSession(); }
            catch
            {
                // Do not leave a tray icon or system listeners behind when start-up fails.
                DetachSystemTheme();
                tray?.Dispose();
                tray = null;
                throw;
            }
        }
        Navigate(services.Settings.Current.StartPage);
        // Settings that could not be loaded: say so where it is seen (the copy was kept before anything could save).
        if (services.SettingsProblem is { } problem) ShowSettingsProblem(problem);
    }

    /// <summary>The real session only: tray icon, shortcuts, taskbar, desktop, input tools and system theme changes.</summary>
    private void AttachSession()
    {
        tray = new TrayIcon(this, services);
        services.Utilities.Feedback += message => tray?.ShowMessage(ProductIdentity.DisplayName, message);
        services.Utilities.Attach(this);
        services.TaskbarLook.Attach(this);
        services.Desktop.Attach();
        services.AttachInputTools();
        // "Use Windows setting", high contrast and Windows animation effects can change while the app runs
        // (the dark mode schedule does this twice a day).
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        followingSystem = true;
        ShowWelcomeOnFirstRun();
        // Sonar's shortcuts and automatic output selection run from the Audio page's hidden Sonar tab; create the page
        // now (not shown) when either is on, so they work right after sign-in, even from the tray.
        if (Pages.AudioPage.SonarRunsInBackground(services.AudioPreferencesDirectory)) _ = Page(Destination.Audio);
    }

    /// <summary>
    /// The first-run welcome, once. Settings that failed to load are not a first run (they would read as defaults):
    /// their notice is shown instead, and nothing is saved.
    /// </summary>
    internal void ShowWelcomeOnFirstRun()
    {
        if (services.Settings.Current.WelcomeShown || services.SettingsProblem is not null) return;
        ShowNotice(NoticeSeverity.Information,
            $"Welcome to {ProductIdentity.DisplayName}. Nothing in Windows changes until you turn it on. Changes to Windows settings are listed in History, where you can undo them; quick options such as the taskbar look undo by switching them back. Press Ctrl+K to find any setting.",
            null, null);
        services.TryUpdateSettingsAutomatically(s => s with { WelcomeShown = true });
    }

    private void ShowSettingsProblem(SettingsLoadIssue issue)
    {
        string? copy = issue.CopyPath;
        ShowNotice(NoticeSeverity.Caution, issue.Message, copy is null ? null : "Show copy",
            copy is null ? null : () => ShowInFileExplorer(copy), area: "Settings");
    }

    private void ShowInFileExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ShowNotice(NoticeSeverity.Caution, "File Explorer could not be opened. The copy is at " + path + ".", null, null, area: "Settings");
        }
    }

    // ---------- drift offer ----------
    /// <summary>The drift difference on offer and not answered yet (null when there is none).</summary>
    internal DriftReport? PendingDrift => pendingDrift;

    /// <summary>
    /// Offers to put back what changed. The offer stays until the user answers it: a newer notice may cover it, and it
    /// comes back when that notice is closed. Closing the offer itself means "leave it" (remembered, not offered again).
    /// </summary>
    private void OfferDrift(DriftReport report)
    {
        pendingDrift = report;
        string key = AppSettings.NormalizeIdList(string.Join("|", report.Ids)) ?? "";
        if (key != driftBalloonShown)
        {
            // One balloon per difference per session; the notice itself comes back with each check until answered.
            driftBalloonShown = key;
            tray?.ShowMessage(ProductIdentity.DisplayName, report.Summary);
        }
        ShowDriftOffer(record: true);
    }

    private void ShowDriftOffer(bool record)
    {
        if (pendingDrift is not { } report || shutdownStarted) return;
        ShowNotice(NoticeSeverity.Caution, report.Summary, "Put back", () => Dispatcher.Handle(() => PutBackAsync(report)), record, "Your changes");
        driftOnShow = true;
    }

    private async Task PutBackAsync(DriftReport report)
    {
        if (ReferenceEquals(pendingDrift, report)) pendingDrift = null;
        try
        {
            DriftPutBack result = await services.Drift.PutBackAsync(report);
            ShowNotice(result.Succeeded ? NoticeSeverity.Success : NoticeSeverity.Caution, result.Message, null, null);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowNotice(NoticeSeverity.Caution, "Could not put your settings back: " + error.Message, null, null);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
            QueueThemeRefresh();
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation)) QueueThemeRefresh();
    }

    /// <summary>Windows sends several notices for one change; one refresh on the UI thread covers them.</summary>
    private void QueueThemeRefresh()
    {
        if (Interlocked.Exchange(ref themeRefreshQueued, 1) != 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref themeRefreshQueued, 0);
            if (!shutdownStarted) ThemeService.Apply(Application.Current, services.Settings.Current);
        }, DispatcherPriority.Background);
    }

    private void DetachSystemTheme()
    {
        if (!followingSystem) return;
        followingSystem = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    // ---------- navigation rail ----------
    /// <summary>The full navigation pane's width, and the icon rail's.</summary>
    internal const double ExpandedRailWidth = 240, CompactRailWidth = 64;
    /// <summary>The narrowest page area (in the scaled layout) the full pane may leave; below it the pane becomes an icon rail.</summary>
    internal const double MinimumPageWidth = 600;

    /// <summary>True while the navigation pane shows icons only.</summary>
    internal bool IsRailCompact { get; private set; }

    /// <summary>
    /// The text scale and the window size both change the room in the scaled layout. The rail's own width does not
    /// enter the decision, so it never flips back and forth.
    /// </summary>
    private void ScaleRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            ApplyRail(e.NewSize.Width - ExpandedRailWidth < MinimumPageWidth);
            ApplyPageSpacing(e.NewSize.Width - (IsRailCompact ? CompactRailWidth : ExpandedRailWidth));
        }
        FitRailHeight(e.NewSize.Height);
    }

    // ---------- rail height ----------
    /// <summary>The shortest navigation item a short window gets; below it the page list scrolls.</summary>
    internal const double MinimumRailItemHeight = 28;
    /// <summary>Items shorter than this make the product name step aside (and the spacing tighten) to give them room.</summary>
    private const double NamedMinimumItemHeight = 36;
    /// <summary>Right margin of a navigation item while its list scrolls: the Fluent scroll bar (12) is drawn over the list's edge.</summary>
    internal const double ScrollBarLane = 14;
    /// <summary>The product name's line and margins, and the rule above History and Settings with its margins.</summary>
    private const double ProductNameHeight = 42, RuleHeight = 13, TightRuleHeight = 7;

    /// <summary>True while the window is too short for the density's item height, so the rail uses shorter items.</summary>
    internal bool IsRailShort { get; private set; }
    /// <summary>True while the page list is taller than its room and scrolls.</summary>
    internal bool IsPageListScrolling { get; private set; }
    /// <summary>True while a very short window has the product name step aside and the spacing tighten.</summary>
    private bool railTight;

    /// <summary>
    /// Fits every navigation item in the window's height. With room, items keep the density's height. A little short, they
    /// shrink (not below 36). Shorter still, the product name steps aside, spacing tightens and items shrink down to
    /// <see cref="MinimumRailItemHeight"/>; only below that does the page list scroll. History and Settings stay pinned under
    /// their rule throughout. Decided from the layout height only, so it never flips back and forth.
    /// </summary>
    private void FitRailHeight(double height)
    {
        if (height <= 0) return;
        double standard = Application.Current?.TryFindResource("Shell.NavItemHeight") is double density ? density : 40;
        int items = Math.Max(1, PrimaryNav.Items.Count + FooterNav.Items.Count);
        double search = SearchButton.ActualHeight > 0 ? SearchButton.ActualHeight : 38;
        double margins = Rail.Margin.Top + Rail.Margin.Bottom;
        // The icon rail never shows the product name.
        double named = Math.Floor((height - margins - (IsRailCompact ? 0 : ProductNameHeight) - search - 16 - RuleHeight) / items) - 4;
        double itemHeight = Math.Min(standard, named);
        railTight = named < Math.Min(standard, NamedMinimumItemHeight);
        if (railTight)
            itemHeight = Math.Clamp(Math.Floor((height - margins - search - 8 - TightRuleHeight) / items) - 2, MinimumRailItemHeight, standard);
        IsRailShort = itemHeight < standard;
        if (!IsRailShort) Rail.Resources.Remove("Shell.NavItemHeight");
        else if (!Equals(Rail.Resources["Shell.NavItemHeight"], itemHeight)) Rail.Resources["Shell.NavItemHeight"] = itemHeight;
        FooterRule.Margin = railTight ? new Thickness(14, 3, 14, 3) : new Thickness(14, 6, 14, 6);
        ApplyRailChrome();
        ApplyNavItemMargins();
        KeepSelectedPageInView();
    }

    /// <summary>The product name and the search button's spacing follow both the rail width and height.</summary>
    private void ApplyRailChrome()
    {
        ProductNameText.Visibility = IsRailCompact || railTight ? Visibility.Collapsed : Visibility.Visible;
        double side = IsRailCompact ? 2 : 6;
        SearchButton.Margin = new Thickness(side, 0, side, railTight ? 8 : 16);
    }

    /// <summary>Item spacing: tighter in a very short window, and clear of the scroll bar while the page list scrolls.</summary>
    private void ApplyNavItemMargins()
    {
        double vertical = railTight ? 1 : 2;
        foreach (ListBox nav in new[] { PrimaryNav, FooterNav })
        {
            double lane = ReferenceEquals(nav, PrimaryNav) && IsPageListScrolling ? ScrollBarLane : 0;
            SetLocal(nav.Resources, "Shell.NavItemMargin", new Thickness(6, vertical, Math.Max(6, lane), vertical));
            SetLocal(nav.Resources, "Shell.NavItemCompactMargin", new Thickness(2, vertical, Math.Max(2, lane), vertical));
        }
    }

    private static void SetLocal(ResourceDictionary resources, string key, object value)
    {
        if (!Equals(resources[key], value)) resources[key] = value;
    }

    private void PrimaryNav_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer) return;
        bool scrolling = viewer.ExtentHeight > viewer.ViewportHeight + 0.01;
        if (scrolling == IsPageListScrolling) return;
        IsPageListScrolling = scrolling;
        ApplyNavItemMargins();
        KeepSelectedPageInView();
    }

    /// <summary>A short window may scroll the page list: the selected page is scrolled into view once layout settles.</summary>
    private void KeepSelectedPageInView() => Dispatcher.InvokeAsync(() =>
    {
        if (PrimaryNav.SelectedItem is { } selected) PrimaryNav.ScrollIntoView(selected);
    }, DispatcherPriority.Loaded);

    /// <summary>
    /// Pages take their padding from Shell.PagePadding and Shell.CardPadding. In a narrow page area the window supplies
    /// slimmer values for its own pages (they shadow the app-wide density values), so content keeps its room; with room
    /// to spare the window's values are removed and the density values apply again.
    /// </summary>
    private void ApplyPageSpacing(double pageWidth)
    {
        (Thickness page, Thickness card)? narrow = pageWidth switch
        {
            < 480 => (new Thickness(16, 20, 16, 28), new Thickness(14, 12, 14, 14)),
            < 720 => (new Thickness(24, 24, 24, 32), new Thickness(18, 14, 18, 16)),
            _ => null
        };
        if (narrow is { } values)
        {
            if (!Equals(Resources["Shell.PagePadding"], values.page)) Resources["Shell.PagePadding"] = values.page;
            if (!Equals(Resources["Shell.CardPadding"], values.card)) Resources["Shell.CardPadding"] = values.card;
        }
        else
        {
            Resources.Remove("Shell.PagePadding");
            Resources.Remove("Shell.CardPadding");
        }
        // Narrower than any page can use: the page keeps that width and scrolls sideways, with its scroll bar showing.
        // Otherwise the page fills the page area (never its own content's width), so every page's scroll bar is at the
        // window edge and every content column starts and is capped at the same place.
        bool scroll = pageWidth < MinimumScrollFreePageWidth;
        PageHost.Width = scroll ? MinimumScrollFreePageWidth : double.NaN;
        PageHost.HorizontalAlignment = scroll ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        PageViewport.HorizontalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    /// <summary>The narrowest page area pages are laid out for; below it the page area scrolls sideways.</summary>
    internal const double MinimumScrollFreePageWidth = 380;

    private void ApplyRail(bool compact)
    {
        if (compact == IsRailCompact) return;
        IsRailCompact = compact;
        RailColumn.Width = new GridLength(compact ? CompactRailWidth : ExpandedRailWidth);
        Rail.Margin = compact ? new Thickness(6, 18, 6, 14) : new Thickness(10, 18, 6, 14);
        // Search stays one press (or Ctrl+K) away: the button keeps its icon, name and tooltip.
        SearchLabel.Visibility = SearchShortcut.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchGlyph.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        SearchButton.Padding = compact ? new Thickness(0, 9, 0, 9) : new Thickness(14, 9, 14, 9);
        SearchButton.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ApplyRailChrome();
        foreach (ListBox nav in new[] { PrimaryNav, FooterNav })
        {
            nav.ItemContainerStyle = (Style)FindResource(compact ? "Shell.NavItemCompact" : "Shell.NavItem");
            nav.ItemTemplate = (DataTemplate)FindResource(compact ? "Shell.NavItemCompactTemplate" : "Shell.NavItemTemplate");
        }
    }

    internal AudioPage? Audio => pages.TryGetValue(Destination.Audio, out IShellPage? page) ? page as AudioPage : null;

    internal IShellPage Page(Destination destination)
    {
        if (!pages.TryGetValue(destination, out IShellPage? page))
        {
            page = destination switch
            {
                Destination.MyPc => new MyPcPage(context),
                _ when CustomizePage.Handles(destination) => new CustomizePage(context, destination),
                Destination.History => new HistoryPage(context),
                Destination.Audio => new AudioPage(context),
                Destination.Tools => new ToolsPage(context),
                Destination.Desktop => new DesktopPage(context),
                _ => new SettingsPage(context)
            };
            pages[destination] = page;
            RebuildSearchIndex();
        }
        return page;
    }

    internal void Navigate(Destination destination, string? anchor = null) => Navigate(destination, anchor, focusPage: false);

    /// <summary>
    /// Shows a page and selects it in the rail. A page hidden in Settings (opened from the tray, --audio or a link) joins
    /// the rail while it is open, so it is always shown as the selected page. With <paramref name="focusPage"/>, focus moves
    /// into the page even when the anchor names no control: to the page's first control, else to its rail item.
    /// </summary>
    internal void Navigate(Destination destination, string? anchor, bool focusPage)
    {
        IShellPage page = Page(destination);
        syncingSelection = true;
        try
        {
            if (!ReferenceEquals(page, current))
            {
                current?.OnHidden();
                current = page;
                PageHost.Content = page.View;
                Title = page.Title + " - " + ProductIdentity.DisplayName;
                UpdatePageActivity();
                ApplyNavigationVisibility();
            }
            PrimaryNav.SelectedItem = PrimaryNav.Items.Cast<NavItem>().FirstOrDefault(i => i.Destination == destination);
            FooterNav.SelectedItem = destination switch { Destination.Settings => SettingsItem, Destination.History => HistoryItem, _ => null };
        }
        finally { syncingSelection = false; }
        KeepSelectedPageInView();
        if (anchor is not null || focusPage)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (anchor is not null) page.Reveal(anchor);
                if (focusPage && !FocusIsInside(page.View)) FocusPageOrRail(page);
            }, DispatcherPriority.Loaded);
        }
    }

    private bool FocusIsInside(DependencyObject root)
    {
        DependencyObject? node = FocusManager.GetFocusedElement(this) as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = (node is Visual ? VisualTreeHelper.GetParent(node) : null) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Focuses the page's first control in tab order, or the page's rail item when the page has none.</summary>
    private void FocusPageOrRail(IShellPage page)
    {
        if (FirstTabStop(page.View) is { } control)
        {
            (control as FrameworkElement)?.BringIntoView();
            control.Focus();
            return;
        }
        ListBox nav = FooterNav.SelectedItem is not null ? FooterNav : PrimaryNav;
        if (nav.SelectedItem is { } item && nav.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
    }

    private static UIElement? FirstTabStop(DependencyObject root)
    {
        if (root is UIElement { Focusable: true, IsEnabled: true, IsVisible: true } element and not ScrollViewer && KeyboardNavigation.GetIsTabStop(element))
            return element;
        if (root is UIElement { IsVisible: false }) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FirstTabStop(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private void UpdatePageActivity()
    {
        if (current is null) return;
        bool active = IsVisible && WindowState != WindowState.Minimized && !shutdownStarted;
        if (active) current.OnShown(); else current.OnHidden();
    }

    internal void RebuildSearchIndex()
    {
        // Every destination contributes entries even before its page is opened, so search can reach it.
        List<SearchEntry> entries = [];
        foreach (Destination destination in Enum.GetValues<Destination>())
        {
            if (services.Settings.Current.HiddenDestinations.Contains(destination)) continue;
            entries.AddRange(pages.TryGetValue(destination, out IShellPage? page)
                ? page.SearchEntries()
                : StaticSearch.For(destination));
        }
        search.Replace(entries);
    }

    /// <summary>The rail lists the pages not hidden in Settings, plus a hidden page while it is the open one.</summary>
    private void ApplyNavigationVisibility()
    {
        IReadOnlyList<Destination> hidden = services.Settings.Current.HiddenDestinations;
        Destination? open = current?.Destination;
        NavItem[] items = [.. Primary.Where(i => !hidden.Contains(i.Destination) || i.Destination == open)];
        if (PrimaryNav.ItemsSource is NavItem[] shown && shown.SequenceEqual(items)) return;
        bool syncing = syncingSelection;
        syncingSelection = true;
        try { PrimaryNav.ItemsSource = items; }
        finally { syncingSelection = syncing; }
        if (ScaleRoot.ActualHeight > 0) FitRailHeight(ScaleRoot.ActualHeight);
    }

    /// <summary>Applies only what changed: most changes (desktop groups, taskbar tint, remaps) need nothing from the shell.</summary>
    private void OnSettingsChanged(AppSettings settings)
    {
        Dispatcher.InvokeAsync(() =>
        {
            AppSettings previous = applied;
            applied = settings;
            if (shutdownStarted) return;
            if (settings.Theme != previous.Theme || settings.Density != previous.Density || settings.Motion != previous.Motion)
                ThemeService.Apply(Application.Current, settings);
            if (settings.ScalePercent != previous.ScalePercent) ThemeService.ApplyScale(ScaleRoot, settings);
            if (settings.StartPage == previous.StartPage && settings.HiddenDestinations.SequenceEqual(previous.HiddenDestinations)) return;
            Destination? selected = current?.Destination;
            ApplyNavigationVisibility();
            RebuildSearchIndex();
            // Leave the open page only when this change hid it (a page opened while hidden stays open).
            if (selected is { } destination && settings.HiddenDestinations.Contains(destination) && !previous.HiddenDestinations.Contains(destination))
                Navigate(settings.StartPage);
            else if (selected is { } same) Navigate(same);
        });
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection) return;
        if (sender is ListBox { SelectedItem: NavItem item }) Navigate(item.Destination);
    }

    // ---------- notices ----------
    /// <summary>
    /// Shows a notice above the page (replacing the one shown) and records it in Recent events unless
    /// <paramref name="record"/> is false. Screen readers read it: politely, or at once for a critical notice.
    /// </summary>
    internal void ShowNotice(NoticeSeverity severity, string message, string? actionLabel, Action? action, bool record = true, string? area = null)
    {
        driftOnShow = false;
        NoticeBar.Tag = severity.ToString();
        NoticeGlyph.Text = severity switch
        {
            NoticeSeverity.Success => "",
            NoticeSeverity.Caution => "",
            NoticeSeverity.Critical => "",
            _ => ""
        };
        NoticeText.Text = message;
        // The icon carries the severity for sighted users; the spoken name carries it for screen reader users.
        AutomationProperties.SetName(NoticeText, severity switch
        {
            NoticeSeverity.Caution => "Warning: " + message,
            NoticeSeverity.Critical => "Problem: " + message,
            _ => message
        });
        AutomationProperties.SetLiveSetting(NoticeText, severity == NoticeSeverity.Critical ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        noticeAction = action;
        NoticeAction.Content = actionLabel;
        NoticeAction.Visibility = action is null || actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBar.Visibility = Visibility.Visible;
        // Read every notice, also one whose text is the same as the notice before it.
        LiveRegion.Notify(NoticeText);
        if (record) services.Report(severity, area ?? current?.Title ?? ProductIdentity.DisplayName, message);
    }

    private void HideNotice()
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        driftOnShow = false;
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e)
    {
        bool wasDrift = driftOnShow;
        HideNotice();
        if (wasDrift && pendingDrift is { } report)
        {
            // "Leave it as it is": remembered, so these differences are not offered again.
            pendingDrift = null;
            services.Drift.Dismiss(report);
        }
        else ShowDriftOffer(record: false); // an unanswered offer comes back once the newer notice is closed
    }

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        Action? action = noticeAction;
        bool wasDrift = driftOnShow;
        HideNotice();
        if (wasDrift) pendingDrift = null; // answered: Put back runs now
        action?.Invoke();
        if (!wasDrift && NoticeBar.Visibility == Visibility.Collapsed) ShowDriftOffer(record: false);
    }

    // ---------- search ----------
    private void SearchButton_Click(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        RebuildSearchIndex();
        SearchOverlay.Visibility = Visibility.Visible;
        SearchBox.Text = string.Empty;
        SearchResults.ItemsSource = null;
        SearchHint.Visibility = Visibility.Visible;
        SearchBox.Focus();
    }

    /// <summary>Render-test hook: shows the search overlay with a query typed in.</summary>
    internal void OpenSearchForCapture(string query)
    {
        OpenSearch();
        SearchBox.Text = query;
    }

    private void CloseSearch()
    {
        SearchOverlay.Visibility = Visibility.Collapsed;
        SearchButton.Focus();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        IReadOnlyList<SearchEntry> found = search.Find(SearchBox.Text);
        SearchResults.ItemsSource = found;
        if (found.Count > 0) SearchResults.SelectedIndex = 0;
        bool typed = !string.IsNullOrWhiteSpace(SearchBox.Text);
        SearchHint.Text = typed && found.Count == 0
            ? "No matches. Try a shorter word, or words like microphone, theme or scan."
            : "Try: microphone, dark mode, scan a file, start at sign-in, remote help.";
        SearchHint.Visibility = typed && found.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int count = SearchResults.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex + 1) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true; break;
            case Key.Up when count > 0:
                SearchResults.SelectedIndex = (SearchResults.SelectedIndex - 1 + count) % count;
                SearchResults.ScrollIntoView(SearchResults.SelectedItem);
                e.Handled = true; break;
            case Key.Enter:
                OpenSelectedResult(); e.Handled = true; break;
            case Key.Escape:
                CloseSearch(); e.Handled = true; break;
        }
    }

    private void SearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelectedResult(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
    }

    private void SearchResults_MouseUp(object sender, MouseButtonEventArgs e) => OpenSelectedResult();

    internal void OpenSelectedResult()
    {
        if (SearchResults.SelectedItem is not SearchEntry entry) return;
        SearchOverlay.Visibility = Visibility.Collapsed;
        // Focus lands on the setting, or (for a page or section result) on the page's first control, never nowhere.
        Navigate(entry.Destination, entry.Anchor, focusPage: true);
    }

    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseSearch();
    private void SearchCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key == Key.K || e.Key == Key.F || e.Key == Key.E))
        {
            OpenSearch(); e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Q)
        {
            RequestQuit(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchOverlay.Visibility == Visibility.Visible)
        {
            CloseSearch(); e.Handled = true;
        }
    }

    // ---------- lifecycle ----------
    public void ShowSuite()
    {
        if (shutdownStarted) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Tray "Open audio" and --audio. Audio opens and is selected in the rail even when it is hidden in Settings.</summary>
    public void OpenAudio()
    {
        ShowSuite();
        Navigate(Destination.Audio);
    }

    public void RequestQuit()
    {
        if (shutdownStarted) return;
        if (!renderSmoke && services.Settings.Current.ConfirmQuitWhileAudioRuns && Audio?.IsAudioRunning == true)
        {
            QuitChoice choice = TrayIcon.AskQuitWhileAudioRuns(this);
            if (choice == QuitChoice.Cancel) return;
            if (choice == QuitChoice.QuitAndStopAsking)
                services.TryUpdateSettings(s => s with { ConfirmQuitWhileAudioRuns = false });
        }
        quitRequested = true;
        Close();
    }

    /// <summary>Called by the sign-in start path when the user asked for audio to resume.</summary>
    internal async Task StartAudioAtSignInAsync()
    {
        if (Page(Destination.Audio) is AudioPage audio)
        {
            try { await audio.StartAudioInBackgroundAsync(); }
            catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or OperationCanceledException)
            {
                services.Report(NoticeSeverity.Caution, "Audio", "Audio could not start automatically: " + error.Message);
                tray?.ShowMessage("Audio did not start", "Open " + ProductIdentity.DisplayName + " to see why and try again.");
            }
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        if (!quitRequested && !renderSmoke)
        {
            Hide();
            if (!services.Settings.Current.CloseToTrayNoticeShown)
            {
                tray?.ShowMessage(ProductIdentity.DisplayName + " is still running",
                    "It stays in the notification area so audio keeps working. Right-click its icon and choose Quit to close it.");
                services.TryUpdateSettingsAutomatically(s => s with { CloseToTrayNoticeShown = true });
            }
            return;
        }
        if (shutdownStarted) return;
        shutdownStarted = true;
        IsEnabled = false;
        current?.OnHidden();
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        DetachSystemTheme();
        try
        {
            foreach (IShellPage page in pages.Values)
            {
                try
                {
                    if (page is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if (page is IDisposable disposable) disposable.Dispose();
                }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine("Page shutdown failed: " + error); }
            }
            await services.DisposeAsync();
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("Shutdown failed: " + error); }
        finally
        {
            tray?.Dispose();
            shutdownComplete = true;
            _ = Dispatcher.InvokeAsync(Close);
        }
    }

    internal void RefreshTray() => tray?.Rebuild();
}
