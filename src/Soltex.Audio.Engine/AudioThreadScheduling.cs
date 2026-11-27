using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Soltex.Audio.Engine;

/// <summary>
/// Registers audio threads with the Multimedia Class Scheduler Service ("Pro Audio" task) so capture, render and
/// transport pacing threads keep running on time while games or other work load the CPU. NAudio's WASAPI threads run
/// at normal priority without MMCSS, so each owned callback registers its own thread once (thread-static handle).
/// The registration is released explicitly: by <see cref="ReleaseCurrentThread"/> at the end of an owned worker thread,
/// and, for NAudio streams, by the stop event that NAudio raises on the stream's own thread (<see cref="ReleaseOnStop(IWaveIn)"/>),
/// so starting and stopping streams all day never accumulates MMCSS registrations.
/// </summary>
public static partial class AudioThreadScheduling
{
    public const string TaskName = "Pro Audio";

    /// <summary>The MMCSS calls, replaceable by tests.</summary>
    internal interface IScheduler
    {
        /// <summary>Returns a task handle, or zero when MMCSS refused.</summary>
        nint Register(string taskName);
        void Revert(nint handle);
    }

    private sealed partial class WindowsScheduler : IScheduler
    {
        public nint Register(string taskName) { uint taskIndex = 0; return AvSetMmThreadCharacteristics(taskName, ref taskIndex); }
        public void Revert(nint handle) => AvRevertMmThreadCharacteristics(handle);

        [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

        [LibraryImport("avrt.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AvRevertMmThreadCharacteristics(nint handle);
    }

    private static IScheduler scheduler = new WindowsScheduler();
    [ThreadStatic] private static bool attempted;
    [ThreadStatic] private static nint handle;
    [ThreadStatic] private static IScheduler? owner;
    private static long registrations, releases, failures;

    /// <summary>Test seam: the scheduler used for registrations made from now on. Each handle is released through the
    /// scheduler that granted it.</summary>
    internal static IScheduler Scheduler
    {
        get => Volatile.Read(ref scheduler);
        set => Volatile.Write(ref scheduler, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Threads registered since start (diagnostics).</summary>
    public static long Registrations => Interlocked.Read(ref registrations);
    /// <summary>Registrations released since start (diagnostics).</summary>
    public static long Releases => Interlocked.Read(ref releases);
    /// <summary>Registrations currently held (registered minus released).</summary>
    public static long ActiveRegistrations => Registrations - Releases;
    /// <summary>Registration attempts MMCSS refused or that could not run (diagnostics).</summary>
    public static long Failures => Interlocked.Read(ref failures);
    /// <summary>True when the calling thread is registered.</summary>
    public static bool CurrentThreadRegistered => handle != 0;

    /// <summary>Cheap after the first call on a thread; safe to call at the top of every audio callback.</summary>
    public static bool RegisterCurrentThread()
    {
        if (attempted) return handle != 0;
        attempted = true;
        var granting = Scheduler;
        try
        {
            nint granted = granting.Register(TaskName);
            if (granted != 0)
            {
                handle = granted; owner = granting; Interlocked.Increment(ref registrations); return true;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        Interlocked.Increment(ref failures);
        return false;
    }

    /// <summary>
    /// Ends the calling thread's registration (stream stopped or worker finishing). Safe to call on any thread and more
    /// than once; a later <see cref="RegisterCurrentThread"/> on the same thread registers again.
    /// </summary>
    public static void ReleaseCurrentThread()
    {
        nint held = handle; var granting = owner;
        attempted = false; handle = 0; owner = null;
        if (held == 0 || granting is null) return;
        try { granting.Revert(held); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        Interlocked.Increment(ref releases);
    }

    /// <summary>
    /// Releases the capture thread's registration when the stream stops. NAudio raises RecordingStopped on the capture
    /// thread itself when the stream was created without a SynchronizationContext (as every Soltex stream is). The handler
    /// keeps no state and stays attached for the stream's lifetime, so it also runs after other handlers were removed.
    /// </summary>
    public static void ReleaseOnStop(IWaveIn input)
    {
        ArgumentNullException.ThrowIfNull(input);
        input.RecordingStopped += ReleaseOnStopped;
    }

    /// <summary>Releases the render thread's registration when playback stops (PlaybackStopped runs on the render thread).</summary>
    public static void ReleaseOnStop(IWavePlayer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.PlaybackStopped += ReleaseOnStopped;
    }

    private static void ReleaseOnStopped(object? sender, StoppedEventArgs args) => ReleaseCurrentThread();
}
