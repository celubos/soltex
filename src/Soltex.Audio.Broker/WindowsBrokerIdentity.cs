using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Soltex.Audio.Broker;

internal sealed partial class WindowsBrokerIdentity : IBrokerIdentity
{
    public void BeforeConnect()
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) throw new IOException("Soltex audio broker service status cannot be read.", new Win32Exception(Marshal.GetLastPInvokeError()));
        using var service = OpenServiceW(manager, BrokerProtocol.ServiceName, 4);
        if (service.IsInvalid) throw new IOException("Soltex audio broker service is not installed or is inaccessible; independent driver transport is unavailable.");
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) || status.State != 4 || status.ProcessId == 0 || status.Type != 0x10)
            throw new IOException("Soltex audio broker service is not running; independent driver transport is unavailable.");
    }

    public BrokerPeer Client(NamedPipeServerStream pipe)
    {
        uint session = 0;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out session)) Fail();
        if (pid == 0 || session == 0 || session == uint.MaxValue) throw new UnauthorizedAccessException("No interactive client identity.");
        BrokerIdentityPolicy.ConsoleSession(session, WTSGetActiveConsoleSessionId());
        string? sid = null; bool authenticated = false; TokenImpersonationLevel level = TokenImpersonationLevel.None;
        pipe.RunAsClient(() =>
        {
            using var token = WindowsIdentity.GetCurrent(true);
            if (token is null || !token.IsAuthenticated || token.IsAnonymous || token.ImpersonationLevel < TokenImpersonationLevel.Impersonation)
                throw new UnauthorizedAccessException("An authenticated impersonation token is required.");
            sid = token.User?.Value;
            authenticated = token.IsAuthenticated && !token.IsAnonymous; level = token.ImpersonationLevel;
        });
        using var process = OpenProcess(0x1000, false, pid);
        long birth = 0;
        uint actualSession = 0;
        if (process.IsInvalid || !GetProcessTimes(process, out birth, out _, out _, out _) ||
            !GetExitCodeProcess(process, out uint exit) || exit != 259 || !ProcessIdToSessionId(pid, out actualSession) || actualSession != session) Fail();
        if (!OpenProcessToken(process, 8, out var processToken)) Fail();
        string? processSid;
        using (processToken)
        using (var identity = new WindowsIdentity(processToken.DangerousGetHandle()))
            processSid = identity.User?.Value;
        if (!WTSQuerySessionInformationW(0, session, 8, out nint state, out uint bytes)) Fail();
        bool active;
        try { active = bytes == 4 && Marshal.ReadInt32(state) == 0; }
        finally { WTSFreeMemory(state); }
        if (!WTSQueryUserToken(session, out var userToken)) Fail();
        string? userSid;
        using (userToken)
        using (var user = new WindowsIdentity(userToken.DangerousGetHandle()))
            userSid = user.User?.Value;
        return BrokerIdentityPolicy.Client(pid, birth, session, actualSession, sid, processSid, userSid, active, authenticated, level);
    }

    public BrokerPeer Server(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0) Fail();
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid) Fail();
        using var service = OpenServiceW(manager, BrokerProtocol.ServiceName, 5); // QUERY_CONFIG | QUERY_STATUS, never START/CHANGE_CONFIG.
        if (service.IsInvalid) Fail();
        if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _) ||
            status.State != 4 || status.ProcessId != pid || status.Type != 0x10) throw new UnauthorizedAccessException("Pipe is not the running dedicated Soltex broker service.");
        _ = QueryServiceConfigW(service, 0, 0, out uint size);
        if (size is 0 or > 8192) throw new UnauthorizedAccessException("Unrecognized service configuration.");
        nint memory = Marshal.AllocHGlobal((int)size); string? account;
        try
        {
            if (!QueryServiceConfigW(service, memory, size, out _)) Fail();
            var config = Marshal.PtrToStructure<ServiceConfig>(memory); account = Marshal.PtrToStringUni(config.Account);
            if (config.Type != 0x10 || !(string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("Broker service must run as LocalSystem.");
        }
        finally { Marshal.FreeHGlobal(memory); }
        using var process = OpenProcess(0x1000, false, pid);
        long birth = 0; uint session = uint.MaxValue;
        if (process.IsInvalid || !GetProcessTimes(process, out birth, out _, out _, out _) ||
            !GetExitCodeProcess(process, out uint exit) || exit != 259 || !ProcessIdToSessionId(pid, out session) || session != 0) Fail();
        // SCM is the authority for the service's primary account. Ordinary users need not open a SYSTEM token.
        return BrokerIdentityPolicy.Server(pid, status.ProcessId, status.State, status.Type, account, birth, session);
    }

    internal static NamedPipeServerStream CreateServerPipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (!identity.IsSystem || process.SessionId != 0) throw new UnauthorizedAccessException("Audio broker must run as the configured LocalSystem service.");
        // Interactive users may connect; per-request token/WTS validation grants the actual lease.
        // First-instance ownership plus a one-instance limit prevents a parallel counterfeit server;
        // clients independently check SCM PID/account. Remote clients are rejected at the kernel boundary.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:SYG:SYD:P(A;;GA;;;SY)(A;;GRGW;;;IU)", 1, out nint descriptor, out _)) Fail();
        try
        {
            SecurityAttributes attributes = new() { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW(@"\\.\pipe\" + BrokerProtocol.PipeName, 0x40080003, 8, 1, 32768, 32768, 0, ref attributes);
            if (handle.IsInvalid) { handle.Dispose(); Fail(); }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail() => throw new Win32Exception(Marshal.GetLastPInvokeError(), "Broker identity could not be verified.");
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, Start, Error; public nint Path, Group; public uint Tag; public nint Dependencies, Account, Display; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)] private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ProcessIdToSessionId(uint pid, out uint session);
    [LibraryImport("kernel32.dll")] private static partial uint WTSGetActiveConsoleSessionId();
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [LibraryImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool WTSQuerySessionInformationW(nint server, uint session, int info, out nint buffer, out uint bytes);
    [LibraryImport("wtsapi32.dll")] private static partial void WTSFreeMemory(nint buffer);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, uint size, out uint needed);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryServiceConfigW(ServiceHandle service, nint config, uint size, out uint needed);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseServiceHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [LibraryImport("kernel32.dll")] private static partial nint LocalFree(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial SafePipeHandle CreateNamedPipeW(string name, uint open, uint mode, uint instances, uint outSize, uint inSize, uint timeout, ref SecurityAttributes security);
}
