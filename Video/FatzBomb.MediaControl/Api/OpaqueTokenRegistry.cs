using System.Collections.Concurrent;

namespace FatzBomb.MediaControl;

/// <summary>
/// Thread-safe registry that strongly roots managed objects while they are exposed to unmanaged
/// code as an opaque, nonzero, never-reused token. Backs the frame-sink and shared-image handles.
/// </summary>
internal sealed class OpaqueTokenRegistry<T>(string exhaustedMessage) where T : class
{
    private readonly ConcurrentDictionary<long, T> _entries = new();
    private long _nextToken;

    /// <summary>Registers <paramref name="value"/> and returns its fresh token.</summary>
    public long Register(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        long token;
        do
        {
            token = Interlocked.Increment(ref _nextToken);
            if (token <= 0 || (IntPtr.Size == 4 && token > int.MaxValue))
                throw new InvalidOperationException(exhaustedMessage);
        }
        while (!_entries.TryAdd(token, value));

        return token;
    }

    /// <summary>Resolves a token while it is registered; zero never resolves.</summary>
    public bool TryResolve(nint token, out T? value)
    {
        value = null;
        return token != 0 && _entries.TryGetValue(token, out value);
    }

    /// <summary>Removes a token; unknown tokens are ignored.</summary>
    public void Remove(long token) => _entries.TryRemove(token, out _);
}
