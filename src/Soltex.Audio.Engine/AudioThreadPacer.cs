using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Soltex.Audio.Engine;

/// <summary>
/// Paces a periodic audio thread (the owned transport router) in short intervals without touching the system timer
/// resolution. A plain 10 ms wait lands on the default 15.6 ms Windows timer tick; a high-resolution waitable timer
/// (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803 and later) wakes this thread alone close to the requested time,
/// with no global timeBeginPeriod and no busy waiting. Where that timer is unavailable it falls back to an ordinary wait
/// on the stop event. Created, used and disposed by the one thread it paces.
/// </summary>
public sealed partial class AudioThreadPacer : IDisposable
{
    private const uint CreateHighResolution = 0x00000002;
    private const uint TimerModifyState = 0x0002, Synchronize = 0x00100000;
    private readonly ManualResetEventSlim stop;
    private readonly TimerHandle? timer;
    private readonly WaitHandle[]? handles;

    /// <summary>True when waits use the high-resolution timer; false on the plain-wait fallback.</summary>
    public bool IsHighResolution => timer is not null;

    /// <param name="stop">Wakes a wait early when set (the thread's shutdown signal).</param>
    public AudioThreadPacer(ManualResetEventSlim stop)
    {
        this.stop = stop ?? throw new ArgumentNullException(nameof(stop));
        try
        {
            nint created = CreateWaitableTimerExW(0, null, CreateHighResolution, TimerModifyState | Synchronize);
            if (created != 0) { timer = new TimerHandle(created); handles = [stop.WaitHandle, timer]; }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>Waits <paramref name="milliseconds"/>, or less if stop is set. Returns true when stop was set.</summary>
    public bool Wait(int milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(milliseconds);
        if (timer is null || handles is null) return stop.Wait(milliseconds);
        long due = -10_000L * milliseconds; // relative, in 100 ns units
        if (!SetWaitableTimer(timer.SafeWaitHandle, in due, 0, 0, 0, false)) return stop.Wait(milliseconds);
        // The bound only guards against a timer that never fires; the timer or the stop event wakes the thread.
        return WaitHandle.WaitAny(handles, milliseconds + 100) == 0;
    }

    public void Dispose() => timer?.Dispose();

    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, nint completionRoutine, nint argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}
