using System.Diagnostics;

namespace Soltex.App.Shell;

/// <summary>
/// Restarts File Explorer for the current user session only, when the user asks. Windows normally restarts the
/// shell by itself; if it has not come back within a few seconds, it is started explicitly.
/// </summary>
internal static class ExplorerRestart
{
    public static async Task<bool> RestartAsync()
    {
        int session = Process.GetCurrentProcess().SessionId;
        Process[] shells = Process.GetProcessesByName("explorer").Where(p => p.SessionId == session).ToArray();
        try
        {
            foreach (Process shell in shells)
            {
                try { shell.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            foreach (Process shell in shells)
            {
                try { await shell.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            foreach (Process shell in shells) shell.Dispose();
        }

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (Running(session)) return true;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { return false; }
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(100);
            if (Running(session)) return true;
        }
        return false;
    }

    private static bool Running(int session)
    {
        Process[] found = Process.GetProcessesByName("explorer");
        try { return found.Any(p => p.SessionId == session); }
        finally { foreach (Process p in found) p.Dispose(); }
    }
}
