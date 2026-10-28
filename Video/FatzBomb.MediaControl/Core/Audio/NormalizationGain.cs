namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// How the two normalization passes combine, for file audio and video soundtracks alike. The
/// quick pass reads only the first seconds, which are often quiet (an intro, a film's opening):
/// it may only attenuate, so a loud song never starts too loud and a quiet intro is not boosted
/// up to 8x for a moment. The deep pass decides the boost from the louder of the two peaks, so a
/// transient the quick pass saw is never lost to the deep pass's sampling.
/// </summary>
internal static class NormalizationGainPolicy
{
    /// <summary>The most the quick pass may apply: no boost.</summary>
    public const double MaxQuickPassGain = 1.0;

    /// <summary>Gets the gain the quick pass applies for <paramref name="quickPeak"/>.</summary>
    public static double QuickGain(double quickPeak) =>
        Math.Min(AudioNormalizer.CalculateGainFromPeak(quickPeak), MaxQuickPassGain);

    /// <summary>Whether the quick pass held back a boost the deep pass may apply.</summary>
    public static bool QuickPassHeldBackBoost(double quickPeak) =>
        AudioNormalizer.CalculateGainFromPeak(quickPeak) > MaxQuickPassGain;

    /// <summary>Gets the peak the final gain comes from.</summary>
    public static double FinalPeak(double quickPeak, double deepPeak) => Math.Max(quickPeak, deepPeak);
}

/// <summary>
/// Final normalization gains by source (file, archive entry or video, with size and time stamp in
/// the key), shared by every player in the process so a file is analyzed once. Bounded: the least
/// recently used entry is dropped past <see cref="DefaultCapacity"/>.
/// </summary>
internal sealed class NormalizationGainCache(int capacity = NormalizationGainCache.DefaultCapacity)
{
    public const int DefaultCapacity = 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, double Gain)>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Key, double Gain)> _usageOrder = [];
    private readonly int _capacity = capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public static NormalizationGainCache Shared { get; } = new();

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool TryGetValue(string key, out double gain)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<(string Key, double Gain)>? node))
            {
                gain = 0;
                return false;
            }

            _usageOrder.Remove(node);
            _usageOrder.AddFirst(node);
            gain = node.Value.Gain;
            return true;
        }
    }

    public void Set(string key, double gain)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(string Key, double Gain)>? existing))
            {
                _usageOrder.Remove(existing);
                existing.Value = (key, gain);
                _usageOrder.AddFirst(existing);
                return;
            }

            _entries[key] = _usageOrder.AddFirst((key, gain));
            if (_entries.Count > _capacity && _usageOrder.Last is { } oldest)
            {
                _usageOrder.RemoveLast();
                _ = _entries.Remove(oldest.Value.Key);
            }
        }
    }
}
