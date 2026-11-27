using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App.Pages;

public partial class SettingsPage : UserControl, IShellPage
{
    internal static readonly SearchEntry[] Entries =
    [
        new("Settings", "Settings", Destination.Settings, "top", "preferences", "options"),
        new("App theme: light, dark or Windows", "Settings › Appearance", Destination.Settings, "theme", "dark mode", "light mode", "colors", "colours", "appearance"),
        new("Text and control size", "Settings › Appearance", Destination.Settings, "scale", "zoom", "bigger text", "font size", "accessibility"),
        new("Spacing: comfortable or compact", "Settings › Appearance", Destination.Settings, "density", "compact", "dense"),
        new("Reduce animations", "Settings › Appearance", Destination.Settings, "motion", "animation", "reduced motion"),
        new("Start when I sign in to Windows", "Settings › Starting and closing", Destination.Settings, "startup", "launch at login", "boot", "autostart"),
        new("Start audio automatically after sign-in", "Settings › Starting and closing", Destination.Settings, "startaudio", "auto start audio", "resume audio", "microphone at login"),
        new("Start page", "Settings › Starting and closing", Destination.Settings, "startpage", "home page", "default page"),
        new("Ask before quitting while audio is running", "Settings › Starting and closing", Destination.Settings, "confirmquit", "quit", "exit", "close"),
        new("Show or hide pages", "Settings › Pages", Destination.Settings, "navigation", "hide", "menu", "sidebar", "navigation"),
        new("Keyboard shortcuts", "Settings › Keyboard shortcuts", Destination.Settings, "shortcuts", "hotkeys", "keys"),
        new("Privacy and data folder", "Settings › Privacy and data", Destination.Settings, "data", "privacy", "logs", "crash reports", "folder"),
        new("Recent events", "Settings › Recent events", Destination.Settings, "events", "errors", "history", "problems"),
        new("About and version", "Settings › About", Destination.Settings, "about", "version")
    ];

    private static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("Ctrl+K, Ctrl+F", "Search settings and tools"),
        ("Up, Down, Enter", "Move through search results and open one"),
        ("Esc", "Close search"),
        ("Ctrl+Q", "Quit (closing the window keeps running in the notification area)"),
        ("Tab, Shift+Tab", "Move between controls"),
        ("Alt+F4", "Close the window to the notification area")
    ];

    private readonly ShellContext context;
    private readonly PageAnchors anchors = new();
    /// <summary>One switch per page the rail can hide, in rail order.</summary>
    private readonly Dictionary<Destination, CheckBox> pageSwitches = [];
    private bool loading, draggingScale;

    internal SettingsPage(ShellContext context)
    {
        this.context = context;
        InitializeComponent();
        ProfileHost.Content = new ProfilePanel(context);
        Intro.Text = $"How {ProductIdentity.DisplayName} looks, starts and behaves.";
        BuildPageChoices();
        anchors.Add("top", Intro);
        anchors.Add("theme", ThemeChoice);
        anchors.Add("scale", ScaleSlider);
        anchors.Add("density", DensityChoice);
        anchors.Add("motion", ReduceMotion);
        anchors.Add("startup", StartAtSignIn);
        anchors.Add("startaudio", StartAudio);
        anchors.Add("startpage", StartPageChoice);
        anchors.Add("confirmquit", ConfirmQuit);
        anchors.Add("navigation", pageSwitches.Values.First());
        anchors.Add("shortcuts", ShortcutGrid);
        anchors.Add("data", OpenDataFolder);
        anchors.Add("events", EventsSection);
        anchors.Add("about", AboutSection);
        for (int i = 0; i < Shortcuts.Length; i++)
        {
            ShortcutGrid.RowDefinitions.Add(new RowDefinition());
            TextBlock keys = new() { Text = Shortcuts[i].Keys, Style = (Style)FindResource("Shell.Body"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
            TextBlock action = new() { Text = Shortcuts[i].Action, Style = (Style)FindResource("Shell.Body"), Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(keys, i); Grid.SetRow(action, i); Grid.SetColumn(action, 1);
            ShortcutGrid.Children.Add(keys); ShortcutGrid.Children.Add(action);
        }
        DataFolderText.Text = "Folder: " + context.Services.DataRoot;
        // A long path is trimmed on a narrow page; the tooltip has all of it.
        DataFolderText.ToolTip = context.Services.DataRoot;
        EventsList.ItemsSource = context.Services.Notices;
        context.Services.Notices.CollectionChanged += (_, _) => UpdateEventsEmpty();
        UpdateEventsEmpty();
        AboutName.Text = ProductIdentity.DisplayName;
        AboutVersion.Text = "Version " + (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "development") +
            " · " + WindowsDescription.Describe() + " · " + (Environment.Is64BitProcess ? "64-bit" : "32-bit");
        context.Services.Settings.Changed += _ => Dispatcher.InvokeAsync(Load);
        Load();
    }

    public Destination Destination => Destination.Settings;
    public string Title => "Settings";
    public FrameworkElement View => this;
    public void OnShown() => Load();
    public void OnHidden() { }
    public IEnumerable<SearchEntry> SearchEntries() => Entries;
    public bool Reveal(string anchor) => anchors.Reveal(anchor);

    /// <summary>Start page choices and page switches use the rail's own names, so they always match what the user sees.</summary>
    private void BuildPageChoices()
    {
        foreach (NavItem page in ShellWindow.PrimaryPages)
        {
            StartPageChoice.Items.Add(new ComboBoxItem { Content = page.Label, Tag = page.Destination });

            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(new TextBlock { Text = page.Label, Style = (Style)FindResource("Shell.Body") });
            text.Children.Add(new TextBlock { Text = page.Description, Style = (Style)FindResource("Shell.Secondary"), Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(text);
            CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), VerticalAlignment = VerticalAlignment.Center, Tag = page.Destination };
            AutomationProperties.SetName(toggle, "Show " + page.Label);
            ToolTipService.SetShowOnDisabled(toggle, true);
            toggle.Checked += NavVisibility_Changed;
            toggle.Unchecked += NavVisibility_Changed;
            Grid.SetColumn(toggle, 1);
            grid.Children.Add(toggle);
            NavigationRows.Children.Add(new Border { Style = (Style)FindResource("Studio.Row"), Child = grid });
            pageSwitches[page.Destination] = toggle;
        }
    }

    private void UpdateEventsEmpty() =>
        EventsEmpty.Visibility = context.Services.Notices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Load()
    {
        loading = true;
        try
        {
            AppSettings s = context.Services.Settings.Current;
            ThemeChoice.SelectedIndex = (int)s.Theme;
            if (!draggingScale)
            {
                ScaleSlider.Value = s.ScalePercent;
                ScaleValue.Text = s.ScalePercent + "%";
            }
            DensityChoice.SelectedIndex = (int)s.Density;
            ReduceMotion.IsChecked = s.Motion == MotionPreference.Reduced;
            StartPageChoice.SelectedItem = StartPageChoice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag is Destination d && d == s.StartPage);
            ConfirmQuit.IsChecked = s.ConfirmQuitWhileAudioRuns;
            foreach ((Destination destination, CheckBox toggle) in pageSwitches)
            {
                toggle.IsChecked = !s.HiddenDestinations.Contains(destination);
                toggle.IsEnabled = s.StartPage != destination;
                toggle.ToolTip = toggle.IsEnabled ? null : "This is your start page.";
            }

            if (context.Services.ExplicitProfile)
            {
                // A test or portable profile must never register itself to run at sign-in.
                StartAtSignIn.IsChecked = false;
                StartAtSignIn.IsEnabled = false;
                StartAtSignInHint.Text = "Not available while running with a separate profile folder.";
            }
            else
            {
                StartAtSignIn.IsChecked = SuiteStartupRegistration.IsEnabled();
            }
            StartAudio.IsChecked = s.StartAudioWhenSignedIn;
            StartAudio.IsEnabled = StartAtSignIn.IsChecked == true;
        }
        finally { loading = false; }
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (loading) return;
        try { context.Services.Settings.Update(change); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "Your change could not be saved: " + error.Message);
            Load();
        }
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s with { Theme = (ThemePreference)Math.Max(0, ThemeChoice.SelectedIndex) });

    private void ScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleValue is null) return;
        ScaleValue.Text = (int)ScaleSlider.Value + "%";
        // While the thumb is dragged only the number changes: rescaling the window under the pointer would move the
        // slider and make the value jump. Keyboard and track clicks still apply at once.
        if (!draggingScale) Update(s => s with { ScalePercent = (int)ScaleSlider.Value });
    }

    private void ScaleSlider_DragStarted(object sender, DragStartedEventArgs e) => draggingScale = true;

    private void ScaleSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        draggingScale = false;
        Update(s => s with { ScalePercent = (int)ScaleSlider.Value });
    }

    private void DensityChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Update(s => s with { Density = (DensityPreference)Math.Max(0, DensityChoice.SelectedIndex) });

    private void ReduceMotion_Changed(object sender, RoutedEventArgs e) =>
        Update(s => s with { Motion = ReduceMotion.IsChecked == true ? MotionPreference.Reduced : MotionPreference.System });

    private void StartPageChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StartPageChoice.SelectedItem is not ComboBoxItem { Tag: Destination start }) return;
        Update(s => s with { StartPage = start, HiddenDestinations = s.HiddenDestinations.Where(d => d != start).ToArray() });
    }

    private void ConfirmQuit_Changed(object sender, RoutedEventArgs e) =>
        Update(s => s with { ConfirmQuitWhileAudioRuns = ConfirmQuit.IsChecked == true });

    private void StartAudio_Changed(object sender, RoutedEventArgs e) =>
        Update(s => s with { StartAudioWhenSignedIn = StartAudio.IsChecked == true });

    private void StartAtSignIn_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || context.Services.ExplicitProfile) return;
        bool wanted = StartAtSignIn.IsChecked == true;
        try
        {
            SuiteStartupRegistration.SetEnabled(wanted);
            bool actual = SuiteStartupRegistration.IsEnabled();
            if (actual != wanted)
                context.ShowNotice(NoticeSeverity.Caution, "Windows did not keep the sign-in setting. It may be managed by a policy.");
            else
                context.Services.Report(NoticeSeverity.Information, "Settings", wanted ? "Starts when you sign in." : "No longer starts when you sign in.");
            if (!actual) Update(s => s with { StartAudioWhenSignedIn = false });
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            context.ShowNotice(NoticeSeverity.Caution, "The sign-in setting could not be changed: " + error.Message);
        }
        Load();
    }

    private void NavVisibility_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        Destination[] hidden = [.. pageSwitches.Where(p => p.Value.IsChecked != true).Select(p => p.Key)];
        Update(s => s with { HiddenDestinations = hidden });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(context.Services.DataRoot);
    private void OpenCrashFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(context.Services.CrashReportDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            context.ShowNotice(NoticeSeverity.Caution, "The folder could not be opened: " + error.Message);
        }
    }
}
