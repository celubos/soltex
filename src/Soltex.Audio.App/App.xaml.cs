using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Soltex.Audio.App;

public partial class App : System.Windows.Application, IDisposable
{
    private Mutex? _instance;
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsMutex;
    private bool _disposed;
    public App() => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Contains("--capture", StringComparer.Ordinal))
        {
            using var identity = WindowsIdentity.GetCurrent();
            string name = "SoltexAudio." + (identity.User?.Value ?? Environment.UserName);
            _instance = new Mutex(true, "Local\\" + name, out _ownsMutex);
            if (!_ownsMutex)
            {
                try { _ownsMutex = _instance.WaitOne(0); }
                catch (AbandonedMutexException) { _ownsMutex = true; }
            }
            if (!_ownsMutex)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
                    pipe.Connect(1500); pipe.WriteByte(e.Args.Contains("--quit", StringComparer.Ordinal) ? (byte)2 : (byte)1);
                }
                catch (Exception error) when (error is IOException or TimeoutException)
                { MessageBox.Show("The running mixer did not respond. Close it from its tray menu and try again.", "Soltex"); }
                Shutdown(); return;
            }
            if (e.Args.Contains("--quit", StringComparer.Ordinal)) { Shutdown(); return; }
            _ = ListenAsync(name);
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }

    private async Task ListenAsync(string name)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                byte[] command = new byte[1];
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                if (await pipe.ReadAsync(command, deadline.Token) == 1 && MainWindow is MainWindow mixer)
                {
                    if (command[0] == 1) mixer.ShowMixer();
                    else if (command[0] == 2) mixer.QuitMixer();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException)
            {
                try { await Task.Delay(250, _stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void WindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Window window) return;
        int enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
