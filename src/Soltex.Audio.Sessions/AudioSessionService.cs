using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Soltex.Audio.Sessions;

internal interface ISessionBackend : IDisposable
{
    AudioSessionSnapshot Capture();
    AudioSessionControlResult Control(AudioSessionKey key, double? volume, bool? mute);
}

/// <summary>Owns all COM objects on one MTA. Reads never change routing, defaults or audio state.</summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Asynchronous disposal drains and joins the MTA worker.")]
public sealed class AudioSessionService : IAsyncDisposable
{
    private readonly BlockingCollection<Action<ISessionBackend>> _commands = new(32);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task? _dispose;

    public AudioSessionService() : this(() => new WindowsSessionBackend()) { }

    internal AudioSessionService(Func<ISessionBackend> factory)
    {
        Thread worker = new(() => Work(factory)) { IsBackground = true, Name = "Soltex Windows audio sessions" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    public Task<AudioSessionSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Queue(backend => backend.Capture(), cancellationToken);

    internal Task<SessionDiagnostics> GetDiagnosticsAsync() => Queue(backend => backend is WindowsSessionBackend windows
        ? windows.Diagnostics : new SessionDiagnostics(0, 0, 0), CancellationToken.None);

    public Task<AudioSessionControlResult> SetSessionVolumeAsync(AudioSessionKey key, double scalar,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        SessionPolicy.ValidateVolume(scalar);
        return Queue(backend => backend.Control(key, scalar, null), cancellationToken);
    }

    public Task<AudioSessionControlResult> SetSessionMuteAsync(AudioSessionKey key, bool muted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Queue(backend => backend.Control(key, null, muted), cancellationToken);
    }

    /// <summary>Sequential, no accumulated frames. Discovery is cached for one second; peaks are fresh each frame.</summary>
    public async IAsyncEnumerable<AudioSessionSnapshot> WatchAsync(TimeSpan pollInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (pollInterval < TimeSpan.FromMilliseconds(100) || pollInterval > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<T> Queue<T>(Func<ISessionBackend, T> action, CancellationToken token)
    {
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_dispose is not null) return Task.FromException<T>(new ObjectDisposedException(nameof(AudioSessionService)));
            if (token.IsCancellationRequested) return Task.FromCanceled<T>(token);
            if (!_commands.TryAdd(backend =>
            {
                if (token.IsCancellationRequested) { result.TrySetCanceled(token); return; }
                // Once an operation starts, preserve its write/readback result even if cancellation arrives.
                try { result.TrySetResult(action(backend)); }
                catch (Exception exception) { result.TrySetException(exception); }
            })) result.TrySetException(new InvalidOperationException("The audio session queue is busy. Wait for the pending operation."));
        }
        return result.Task;
    }

    private void Work(Func<ISessionBackend> factory)
    {
        ISessionBackend? backend = null;
        try
        {
            try { backend = factory(); }
            catch (Exception exception) { backend = new FailedBackend(exception.Message); }
            foreach (Action<ISessionBackend> action in _commands.GetConsumingEnumerable()) action(backend);
        }
        finally
        {
            try { backend?.Dispose(); _finished.TrySetResult(); }
            catch (Exception exception) { _finished.TrySetException(exception); }
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (_gate)
        {
            if (_dispose is null) { _commands.CompleteAdding(); _dispose = FinishAsync(); }
            return new(_dispose);
        }
    }

    private async Task FinishAsync()
    {
        await _finished.Task.ConfigureAwait(false);
        _commands.Dispose();
    }

    private sealed class FailedBackend(string error) : ISessionBackend
    {
        public AudioSessionSnapshot Capture() => new(DateTimeOffset.UtcNow, [], [error]);
        public AudioSessionControlResult Control(AudioSessionKey key, double? volume, bool? mute) => new(false, false, null, error);
        public void Dispose() { }
    }
}
