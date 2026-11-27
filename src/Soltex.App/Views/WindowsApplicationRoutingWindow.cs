using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Soltex.Audio.Sessions;
using Soltex.Audio.WindowsRouting;

namespace Soltex.App.Views;

/// <summary>One explicit, identity-bound application route. Opening this window changes no audio policy.</summary>
public sealed class WindowsApplicationRoutingWindow : Window
{
    private readonly AudioSessionInfo _selected;
    private readonly string _directory;
    private readonly ComboBox _target = new() { MinHeight = 36, DisplayMemberPath = nameof(RouteChoice.Label) };
    private readonly Button _preview = new() { Content = "Check route", Padding = new Thickness(14, 7, 14, 7) };
    private readonly Button _apply = new() { Content = "Apply output", Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private readonly Button _restore = new() { Content = "Undo selected route", Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
    private readonly Button _saved = new() { Content = "Use saved choice", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _import = new() { Content = "Import rules…", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _export = new() { Content = "Export rules…", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _historyButton = new() { Content = "Recovery history", Padding = new Thickness(12, 6, 12, 6) };
    private readonly ComboBox _history = new() { MinHeight = 32, DisplayMemberPath = nameof(HistoryChoice.Label), Visibility = Visibility.Collapsed };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
    private readonly List<RouteChoice> _choices;
    private AppRoutingService? _service;
    private Task? _operation, _shutdown;
    private bool _closing, _closed, _routeChecked;
    private string? _journal;
    // ToString is what a screen reader announces for a list item.
    private sealed record RouteChoice(string? Id, string Label) { public override string ToString() => Label; }
    private sealed record HistoryChoice(string Path, string Label) { public override string ToString() => Label; }

    public WindowsApplicationRoutingWindow(AudioSessionInfo selected, IReadOnlyList<AudioSessionEndpoint> endpoints, string directory)
    {
        _selected = selected; _directory = directory;
        Title = "Application output · " + selected.Name;
        Width = 600; Height = 480; MinWidth = 440; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _choices = [new(null, "Windows default")];
        // Devices that share a name are numbered "(2)", "(3)" like the rest of the app, never labelled with internal IDs.
        _choices.AddRange(endpoints.Select(e => new RouteChoice(e.Id, e.Name + Soltex.Audio.UI.DeviceNames.DuplicateSuffix(endpoints, e, endpoint => endpoint.Name))));
        _target.ItemsSource = _choices; _target.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(_target, "Output for " + selected.Name);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = selected.Name, FontSize = 23, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = "Choose where this application plays", Margin = new Thickness(0, 8, 0, 8) });
        panel.Children.Add(_target);
        panel.Children.Add(new TextBlock { Text = "Windows plays this app on the output you choose. If it is already playing, the app may need to restart its sound to switch.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        var actions = new WrapPanel { Margin = new Thickness(0, 15, 0, 0) };
        foreach (var button in new[] { _preview, _apply, _restore }) { button.Margin = new Thickness(0, 0, 8, 8); actions.Children.Add(button); }
        panel.Children.Add(actions);
        var files = new WrapPanel();
        foreach (var button in new[] { _saved, _import, _export, _historyButton }) { button.Margin = new Thickness(0, 0, 8, 8); files.Children.Add(button); }
        panel.Children.Add(files); panel.Children.Add(_history); panel.Children.Add(_feedback);
        System.Windows.Automation.AutomationProperties.SetName(_history, "Previous routes for " + selected.Name);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _feedback.Text = "Select an output, then check the route. Nothing changes until you apply it.";
        _target.SelectionChanged += (_, _) => { _routeChecked = false; _apply.IsEnabled = false; };
        _preview.Click += (_, _) => Begin(PreviewAsync);
        _apply.Click += (_, _) => Begin(ApplyAsync);
        _restore.Click += (_, _) => Begin(RestoreAsync);
        _saved.Click += (_, _) => Begin(LoadChoiceAsync);
        _import.Click += (_, _) => Import();
        _export.Click += (_, _) => Export();
        _historyButton.Click += (_, _) => Begin(LoadHistoryAsync);
        _history.SelectionChanged += (_, _) =>
        {
            if (_history.SelectedItem is HistoryChoice choice) _journal = choice.Path;
            _restore.IsEnabled = !_closing && _operation is not { IsCompleted: false } && _journal is not null;
        };
        Closing += OnClosing;
    }

    private async Task<AppRoutingService> ServiceAsync()
    {
        // Native activation is deliberately delayed until an explicit user action.
        return _service ??= await Task.Run(() => new AppRoutingService(_directory));
    }

    private async Task<AudioSessionInfo> CurrentSelectionAsync()
    {
        await using AudioSessionService observer = new();
        var snapshot = await observer.GetSnapshotAsync();
        return ResolveSelection(_selected, snapshot);
    }

    internal static AudioSessionInfo ResolveSelection(AudioSessionInfo original, AudioSessionSnapshot snapshot)
    {
        if (snapshot.Errors.Count != 0) throw new IOException("Application sessions could not be fully refreshed.");
        var candidates = snapshot.Endpoints.SelectMany(endpoint => endpoint.Sessions).Where(session =>
            session.Key.ProcessId == original.Key.ProcessId && session.Key.ProcessStartTimeUtc == original.Key.ProcessStartTimeUtc &&
            string.Equals(session.ExecutablePath, original.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            session.State != AudioSessionState.Expired && session.CanControl && !session.IsSystemSounds).ToArray();
        return candidates.FirstOrDefault(session => session.Key == original.Key) ?? candidates.FirstOrDefault() ??
            throw new IOException("This application's original process is no longer available. Select its current session from the mixer.");
    }

    private void Begin(Func<Task> action)
    {
        if (_closing || _operation is { IsCompleted: false }) return;
        _operation = RunAsync(action);
    }

    private async Task RunAsync(Func<Task> action)
    {
        SetBusy(true);
        try { await action(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or
            System.Runtime.InteropServices.COMException or System.Text.Json.JsonException or OperationCanceledException or NotSupportedException)
        { _feedback.Text = "The route was not completed: " + error.Message; _routeChecked = false; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var button in new[] { _preview, _saved, _import, _export, _historyButton }) button.IsEnabled = !busy && !_closing;
        _history.IsEnabled = !busy && !_closing;
        _target.IsEnabled = !busy && !_closing;
        _restore.IsEnabled = !busy && !_closing && _journal is not null;
        _apply.IsEnabled = !busy && !_closing && _routeChecked;
    }

    private async Task PreviewAsync()
    {
        string? destination = (_target.SelectedItem as RouteChoice)?.Id;
        _feedback.Text = "Checking application identity and output availability…";
        var service = await ServiceAsync();
        var current = await CurrentSelectionAsync();
        var preview = await Task.Run(() => service.PreviewAsync(current, destination));
        _feedback.Text = preview.Message; _routeChecked = preview.Allowed;
    }
    private async Task ApplyAsync()
    {
        _routeChecked = false;
        string? destination = (_target.SelectedItem as RouteChoice)?.Id;
        _feedback.Text = "Applying the selected application's output…";
        var service = await ServiceAsync();
        var current = await CurrentSelectionAsync();
        var result = await Task.Run(() => service.RouteAsync(current, destination));
        if (!string.IsNullOrWhiteSpace(result.JournalPath)) _journal = result.JournalPath;
        _feedback.Text = result.Message + (result.RequiresStreamRestart ? " Reopen playback in the application to move an existing stream." : "");
        _routeChecked = false;
    }
    private async Task RestoreAsync()
    {
        if (_journal is null) return;
        var service = await ServiceAsync();
        string journal = _journal;
        _feedback.Text = "Restoring the previous application policy…";
        var current = await CurrentSelectionAsync();
        var result = await Task.Run(() => service.RestoreAsync(journal, current));
        _feedback.Text = result.Message;
        if (result.PolicyConfirmed) _journal = null;
        _routeChecked = false;
    }
    private async Task LoadChoiceAsync()
    {
        var profile = await new RoutingProfileStore(_directory).LoadAsync();
        var rule = profile.Rules.FirstOrDefault(r => r.Direction == RoutingDirection.Render &&
            string.Equals(r.ExecutablePath, _selected.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (rule is null) { _feedback.Text = "No saved output choice for this application."; return; }
        var choice = _choices.FirstOrDefault(c => c.Id == rule.EndpointId);
        if (choice is null) { _feedback.Text = "The saved output is unavailable. Choose an available device."; return; }
        _target.SelectedItem = choice; _routeChecked = false;
        _feedback.Text = "Saved choice loaded. Check the route before applying it.";
    }
    private async Task LoadHistoryAsync()
    {
        var history = await (await ServiceAsync()).GetHistoryAsync();
        var choices = history.Where(h => h.Direction == RoutingDirection.Render &&
                string.Equals(h.ExecutablePath, _selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            .Select(h => new HistoryChoice(h.JournalPath, $"{h.CreatedAtUtc.ToLocalTime():g} · {h.State}")).ToArray();
        _history.ItemsSource = choices; _history.Visibility = choices.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _feedback.Text = choices.Length == 0 ? "No recovery records for this application." :
            "Select a previous route, then use Undo selected route. Newer Windows choices will be preserved.";
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import application routing rules", Filter = "Routing profiles (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        Begin(async () => { var profile = await new RoutingProfileStore(_directory).ImportAsync(dialog.FileName);
            _feedback.Text = $"Imported {profile.Rules.Count} saved rules. No application routes were changed."; _routeChecked = false; });
    }
    private void Export()
    {
        var dialog = new SaveFileDialog { Title = "Export application routing rules", Filter = "Routing profiles (*.json)|*.json", FileName = "Soltex-app-routes.json" };
        if (dialog.ShowDialog(this) != true) return;
        Begin(async () => { await new RoutingProfileStore(_directory).ExportAsync(dialog.FileName); _feedback.Text = "Saved application rules exported."; });
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        await ShutdownAsync();
    }
    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        _closing = true; SetBusy(true);
        if (_operation is not null) await _operation;
        if (_service is not null) await _service.DisposeAsync();
        _closed = true; Close();
    }
}
