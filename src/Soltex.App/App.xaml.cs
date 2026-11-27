using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Soltex.App.Settings;
using Soltex.App.Shell;

namespace Soltex.App;

public partial class App : Application
{
    private const string RenderSmokeArgument = "--render-smoke";
    private ShellServices? services;

    /// <summary>
    /// Set by in-process test hosts before constructing the application. WPF always runs OnStartup once the
    /// dispatcher pumps; without this a test process would take the real single-instance lock, install the crash
    /// reporter into the real profile and start real services.
    /// </summary>
    internal static bool HostedByTests { get; set; }

    public App()
    {
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (string.Equals(argument, RenderSmokeArgument, StringComparison.OrdinalIgnoreCase))
            {
                // Must happen before any font-backed resource loads; OnStartup is too late for deterministic capture.
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                break;
            }
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (HostedByTests) return;
        if (e.Args.Length == 2 && e.Args[0] == SystemRunner.Argument)
        {
            // Elevated De-Windows helper: no window, no single-instance lock; runs the request and exits.
            Shutdown(SystemRunner.Run(e.Args[1]));
            return;
        }
        bool renderSmoke = e.Args.Contains(RenderSmokeArgument, StringComparer.OrdinalIgnoreCase);
        if (renderSmoke)
        {
            // Unattended capture: a failure must end the process with a code and a file, never wait on a dialog.
            DispatcherUnhandledException += (_, failure) =>
            {
                failure.Handled = true;
                FailRenderSmoke(e.Args, failure.Exception);
            };
            try { Start(e.Args); }
            catch (Exception error) { FailRenderSmoke(e.Args, error); }
            return;
        }
        Start(e.Args);
    }

    private void Start(string[] args)
    {
        SuiteLaunchOptions options;
        try { options = SuiteLaunchOptions.Parse(args); }
        catch (ArgumentException error)
        {
            if (args.Contains(RenderSmokeArgument, StringComparer.OrdinalIgnoreCase)) { FailRenderSmoke(args, error); return; }
            MessageBox.Show(error.Message, ProductIdentity.DisplayName);
            Shutdown(2);
            return;
        }
        if (options.CapturePath is null) CrashReporter.Install(this, options.ProfileDirectory);
        if (!AcquireInstance(options)) return;

        AppSettingsStore store = new(AppSettingsStore.DefaultPath(options.ProfileDirectory));
        AppSettings effective = store.Current;
        if (options.Theme is { } theme) effective = effective with { Theme = theme };
        if (options.Scale is { } scale) effective = effective with { ScalePercent = scale };
        ThemeService.Apply(this, effective);

        services = new ShellServices(options.ProfileDirectory, store, Dispatcher);
        bool renderSmoke = options.CapturePath is not null;
        DrawingCheck? drawing = null;
        if (!renderSmoke)
        {
            // Before the first window: a driver that showed a blank window earlier this boot gets software drawing at once.
            ShellServices shared = services;
            drawing = new DrawingCheck(Path.Combine(services.DataRoot, "Settings", "software-drawing.txt"),
                message => shared.Report(NoticeSeverity.Information, "Display", message));
            drawing.ApplyAtStartup();
        }
        if (!renderSmoke) services.Start();
        else
        {
            // A capture never reaches real audio: the Audio page shows made-up state, Sonar's tab included,
            // instead of starting the audio engine, reading Windows sessions or contacting Sonar.
            Pages.AudioPreview.Enabled = true;
            Pages.AudioPage.SonarInstalled = true;
        }
        ShellWindow window = new(services, renderSmoke);
        MainWindow = window;
        drawing?.Watch(window);
        if (options.Scale is not null) ThemeService.ApplyScale((FrameworkElement)window.Content, effective);
        window.Closed += (_, _) => Shutdown();

        if (options.CapturePath is { } outputPath)
        {
            switch (options.Panel)
            {
                case "audio": window.Navigate(Destination.Audio); break;
                case "audio-mic": window.Navigate(Destination.Audio, "microphone"); break;
                case "audio-apps": window.Navigate(Destination.Audio, "apps"); break;
                case "audio-sonar": window.Navigate(Destination.Audio, "sonar"); break;
                case "tools": window.Navigate(Destination.Tools); break;
                case "customize": window.Navigate(Destination.Customize); break;
                case "taskbar": window.Navigate(Destination.Taskbar); break;
                case "start": window.Navigate(Destination.Start); break;
                case "explorer": window.Navigate(Destination.Explorer); break;
                case "appearance": window.Navigate(Destination.Appearance); break;
                case "multitasking": window.Navigate(Destination.Multitasking); break;
                case "desktop": window.Navigate(Destination.Desktop); break;
                case "history": window.Navigate(Destination.History); break;
                case "settings": window.Navigate(Destination.Settings); break;
                case "my-pc": window.Navigate(Destination.MyPc); break;
            }
            RenderSmokeSnapshot(window, outputPath, options.Panel == "search",
                options.CaptureWidth ?? RenderSmokeCapture.PixelWidth, options.CaptureHeight ?? RenderSmokeCapture.PixelHeight);
            return;
        }

        if (options.Tray)
        {
            // Sign-in start: stay in the notification area; resume audio only if the user asked for it.
            if (store.Current.StartAudioWhenSignedIn) _ = window.StartAudioAtSignInAsync();
        }
        else
        {
            window.Show();
            if (options.Audio) window.OpenAudio();
        }
    }

    /// <summary>Ends a render capture that could not start or finish: the error goes to stderr and next to the capture path.</summary>
    private void FailRenderSmoke(string[] args, Exception error)
    {
        string text = error.ToString();
        try { Console.Error.WriteLine(text); }
        catch (IOException) { }
        if (RenderSmokeOutput(args) is { } output)
        {
            try
            {
                if (Path.GetDirectoryName(output) is { } directory) Directory.CreateDirectory(directory);
                File.WriteAllText(output + ".error.txt", text);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                try { Console.Error.WriteLine("The error file could not be written: " + writeError.Message); }
                catch (IOException) { }
            }
        }
        Environment.ExitCode = 2;
        Shutdown(2);
    }

    /// <summary>The capture path that follows --render-smoke, when it is an absolute path.</summary>
    private static string? RenderSmokeOutput(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, RenderSmokeArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length) return null;
        try { return Path.IsPathFullyQualified(args[index + 1]) ? Path.GetFullPath(args[index + 1]) : null; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static void RenderSmokeSnapshot(ShellWindow window, string outputPath, bool openSearch, int width, int height)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The render-smoke output path must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        RenderSmokeCapture.ConfigureWindow(window, width, height);
        window.Left = -32_000;
        window.Top = -32_000;
        window.Show();
        if (openSearch) window.OpenSearchForCapture("micro");

        DispatcherTimer timer = new(DispatcherPriority.ContextIdle, window.Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            int exitCode = 0;
            try
            {
                window.UpdateLayout();
                RenderTargetBitmap bitmap = RenderSmokeCapture.Capture(window, width, height);
                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using FileStream stream = new(fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(stream);
            }
            catch (Exception exception)
            {
                // Any failure is reported in a file and the exit code; a capture run has no one to answer a dialog.
                exitCode = 1;
                try { File.WriteAllText(fullOutputPath + ".error.txt", exception.ToString()); }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                try { Console.Error.WriteLine(exception); }
                catch (IOException) { }
            }
            finally
            {
                Environment.ExitCode = exitCode;
                window.RequestQuit();
            }
        };
        timer.Start();
    }
}
