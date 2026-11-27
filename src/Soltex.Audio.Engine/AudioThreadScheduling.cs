using System.Runtime.InteropServices;

namespace Soltex.Audio.Engine;

/// <summary>
/// Registers audio threads with the Multimedia Class Scheduler Service ("Pro Audio" task) so capture, render and
/// transport pacing threads keep running on time while games or other work load the CPU. NAudio's WASAPI threads run
/// at normal priority without MMCSS, so each owned callback registers its own thread once (thread-static flag).
/// The registration ends automatically when the stream's thread exits.
/// </summary>
public static partial class AudioThreadScheduling
{
    public const string TaskName = "Pro Audio";
    [ThreadStatic] private static bool attempted;
    [ThreadStatic] private static bool registered;
    private static long registrations, failures;

    /// <summary>Threads registered since start (diagnostics).</summary>
    public static long Registrations => Interlocked.Read(ref registrations);
    /// <summary>Registration attempts MMCSS refused or that could not run (diagnostics).</summary>
    public static long Failures => Interlocked.Read(ref failures);
    /// <summary>True when the calling thread is registered.</summary>
    public static bool CurrentThreadRegistered => registered;

    /// <summary>Cheap after the first call on a thread; safe to call at the top of every audio callback.</summary>
    public static bool RegisterCurrentThread()
    {
        if (attempted) return registered;
        attempted = true;
        try
        {
            uint taskIndex = 0;
            if (AvSetMmThreadCharacteristics(TaskName, ref taskIndex) != 0)
            {
                registered = true; Interlocked.Increment(ref registrations); return true;
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
        Interlocked.Increment(ref failures);
        return false;
    }

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
}
