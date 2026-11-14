using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace Soltex.App;

public partial class App : IDisposable
{
    private Mutex? _instanceMutex;
    private bool _ownsInstance, _instanceDisposed;
    private readonly CancellationTokenSource _instanceStop = new();

    private bool AcquireInstance(SuiteLaunchOptions options)
    {
        if (options.CapturePath is not null) return true;
        using var identity = WindowsIdentity.GetCurrent();
        string profile = options.ProfileDirectory?.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() ?? "DEFAULT";
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..16];
        string name = "Soltex.Suite." + identity.User?.Value + "." + suffix;
        _instanceMutex = new Mutex(true, "Local\\" + name, out _ownsInstance);
        if (!_ownsInstance)
        {
            try { _ownsInstance = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
        }
        if (!_ownsInstance)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
                pipe.Connect(3000);
                // This process was started by the user (Start menu, shortcut) and may bring a window to the front;
                // pass that right on so the running copy's window comes to the front instead of only flashing.
                _ = AllowSetForegroundWindow(AnyProcess);
                pipe.WriteByte(options.Quit ? (byte)2 : options.Mixer ? (byte)3 : (byte)1);
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            { MessageBox.Show($"The running copy of {ProductIdentity.DisplayName} did not respond. Use its icon in the notification area to reopen or quit it.", ProductIdentity.DisplayName); }
            Shutdown(); return false;
        }
        if (options.Quit) { Shutdown(); return false; }
        _ = ListenForInstanceAsync(name, _instanceStop.Token);
        return true;
    }

    private async Task ListenForInstanceAsync(string name, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] command = new byte[1];
                if (await pipe.ReadAsync(command, timeout.Token) == 1 && MainWindow is Shell.ISuiteWindow window)
                {
                    if (command[0] == 2) window.RequestQuit();
                    else if (command[0] == 3) window.OpenAudio();
                    else if (command[0] == 1) window.ShowSuite();
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException)
            {
                try { await Task.Delay(250, stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    protected override void OnExit(ExitEventArgs e) { Dispose(); base.OnExit(e); }

    private const int AnyProcess = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        if (_instanceDisposed) return;
        _instanceDisposed = true; _instanceStop.Cancel();
        if (_ownsInstance) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose(); _instanceStop.Dispose();
        GC.SuppressFinalize(this);
    }
}
