using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Soltex.Audio.Sessions;

namespace Soltex.Audio.WindowsRouting;

public enum RoutingDirection { Render, Capture }
public enum RoutingRole { Console, Multimedia, Communications }
public sealed record ProcessPolicy(RoutingDirection Direction, RoutingRole Role, string? RawDeviceId);
public sealed record OwnedProcessIdentity(int ProcessId, DateTime StartedUtc, string ExecutablePath, string Sha256);

/// <summary>
/// Experimental OS-dependent ABI. Owns a dedicated MTA. Public probes own their child processes;
/// selected-session writes are internal to the journaled AppRoutingService.
/// No default-endpoint, all-app-reset or registry-writing operation is exposed.
/// </summary>
public sealed class WindowsAudioPolicy : IDisposable
{
    private readonly BlockingCollection<Action<NativePolicy>> work = new();
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    public static Guid InterfaceId { get; } = new("ab3d4648-e242-459f-b02f-541c70306324");
    public WindowsAudioPolicy()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 21390)) throw new PlatformNotSupportedException("This verified ABI requires Windows build21390 or newer.");
        thread = new Thread(Run) { IsBackground = true, Name = "Soltex owned-process policy MTA" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch { work.Dispose(); throw; }
    }

    private void Run()
    {
        try
        {
            using var policy = new NativePolicy();
            ready.SetResult();
            foreach (var action in work.GetConsumingEnumerable()) action(policy);
        }
        catch (Exception error) { ready.TrySetException(error); }
    }
    private T Invoke<T>(Func<NativePolicy, T> operation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        work.Add(policy =>
        {
            try { completion.SetResult(operation(policy)); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    public IReadOnlyList<ProcessPolicy> ReadCurrentProcess() => Read(checked((uint)Environment.ProcessId));
    internal SelectedRoutingProcess OpenSelected(AudioSessionInfo selected) => new(this, selected);
    private ProcessPolicy[] Read(uint pid) => Invoke(policy =>
        (from direction in Enum.GetValues<RoutingDirection>() from role in Enum.GetValues<RoutingRole>()
         select new ProcessPolicy(direction, role, policy.Get(pid, direction, role))).ToArray());

    internal sealed class SelectedRoutingProcess : IDisposable
    {
        private readonly WindowsAudioPolicy owner;
        private readonly Process process;
        private readonly AudioSessionInfo selected;
        private readonly int sessionId;
        internal SelectedRoutingProcess(WindowsAudioPolicy owner, AudioSessionInfo selected)
        {
            this.owner = owner; this.selected = selected;
            using (var self = Process.GetCurrentProcess()) sessionId = self.SessionId;
            process = Process.GetProcessById(selected.Key.ProcessId);
            try { Validate(); } catch { process.Dispose(); throw; }
        }
        private void Validate()
        {
            if (selected.Key.ProcessStartTimeUtc is null || string.IsNullOrWhiteSpace(selected.ExecutablePath) ||
                process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != selected.Key.ProcessStartTimeUtc ||
                process.SessionId != sessionId ||
                !string.Equals(ReadProcessPath(process), Path.GetFullPath(selected.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The selected application's identity changed or cannot be verified; refresh the application list.");
        }
        internal ProcessPolicy[] Read() => owner.Invoke(policy =>
        {
            Validate();
            return (from direction in Enum.GetValues<RoutingDirection>() from role in Enum.GetValues<RoutingRole>()
                select new ProcessPolicy(direction, role, policy.Get(checked((uint)process.Id), direction, role))).ToArray();
        });
        internal ProcessPolicy CompareSet(RoutingDirection direction, RoutingRole role, string? expected, string? desired) => owner.Invoke<ProcessPolicy>(policy =>
        {
            Validate();
            if (!Enum.IsDefined(direction) || !Enum.IsDefined(role)) throw new ArgumentException("Invalid routing direction or role.");
            if (!string.Equals(policy.Get(checked((uint)process.Id), direction, role), expected, StringComparison.Ordinal))
                throw new IOException("The application's policy changed elsewhere; the newer choice was preserved.");
            Validate();
            policy.Set(checked((uint)process.Id), direction, role, desired);
            Validate();
            return new(direction, role, policy.Get(checked((uint)process.Id), direction, role));
        });
        public void Dispose() => process.Dispose();
    }

    public OwnedRoutingProcess LaunchOwned(string executablePath, params string[] arguments)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var path = Path.GetFullPath(executablePath);
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a dedicated apphost executable, never a shared runtime host.", nameof(executablePath));
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new IOException("Owned test process did not start.");
        try
        {
            var actual = ReadProcessPath(process);
            if (!Path.GetFullPath(actual).Equals(path, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Child executable identity mismatch: expected '{path}', observed '{actual}'.");
            var launchedDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actual)));
            if (launchedDigest != digest) throw new IOException("Child executable bytes changed during launch.");
            return new OwnedRoutingProcess(this, process, new(process.Id, process.StartTime.ToUniversalTime(), path, digest));
        }
        catch { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } process.Dispose(); throw; }
    }

    public sealed class OwnedRoutingProcess : IDisposable
    {
        private readonly WindowsAudioPolicy owner;
        private readonly Process process;
        private bool disposed;
        internal OwnedRoutingProcess(WindowsAudioPolicy owner, Process process, OwnedProcessIdentity identity)
        { this.owner = owner; this.process = process; Identity = identity; }
        public OwnedProcessIdentity Identity { get; }
        public StreamWriter Input => process.StandardInput;
        public StreamReader Output => process.StandardOutput;
        public StreamReader Error => process.StandardError;
        private void Validate()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process.HasExited || process.StartTime.ToUniversalTime() != Identity.StartedUtc ||
                !string.Equals(ReadProcessPath(process), Identity.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Owned process exited or changed identity; routing operation rejected.");
        }
        public IReadOnlyList<ProcessPolicy> Read()
        {
            Validate();
            return owner.Read(checked((uint)Identity.ProcessId));
        }
        public ProcessPolicy Set(RoutingDirection direction, RoutingRole role, string? rawDeviceId)
        {
            Validate();
            if (!Enum.IsDefined(direction) || !Enum.IsDefined(role)) throw new ArgumentException("Invalid direction or role.");
            return owner.Invoke(policy =>
            {
                Validate();
                policy.Set(checked((uint)Identity.ProcessId), direction, role, rawDeviceId);
                return new ProcessPolicy(direction, role, policy.Get(checked((uint)Identity.ProcessId), direction, role));
            });
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.WriteLine("exit"); process.StandardInput.Flush();
                    if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(3000); }
                }
            }
            finally { process.Dispose(); }
        }
    }

    public static string ToRawDeviceId(string endpointId, RoutingDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        return @"\\?\SWD#MMDEVAPI#" + endpointId + (direction == RoutingDirection.Render
            ? "#{e6327cad-dcec-4949-ae8a-991e976a79d2}" : "#{2eef81be-33fa-4800-9670-1cd474972c3f}");
    }
    private static string ReadProcessPath(Process process)
    {
        // Query the retained kernel process handle; MainModule can be null before the loader publishes its module list.
        var buffer = new char[32768];
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var length = checked((uint)buffer.Length);
            if (QueryFullProcessImageName(process.SafeHandle, 0, buffer, ref length))
                return new string(buffer, 0, checked((int)length));
            var error = Marshal.GetLastPInvokeError();
            if (process.HasExited || attempt == 19) throw new IOException($"Cannot verify owned child image path (Win32 {error}).");
            Thread.Sleep(10);
        }
        throw new IOException("Owned child image verification exhausted its bounded wait.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        work.CompleteAdding();
        thread.Join();
        work.Dispose();
    }

    private sealed class NativePolicy : IDisposable
    {
        private readonly nint instance;
        private readonly SetEndpoint set;
        private readonly GetEndpoint get;
        internal NativePolicy()
        {
            Marshal.ThrowExceptionForHR(RoInitialize(1));
            try
            {
                var id = InterfaceId;
                using var name = new HString("Windows.Media.Internal.AudioPolicyConfig");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(name.Handle, ref id, out instance));
                var table = Marshal.ReadIntPtr(instance);
                // Vtable order from the interface metadata: IUnknown (3) + IInspectable (3) + 19 preceding methods.
                set = Marshal.GetDelegateForFunctionPointer<SetEndpoint>(Marshal.ReadIntPtr(table, 25 * IntPtr.Size));
                get = Marshal.GetDelegateForFunctionPointer<GetEndpoint>(Marshal.ReadIntPtr(table, 26 * IntPtr.Size));
            }
            catch { if (instance != 0) Marshal.Release(instance); RoUninitialize(); throw; }
        }
        internal string? Get(uint pid, RoutingDirection direction, RoutingRole role)
        {
            nint result = 0;
            try
            {
                var status = get(instance, pid, (int)direction, (int)role, out result);
                if (status < 0) throw new IOException($"GetPersistedDefaultAudioEndpoint pid={pid} direction={direction} role={role} failed HRESULT0x{status:X8}.");
                if (result == 0) return null;
                var buffer = WindowsGetStringRawBuffer(result, out var length);
                return Marshal.PtrToStringUni(buffer, checked((int)length));
            }
            finally { ReleaseString(result); }
        }
        internal void Set(uint pid, RoutingDirection direction, RoutingRole role, string? rawId)
        {
            using var id = new HString(rawId);
            Marshal.ThrowExceptionForHR(set(instance, pid, (int)direction, (int)role, id.Handle));
        }
        public void Dispose() { Marshal.Release(instance); RoUninitialize(); }
    }
    private sealed class HString : IDisposable
    {
        internal nint Handle { get; }
        internal HString(string? value)
        {
            if (value is null) return;
            Marshal.ThrowExceptionForHR(WindowsCreateString(value, checked((uint)value.Length), out var handle));
            Handle = handle;
        }
        public void Dispose() => ReleaseString(Handle);
    }
    private static void ReleaseString(nint handle)
    {
        if (handle == 0) return;
        var result = WindowsDeleteString(handle);
        if (result < 0) Trace.TraceError("WindowsDeleteString failed: 0x{0:X8}", result);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEndpoint(nint self, uint pid, int direction, int role, nint endpoint);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEndpoint(nint self, uint pid, int direction, int role, out nint endpoint);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(uint mode);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void RoUninitialize();
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, uint length, out nint result);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern nint WindowsGetStringRawBuffer(nint value, out uint length);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] buffer, ref uint length);
}
