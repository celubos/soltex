using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Soltex.Audio.Transport;

/// <summary>Overlapped device operations. Timeout cancels the exact I/O; its pinned storage lives until kernel completion.</summary>
internal sealed partial class WindowsOwnedAudioDevice : IOwnedAudioDevice
{
    private readonly SafeFileHandle handle;
    private bool disposed;
    public WindowsOwnedAudioDevice(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        handle = CreateFile(path, 0xc0000000, 0, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid) { int error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error, "Soltex owned transport could not open. A signed installed driver and authorized broker are required."); }
    }
    public unsafe byte[] Exchange(uint controlCode, byte[] request, int responseCapacity)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.Length > 19232 || responseCapacity < 56 || responseCapacity > 19256) throw new ArgumentOutOfRangeException(nameof(responseCapacity));
        var pending = new Pending(handle, request, responseCapacity);
        bool transferred = false;
        bool inFlight = false;
        try
        {
            bool immediate = DeviceIoControl(handle, controlCode, pending.Input.AddrOfPinnedObject(), (uint)request.Length,
                pending.Output.AddrOfPinnedObject(), (uint)responseCapacity, out _, pending.Overlapped);
            int error = immediate ? 0 : Marshal.GetLastPInvokeError();
            if (!immediate && error != 997) throw new Win32Exception(error);
            inFlight = !immediate;
            if (!immediate && !pending.Event.WaitOne(TimeSpan.FromSeconds(2)))
            {
                CancelIoEx(handle, pending.Overlapped);
                // The request may still own these buffers. Never free them just because cancellation was requested.
                transferred = true; pending.ReleaseWhenCompleted();
                disposed = true; handle.Dispose();
                throw new TimeoutException("Soltex transport exceeded the two-second I/O bound; cancellation is pending and this client is disabled.");
            }
            inFlight = false;
            if (!GetOverlappedResult(handle, pending.Overlapped, out uint count, false)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (count > responseCapacity) throw new InvalidDataException("Driver returned an invalid byte count.");
            return pending.Bytes.AsSpan(0, (int)count).ToArray();
        }
        finally
        {
            if (!transferred && inFlight) { CancelIoEx(handle, pending.Overlapped); pending.ReleaseWhenCompleted(); }
            else if (!transferred) pending.Dispose();
        }
    }
    public void Dispose() { if (disposed) return; disposed = true; handle.Dispose(); }

    private sealed unsafe class Pending : IDisposable
    {
        // Extremely rare wait-registration failure must retain kernel-owned storage, never free it early.
        private static readonly object quarantineSync = new();
        private static Pending? quarantineHead;
        private Pending? next;
        private readonly SafeFileHandle handle;
        private bool added;
        internal readonly GCHandle Input, Output;
        internal readonly byte[] Bytes;
        internal readonly EventWaitHandle Event = new(false, EventResetMode.ManualReset);
        internal readonly nint Overlapped;
        internal Pending(SafeFileHandle handle, byte[] input, int size)
        {
            this.handle = handle;
            try
            {
                handle.DangerousAddRef(ref added);
                Bytes = new byte[size]; Input = GCHandle.Alloc(input, GCHandleType.Pinned); Output = GCHandle.Alloc(Bytes, GCHandleType.Pinned);
                Overlapped = (nint)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
                if (Overlapped == 0) throw new InsufficientMemoryException("Cannot allocate native overlapped storage.");
                ((NativeOverlapped*)Overlapped)->EventHandle = Event.SafeWaitHandle.DangerousGetHandle();
            }
            catch { Dispose(); throw; }
        }
        internal void ReleaseWhenCompleted()
        {
            // Intrusive list insertion allocates nothing, including under memory pressure.
            lock (quarantineSync) { next = quarantineHead; quarantineHead = this; }
            try
            {
                ThreadPool.RegisterWaitForSingleObject(Event, static (state, timedOut) =>
                {
                    var pending = (Pending)state!;
                    lock (quarantineSync)
                    {
                        Pending? previous = null;
                        for (var item = quarantineHead; item is not null; item = item.next)
                        {
                            if (item == pending) { if (previous is null) quarantineHead = item.next; else previous.next = item.next; break; }
                            previous = item;
                        }
                    }
                    pending.Dispose();
                }, this, Timeout.Infinite, true);
            }
            catch (Exception) { /* Retain quarantined native storage until process teardown if registration cannot be allocated. */ }
        }
        public void Dispose()
        {
            NativeMemory.Free((void*)Overlapped); if (Input.IsAllocated) Input.Free(); if (Output.IsAllocated) Output.Free(); Event.Dispose();
            if (added) { added = false; handle.DangerousRelease(); }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint sharing, nint security, uint creation, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(SafeFileHandle device, nint overlapped, out uint returned, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(SafeFileHandle device, nint overlapped);
}
