using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Soltex.App.Settings;
using Soltex.App.Shell;
using Soltex.App.Tools;
using Soltex.Customize;

namespace Soltex.App.Pages;

/// <summary>Startup apps on the Tools page: one row per app with Task Manager's on/off switch.</summary>
public sealed class StartupAppsPanel : UserControl
{
    internal const string OnlyYou = "Only for you";
    internal const string EveryUser = "For every user · change it in Task Manager";

    private readonly StartupApps apps;
    private readonly StackPanel rows = new();
    private readonly ShellContext context;
    /// <summary>The apps shown, with their switches, in list order.</summary>
    private readonly List<(StartupApp App, CheckBox Toggle)> shown = [];
    private bool loading;

    internal StartupAppsPanel(ShellContext context, StartupApps? source = null)
    {
        this.context = context;
        apps = source ?? new StartupApps();
        StackPanel body = new();
        body.Children.Add(new TextBlock { Text = "Startup apps", Style = (Style)FindResource("Shell.CardTitle"), Margin = new Thickness(2, 10, 0, 8) });
        body.Children.Add(rows);
        Content = body;
        Refresh();
    }

    /// <summary>The switch shown for an app, or null when it is not listed.</summary>
    internal CheckBox? SwitchFor(string name) => shown.FirstOrDefault(s => s.App.Name == name).Toggle;

    /// <summary>
    /// Reads the list again (apps change in Task Manager, installers and Settings). When the same apps are listed, only
    /// their switches change, so a focused switch keeps focus; the rows are rebuilt only when apps came or went.
    /// </summary>
    internal void Refresh()
    {
        IReadOnlyList<StartupApp> list;
        try { list = apps.List(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { list = []; }
        loading = true;
        try
        {
            if (list.Count > 0 && list.Select(Key).SequenceEqual(shown.Select(s => Key(s.App))))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    shown[i] = (list[i], shown[i].Toggle);
                    shown[i].Toggle.IsChecked = list[i].Enabled;
                    shown[i].Toggle.IsEnabled = list[i].CanChange;
                }
                return;
            }
            rows.Children.Clear();
            shown.Clear();
            if (list.Count == 0)
            {
                rows.Children.Add(new TextBlock { Text = "No apps start at sign-in.", Style = (Style)FindResource("Shell.Secondary"), Margin = new Thickness(2, 0, 0, 8) });
                return;
            }
            foreach (StartupApp app in list) rows.Children.Add(BuildRow(app));
        }
        finally { loading = false; }
    }

    private static string Key(StartupApp app) => app.Source + "|" + app.Name + "|" + app.Command;

    private Border BuildRow(StartupApp app)
    {
        CheckBox toggle = new() { Style = (Style)FindResource("Studio.Switch"), IsChecked = app.Enabled, IsEnabled = app.CanChange, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        AutomationProperties.SetName(toggle, "Start " + app.Name + " at sign-in");
        int index = shown.Count;
        shown.Add((app, toggle));
        toggle.Checked += (_, _) => Set(index, true);
        toggle.Unchecked += (_, _) => Set(index, false);
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.Name, Style = (Style)FindResource("Shell.Body") });
        text.Children.Add(new TextBlock { Text = app.CanChange ? OnlyYou : EveryUser, Style = (Style)FindResource("Shell.Secondary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return new Border { Style = (Style)FindResource("Studio.Row"), Child = grid, ToolTip = app.Command };
    }

    private void Set(int index, bool enabled)
    {
        if (loading || index >= shown.Count) return;
        StartupApp app = shown[index].App;
        try { apps.SetEnabled(app, enabled); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            context.ShowNotice(NoticeSeverity.Caution, app.Name + ": " + error.Message);
            Refresh();
        }
    }
}

/// <summary>Export and import of this app's preferences and the Windows changes it made (Settings page).</summary>
public sealed class ProfilePanel : UserControl
{
    /// <summary>Suffix of the copy of the app settings kept before an import replaces them.</summary>
    internal const string BeforeImportSuffix = "before-import";

    private readonly ShellContext context;
    private readonly TextBlock status = new();

    internal ProfilePanel(ShellContext context)
    {
        this.context = context;
        StackPanel body = new();
        body.Children.Add(new TextBlock { Text = "Your setup", Style = (Style)FindResource("Shell.CardTitle"), Margin = new Thickness(2, 14, 0, 8) });

        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        StackPanel text = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = "Export or import", Style = (Style)FindResource("Shell.Body") });
        text.Children.Add(new TextBlock
        {
            Text = "Save this app's settings and the Windows options it set to a file, or restore them on this PC.",
            Style = (Style)FindResource("Shell.Secondary"),
            Margin = new Thickness(0, 2, 0, 0)
        });
        row.Children.Add(text);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // Both ask for a file first, so they end with "…" like the other buttons that open a dialog.
        Button export = new() { Content = "Export…", Margin = new Thickness(0, 0, 8, 0) };
        ExportButton = export;
        AutomationProperties.SetHelpText(export, "Saves this app's settings and the Windows options it set to a file");
        export.Click += (_, _) => Dispatcher.Handle(ExportAsync);
        Button import = new() { Content = "Import…" };
        AutomationProperties.SetHelpText(import, "Restores a saved setup on this PC");
        import.Click += (_, _) => Dispatcher.Handle(ImportAsync);
        buttons.Children.Add(export);
        buttons.Children.Add(import);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        body.Children.Add(new Border { Style = (Style)FindResource("Studio.Row"), Child = row });

        status.Style = (Style)FindResource("Shell.Secondary");
        status.Margin = new Thickness(2, 4, 0, 0);
        status.Visibility = Visibility.Collapsed;
        // Screen readers read the result of an export or import when it appears.
        Controls.LiveRegion.SetIsLive(status, true);
        body.Children.Add(status);
        Content = body;
    }

    /// <summary>Export… (search for the setup reveals it).</summary>
    internal Button ExportButton { get; }

    private void ShowStatus(string text)
    {
        status.Text = text;
        status.Visibility = Visibility.Visible;
        // The same result twice (two exports in a row) is read again too.
        Controls.LiveRegion.Notify(status);
    }

    private async Task ExportAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new() { Title = "Export your setup", Filter = "Setup|*.json", FileName = ProductIdentity.DisplayName + " setup.json" };
        if (dialog.ShowDialog(context.Owner) != true) return;
        ShellServices services = context.Services;
        try
        {
            SettingsProfile profile = await Task.Run(() => SettingsProfile.Capture(services.Settings.Current, services.Calm, services.SystemChanges.Inspector()));
            profile.Save(dialog.FileName);
            ShowStatus($"Saved: app settings, {profile.WindowsChoices.Count} Windows option(s) and {profile.SystemItems.Count} De-Windows item(s).");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            ShowStatus("The setup could not be saved: " + error.Message);
        }
    }

    private async Task ImportAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Title = "Import a setup", Filter = "Setup|*.json|All files|*.*" };
        if (dialog.ShowDialog(context.Owner) != true) return;
        SettingsProfile profile;
        try { profile = SettingsProfile.Load(dialog.FileName); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowStatus("That file could not be read: " + error.Message);
            return;
        }
        if (MessageBox.Show(context.Owner,
                $"Replace this app's settings and set {profile.WindowsChoices.Count} Windows option(s)?\n\n" +
                "The Windows options can be undone in History. A copy of this app's current settings is kept in the data folder.",
                "Import setup", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        // Network locations are opened as soon as they are shown (the wallpaper plays, groups list the folder),
        // which sends this PC's Windows sign-in to that computer. Keep them only when the user says so.
        IReadOnlyList<string> network = profile.NonLocalPaths();
        if (network.Count > 0)
        {
            MessageBoxResult keep = MessageBox.Show(context.Owner,
                (network.Count == 1 ? "This setup uses a location that is not on this PC:\n" : $"This setup uses {network.Count} locations that are not on this PC, such as:\n")
                + network[0] + "\n\nOpening it can send your Windows sign-in to that computer. Keep these locations?\n\n" +
                "Yes keeps them. No removes them from the wallpaper and desktop groups.",
                "Import setup", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.No);
            if (keep == MessageBoxResult.Cancel) return;
            if (keep != MessageBoxResult.Yes) profile = profile.WithoutNonLocalPaths();
        }

        ShellServices services = context.Services;
        string? copy;
        try
        {
            copy = services.Settings.SaveCopy(BeforeImportSuffix);
            services.Settings.Update(_ => profile.App);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("Nothing was imported. This app's settings could not be saved: " + error.Message);
            return;
        }

        IReadOnlyList<ChangeResult> results;
        try { results = await Task.Run(() => profile.ApplyWindowsChoices(services.Calm)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            services.RaiseCalmChanged();
            ShowStatus("App settings imported. The Windows options could not be set: " + error.Message);
            return;
        }
        services.RaiseCalmChanged();
        int failed = results.Count(r => r.Outcome is ChangeOutcome.Failed or ChangeOutcome.Refused);
        ShowStatus($"Imported. {results.Count(r => r.Outcome == ChangeOutcome.Applied)} Windows option(s) changed" + (failed > 0 ? $", {failed} not changed" : "")
            + (profile.SystemItems.Count > 0 ? $". {profile.SystemItems.Count} De-Windows item(s) need administrator: Home › Full De-Windows." : ".")
            + (copy is null ? "" : " Your previous app settings are in " + Path.GetFileName(copy) + "."));
    }
}
