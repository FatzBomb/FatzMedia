using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Core.Subtitle;

internal sealed class SubtitleEngine : ISubtitleEngine
{
    private readonly Lock _gate = new();
    private IReadOnlyList<SubtitleCue> _cues = [];

    public bool IsOpen { get; private set; }

    public event EventHandler? CuesChanged;

    public void Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var content = File.ReadAllText(filePath);
        IReadOnlyList<SubtitleCue> cues = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ass" or ".ssa" => AssParser.Parse(content),
            _ => SrtParser.Parse(content)
        };

        lock (_gate)
        {
            _cues = cues;
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
            return [.. _cues.Where(cue => cue.Start <= position && position < cue.End)];
        }
    }

    public void LoadCues(IReadOnlyList<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);

        lock (_gate)
        {
            _cues = [.. cues.OrderBy(static cue => cue.Start).ThenBy(static cue => cue.Sequence)];
            IsOpen = _cues.Count > 0;
        }

        CuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        lock (_gate)
        {
            _cues = [];
            IsOpen = false;
        }

        CuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Close();
}
