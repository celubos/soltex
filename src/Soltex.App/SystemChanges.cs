using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Soltex.Customize;

namespace Soltex.App;

/// <summary>A request to the elevated helper: catalogue ids only, never actions.</summary>
internal sealed record SystemRequest(string Action, string[] Items);

internal sealed record SystemResponse(SystemResult[] Results, string? Error);

/// <summary>
/// The elevated side (Soltex.exe --system &lt;request&gt;). It runs only <see cref="SystemCatalog"/> entries named by id,
/// and writes only its journal and result next to the request, inside the user's own app-data folder.
/// </summary>
internal static class SystemRunner
{
    public const string Argument = "--system";
    public const string RequestName = "system-request.json", ResultName = "system-result.json", JournalName = "system-journal-v1.jsonl";

    public static int Run(string requestPath)
    {
        string? directory = null;
        SystemResponse response;
        try
        {
            directory = TrustedDirectory(requestPath);
            SystemRequest request = JsonSerializer.Deserialize<SystemRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException("Empty request.");
            SystemItem[] items = request.Items.Select(id => SystemCatalog.All.FirstOrDefault(i => i.Id == id) ?? throw new InvalidDataException("Unknown change: " + id)).ToArray();
            SystemEngine engine = new(new WindowsSystemHost(), new ChangeJournal(Path.Combine(directory, JournalName)));
            IReadOnlyList<SystemResult> results = request.Action switch
            {
                "apply" => engine.Apply(items),
                "undo" => engine.Undo(items),
                _ => throw new InvalidDataException("Unknown action.")
            };
            response = new([.. results], null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            response = new([], error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Anything unexpected still produces a result file, so the app shows the reason instead of "no result".
            response = new([], "The administrator helper stopped: " + error.Message);
        }
        if (directory is not null)
        {
            try { File.WriteAllText(Path.Combine(directory, ResultName), JsonSerializer.Serialize(response)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 2; }
        }
        return response.Error is null ? 0 : 1;
    }

    /// <summary>The request must sit, under its fixed name, in a real (non-redirected) folder inside this app's data folder.</summary>
    internal static string TrustedDirectory(string requestPath)
    {
        string full = Path.GetFullPath(requestPath);
        if (!string.Equals(Path.GetFileName(full), RequestName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unexpected request file.");
        string directory = Path.GetDirectoryName(full)!;
        // Only this app's own data folder (not all of local app data, which also holds the temp folder).
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.DataFolderName));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The request is outside this app's data folder.");
        for (DirectoryInfo? d = new(directory); d is not null && d.FullName.Length > root.Length; d = d.Parent)
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new UnauthorizedAccessException("The request folder is redirected.");
        }
        return directory;
    }
}

/// <summary>The unelevated side: reads the current state freely and asks Windows for administrator rights to change it.</summary>
/// <param name="dataRoot">This app's data folder.</param>
/// <param name="explicitProfile">True with --profile-dir: the helper only accepts requests from the default data folder.</param>
/// <param name="ran">Called after the helper ran (whatever its result), so views showing system state can refresh.</param>
internal sealed class SystemChanges(string dataRoot, bool explicitProfile = false, Action? ran = null)
{
    private readonly string directory = Path.Combine(dataRoot, "System");

    /// <summary>Read-only view of the system state and of what this app changed (no administrator rights needed).</summary>
    public SystemEngine Inspector() => new(new WindowsSystemHost(), new ChangeJournal(Path.Combine(directory, SystemRunner.JournalName)));

    /// <summary>Why changes cannot be made from this session, or null when they can.</summary>
    public string? UnavailableReason => explicitProfile
        ? "Full De-Windows is not available while running with a separate profile folder."
        : !AdministratorAccount.IsAdministrator
            ? "Full De-Windows needs a Windows administrator account. Sign in as an administrator to use it."
            : null;

    /// <summary>Runs the elevated helper. Returns no results and an error when it cannot run, the user declines the prompt or it fails.</summary>
    public async Task<SystemResponse> RunAsync(string action, IReadOnlyList<string> ids)
    {
        if (UnavailableReason is { } reason) return new([], reason);
        if (Environment.ProcessPath is not { } executable) return new([], "The app's location is unknown.");
        string request = Path.Combine(directory, SystemRunner.RequestName);
        string result = Path.Combine(directory, SystemRunner.ResultName);
        try
        {
            Directory.CreateDirectory(directory);
            File.Delete(result);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new SystemRequest(action, [.. ids])));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new([], "The request could not be saved: " + error.Message);
        }
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = SystemRunner.Argument + " \"" + request + "\"",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        int exitCode;
        try
        {
            using Process? process = Process.Start(start);
            if (process is null) return new([], "The administrator helper did not start.");
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return new([], "Cancelled at the administrator prompt.");
        }
        catch (Win32Exception error)
        {
            return new([], "The administrator helper did not start: " + error.Message);
        }
        try
        {
            if (!File.Exists(result))
                return new([], exitCode == 1 ? "The administrator helper refused the request. Nothing was changed." : "The administrator helper ended without a result.");
            return JsonSerializer.Deserialize<SystemResponse>(await File.ReadAllTextAsync(result)) ?? new([], "The result could not be read.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new([], "The result could not be read: " + error.Message);
        }
        finally
        {
            ran?.Invoke();
        }
    }
}

/// <summary>Whether this Windows account can approve the administrator prompt itself.</summary>
internal static partial class AdministratorAccount
{
    private const int TokenElevationTypeClass = 18, TokenElevationTypeLimited = 3;
    private static readonly Lazy<bool> Cached = new(Detect);

    /// <summary>
    /// True for an administrator, elevated or not. A standard account would need someone else's credentials, and the helper
    /// would then run as that other account, outside this user's profile.
    /// </summary>
    public static bool IsAdministrator => Cached.Value;

    private static bool Detect()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        // Elevated, or User Account Control is off.
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        // An administrator running with the filtered (non-elevated) token.
        return GetTokenInformation(identity.Token, TokenElevationTypeClass, out int type, sizeof(int), out _) && type == TokenElevationTypeLimited;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
