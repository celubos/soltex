using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Soltex.Shared;

/// <summary>
/// Moves a fully written temporary file over its destination. Windows refuses the replace with
/// sharing or access errors while any process holds the destination open without delete sharing
/// (antivirus scanning a fresh write, the search indexer, backup tools, or a reader). Those holds
/// are brief, so the replace is retried for a bounded time instead of silently dropping the save.
/// A failure that outlasts the budget is still thrown to the caller unchanged.
/// </summary>
internal static class AtomicFileReplace
{
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);

    internal static void Move(string temporary, string destination, TimeSpan? budget = null)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan limit = budget ?? DefaultBudget;
        int delayMilliseconds = 10;
        while (true)
        {
            try
            {
                File.Move(temporary, destination, overwrite: true);
                return;
            }
            catch (Exception error) when (IsTransientLock(error) && elapsed.Elapsed < limit)
            {
                Thread.Sleep(delayMilliseconds);
                delayMilliseconds = Math.Min(delayMilliseconds * 2, 200);
            }
        }
    }

    private static bool IsTransientLock(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult & 0xFFFF) is 32 or 33 or 5, // sharing violation, lock violation, access denied
        _ => false
    };
}
