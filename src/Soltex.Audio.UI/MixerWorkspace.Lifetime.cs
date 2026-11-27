using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Soltex.Audio.UI;

public partial class MixerWorkspace : IAsyncDisposable
{
    private MixerHostOptions _hostOptions = new();
    private Task? _initialization;
    private readonly HashSet<Window> _dialogs = [];
    private nint _shortcutHandle;
    public event EventHandler? OutputsChanged;
    public event EventHandler? QuitRequested;
    public IReadOnlyList<MixerOutput> AvailableOutputs => _model.Outputs.Select(output => new MixerOutput(output.Id, output.Label)).ToArray();
    public Size PreferredWindowSize => new(_preferences.Width, _preferences.Height);
    public bool IsBackendInitialized => _initialized;
    public bool IsActive => _active;

    public MixerWorkspace() : this(new MixerHostOptions()) { }

    public MixerWorkspace(MixerHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hostOptions = options;
        InitializeComponent();
        // The standalone mixer carries its own palette and control styles; embedded, the host's apply instead.
        if (!options.Embedded) AudioTheme.Apply(this);
        InitializeChatMix();
        DataContext = _model;
        _stateTimer.Tick += StateTick;
        _meterTimer.Tick += MeterTick;
        PreviewMouseLeftButtonDown += (_, e) => _dragOrigin = e.GetPosition(this);
        if (options.Embedded) ApplyEmbeddedPresentation();
        StatusText.Text = "Connecting to Sonar…";
        ShowConnection(SonarConnection.Connecting);
    }

    /// <summary>
    /// A section of the host's page: the host supplies padding, scrolling, colours and control styles; the mixer keeps
    /// a section heading that matches the host's other sections.
    /// </summary>
    private void ApplyEmbeddedPresentation()
    {
        MixerTitle.Text = "SteelSeries Sonar";
        MixerTitle.FontSize = 20;
        BackendNote.Visibility = Visibility.Collapsed;
        MixerLayout.Margin = new Thickness(0);
        HeaderRow.Margin = new Thickness(0, 0, 0, 16);
        // The host page scrolls. Filling the viewport would only keep the height of an earlier, narrower layout
        // and stretch the strips when the window widens.
        System.Windows.Data.BindingOperations.ClearBinding(MixerLayout, MinHeightProperty);
        MixerViewport.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        Background = System.Windows.Media.Brushes.Transparent;
        HostThemeBridge.Attach(this);
    }

    /// <summary>Dialogs follow the mixer: the host theme when embedded, the audio palette otherwise.</summary>
    private void ApplyDialogTheme(Window dialog)
    {
        if (_hostOptions.Embedded) HostThemeBridge.Attach(dialog); else AudioTheme.Apply(dialog);
        dialog.SetResourceReference(BackgroundProperty, "CanvasBrush");
        dialog.SetResourceReference(ForegroundProperty, "TextBrush");
    }

    public Task InitializeAsync(MixerHostOptions? options = null)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialization is not null) return _initialization;
        // Embedded presentation is fixed when the control is built; later options cannot switch it.
        _hostOptions = (options ?? _hostOptions) with { Embedded = _hostOptions.Embedded };
        ArgumentException.ThrowIfNullOrWhiteSpace(_hostOptions.PreferencesDirectory);
        if (!Path.IsPathFullyQualified(_hostOptions.PreferencesDirectory)) throw new ArgumentException("PreferencesDirectory must be absolute.", nameof(options));
        _initialization = InitializeCoreAsync();
        return _initialization;
    }

    private async Task InitializeCoreAsync()
    {
        _preferences = Preferences.Load(Path.Combine(_hostOptions.PreferencesDirectory, "preferences.json"));
        _outputPolicy = LinkedOutputPolicySettings.Load(Path.Combine(_hostOptions.PreferencesDirectory, "device-policy.json"));
        LinkOutputs.IsChecked = _preferences.Linked;
        _devices = new AudioDeviceService();
        _devices.SnapshotChanged += DeviceSnapshotChanged;
        _initialized = true;
        if (_hostOptions.EnableGlobalShortcuts && Window.GetWindow(this) is { } owner)
        {
            _shortcutHandle = new WindowInteropHelper(owner).Handle;
            _windowSource = HwndSource.FromHwnd(_shortcutHandle);
            _windowSource?.AddHook(WindowMessage);
            RegisterShortcuts();
        }
        await ConnectAsync();
        if (_disposed) return;
        UpdateStateTimer();
        if (_active) { _meterTimer.Start(); _chatMixTimer.Start(); }
    }

    /// <summary>
    /// Sonar state is polled only while the mixer is shown, or while automatic output selection needs to watch
    /// for device changes. A hidden page or a window in the tray otherwise sends no requests to Sonar.
    /// </summary>
    private void UpdateStateTimer()
    {
        if (_initialized && !_disposed && (_active || _outputPolicy.Enabled)) { if (!_stateTimer.IsEnabled) _stateTimer.Start(); }
        else _stateTimer.Stop();
    }

    public void Activate()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active) return;
        _active = true;
        if (_initialized) { UpdateStateTimer(); _meterTimer.Start(); _chatMixTimer.Start(); _ = RefreshAsync(); }
    }

    public void Deactivate()
    {
        Dispatcher.VerifyAccess();
        _active = false; _meterTimer.Stop(); _chatMixTimer.Stop();
        UpdateStateTimer();
    }

    public void SaveHostWindowSize(Size size)
    {
        Dispatcher.VerifyAccess();
        if (!_initialized || _disposed) return;
        if (double.IsFinite(size.Width) && size.Width > 0) _preferences.Width = size.Width;
        if (double.IsFinite(size.Height) && size.Height > 0) _preferences.Height = size.Height;
        _ = SavePreferences();
    }

    public Task SelectLinkedOutputAsync(string id)
    {
        Dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return ManualOutputAsync("game", id, true);
    }
    public Task TogglePlaybackMuteAsync()
    {
        Dispatcher.VerifyAccess();
        return CommandAsync(client => client.SetMuteAsync("Master", !_model.Channels[0].Muted, _lifetime.Token));
    }
    public void ShowDevicePolicy() { Dispatcher.VerifyAccess(); DevicesClick(this, new RoutedEventArgs()); }
    public void ShowProcessing() { Dispatcher.VerifyAccess(); ProcessingClick(this, new RoutedEventArgs()); }

    private void MixerShortcut(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; QuitRequested?.Invoke(this, EventArgs.Empty); }
    }

    private void ShowOwnedDialog(Window dialog)
    {
        _dialogs.Add(dialog);
        try { dialog.ShowDialog(); }
        finally { _dialogs.Remove(dialog); }
    }

    public Task ShutdownAsync()
    {
        Dispatcher.VerifyAccess();
        if (_shutdown is not null) return _shutdown;
        _disposed = true;
        Deactivate(); CloseChatMix();
        _policyPending?.Cancel(); _stateTimer.Stop(); _lifetime.Cancel();
        foreach (var cancel in _pendingLevels.Values) cancel.Cancel();
        _pendingLevels.Clear();
        _windowSource?.RemoveHook(WindowMessage);
        if (_shortcutHandle != 0)
        { NativeHotkeys.UnregisterHotKey(_shortcutHandle, 8101); NativeHotkeys.UnregisterHotKey(_shortcutHandle, 8102); }
        if (_devices is not null) _devices.SnapshotChanged -= DeviceSnapshotChanged;
        _shutdown = ShutdownCoreAsync();
        return _shutdown;
    }

    private async Task ShutdownCoreAsync()
    {
        try
        {
            foreach (var dialog in _dialogs.ToArray()) dialog.Close();
            if (_processingWindow is { } processing) await processing.ShutdownAsync();
        }
        finally { await FinishShutdownAsync(); }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return new(ShutdownAsync());
    }
}
