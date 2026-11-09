using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Core.Subtitle;

sealed class SubtitleEngine : ISubtitleEngine
{
    readonly Lock _gate = new();
    // Sorted by start; _latestEndThrough[i] is the latest end among cues 0..i, which bounds the
    // backward scan for cues still active at a position.
    SubtitleCue[] _cues = [];
    TimeSpan[] _latestEndThrough = [];

    public bool IsOpen { get; private set; }

    public event EventHandler? CuesChanged;

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var content = File.ReadAllText(filePath);
        var cues = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => AssParser.Parse(content),
            _ => SrtParser.Parse(content)
        };

        lock (_gate)
        {
            SetCues(cues);
            IsOpen = true;
        }

        CuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<SubtitleCue> GetActiveCues(double positionSeconds)
    {
        if (!double.IsFinite(positionSeconds) || positionSeconds < 0)
            return [];

        lock (_gate)
        {
            if (!IsOpen)
                return [];

            var position = TimeSpan.FromSeconds(positionSeconds);
            List<SubtitleCue>? active = null;
            for (var index = LastStartingAtOrBefore(position); index >= 0 && _latestEndThrough[index] > position; index--)
            {
                if (_cues[index].End > position)
                    (active ??= []).Add(_cues[index]);
            }

            if (active is null)
                return [];

            active.Reverse();
            return active;
        }
    }

    public void LoadCues(IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);

        lock (_gate)
        {
            SetCues(cues);
            IsOpen = _cues.Length > 0;
        }

        CuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        lock (_gate)
        {
            SetCues([]);
            IsOpen = false;
        }

        CuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Close();

    void SetCues(IReadOnlyList<SubtitleCue> cues)
    {
        _cues = [.. cues.OrderBy(static cue => cue.Start).ThenBy(static cue => cue.Sequence)];
        _latestEndThrough = new TimeSpan[_cues.Length];
        var latestEnd = TimeSpan.MinValue;
        for (var index = 0; index < _cues.Length; index++)
        {
            if (_cues[index].End > latestEnd)
                latestEnd = _cues[index].End;
            _latestEndThrough[index] = latestEnd;
        }
    }

    /// <summary>The index of the last cue starting at or before <paramref name="position"/>, or -1.</summary>
    int LastStartingAtOrBefore(TimeSpan position)
    {
        int low = 0, high = _cues.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_cues[middle].Start <= position)
                low = middle + 1;
            else
                high = middle;
        }

        return low - 1;
    }
}
