using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace Soltex.App;

public partial class App : IDisposable
{
    private Mutex? _instanceMutex;
    private bool _ownsInstance, _instanceDisposed;
    private readonly CancellationTokenSource _instanceStop = new();

    private bool AcquireInstance(SuiteLaunchOptions options)
    {
        if (options.CapturePath is not null) return true;
        string name = SuiteInstance.Name(options.ProfileDirectory);
        if (options.Quit)
        {
            // Setup and the uninstaller run this too, often silently: no window and no message, only the exit code.
            // A question the running copy asks (audio is playing) may come to the front.
            _ = AllowSetForegroundWindow(AnyProcess);
            int code = (int)SuiteInstance.QuitRunningCopy(name, SuiteInstance.QuitTimeout);
            Environment.ExitCode = code;
            Shutdown(code);
            return false;
        }
        _instanceMutex = new Mutex(true, SuiteInstance.MutexName(name), out _ownsInstance);
        if (!_ownsInstance)
        {
            try { _ownsInstance = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
        }
        if (!_ownsInstance)
        {
            // This process was started by the user (Start menu, shortcut) and may bring a window to the front;
            // pass that right on so the running copy's window comes to the front instead of only flashing.
            _ = AllowSetForegroundWindow(AnyProcess);
            byte command = options.Mixer ? SuiteInstance.AudioCommand : SuiteInstance.ShowCommand;
            if (!SuiteInstance.Send(name, command, TimeSpan.FromSeconds(3)))
                MessageBox.Show($"The running copy of {ProductIdentity.DisplayName} did not respond. Use its icon in the notification area to reopen or quit it.", ProductIdentity.DisplayName);
            Shutdown(); return false;
        }
        _ = SuiteInstance.ListenAsync(name, OnInstanceCommand, _instanceStop.Token);
        return true;
    }

    /// <summary>A command from a later launch, on the UI thread. For Quit, returns whether quitting went ahead.</summary>
    private bool OnInstanceCommand(byte command)
    {
        if (MainWindow is not Shell.ISuiteWindow window)
        {
            // Startup did not get as far as the window, so there is nothing to ask before quitting.
            if (command != SuiteInstance.QuitCommand) return false;
            Shutdown();
            return true;
        }
        switch (command)
        {
            case SuiteInstance.QuitCommand: return SuiteInstance.QuitWindow(window);
            case SuiteInstance.AudioCommand: window.OpenAudio(); return true;
            case SuiteInstance.ShowCommand: window.ShowSuite(); return true;
            default: return false;
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

/// <summary>Exit codes of <c>Soltex.exe --quit</c>. eng/soltex.iss and eng/installer/test-installer.ps1 rely on them.</summary>
internal enum QuitResult
{
    /// <summary>The running copy quit and its process has exited.</summary>
    Exited = 0,
    /// <summary>No copy was running for this user and profile.</summary>
    NotRunning = 1,
    /// <summary>The running copy asked first (audio is playing) and the answer was Cancel.</summary>
    Cancelled = 2,
    /// <summary>The running copy did not answer or did not exit in time.</summary>
    TimedOut = 3
}

/// <summary>
/// One copy per user and profile. The first copy holds a <c>Local\</c> mutex and listens on a current-user pipe of the
/// same name; a later launch sends it one command byte. For Quit the running copy answers whether it is quitting, and the
/// later launch waits for its process to exit.
/// </summary>
internal static partial class SuiteInstance
{
    internal const byte ShowCommand = 1, QuitCommand = 2, AudioCommand = 3;

    /// <summary>The running copy's answer to <see cref="QuitCommand"/>. Copies before 1.1 do not answer.</summary>
    internal const byte Quitting = 1, QuitCancelled = 2;

    /// <summary>How long <c>--quit</c> waits in all, including a question the running copy asks while audio plays.</summary>
    internal static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The pipe name; the mutex adds <c>Local\</c>. Unchanged since 1.0, so copies of both versions find each other.</summary>
    internal static string Name(string? profileDirectory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string profile = profileDirectory?.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant() ?? "DEFAULT";
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..16];
        return "Soltex.Suite." + identity.User?.Value + "." + suffix;
    }

    internal static string MutexName(string name) => "Local\\" + name;

    /// <summary>True while a copy holds the instance mutex. Call it from a thread that does not own the mutex.</summary>
    internal static bool IsHeld(string name)
    {
        Mutex? mutex;
        try
        {
            if (!Mutex.TryOpenExisting(MutexName(name), out mutex)) return false;
        }
        catch (UnauthorizedAccessException) { return true; } // made by a copy running with other rights
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException) { } // its owner ended without releasing it; this thread owns it now
            mutex.ReleaseMutex();
            return false;
        }
    }

    /// <summary>Passes Show or Audio to the running copy. False when it did not take the command in time.</summary>
    internal static bool Send(string name, byte command, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out);
            pipe.Connect(Milliseconds(timeout));
            pipe.WriteByte(command);
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Asks the running copy to quit and waits until its process has exited, for at most <paramref name="timeout"/> in all.
    /// Shows nothing. <paramref name="waitForExit"/> (process id, time left; true once exited) stands in for the wait on the
    /// running copy's process in tests.
    /// </summary>
    internal static QuitResult QuitRunningCopy(string name, TimeSpan timeout, Func<int, TimeSpan, bool>? waitForExit = null)
    {
        waitForExit ??= WaitForProcessExit;
        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan Left() => timeout > clock.Elapsed ? timeout - clock.Elapsed : TimeSpan.Zero;
        if (!IsHeld(name)) return QuitResult.NotRunning;

        NamedPipeClientStream pipe;
        bool answers = true;
        try { pipe = Open(name, PipeDirection.InOut, Left()); }
        catch (UnauthorizedAccessException)
        {
            // A copy from before 1.1 listens for one byte and never answers, so it accepts only a write-only client.
            try { pipe = Open(name, PipeDirection.Out, Left()); answers = false; }
            catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
            { return IsHeld(name) ? QuitResult.TimedOut : QuitResult.Exited; }
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        { return IsHeld(name) ? QuitResult.TimedOut : QuitResult.Exited; }

        using (pipe)
        {
            int? processId = GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id) ? (int)id : null;
            try
            {
                pipe.WriteByte(QuitCommand);
                pipe.Flush();
            }
            catch (IOException) { return IsHeld(name) ? QuitResult.TimedOut : QuitResult.Exited; }

            // Quitting, or no answer at all (an older copy, or one already closing): wait for the process to end.
            if (answers && ReadAnswer(pipe, Left()) == QuitCancelled) return QuitResult.Cancelled;
            bool exited = processId is { } server ? waitForExit(server, Left()) : WaitUntilReleased(name, Left());
            return exited ? QuitResult.Exited : QuitResult.TimedOut;
        }
    }

    /// <summary>
    /// Asks the window to quit the way Quit in the tray menu does; it may ask first while audio plays. True when quitting
    /// went ahead: the window started closing, or it is already shutting down (it disables itself then).
    /// </summary>
    internal static bool QuitWindow(Shell.ISuiteWindow window)
    {
        if (window is not Window shell)
        {
            window.RequestQuit();
            return true;
        }
        bool closing = false;
        void OnClosing(object? sender, CancelEventArgs e) => closing = true;
        shell.Closing += OnClosing;
        try { window.RequestQuit(); }
        finally { shell.Closing -= OnClosing; }
        return closing || !shell.IsEnabled;
    }

    /// <summary>
    /// Serves later launches until <paramref name="stop"/>: reads one command byte, runs <paramref name="onCommand"/> on the
    /// caller's context and, for Quit, answers whether quitting went ahead.
    /// </summary>
    internal static async Task ListenAsync(string name, Func<byte, bool> onCommand, CancellationToken stop)
    {
        byte[] command = new byte[1];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    timeout.CancelAfter(ExchangeTimeout);
                    if (await pipe.ReadAsync(command, timeout.Token) != 1) continue;
                }
                bool accepted = onCommand(command[0]);
                if (command[0] == QuitCommand)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    timeout.CancelAfter(ExchangeTimeout);
                    // A launch from before 1.1 has already gone; writing then only ends this connection.
                    await pipe.WriteAsync(new byte[] { accepted ? Quitting : QuitCancelled }, timeout.Token);
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

    private static NamedPipeClientStream Open(string name, PipeDirection direction, TimeSpan timeout)
    {
        NamedPipeClientStream pipe = new(".", name, direction, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            pipe.Connect(Milliseconds(timeout));
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>The running copy's answer, or -1 when none came in time.</summary>
    private static int ReadAnswer(NamedPipeClientStream pipe, TimeSpan timeout)
    {
        byte[] answer = new byte[1];
        using CancellationTokenSource cancel = new(timeout);
        try { return pipe.ReadAsync(answer, cancel.Token).AsTask().GetAwaiter().GetResult() == 1 ? answer[0] : -1; }
        catch (Exception error) when (error is IOException or OperationCanceledException) { return -1; }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException) { return true; } // no longer running
        catch (InvalidOperationException) { return true; } // exited while being opened
        catch (Win32Exception) { return false; } // cannot be watched, so it cannot be said to have exited
    }

    private static bool WaitUntilReleased(string name, TimeSpan timeout)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (IsHeld(name))
        {
            if (clock.Elapsed >= timeout) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    private static int Milliseconds(TimeSpan timeout) => (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
