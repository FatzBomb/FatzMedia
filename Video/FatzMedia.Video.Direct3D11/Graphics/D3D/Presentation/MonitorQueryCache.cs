using System.Diagnostics;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Caches a per-monitor value for each window: the refresh rate of its monitor
/// (<see cref="MonitorRefreshRateCache"/>, Stage 017A slice 3) or its HDR state (Stage 042A
/// slice 2). Reads come from the video presentation thread, so they never query Windows:
/// <see cref="PrimeIfMissing"/> queries synchronously when a new window is attached (on the
/// host's thread), and a read of a value older than <see cref="MaxAge"/> returns it and
/// schedules one background refresh, which follows the window to another monitor.
/// </summary>
class MonitorQueryCache<T>(Func<nint, T?> query, Func<long>? timestamp = null)
    where T : struct
{
    /// <summary>How long a cached value is used before it is refreshed in the background.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

    readonly Func<nint, T?> _query = query ?? throw new ArgumentNullException(nameof(query));
    readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    readonly Lock _gate = new();
    readonly Dictionary<nint, Entry> _entries = [];

    /// <summary>
    /// Queries and caches the value of a window not cached yet; a known window is left to the
    /// background refresh, so re-attaching it never queries on the caller's thread.
    /// </summary>
    public void PrimeIfMissing(nint hwnd)
    {
        if (hwnd == 0)
            return;

        lock (_gate)
        {
            if (_entries.ContainsKey(hwnd))
                return;
        }

        var value = SafeQuery(hwnd);
        lock (_gate)
            _ = _entries.TryAdd(hwnd, new Entry(value, _timestamp(), Refreshing: false));
    }

    /// <summary>The cached value (null when unknown or never primed); schedules a refresh when stale.</summary>
    public T? Get(nint hwnd)
    {
        if (hwnd == 0)
            return null;

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(hwnd, out entry))
                return null;

            if (entry.Refreshing || Stopwatch.GetElapsedTime(entry.Timestamp, _timestamp()) < MaxAge)
                return entry.Value;

            _entries[hwnd] = entry with { Refreshing = true };
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(static state => state.Cache.RefreshInBackground(state.Hwnd), (Cache: this, Hwnd: hwnd), preferLocal: false);
        return entry.Value;
    }

    /// <summary>Forgets every window, as when the backend is disposed.</summary>
    public void Clear()
    {
        lock (_gate)
            _entries.Clear();
    }

    /// <summary>Forgets <paramref name="hwnd"/>, as when its surface is detached.</summary>
    public void Remove(nint hwnd)
    {
        lock (_gate)
            _ = _entries.Remove(hwnd);
    }

    void RefreshInBackground(nint hwnd)
    {
        var value = SafeQuery(hwnd);
        lock (_gate)
        {
            // Detached while the query ran: do not bring the entry back.
            if (_entries.ContainsKey(hwnd))
                _entries[hwnd] = new Entry(value, _timestamp(), Refreshing: false);
        }
    }

    T? SafeQuery(nint hwnd)
    {
        try
        {
            return _query(hwnd);
        }
        catch (Exception ex)
        {
            // The caller then falls back (the platform's global rate, SDR output); never fail
            // a present over this.
            Debug.WriteLine($"[MonitorQueryCache] {typeof(T).Name} query failed: {ex.Message}");
            return null;
        }
    }

    readonly record struct Entry(T? Value, long Timestamp, bool Refreshing);
}

/// <summary>
/// Caches the refresh rate of each window's monitor for <c>IMediaRendererBackend.GetSurfaceRefreshRateHz</c>
/// (Stage 017A slice 3); see <see cref="MonitorQueryCache{T}"/>.
/// </summary>
sealed class MonitorRefreshRateCache(Func<nint, double?> query, Func<long>? timestamp = null)
    : MonitorQueryCache<double>(query, timestamp);
