using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Soltex.App.Pages;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

internal sealed record NavItem(Destination Destination, string Label, string Glyph, string Description);

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
    private static readonly NavItem HistoryItem = new(Destination.History, "History", "\uE81C", "Every change this app made, with undo");

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
        services.Drift.Found += report =>
        {
            tray?.ShowMessage(ProductIdentity.DisplayName, report.Summary);
            ShowNotice(NoticeSeverity.Caution, report.Summary, "Put back", () => Dispatcher.Handle(() => PutBackAsync(report)));
        };
        // "Use Windows setting", high contrast and Windows animation effects can change while the app runs
        // (the dark mode schedule does this twice a day).
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        followingSystem = true;
        if (!services.Settings.Current.WelcomeShown)
        {
            ShowNotice(NoticeSeverity.Information,
                $"Welcome to {ProductIdentity.DisplayName}. Nothing in Windows changes until you turn it on, and History can undo every change. Press Ctrl+K to find any setting.",
                null, null);
            services.TryUpdateSettings(s => s with { WelcomeShown = true });
        }
    }

    private async Task PutBackAsync(DriftReport report)
    {
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

    internal void Navigate(Destination destination, string? anchor = null)
    {
        IShellPage page = Page(destination);
        if (!ReferenceEquals(page, current))
        {
            current?.OnHidden();
            current = page;
            PageHost.Content = page.View;
            Title = page.Title + " - " + ProductIdentity.DisplayName;
            UpdatePageActivity();
        }
        syncingSelection = true;
        try
        {
            PrimaryNav.SelectedItem = PrimaryNav.Items.Cast<NavItem>().FirstOrDefault(i => i.Destination == destination);
            FooterNav.SelectedItem = destination switch { Destination.Settings => SettingsItem, Destination.History => HistoryItem, _ => null };
        }
        finally { syncingSelection = false; }
        if (anchor is not null)
        {
            Dispatcher.InvokeAsync(() => page.Reveal(anchor), System.Windows.Threading.DispatcherPriority.Loaded);
        }
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

    private void ApplyNavigationVisibility()
    {
        IReadOnlyList<Destination> hidden = services.Settings.Current.HiddenDestinations;
        PrimaryNav.ItemsSource = Primary.Where(i => !hidden.Contains(i.Destination)).ToArray();
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
            if (selected is { } destination && settings.HiddenDestinations.Contains(destination)) Navigate(settings.StartPage);
            else if (selected is { } same) Navigate(same);
        });
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingSelection) return;
        if (sender is ListBox { SelectedItem: NavItem item }) Navigate(item.Destination);
    }

    // ---------- notices ----------
    internal void ShowNotice(NoticeSeverity severity, string message, string? actionLabel, Action? action)
    {
        NoticeBar.Tag = severity.ToString();
        NoticeGlyph.Text = severity switch
        {
            NoticeSeverity.Success => "",
            NoticeSeverity.Caution => "",
            NoticeSeverity.Critical => "",
            _ => ""
        };
        NoticeText.Text = message;
        noticeAction = action;
        NoticeAction.Content = actionLabel;
        NoticeAction.Visibility = action is null || actionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBar.Visibility = Visibility.Visible;
        services.Report(severity, current?.Title ?? ProductIdentity.DisplayName, message);
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e) => NoticeBar.Visibility = Visibility.Collapsed;

    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        Action? action = noticeAction;
        NoticeBar.Visibility = Visibility.Collapsed;
        action?.Invoke();
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

    private void OpenSelectedResult()
    {
        if (SearchResults.SelectedItem is not SearchEntry entry) return;
        SearchOverlay.Visibility = Visibility.Collapsed;
        Navigate(entry.Destination, entry.Anchor);
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
                services.TryUpdateSettings(s => s with { CloseToTrayNoticeShown = true });
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
