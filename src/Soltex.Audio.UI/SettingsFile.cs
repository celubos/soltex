using System.IO;

namespace Soltex.Audio.UI;

/// <summary>
/// Writes one small settings file off the UI thread. The caller serializes on its own thread (so the content is a
/// consistent snapshot), writes are kept in order, and content identical to the last write is skipped, so a periodic
/// refresh that changes nothing touches no disk. The file is replaced atomically; it is not flushed with write-through,
/// because a lost last change after a power cut is acceptable for these preferences and a forced flush can stall.
/// </summary>
internal sealed class SettingsFile(string path)
{
    private byte[]? _last;
    private Task _pending = Task.CompletedTask;

    public string Path { get; } = path;

    /// <summary>Completes when every write requested so far has finished (successfully or not).</summary>
    public Task Pending => _pending.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private int _writes;

    /// <summary>Number of writes that reached the disk (for tests).</summary>
    internal int Writes => Volatile.Read(ref _writes);

    /// <summary>Queues <paramref name="content"/> unless it equals the last content written; throws when the write fails.</summary>
    public async Task SaveAsync(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_last is not null && content.AsSpan().SequenceEqual(_last)) { await Pending.ConfigureAwait(true); return; }
        _last = content;
        Task previous = _pending;
        Task write = WriteAfterAsync(previous, content);
        _pending = write;
        try { await write.ConfigureAwait(true); }
        catch
        {
            // Not written: forget it, so the next request with the same content tries again.
            if (ReferenceEquals(_last, content)) _last = null;
            throw;
        }
    }

    private async Task WriteAfterAsync(Task previous, byte[] content)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException) { }
        await Task.Run(() => Write(content)).ConfigureAwait(false);
        Interlocked.Increment(ref _writes);
    }

    private void Write(byte[] content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
                stream.Write(content);
            Soltex.Shared.AtomicFileReplace.Move(temporary, Path);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
