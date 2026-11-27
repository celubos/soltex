using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Soltex.Audio.Sonar;
namespace Soltex.Audio.UI;

public partial class MixerWorkspace : UserControl
{
    private readonly MixerModel _model = new();
    private AudioDeviceService? _devices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _pendingLevels = [];
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private SonarClient? _sonar;
    private SonarSnapshot? _snapshot;
    private bool _applying, _refreshing, _readingMeters, _disposed;
    private Preferences _preferences = new();
    private HwndSource? _windowSource;
    private Task? _shutdown;
    private bool _initialized, _active;
    private Point _dragOrigin;

    private async Task ConnectAsync()
    {
        if (_disposed || !_initialized) return;
        bool entered = false;
        StatusText.Text = "Connecting to the installed Sonar engine…";
        ShowConnection(SonarConnection.Connecting);
        try
        {
            await _operations.WaitAsync(_lifetime.Token); entered = true;
            if (_disposed) return;
            var next = await SonarClient.DiscoverAsync(_lifetime.Token);
            if (_disposed) { next.Dispose(); return; }
            SonarSnapshot snapshot;
            try { snapshot = await next.GetSnapshotAsync(_lifetime.Token); }
            catch { next.Dispose(); throw; }
            if (_disposed) { next.Dispose(); return; }
            var previous = _sonar;
            _sonar = next; previous?.Dispose();
            Apply(snapshot);
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed) Disconnected(ex.Message); }
        finally { if (entered) _operations.Release(); }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Net.Http.HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ArgumentException or COMException;

    private void Disconnected(string error)
    {
        foreach (var channel in _model.Channels) { channel.IsReady = false; channel.CanSelectDevice = false; channel.Status = "Unavailable"; channel.Peak = -1; }
        foreach (var application in _model.Applications) { application.Status = "Connection unavailable"; application.Routes.Clear(); }
        LinkedDevice.IsEnabled = false;
        ChatMixSlider.IsEnabled = false;
        ChatMixStatus.Text = "ChatMix needs Sonar. It comes back when Sonar does.";
        StatusText.Text = "Sonar isn't available. Turn it on in SteelSeries GG, then choose Reconnect.";
        StatusText.ToolTip = error;
        ShowConnection(SonarConnection.Unavailable);
    }

    private async void ReconnectClick(object sender, RoutedEventArgs e) => await ConnectAsync();
    private async void StateTick(object? sender, EventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_refreshing || _sonar is null || _disposed) return;
        _refreshing = true;
        bool entered = false;
        try
        {
            await _operations.WaitAsync(_lifetime.Token); entered = true;
            if (_disposed || _sonar is null) return;
            var snapshot = await _sonar.GetSnapshotAsync(_lifetime.Token);
            if (!_disposed) Apply(snapshot);
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed) Disconnected(ex.Message); }
        finally { if (entered) _operations.Release(); _refreshing = false; }
    }

    private void Apply(SonarSnapshot snapshot)
    {
        if (_disposed) return;
        _applying = true;
        try
        {
            _snapshot = snapshot;
            LinkedDevice.IsEnabled = snapshot.IsReady;
            StatusText.ToolTip = null;
            var outputs = Choices(snapshot, "render");
            bool outputChanged = !outputs.SequenceEqual(_model.Outputs);
            ReplaceWhenChanged(_model.Outputs, outputs);
            var linkedIds = snapshot.Channels.Where(c => c.Id is "game" or "chat" or "media" or "aux").Select(c => c.DeviceId).Distinct().ToArray();
            LinkedDevice.SelectedValue = linkedIds.Length == 1 ? linkedIds[0] : null;
            foreach (var channel in _model.Channels)
            {
                var state = snapshot.Channels.Single(c => c.Id == channel.Id);
                channel.VirtualDeviceId = state.VirtualDeviceId;
                if (!_pendingLevels.ContainsKey(channel.Id)) channel.Level = state.Volume * 100;
                channel.Muted = state.Muted;
                channel.IsReady = ChannelReady(snapshot, state);
                channel.DeviceId = state.DeviceId;
                channel.DeviceName = snapshot.Devices.FirstOrDefault(d => d.Id == state.DeviceId)?.FriendlyName ?? (channel.Id == "master" ? "All playback channels" : "Device unavailable");
                ReplaceWhenChanged(channel.Devices, channel.Id == "master" ? [new DeviceChoice("", "All playback channels")] : Choices(snapshot, channel.Id == "mic" ? "capture" : "render"));
                channel.CanSelectDevice = channel.Id != "master" && channel.Devices.Count > 0 && snapshot.Devices.Any(d => d.Id == state.VirtualDeviceId && d.IsVad && d.State == "active");
                if (channel.Id == "master") channel.DeviceId = "";
                if (!_pendingLevels.ContainsKey(channel.Id)) channel.Status = channel.IsReady ? channel.Id == "master" ? "Playback master" : "Route active" : "Route unavailable";
            }
            UpdateApplications(snapshot);
            if (outputChanged) OutputsChanged?.Invoke(this, EventArgs.Empty);
            if (!snapshot.IsReady) StatusText.Text = "Turn on Sonar in SteelSeries GG to use its channels.";
            else StatusText.Text = "Connected to Sonar. Audio keeps playing when you close this window.";
            ShowConnection(snapshot.IsReady ? SonarConnection.Connected : SonarConnection.Off);
        }
        finally { _applying = false; }
        ScheduleOutputPolicy(snapshot);
    }

    private static bool ChannelReady(SonarSnapshot snapshot, SonarChannel channel)
    {
        if (channel.Id == "master")
        {
            var playback = snapshot.Channels.Where(c => c.Id is "game" or "chat" or "media" or "aux").ToArray();
            return playback.Length == 4 && playback.All(c => ChannelReady(snapshot, c));
        }
        string flow = channel.Id == "mic" ? "capture" : "render";
        return channel.IsRunning && snapshot.Devices.Any(d => d.Id == channel.VirtualDeviceId && d.IsVad && d.State == "active" && d.DataFlow == flow) &&
            snapshot.Devices.Any(d => d.Id == channel.DeviceId && !d.IsVad && d.State == "active" && d.DataFlow == flow);
    }

    private static DeviceChoice[] Choices(SonarSnapshot snapshot, string flow)
    {
        var devices = snapshot.Devices.Where(d => !d.IsVad && d.DataFlow == flow && d.State == "active").ToArray();
        return devices.Select(d => new DeviceChoice(d.Id, devices.Count(other => other.FriendlyName == d.FriendlyName) > 1 ? d.FriendlyName + " · " + d.Id[Math.Max(0, d.Id.Length - 9)..] : d.FriendlyName)).ToArray();
    }

    private static void ReplaceWhenChanged<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        var items = source.ToArray();
        if (items.SequenceEqual(destination)) return;
        destination.Clear(); foreach (T item in items) destination.Add(item);
    }

    private void UpdateApplications(SonarSnapshot snapshot)
    {
        var groups = snapshot.Applications.Where(a => a.ProcessId != 0 && !a.IsSystemSound && a.State != "expired").GroupBy(a => a.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + a.DataFlow).ToArray();
        var keys = groups.Select(ApplicationKey).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _model.Applications.Where(a => !keys.Contains(a.Key)).ToArray()) _model.Applications.Remove(old);
        foreach (var group in groups)
        {
            var observed = group.First();
            string key = ApplicationKey(group);
            var row = _model.Applications.FirstOrDefault(a => a.Key == key);
            if (row is null) { row = new(key, observed.ProcessId, observed.Name, observed.DataFlow); _model.Applications.Add(row); }
            row.SessionIds = group.Select(a => a.SessionId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var routes = snapshot.Channels.Where(c => row.SessionIds.Count > 0 && c.VirtualDeviceId is not null && ChannelReady(snapshot, c) && (observed.DataFlow == "capture" ? c.Id == "mic" : c.Id is "game" or "chat" or "media" or "aux")).Select(c => new RouteChoice(c.VirtualDeviceId!, c.Name));
            ReplaceWhenChanged(row.Routes, routes);
            var active = group.Where(a => a.State == "active").ToArray();
            var ids = (active.Length > 0 ? active : group.ToArray()).Select(a => a.DeviceId).Distinct(StringComparer.Ordinal).ToArray();
            row.RouteId = ids.Length == 1 ? ids[0] : null;
            row.Status = group.Any(a => a.RoutingErrorDetected) ? "Windows reported a routing error" : active.Length == 0 ? "Idle audio session" :
                active.Select(a => a.DeviceId).Distinct(StringComparer.Ordinal).Count() > 1 ? "Active sessions on different devices" :
                row.Routes.Any(r => r.Id == active[0].DeviceId) ? "Active session on this channel" : "Active on another Windows device";
        }
        EmptyApps.Visibility = _model.Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // A PID is reusable. Include the observed session identity and never persist it as an app preference.
    private static string ApplicationKey(IGrouping<string, SonarApplication> group) =>
        group.Key + ":" + string.Join("|", group.Select(a => a.SessionId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private async void MeterTick(object? sender, EventArgs e)
    {
        if (!_active || _devices is null || _readingMeters || !IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized || _disposed) return;
        _readingMeters = true;
        var snapshot = _snapshot;
        try
        {
            List<(ChannelModel Channel, string Id)> sources = [];
            foreach (var channel in _model.Channels)
            {
                string? id = channel.Id == "master" ? snapshot?.Channels.FirstOrDefault(c => c.Id == "game")?.DeviceId : channel.VirtualDeviceId;
                if (id is null || !channel.IsReady) { channel.Peak = -1; continue; }
                sources.Add((channel, id));
            }
            if (sources.Count == 0) return;
            var readings = await _devices.ReadPeaksAsync(sources.Select(source => source.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            if (_disposed || !IsVisible || Window.GetWindow(this)?.WindowState == WindowState.Minimized || !ReferenceEquals(snapshot, _snapshot)) return;
            foreach (var source in sources)
            {
                var reading = readings.First(result => string.Equals(result.EndpointId, source.Id, StringComparison.OrdinalIgnoreCase));
                source.Channel.Peak = reading.Peak is { } peak ? Math.Max(peak, Math.Max(0, source.Channel.Peak) * 0.72) : -1;
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed) foreach (var channel in _model.Channels) channel.Peak = -1; }
        finally { _readingMeters = false; }
    }

    private void DeviceSnapshotChanged(object? sender, AudioDeviceSnapshot snapshot)
    {
        if (!_disposed && !Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(async () => await RefreshAsync());
    }

    private async Task CommandAsync(Func<SonarClient, Task<SonarSnapshot>> command, ChannelModel? channel = null, Action? confirmed = null)
    {
        if (_sonar is null || _disposed) return;
        bool entered = false;
        if (channel is not null) channel.Status = "Applying…";
        try
        {
            await _operations.WaitAsync(_lifetime.Token); entered = true;
            if (_disposed || _sonar is null) return;
            var snapshot = await command(_sonar);
            if (!_disposed) { confirmed?.Invoke(); Apply(snapshot); StatusText.Text = "Change confirmed by Sonar."; }
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed) { StatusText.Text = ex.Message; if (channel is not null) channel.Status = "Change not confirmed"; } }
        finally { if (entered) _operations.Release(); }
    }

    private void FaderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_applying && IsLoaded && sender is Slider { DataContext: ChannelModel channel } && channel.IsReady) QueueLevel(channel, e.NewValue);
    }

    private async void QueueLevel(ChannelModel channel, double value)
    {
        if (!double.IsFinite(value) || _disposed) return;
        if (_pendingLevels.Remove(channel.Id, out var old)) old.Cancel();
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _pendingLevels[channel.Id] = cancel;
        channel.Level = Math.Clamp(value, 0, 100); channel.Status = "Adjusting…";
        double requested = channel.Level / 100;
        bool entered = false;
        try
        {
            await Task.Delay(65, cancel.Token);
            await _operations.WaitAsync(cancel.Token); entered = true;
            if (_disposed || cancel.IsCancellationRequested) return;
            if (_sonar is not null)
            {
                // Cancellation coalesces pending intents; once sent, keep its confirmation ordered.
                var snapshot = await _sonar.SetVolumeAsync(channel.Role, requested, _lifetime.Token);
                if (_disposed) return;
                if (_pendingLevels.TryGetValue(channel.Id, out var current) && ReferenceEquals(current, cancel)) { _pendingLevels.Remove(channel.Id); Apply(snapshot); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed && _pendingLevels.TryGetValue(channel.Id, out var active) && ReferenceEquals(active, cancel)) { channel.Status = "Level not confirmed"; StatusText.Text = ex.Message; } }
        finally
        {
            if (entered) _operations.Release();
            if (_pendingLevels.TryGetValue(channel.Id, out var current) && ReferenceEquals(current, cancel)) _pendingLevels.Remove(channel.Id);
            cancel.Dispose();
        }
    }

    private void FaderWheel(object sender, MouseWheelEventArgs e) { e.Handled = IsLoaded; }
    private void NumericKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyNumeric(sender); e.Handled = true; } }
    private void NumericLostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyNumeric(sender);
    private void ApplyNumeric(object sender)
    {
        if (_disposed || _applying || sender is not TextBox { DataContext: ChannelModel channel } box || !channel.IsReady) return;
        if (box.Text.Trim() == channel.LevelText) return;
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value) && value is >= 0 and <= 100)
        {
            if (Math.Abs(value - channel.Level) > 0.0001) QueueLevel(channel, value);
        }
        else { box.Text = channel.LevelText; StatusText.Text = "Enter a volume between 0 and 100."; }
    }

    private async void MuteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChannelModel channel }) await CommandAsync(c => c.SetMuteAsync(channel.Role, !channel.Muted, _lifetime.Token), channel);
    }
    private async void ChannelDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || !IsLoaded || sender is not ComboBox { DataContext: ChannelModel channel, SelectedItem: DeviceChoice choice } || channel.Id == "master" || choice.Id == channel.DeviceId) return;
        await ManualOutputAsync(channel.Id, choice.Id, channel.Id != "mic" && LinkOutputs.IsChecked == true, channel);
    }
    private async void LinkedDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || !IsLoaded || LinkedDevice.SelectedItem is not DeviceChoice choice) return;
        await ManualOutputAsync("game", choice.Id, true);
    }
    private void LinkChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _preferences.Linked = LinkOutputs.IsChecked == true;
        _ = SavePreferences();
    }

    private async void ApplicationRouteChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || sender is not ComboBox { DataContext: ApplicationModel app, SelectedItem: RouteChoice choice } || choice.Id == app.RouteId) return;
        await RouteAsync(app, choice.Id);
    }
    private async Task RouteAsync(ApplicationModel app, string endpointId)
    {
        if (_sonar is null || _disposed) return;
        string[] sessionIds = app.SessionIds.ToArray();
        bool entered = false;
        app.Status = "Applying route…";
        try
        {
            await _operations.WaitAsync(_lifetime.Token); entered = true;
            if (_disposed || _sonar is null) return;
            var current = await _sonar.GetSnapshotAsync(_lifetime.Token);
            if (_disposed) return;
            if (!_model.Applications.Contains(app) || !current.Applications.Any(a => a.ProcessId == app.ProcessId && a.DataFlow == app.Flow && a.State != "expired" && sessionIds.Contains(a.SessionId, StringComparer.Ordinal)))
                throw new InvalidOperationException("This audio session ended or changed. Refresh and select the current application again.");
            if (!current.Channels.Any(c => c.VirtualDeviceId == endpointId && ChannelReady(current, c)))
                throw new InvalidOperationException("The selected audio channel is unavailable.");
            var result = await _sonar.RouteApplicationAsync(app.Flow, endpointId, app.ProcessId, _lifetime.Token);
            if (_disposed) return;
            Apply(result.Snapshot);
            var currentRow = _model.Applications.FirstOrDefault(a => a.ProcessId == app.ProcessId && a.Flow == app.Flow);
            if (currentRow is not null) currentRow.Status = result.ActiveSessionsMatch ? "Active audio route confirmed" : result.PolicyConfirmed ? "Policy confirmed · restart playback or select this device in the app" : "Route was not confirmed";
        }
        catch (Exception ex) when (IsExpected(ex)) { if (!_disposed) { app.Status = "Route failed"; StatusText.Text = ex.Message; } }
        finally { if (entered) _operations.Release(); }
    }
    private void ApplicationDrag(object sender, MouseEventArgs e)
    {
        if (_disposed || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: ApplicationModel app }) return;
        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is ComboBox or System.Windows.Controls.Primitives.ButtonBase or TextBox) return;
            source = source is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("SoltexAudioApplication", app.Key), DragDropEffects.Move);
    }
    private void ChannelDragOver(object sender, DragEventArgs e) { e.Effects = IsLoaded && e.Data.GetDataPresent("SoltexAudioApplication") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private async void ChannelDrop(object sender, DragEventArgs e)
    {
        if (_disposed || sender is not FrameworkElement { DataContext: ChannelModel channel } || !channel.IsReady || channel.VirtualDeviceId is null || e.Data.GetData("SoltexAudioApplication") is not string key) return;
        var app = _model.Applications.FirstOrDefault(a => a.Key == key);
        if (app is null || (app.Flow == "capture") != (channel.Id == "mic")) return;
        await RouteAsync(app, channel.VirtualDeviceId);
    }

    private bool SavePreferences()
    {
        if (!_initialized || _disposed) return false;
        try { _preferences.Save(); return true; }
        catch (Exception ex) when (IsExpected(ex)) { StatusText.Text = "Settings were not saved: " + ex.Message; return false; }
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _disposed) return;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Sonar settings", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock { Text = "Soltex controls the Sonar engine that SteelSeries GG installs, so GG and its driver must stay installed. Closing the window keeps audio playing, and quitting Soltex leaves Sonar running.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var enabled = new CheckBox { Content = "Enable global device shortcuts (Ctrl + Alt + 1 / 2)", IsChecked = _preferences.Shortcuts, Margin = new Thickness(0, 0, 0, 14) }; panel.Children.Add(enabled);
        enabled.IsEnabled = _hostOptions.EnableGlobalShortcuts;
        if (!enabled.IsEnabled) enabled.ToolTip = "Device shortcuts aren't available here.";
        var launchAtLogin = new CheckBox { Content = "Launch this mixer in the tray when I sign in", Margin = new Thickness(0, 0, 0, 14) };
        try { launchAtLogin.IsChecked = _hostOptions.GetStartupEnabled?.Invoke() ?? false; }
        catch (Exception ex) when (IsExpected(ex)) { launchAtLogin.IsEnabled = false; launchAtLogin.ToolTip = ex.Message; }
        if (_hostOptions.GetStartupEnabled is not null && _hostOptions.SetStartupEnabled is not null) panel.Children.Add(launchAtLogin);
        var selectors = new List<ComboBox>();
        for (int i = 0; i < 2; i++)
        {
            panel.Children.Add(new TextBlock { Text = "Device " + (i + 1), Margin = new Thickness(0, 8, 0, 4) });
            string? savedId = i == 0 ? _preferences.Favorite1 : _preferences.Favorite2;
            var choices = _model.Outputs.ToList();
            if (savedId is not null && choices.All(choice => choice.Id != savedId)) choices.Add(new(savedId, "Saved device (currently unavailable)"));
            var select = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedValue = savedId };
            selectors.Add(select); panel.Children.Add(select);
        }
        panel.Children.Add(new TextBlock { Text = "Meters show the level Windows reports for each device. Sonar handles the microphone.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 18) });
        var save = new Button { Content = "Save settings", HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(save);
        var dialog = new Window { Title = "Sonar settings", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        save.Click += (_, _) =>
        {
            _preferences.Shortcuts = enabled.IsChecked == true;
            _preferences.Favorite1 = selectors[0].SelectedValue as string; _preferences.Favorite2 = selectors[1].SelectedValue as string;
            if (SavePreferences())
            {
                try { if (launchAtLogin.IsEnabled) _hostOptions.SetStartupEnabled?.Invoke(launchAtLogin.IsChecked == true); RegisterShortcuts(); dialog.Close(); }
                catch (Exception ex) when (IsExpected(ex)) { save.Content = "Startup setting failed — retry"; save.ToolTip = ex.Message; }
            }
            else save.Content = "Save failed — retry";
        };
        ApplyDialogTheme(dialog); ShowOwnedDialog(dialog);
    }

    private void RegisterShortcuts()
    {
        if (!_hostOptions.EnableGlobalShortcuts) return;
        nint handle = _shortcutHandle;
        if (handle == 0) return;
        NativeHotkeys.UnregisterHotKey(handle, 8101); NativeHotkeys.UnregisterHotKey(handle, 8102);
        if (!_preferences.Shortcuts) return;
        bool first = NativeHotkeys.RegisterHotKey(handle, 8101, 0x4003, 0x31);
        bool second = NativeHotkeys.RegisterHotKey(handle, 8102, 0x4003, 0x32);
        if (!first || !second) StatusText.Text = "A device shortcut is already used by another app. Change or disable it in Settings.";
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && (wParam == 8101 || wParam == 8102))
        {
            string? id = wParam == 8101 ? _preferences.Favorite1 : _preferences.Favorite2;
            if (id is not null) _ = ManualOutputAsync("game", id, true);
            handled = true;
        }
        return 0;
    }
    private async Task FinishShutdownAsync()
    {
        // The closing handler awaits this without blocking Dispatcher continuations.
        await _operations.WaitAsync();
        try
        {
            try { _sonar?.Dispose(); }
            finally { if (_devices is not null) await _devices.DisposeAsync(); }
        }
        finally { _operations.Dispose(); _lifetime.Dispose(); }
    }
}

internal static class NativeHotkeys
{
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);
}

internal sealed class Preferences
{
    private string PathName = "";
    private bool _preserveUnreadable;
    public int Version { get; set; } = 1;
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 810;
    public bool Linked { get; set; } = true;
    public bool Shortcuts { get; set; }
    public string? Favorite1 { get; set; }
    public string? Favorite2 { get; set; }
    public static Preferences Load(string path)
    {
        try
        {
            var preferences = File.Exists(path) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path)) ?? new() : new();
            preferences.PathName = path; return preferences;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new() { _preserveUnreadable = true, PathName = path }; }
    }
    public void Save()
    {
        if (_preserveUnreadable) throw new InvalidOperationException("The settings file couldn't be read, so it was left as it is. Move preferences.json out of " + Path.GetDirectoryName(PathName) + " and reopen the mixer to reset settings.");
        if (Version != 1) throw new InvalidOperationException("The settings file is from a different Soltex version, so it was left as it is.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        string temporary = PathName + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, this); stream.Flush(flushToDisk: true); }
            Soltex.Shared.AtomicFileReplace.Move(temporary, PathName);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
