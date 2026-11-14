using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Soltex.App;

/// <summary>
/// Last-resort handling for failures nothing else caught. Writes a bounded local report and tells the
/// user in plain language what happened and where the report is. Nothing is uploaded.
/// </summary>
internal static class CrashReporter
{
    private const int MaximumReports = 20;
    private static string? reportDirectory;
    private static int handlingUiFailure;

    public static void Install(Application application, string? profileDirectory)
    {
        reportDirectory = Path.Combine(
            profileDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.DataFolderName),
            "logs",
            "crash");
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => TryWrite("process", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryWrite("background-task", e.Exception);
            e.SetObserved();
        };
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (Interlocked.Exchange(ref handlingUiFailure, 1) != 0) return;
        string? report = TryWrite("window", e.Exception);
        string where = report is null
            ? "A report could not be saved."
            : "A report was saved to:\n" + report;
        MessageBox.Show(
            $"{ProductIdentity.DisplayName} ran into a problem it could not recover from and will close.\n\n" +
            "Your saved settings are unchanged. Audio routes you started stop when the app closes.\n\n" + where,
            ProductIdentity.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Application.Current?.Shutdown(3);
    }

    internal static string? TryWrite(string source, Exception? exception)
    {
        try
        {
            if (reportDirectory is null) return null;
            Directory.CreateDirectory(reportDirectory);
            string path = Path.Combine(reportDirectory, string.Create(CultureInfo.InvariantCulture,
                $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{source}.txt"));
            StringBuilder text = new();
            text.AppendLine($"{ProductIdentity.DisplayName} failure report (local only)");
            CultureInfo invariant = CultureInfo.InvariantCulture;
            text.AppendLine(invariant, $"Time (UTC): {DateTime.UtcNow:O}");
            text.AppendLine(invariant, $"Source: {source}");
            text.AppendLine(invariant, $"Version: {typeof(CrashReporter).Assembly.GetName().Version}");
            text.AppendLine(invariant, $"OS: {Environment.OSVersion.VersionString}; 64-bit process: {Environment.Is64BitProcess}");
            text.AppendLine();
            text.AppendLine(exception?.ToString() ?? "(no exception object)");
            File.WriteAllText(path, text.ToString());
            Prune();
            return path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Prune()
    {
        FileInfo[] reports = new DirectoryInfo(reportDirectory!).GetFiles("crash-*.txt");
        if (reports.Length <= MaximumReports) return;
        foreach (FileInfo old in reports.OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(MaximumReports))
        {
            try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
