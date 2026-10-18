using System.Diagnostics;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Caches the refresh rate of each window's monitor for <see cref="IMediaRendererBackend.GetSurfaceRefreshRateHz"/>
/// (Stage 017A slice 3). Reads come from the video presentation thread, so they never query
/// Windows: <see cref="PrimeIfMissing"/> queries synchronously when a new window is attached
/// (on the host's thread), and a read of a value older than <see cref="MaxAge"/> returns it
/// and schedules one background refresh, which follows the window to another monitor.
/// </summary>
internal sealed class MonitorRefreshRateCache(Func<nint, double?> query, Func<long>? timestamp = null)
{
    /// <summary>How long a cached rate is used before it is refreshed in the background.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);

    private readonly Func<nint, double?> _query = query ?? throw new ArgumentNullException(nameof(query));
    private readonly Func<long> _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    private readonly Lock _gate = new();
    private readonly Dictionary<nint, Entry> _entries = [];

    /// <summary>
    /// Queries and caches the rate of a window not cached yet; a known window is left to the
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

        var hertz = SafeQuery(hwnd);
        lock (_gate)
            _ = _entries.TryAdd(hwnd, new Entry(hertz, _timestamp(), Refreshing: false));
    }

    /// <summary>The cached rate (null when unknown or never primed); schedules a refresh when stale.</summary>
    public double? Get(nint hwnd)
    {
        if (hwnd == 0)
            return null;

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(hwnd, out entry))
                return null;

            if (entry.Refreshing || Stopwatch.GetElapsedTime(entry.Timestamp, _timestamp()) < MaxAge)
                return entry.Hertz;

            _entries[hwnd] = entry with { Refreshing = true };
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(static state => state.Cache.RefreshInBackground(state.Hwnd), (Cache: this, Hwnd: hwnd), preferLocal: false);
        return entry.Hertz;
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

    private void RefreshInBackground(nint hwnd)
    {
        var hertz = SafeQuery(hwnd);
        lock (_gate)
        {
            // Detached while the query ran: do not bring the entry back.
            if (_entries.ContainsKey(hwnd))
                _entries[hwnd] = new Entry(hertz, _timestamp(), Refreshing: false);
        }
    }

    private double? SafeQuery(nint hwnd)
    {
        try
        {
            return _query(hwnd);
        }
        catch (Exception ex)
        {
            // Timing then uses the platform's global rate; never fail a present over this.
            Debug.WriteLine($"[MonitorRefreshRateCache] Refresh rate query failed: {ex.Message}");
            return null;
        }
    }

    private readonly record struct Entry(double? Hertz, long Timestamp, bool Refreshing);
}
