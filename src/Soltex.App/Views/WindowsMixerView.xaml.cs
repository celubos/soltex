using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Soltex.Audio;
using Soltex.Audio.Sessions;

namespace Soltex.App.Views;

public partial class WindowsMixerView : UserControl, IAsyncDisposable
{
    private AudioSessionService? _sessions;
    private AudioDeviceService? _devices;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<AudioSessionKey, CancellationTokenSource> _pending = [];
    private CancellationTokenSource? _endpointPending;
    private AudioSessionSnapshot? _snapshot;
    private Task? _readingTask, _shutdown;
    private string? _selectedId, _initialDefaultId;
    private bool _initialSelectionComplete;
    private bool _active, _reading, _applying, _disposed;
    private string? _routingDirectory;
    private WindowsApplicationRoutingWindow? _routingWindow;
    public ObservableCollection<WindowsSessionRow> Rows { get; } = [];
    private sealed record DeviceChoice(string Id, string Label);

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(WindowsMixerView),
        new PropertyMetadata(false, (d, _) => ((WindowsMixerView)d).ArrangeDeviceRow()));

    /// <summary>True below <see cref="CompactWidth"/>: each name gets its own line and its controls move below it.</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    internal const double CompactWidth = 540;

    public WindowsMixerView()
    {
        InitializeComponent(); DataContext = this;
        _timer.Tick += (_, _) => { if (!_reading) _readingTask = RefreshAsync(); };
        SizeChanged += (_, e) => IsCompact = e.NewSize.Width < CompactWidth;
    }

    /// <summary>The device row follows the same two layouts as the app rows.</summary>
    private void ArrangeDeviceRow()
    {
        bool compact = IsCompact;
        SessionCard.Padding = compact ? new Thickness(14, 4, 14, 8) : new Thickness(20, 6, 20, 10);
        Grid.SetColumnSpan(DeviceName, compact ? 4 : 1);
        DeviceName.Margin = compact ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 16, 0);
        Grid.SetRow(EndpointLevel, compact ? 1 : 0);
        Grid.SetColumn(EndpointLevel, compact ? 0 : 1);
        Grid.SetColumnSpan(EndpointLevel, compact ? 3 : 1);
        Grid.SetRow(DeviceValue, compact ? 1 : 0);
        Grid.SetColumn(DeviceValue, compact ? 3 : 2);
        DeviceValue.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 12, 0);
        Grid.SetRow(EndpointMute, compact ? 2 : 0);
        EndpointMute.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    public void Activate()
    {
        if (_disposed || _active) return;
        _sessions ??= new AudioSessionService(); _devices ??= new AudioDeviceService();
        _active = true; _timer.Start(); if (!_reading) _readingTask = RefreshAsync();
    }
    public void Deactivate() { _active = false; _timer.Stop(); }

    /// <summary>
    /// Shows a supplied snapshot without reading Windows audio or creating any audio service (render captures and
    /// layout checks). Volume and mute stay inert because no session service exists.
    /// </summary>
    internal void ShowPreview(AudioSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_disposed || _active) return;
        _snapshot = snapshot;
        Apply(snapshot);
    }

    public void ConfigureRoutingDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _routingDirectory = Path.GetFullPath(directory);
    }

    private void SessionRouteClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _snapshot is null || sender is not FrameworkElement { DataContext: WindowsSessionRow row } ||
            !row.CanRoute || row.Observation is not { } selected) return;
        if (_routingWindow is not null) { _routingWindow.Activate(); return; }
        if (_routingDirectory is null) { Feedback.Text = "App outputs can't be saved for this profile."; return; }
        _routingWindow = new WindowsApplicationRoutingWindow(selected, _snapshot.Endpoints, _routingDirectory)
        { Owner = Window.GetWindow(this) };
        _routingWindow.Closed += (_, _) => _routingWindow = null;
        _routingWindow.Show();
    }

    private async Task RefreshAsync()
    {
        if (_disposed || !_active || _sessions is null || _reading) return;
        _reading = true;
        try
        {
            if (!_initialSelectionComplete && _devices is not null)
            {
                var devices = await _devices.GetSnapshotAsync();
                _initialDefaultId = devices.Defaults.FirstOrDefault(d => d.Direction == AudioEndpointDirection.Render && d.Role == AudioDeviceRole.Multimedia)?.EndpointId;
            }
            var snapshot = await _sessions.GetSnapshotAsync(_lifetime.Token);
            if (!_disposed && _active) { _snapshot = snapshot; Apply(snapshot); }
        }
        catch (Exception error) when (Expected(error)) { if (!_disposed) Feedback.Text = "Windows audio is unavailable: " + error.Message; }
        finally { _reading = false; }
    }

    private void Apply(AudioSessionSnapshot snapshot)
    {
        _applying = true;
        try
        {
            var endpoints = snapshot.Endpoints.Where(d => ShowVirtual.IsChecked == true || !d.IsVirtual).ToArray();
            // Devices that share a name are numbered in list order, never labelled with internal IDs.
            var choices = endpoints.Select(d => new DeviceChoice(d.Id, d.Name + DuplicateSuffix(endpoints.Where(other => other.Name == d.Name).Select(other => other.Id).ToList(), d.Id))).ToList();
            if (!_initialSelectionComplete && choices.Count > 0)
            {
                // This selects the controls to view, never a Windows default or an audio route.
                // A hidden virtual default must not make the first physical-device view unusable.
                _selectedId = choices.Any(choice => choice.Id == _initialDefaultId)
                    ? _initialDefaultId : choices[0].Id;
                _initialSelectionComplete = true;
            }
            if (_selectedId is not null && choices.All(d => d.Id != _selectedId)) choices.Add(new(_selectedId, "Selected device isn't connected or is hidden"));
            if (!(DeviceSelector.ItemsSource as IEnumerable<DeviceChoice> ?? []).SequenceEqual(choices)) DeviceSelector.ItemsSource = choices;
            DeviceSelector.SelectedValue = _selectedId;
            var selected = endpoints.FirstOrDefault(d => d.Id == _selectedId);
            EndpointLevel.IsEnabled = selected?.Volume is not null; EndpointMute.IsEnabled = selected?.Muted is not null;
            if (_endpointPending is null && selected?.Volume is { } volume) EndpointLevel.Value = volume * 100;
            EndpointValue.Text = selected?.Volume is { } value ? (value * 100).ToString("0", CultureInfo.CurrentCulture) : "—";
            EndpointMute.Content = selected?.Muted == true ? "Unmute" : "Mute";
            EndpointPeak.Level = selected?.Peak ?? -1;
            EndpointPeak.ToolTip = selected?.Peak is null ? "Level not available" : "Device output level";
            var sessions = selected?.Sessions.Where(s => s.State != AudioSessionState.Expired).ToArray() ?? [];
            foreach (var row in Rows.Where(r => sessions.All(s => s.Key != r.Key)).ToArray()) Rows.Remove(row);
            foreach (var session in sessions)
            {
                var row = Rows.FirstOrDefault(r => r.Key == session.Key);
                if (row is null) { row = new(session.Key); Rows.Add(row); }
                row.Update(session, !_pending.ContainsKey(session.Key) && !row.Editing);
            }
            EmptySessions.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selected is null) Feedback.Text = "Choose a connected playback device. Windows defaults haven't changed.";
            else if (snapshot.Errors.Count > 0) Feedback.Text = "Some apps can't be read right now. The others still work.";
            else if (_pending.Count == 0 && _endpointPending is null) Feedback.Text = "Showing live levels for this device and its apps.";
        }
        finally { _applying = false; }
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        _selectedId = DeviceSelector.SelectedValue as string;
        if (_selectedId is not null) _initialSelectionComplete = true;
        if (_snapshot is not null) Apply(_snapshot);
    }
    private void VirtualVisibilityChanged(object sender, RoutedEventArgs e) { if (_snapshot is not null) Apply(_snapshot); }
    private void IgnoreWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;
    private void SessionLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_applying && IsLoaded && sender is FrameworkElement { DataContext: WindowsSessionRow row } && row.CanControl)
            QueueSessionLevel(row, e.NewValue);
    }
    private void LevelFocus(object sender, KeyboardFocusChangedEventArgs e) { if (sender is FrameworkElement { DataContext: WindowsSessionRow row }) row.Editing = true; }
    private void LevelCommit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: WindowsSessionRow row } box) return;
        row.Editing = false;
        // Tabbing through the list must not write anything: LevelText is not refreshed while editing,
        // so unchanged text means nothing was typed.
        if (box.Text.Trim() == row.LevelText) return;
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && double.IsFinite(value) && value is >= 0 and <= 100)
            QueueSessionLevel(row, value);
        else { box.Text = row.LevelText; Feedback.Text = "Enter a volume between 0 and 100."; }
    }
    private void LevelKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box) { e.Handled = true; box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)); }
    }
    private async void QueueSessionLevel(WindowsSessionRow row, double level)
    {
        if (_disposed || !row.CanControl || _sessions is null) return;
        if (_pending.TryGetValue(row.Key, out var old)) old.Cancel();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _pending[row.Key] = pending;
        bool held = false;
        try
        {
            await Task.Delay(70, pending.Token); await _operations.WaitAsync(pending.Token); held = true;
            if (_disposed || pending.IsCancellationRequested) return;
            row.Status = "Applying volume…";
            var result = await _sessions.SetSessionVolumeAsync(row.Key, level / 100, _lifetime.Token);
            if (_pending.TryGetValue(row.Key, out var current) && ReferenceEquals(current, pending)) Confirm(row, result);
        }
        catch (Exception error) when (Expected(error)) { if (!_disposed && error is not OperationCanceledException) Feedback.Text = error.Message; }
        finally { if (held) _operations.Release(); if (_pending.TryGetValue(row.Key, out var current) && ReferenceEquals(current, pending)) _pending.Remove(row.Key); }
    }
    private async void SessionMuteClick(object sender, RoutedEventArgs e)
    {
        if (_sessions is null || sender is not FrameworkElement { DataContext: WindowsSessionRow row } || !row.CanControl) return;
        bool held = false;
        try { await _operations.WaitAsync(_lifetime.Token); held = true; if (!_disposed) Confirm(row, await _sessions.SetSessionMuteAsync(row.Key, !row.Muted, _lifetime.Token)); }
        catch (Exception error) when (Expected(error)) { if (!_disposed) Feedback.Text = error.Message; }
        finally { if (held) _operations.Release(); }
    }
    private void Confirm(WindowsSessionRow row, AudioSessionControlResult result)
    {
        if (_disposed) return;
        _applying = true;
        try { if (result.ConfirmedState is { } confirmed) row.Update(confirmed, !row.Editing); }
        finally { _applying = false; }
        Feedback.Text = result.Applied ? "App volume changed." : result.Error ?? "The change was not confirmed.";
    }
    private async void EndpointLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applying || !IsLoaded || _devices is null || _selectedId is not { } id || _disposed) return;
        _endpointPending?.Cancel(); using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _endpointPending = pending;
        bool held = false;
        try
        {
            await Task.Delay(70, pending.Token); await _operations.WaitAsync(pending.Token); held = true;
            if (_disposed || pending.IsCancellationRequested) return;
            var result = await _devices.SetVolumeAsync(id, e.NewValue / 100);
            if (!_disposed) Feedback.Text = result.Succeeded ? "Device volume changed." : result.Error ?? "The device volume was not confirmed.";
        }
        catch (Exception error) when (Expected(error)) { if (!_disposed && error is not OperationCanceledException) Feedback.Text = error.Message; }
        finally { if (held) _operations.Release(); if (ReferenceEquals(_endpointPending, pending)) _endpointPending = null; }
    }
    private async void EndpointMuteClick(object sender, RoutedEventArgs e) => await ToggleEndpointMuteAsync();
    public async Task ToggleEndpointMuteAsync()
    {
        if (_devices is null || _selectedId is not { } id || _disposed) return;
        bool held = false;
        try
        {
            await _operations.WaitAsync(_lifetime.Token); held = true;
            var before = await _devices.ReadEndpointAsync(id);
            if (_disposed || _lifetime.IsCancellationRequested) return;
            if (before.ConfirmedState?.IsMuted is not { } muted) return;
            var result = await _devices.SetMuteAsync(id, !muted);
            if (!_disposed) Feedback.Text = result.Succeeded ? (muted ? "Device unmuted." : "Device muted.") : result.Error ?? "Mute was not confirmed.";
        }
        catch (Exception error) when (Expected(error)) { if (!_disposed) Feedback.Text = error.Message; }
        finally { if (held) _operations.Release(); }
    }

    /// <summary>"" for the first device of a name, then " (2)", " (3)"… in list order.</summary>
    internal static string DuplicateSuffix(IReadOnlyList<string> idsWithSameName, string id)
    {
        int index = idsWithSameName.ToList().IndexOf(id);
        return idsWithSameName.Count < 2 || index < 1 ? "" : " (" + (index + 1).ToString(CultureInfo.CurrentCulture) + ")";
    }

    private static bool Expected(Exception error) => error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException or System.Runtime.InteropServices.COMException;
    public ValueTask DisposeAsync() { _shutdown ??= ShutdownAsync(); GC.SuppressFinalize(this); return new(_shutdown); }
    private async Task ShutdownAsync()
    {
        _disposed = true; Deactivate(); _lifetime.Cancel();
        if (_routingWindow is not null) await _routingWindow.ShutdownAsync();
        if (_readingTask is not null) await _readingTask;
        await _operations.WaitAsync();
        try
        {
            try { if (_sessions is not null) await _sessions.DisposeAsync(); }
            finally { if (_devices is not null) await _devices.DisposeAsync(); }
        }
        finally { _operations.Release(); _lifetime.Dispose(); }
    }
}

public sealed class WindowsSessionRow(AudioSessionKey key) : INotifyPropertyChanged
{
    public AudioSessionKey Key { get; } = key;
    public string Name { get; private set; } = "";
    public string Detail { get; private set; } = "";
    /// <summary>The full name and where the app runs from, for the name's tooltip (long names are trimmed).</summary>
    public string Details => Detail.Length == 0 || Detail == Name ? Name : Name + Environment.NewLine + Detail;
    public double Level { get; private set; }
    public string LevelText => Level.ToString("0", CultureInfo.CurrentCulture);
    public double Peak { get; private set; }
    public bool Muted { get; private set; }
    public bool CanControl { get; private set; }
    public AudioSessionInfo? Observation { get; private set; }
    public bool CanRoute => CanControl && Observation?.IsSystemSounds == false &&
        Key.ProcessStartTimeUtc is not null && !string.IsNullOrWhiteSpace(Observation.ExecutablePath);
    public string RouteAccessibleName => "Choose output for " + Name;
    public bool Editing { get; set; }
    private string _status = "";
    public string Status { get => _status; set { if (_status == value) return; _status = value; Changed(); } }
    public string MuteLabel => Muted ? "Unmute" : "Mute";
    public string AccessibleName => Name + " volume percent";
    public string MuteAccessibleName => MuteLabel + " " + Name;
    public string MeterAccessibleName => Name + " level";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(AudioSessionInfo session, bool updateLevel)
    {
        bool previouslyRouteable = CanRoute;
        Observation = session;
        string name = session.IsSystemSounds ? "System sounds" : session.Name;
        if (Name != name)
        {
            Name = name; Changed(nameof(Name)); Changed(nameof(AccessibleName));
            Changed(nameof(MuteAccessibleName)); Changed(nameof(MeterAccessibleName));
            Changed(nameof(RouteAccessibleName)); Changed(nameof(Details));
        }
        string detail = session.ExecutablePath ?? name;
        if (Detail != detail) { Detail = detail; Changed(nameof(Detail)); Changed(nameof(Details)); }
        double level = (session.Volume ?? 0) * 100;
        if (updateLevel && Level != level) { Level = level; Changed(nameof(Level)); Changed(nameof(LevelText)); }
        double peak = session.Peak ?? 0;
        if (Peak != peak) { Peak = peak; Changed(nameof(Peak)); }
        bool muted = session.Muted == true;
        if (Muted != muted) { Muted = muted; Changed(nameof(Muted)); Changed(nameof(MuteLabel)); Changed(nameof(MuteAccessibleName)); }
        if (CanControl != session.CanControl) { CanControl = session.CanControl; Changed(nameof(CanControl)); }
        if (CanRoute != previouslyRouteable) Changed(nameof(CanRoute));
        Status = !CanControl ? "Windows doesn't allow changes to this one." : session.State == AudioSessionState.Active ? "Playing on this device" : "Not playing right now";
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
