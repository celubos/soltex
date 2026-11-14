using System.IO;
using System.Text.Json;
using System.Windows;

namespace Soltex.Audio.App.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    [STAThread]
    private static int Main(string[] args)
    {
        string run = "soltex-audio-ui-tests-" + Guid.NewGuid().ToString("N");
        string workRoot = Path.GetTempPath();
        string? renderRoot = null;
        for (int index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !Path.IsPathFullyQualified(args[index + 1]))
                throw new ArgumentException("Use --work-dir <absolute directory> and optional --render-dir <absolute directory>.");
            switch (args[index])
            {
                case "--work-dir": workRoot = args[index + 1]; break;
                case "--render-dir": renderRoot = args[index + 1]; break;
                default: throw new ArgumentException($"Unknown argument: {args[index]}");
            }
        }
        string workingDirectory = Directory.CreateDirectory(Path.Combine(workRoot, run)).FullName;
        string? renderDirectory = renderRoot is null ? null : Directory.CreateDirectory(Path.Combine(renderRoot, run)).FullName;
        Console.WriteLine($"Owned fixture files: {workingDirectory}");
        if (renderDirectory is not null) Console.WriteLine($"Simulated viewport renders (not live OS DPI): {renderDirectory}");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var results = new List<Result>();
        application.Startup += async (_, _) =>
        {
            try
            {
                foreach ((int width, int height) in new[] { (1240, 810), (1000, 680), (960, 540), (800, 600), (640, 420) })
                    await RunAsync($"Mixer reachability {width}x{height}", () => LayoutChecks.ViewportAsync(width, height, renderDirectory));
                await RunAsync("Real engine controls, presets and worker lifecycle", () => EngineWorkspaceChecks.ControlsAndLifecycleAsync(workingDirectory, renderDirectory));
                await RunAsync("Unknown preset preservation and explicit reset", () => EngineWorkspaceChecks.RejectedPreferencesAsync(workingDirectory, false));
                await RunAsync("Malformed preset preservation through close", () => EngineWorkspaceChecks.RejectedPreferencesAsync(workingDirectory, true));
                await RunAsync("Embedded workspace ownership, scoped theme and async shutdown", () => SharedWorkspaceChecks.OwnershipAndLifetimeAsync(workingDirectory));
                await RunAsync("Host Processing override creates no duplicate child", () => SharedWorkspaceChecks.ProcessingOverrideAsync(workingDirectory));
            }
            finally
            {
                File.WriteAllText(Path.Combine(workingDirectory, "results.json"), JsonSerializer.Serialize(results, ReportOptions));
                Console.WriteLine($"{results.Count(result => result.Passed)}/{results.Count} UI checks passed.");
                application.Shutdown(results.All(result => result.Passed) ? 0 : 1);
            }
        };
        return application.Run();

        async Task RunAsync(string name, Func<Task> check)
        {
            try { await check(); results.Add(new(name, true, null)); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { results.Add(new(name, false, error.ToString())); Console.WriteLine($"FAIL {name}\n{error}"); }
        }
    }

    private sealed record Result(string Name, bool Passed, string? Error);
}
