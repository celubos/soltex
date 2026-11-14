using System.Security.Principal;

namespace Soltex.Audio.Broker;

internal static class BrokerIdentityPolicy
{
    internal static void ConsoleSession(uint session, uint activeConsole)
    {
        if (session is 0 or uint.MaxValue || session != activeConsole)
            throw new UnauthorizedAccessException("Only the current console session may own the global audio transport.");
    }
    internal static BrokerPeer Client(uint pid, long birth, uint pipeSession, uint processSession,
        string? pipeSid, string? processSid, string? interactiveSid, bool active, bool authenticated, TokenImpersonationLevel level)
    {
        if (pid == 0 || birth <= 0 || pipeSession is 0 or uint.MaxValue || pipeSession != processSession ||
            string.IsNullOrWhiteSpace(pipeSid) || pipeSid != processSid || pipeSid != interactiveSid ||
            !active || !authenticated || level < TokenImpersonationLevel.Impersonation)
            throw new UnauthorizedAccessException("Client identity is not the active logged-on interactive user.");
        return new(pid, birth, pipeSession, pipeSid);
    }
    internal static BrokerPeer Server(uint pipePid, uint servicePid, uint state, uint type, string? account, long birth, uint session)
    {
        if (pipePid == 0 || pipePid != servicePid || state != 4 || type != 0x10 || birth <= 0 || session != 0 ||
            !(string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Server is not the running dedicated LocalSystem broker service.");
        return new(pipePid, birth, session, "S-1-5-18");
    }
}
