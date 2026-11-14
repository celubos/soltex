using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Soltex.Audio.UI;
using Forms = System.Windows.Forms;

namespace Soltex.Audio.App;

/// <summary>Standalone lifetime only; the suite hosts the same MixerWorkspace directly.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF closing handler awaits workspace shutdown before disposing the tray and closing the host.")]
public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon _tray = new();
    private bool _quit, _closing, _closed;

    public MainWindow()
    {
        InitializeComponent(); AudioTheme.Apply(this);
        Workspace.OutputsChanged += (_, _) => RebuildTray();
        Workspace.QuitRequested += (_, _) => QuitMixer();
        Loaded += LoadedAsync; Closing += ClosingAsync;
        StateChanged += (_, _) => UpdatePresentation();
        IsVisibleChanged += (_, _) => UpdatePresentation();
        _tray.Text = "Soltex Sonar mixer";
        _tray.Icon = System.Drawing.SystemIcons.Application;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMixer);
        _tray.Visible = true; RebuildTray();
    }

    private async void LoadedAsync(object sender, RoutedEventArgs e)
    {
        await Workspace.InitializeAsync(new MixerHostOptions
        {
            EnableGlobalShortcuts = true,
            GetStartupEnabled = () => StartupRegistration.Enabled,
            SetStartupEnabled = StartupRegistration.SetEnabled
        });
        if (_closing || _closed) return;
        var size = Workspace.PreferredWindowSize;
        Width = Math.Clamp(size.Width, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Clamp(size.Height, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        UpdatePresentation();
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains("--tray", StringComparer.Ordinal)) Hide();
        int capture = Array.IndexOf(args, "--capture");
        if (capture >= 0 && capture + 1 < args.Length)
        {
            await Task.Delay(800);
            if (_closing || _closed) return;
            SaveRender(args[capture + 1]); QuitMixer();
        }
    }

    private void UpdatePresentation()
    {
        if (_closing || _closed) return;
        if (IsVisible && WindowState != WindowState.Minimized) Workspace.Activate(); else Workspace.Deactivate();
    }

    private void RebuildTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open mixer", null, (_, _) => Dispatcher.Invoke(ShowMixer));
        menu.Items.Add("Mute / unmute playback", null, async (_, _) => await Dispatcher.InvokeAsync(Workspace.TogglePlaybackMuteAsync).Task.Unwrap());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var output in Workspace.AvailableOutputs)
            menu.Items.Add(output.Label, null, async (_, _) => await Dispatcher.InvokeAsync(() => Workspace.SelectLinkedOutputAsync(output.Id)).Task.Unwrap());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit mixer (Sonar keeps running)", null, (_, _) => Dispatcher.Invoke(QuitMixer));
        var old = _tray.ContextMenuStrip; _tray.ContextMenuStrip = menu; old?.Dispose();
    }

    private async void ClosingAsync(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        Workspace.SaveHostWindowSize(RestoreBounds.Size);
        if (!_quit) { Hide(); return; }
        _closing = true;
        try { await Workspace.ShutdownAsync(); }
        finally
        {
            _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose();
            _closed = true; Close(); System.Windows.Application.Current.Shutdown();
        }
    }

    private void SaveRender(string path)
    {
        Workspace.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Workspace.ActualWidth, (int)Workspace.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Workspace);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new FileStream(path, FileMode.CreateNew); encoder.Save(output);
    }
}
