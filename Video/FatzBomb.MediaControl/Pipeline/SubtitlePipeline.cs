using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Pipeline;

/// <summary>
/// Passive sidecar subtitle source. Cue selection is driven by the active media clock rather than
/// receiving play, pause, stop, or seek commands of its own.
/// </summary>
internal sealed class SubtitlePipeline(ISubtitleEngine engine, MediaState state) : IDisposable
{
    private readonly MediaState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SubtitleSource> _sources = new(StringComparer.Ordinal);

    public ISubtitleEngine Engine { get; } = engine ?? throw new ArgumentNullException(nameof(engine));
    public bool IsOpen => Engine.IsOpen;
    public IReadOnlyList<SubtitleTrack> Tracks
    {
        get
        {
            lock (_gate)
                return [.. _sources.Values.Select(static source => source.Track)];
        }
    }

    public string? SelectedTrackId
    {
        get
        {
            lock (_gate)
                return field;
        }

        private set;
    }

    public bool Open(string filePath)
    {
        try
        {
            var shouldSelect = false;
            lock (_gate)
            {
                shouldSelect = _sources.Count == 0;
                _sources["sidecar"] = new SubtitleSource(
                    new SubtitleTrack("sidecar", Path.GetFileName(filePath), IsEmbedded: false),
                    filePath,
                    null);
            }

            if (shouldSelect)
            {
                var selected = SelectTrack("sidecar");
                if (selected)
                    _state.AddContent(MediaContent.Subtitle);
                return selected;
            }

            _state.AddContent(MediaContent.Subtitle);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Failed to load subtitle file: {ex.Message}", sourcePath: filePath, innerException: ex);
            return false;
        }
    }

    public bool LoadEmbeddedTracks(IReadOnlyList<EmbeddedSubtitleTrack> tracks, bool selectFirstTrack = true)
    {
        try
        {
            if (tracks.Count == 0)
                return false;

            string? firstTrackId;
            bool shouldSelect;
            lock (_gate)
            {
                shouldSelect = selectFirstTrack && _sources.Count == 0;
                firstTrackId = null;
                foreach (EmbeddedSubtitleTrack track in tracks)
                {
                    var trackId = $"embedded:{track.StreamIndex}";
                    _sources[trackId] = new SubtitleSource(
                        new SubtitleTrack(trackId, $"Embedded {track.CodecName} (stream {track.StreamIndex})", IsEmbedded: true),
                        null,
                        track.Cues);
                    firstTrackId ??= trackId;
                }

            }

            if (shouldSelect && firstTrackId != null)
            {
                var selected = SelectTrack(firstTrackId);
                if (selected)
                    _state.AddContent(MediaContent.Subtitle);
                return selected;
            }

            if (firstTrackId != null)
                _state.AddContent(MediaContent.Subtitle);
            return firstTrackId != null;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Failed to load embedded subtitle cues: {ex.Message}", innerException: ex);
            return false;
        }
    }

    public bool SelectTrack(string? trackId)
    {
        if (trackId == null)
        {
            lock (_gate)
                SelectedTrackId = null;
            Engine.Close();
            return true;
        }

        SubtitleSource source;
        lock (_gate)
        {
            if (!_sources.TryGetValue(trackId, out SubtitleSource? found) || found is null)
                return false;

            source = found;
        }

        try
        {
            ActivateTrack(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Failed to select subtitle track: {ex.Message}", sourcePath: source.FilePath, innerException: ex);
            return false;
        }

        lock (_gate)
            SelectedTrackId = trackId;
        return true;
    }

    public void Close()
    {
        lock (_gate)
        {
            _sources.Clear();
            SelectedTrackId = null;
        }

        Engine.Close();
    }

    public void Dispose() => Engine.Dispose();

    private void ActivateTrack(SubtitleSource source)
    {
        if (source.FilePath != null)
            Engine.Load(source.FilePath);
        else
            Engine.LoadCues(source.Cues!);
    }

    private sealed record SubtitleSource(SubtitleTrack Track, string? FilePath, IReadOnlyList<SubtitleCue>? Cues);
}
