namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// The conversions kept for prepared-next items, keyed by source identity. Several players each
/// keep their own next item, so one player preparing does not evict another's; the oldest entry
/// is dropped (and disposed) past <paramref name="capacity"/>. Thread-safe.
/// </summary>
sealed class PreparedConversionCache<T>(int capacity, Func<T, string> identityOf)
    where T : class, IDisposable
{
    readonly Lock _gate = new();
    readonly List<T> _entries = []; // oldest first

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>The kept entry for <paramref name="identity"/>, still owned by the cache.</summary>
    public T? Find(string identity)
    {
        lock (_gate)
            return _entries.Find(entry => identityOf(entry) == identity);
    }

    /// <summary>
    /// Keeps <paramref name="entry"/> and returns the entry now kept for its identity: an equal one
    /// added meanwhile by another thread wins and <paramref name="entry"/> is disposed.
    /// </summary>
    public T Add(T entry)
    {
        var identity = identityOf(entry);
        T? evicted = null;
        T kept;
        lock (_gate)
        {
            var existing = _entries.Find(candidate => identityOf(candidate) == identity);
            if (existing is not null)
            {
                kept = existing;
            }
            else
            {
                _entries.Add(entry);
                kept = entry;
                if (_entries.Count > capacity)
                {
                    evicted = _entries[0];
                    _entries.RemoveAt(0);
                }
            }
        }

        if (!ReferenceEquals(kept, entry))
            entry.Dispose();
        evicted?.Dispose();
        return kept;
    }

    /// <summary>Removes the entry for <paramref name="identity"/> and hands it to the caller, who now owns it.</summary>
    public T? Take(string identity)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => identityOf(entry) == identity);
            if (index < 0)
                return null;

            var entry = _entries[index];
            _entries.RemoveAt(index);
            return entry;
        }
    }

    /// <summary>Removes and disposes the entry for <paramref name="identity"/>, if kept.</summary>
    public void Release(string identity) => Take(identity)?.Dispose();
}
