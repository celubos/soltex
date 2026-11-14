namespace Soltex.Audio;

/// <summary>Small worker-owned LRU; each cached resource has exactly one disposal owner.</summary>
internal sealed class OwnedEndpointCache<T>(int capacity) : IDisposable where T : class, IDisposable
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;
    public int Count => _entries.Count;
    public IEnumerable<string> Keys => _entries.Keys;
    public bool TryGet(string id, out T? value)
    {
        if (_entries.TryGetValue(id, out Entry? entry))
        { entry.LastUse = ++_clock; value = entry.Value; return true; }
        value = null; return false;
    }
    public void Add(string id, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Remove(id);
        if (_entries.Count >= capacity) Remove(_entries.MinBy(pair => pair.Value.LastUse).Key);
        _entries.Add(id, new(value, ++_clock));
    }
    public bool Remove(string id)
    {
        if (!_entries.Remove(id, out Entry? entry)) return false;
        entry.Value.Dispose(); return true;
    }
    public void Clear()
    {
        foreach (Entry entry in _entries.Values) entry.Value.Dispose();
        _entries.Clear();
    }
    public void Dispose() => Clear();
    private sealed class Entry(T value, long lastUse)
    {
        public T Value { get; } = value;
        public long LastUse { get; set; } = lastUse;
    }
}

internal sealed record MeterCacheDiagnostics(int CachedEntries, long ConnectionAttempts, long SuccessfulActivations, long CacheHits,
    IReadOnlyDictionary<string, long> Notifications);
