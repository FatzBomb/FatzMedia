namespace FatzMedia.Video.Core.Audio;

sealed class MediaAnalysisCache
{
    public const int DefaultCapacity = 256;

    public static MediaAnalysisCache Shared { get; } = new();

    readonly int _analyzerVersion;
    readonly int _capacity;
    readonly Lock _gate = new();
    readonly Dictionary<MediaAnalysisCacheKey, CacheEntry> _entries = [];
    readonly LinkedList<MediaAnalysisCacheKey> _usageOrder = [];

    public MediaAnalysisCache(int capacity = DefaultCapacity, int analyzerVersion = MediaAnalysisVersions.Current)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(analyzerVersion, 1);
        _capacity = capacity;
        _analyzerVersion = analyzerVersion;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(MediaAnalysisCacheKey key, out MediaAnalysisResult? result)
    {
        result = null;
        if (key.AnalyzerVersion != _analyzerVersion)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            // Keep the read guard even though writes are validated so a future persisted
            // cache cannot promote malformed or partially migrated data into playback.
            if (!MediaAnalysisResultValidator.IsValid(entry.Result))
            {
                RemoveEntry(key, entry);
                return false;
            }

            _usageOrder.Remove(entry.UsageNode);
            _usageOrder.AddLast(entry.UsageNode);
            result = entry.Result;
            return true;
        }
    }

    public bool TryStore(MediaAnalysisCacheKey key, MediaAnalysisResult result)
    {
        if (key.AnalyzerVersion != _analyzerVersion || !MediaAnalysisResultValidator.IsValid(result))
        {
            return false;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existingEntry))
            {
                existingEntry.Result = result;
                _usageOrder.Remove(existingEntry.UsageNode);
                _usageOrder.AddLast(existingEntry.UsageNode);
                return true;
            }

            var usageNode = _usageOrder.AddLast(key);
            _entries.Add(key, new CacheEntry(result, usageNode));
            while (_entries.Count > _capacity)
            {
                var oldestNode = _usageOrder.First!;
                RemoveEntry(oldestNode.Value, _entries[oldestNode.Value]);
            }

            return true;
        }
    }

    void RemoveEntry(MediaAnalysisCacheKey key, CacheEntry entry)
    {
        _usageOrder.Remove(entry.UsageNode);
        _entries.Remove(key);
    }

    sealed class CacheEntry(MediaAnalysisResult result, LinkedListNode<MediaAnalysisCacheKey> usageNode)
    {
        public MediaAnalysisResult Result { get; set; } = result;

        public LinkedListNode<MediaAnalysisCacheKey> UsageNode { get; } = usageNode;
    }
}
