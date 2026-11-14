using System.ComponentModel;
using System.Runtime.InteropServices;
using Soltex.Audio.Broker;

namespace Soltex.Audio.Broker.Service;

internal static partial class Program
{
    private const string Name = "SoltexAudioBroker";
    private static readonly ServiceMain MainCallback = RunService;
    private static readonly ServiceControl ControlCallback = Control;
    private static readonly CancellationTokenSource Shutdown = new();
    private static readonly object StatusLock = new();
    private static nint statusHandle;
    private static ServiceStatus status;
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--service")
        { Console.Error.WriteLine("This binary is the SoltexAudioBroker LocalSystem service. It installs nothing; reviewed service deployment is required. SCM argument: --service."); return 2; }
        nint name = Marshal.StringToHGlobalUni(Name);
        try
        {
            ServiceTable[] table = [new() { Name = name, Main = Marshal.GetFunctionPointerForDelegate(MainCallback) }, new()];
            if (!StartServiceCtrlDispatcherW(table)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return 0;
        }
        catch (Win32Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { Marshal.FreeHGlobal(name); Shutdown.Dispose(); }
    }
    private static void RunService(uint count, nint arguments)
    {
        statusHandle = RegisterServiceCtrlHandlerExW(Name, Marshal.GetFunctionPointerForDelegate(ControlCallback), 0);
        if (statusHandle == 0) return;
        Report(2, 0, 0, 10000);
        uint error = 0;
        try
        {
            var server = new AudioBrokerServer(); var running = server.RunAsync(Shutdown.Token);
            if (running.IsFaulted) running.GetAwaiter().GetResult();
            Report(4, 5, 0, 0);
            running.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (Shutdown.IsCancellationRequested) { }
        catch (Exception) { error = 1066; }
        finally { Report(1, 0, error, 0); }
    }
    private static uint Control(uint code, uint eventType, nint data, nint context)
    {
        if (code is 1 or 5) { Report(3, 0, 0, 10000); Shutdown.Cancel(); }
        else if (code == 4) { lock (StatusLock) _ = SetServiceStatus(statusHandle, ref status); }
        return 0;
    }
    private static void Report(uint state, uint accepted, uint error, uint wait)
    {
        lock (StatusLock)
        {
            status = new() { Type = 0x10, State = state, Accepted = accepted, Win32Exit = error, ServiceExit = error == 0 ? 0u : 1u, WaitHint = wait };
            _ = SetServiceStatus(statusHandle, ref status);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint count, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ServiceControl(uint code, uint eventType, nint data, nint context);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceTable { public nint Name, Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool StartServiceCtrlDispatcherW([In] ServiceTable[] table);
    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)] private static partial nint RegisterServiceCtrlHandlerExW(string name, nint callback, nint context);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetServiceStatus(nint handle, ref ServiceStatus status);
}
