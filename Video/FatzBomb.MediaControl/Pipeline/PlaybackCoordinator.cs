
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class PlaybackCoordinator : IDisposable
{
    private const string MidiLyricsDisplayName = "Embedded MIDI Lyrics (stream 0)";
    private readonly MediaSession _session;
    private readonly TimeSpan _karaokeTransitionDuration;
    private readonly IMidiLyricProvider _midiLyricProvider;

    public PlaybackCoordinator(
        IAudioEngine audioEngine,
        ICdgEngine cdgEngine,
        ISubtitleEngine subtitleEngine,
        IVideoEngineFactory videoEngineFactory,
        IMidiLyricProvider midiLyricProvider,
        MediaPlayerConfiguration configuration,
        bool ownsAudioEngine = false)
    {
        ArgumentNullException.ThrowIfNull(videoEngineFactory);
        _midiLyricProvider = midiLyricProvider ?? throw new ArgumentNullException(nameof(midiLyricProvider));
        _session = new MediaSession();
        _karaokeTransitionDuration = TimeSpan.FromMilliseconds(configuration.KaraokeStyle.Normalize().LineTransitionMilliseconds);
        var audio = new AudioPipeline(audioEngine, _session.State, ownsAudioEngine);
        var cdg = new CdgPipeline(cdgEngine, _session.Clock, _session.State);
        var subtitle = new SubtitlePipeline(subtitleEngine, _session.State);
        var video = videoEngineFactory.Create(_session.Clock, _session.State, audioEngine, configuration, _session.Sync);

        audio.PlaybackEnded += OnPlaybackEnded;
        cdg.PlaybackEnded += OnPlaybackEnded;
        video.PlaybackEnded += OnPlaybackEnded;

        _session.AttachAudio(audio);
        _session.AttachCdg(cdg);
        _session.AttachSubtitle(subtitle);
        _session.AttachVideo(video);
    }

    public MediaState State => _session.State;
    public IClockSource ClockSource => _session.Clock;
    public event EventHandler? PlaybackEnded;


    public ICdgEngine? Cdg => _session.Cdg?.IsOpen == true ? _session.Cdg.Engine : null;
    public ISubtitleEngine? Subtitle => _session.Subtitle?.IsOpen == true ? _session.Subtitle.Engine : null;
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => _session.Subtitle?.Tracks ?? [];
    public string? SelectedSubtitleTrackId => _session.Subtitle?.SelectedTrackId;
    public bool ShowMidiLyrics
    {
        get;
        set
        {
            if (field == value)
                return;

            field = value;
            SubtitleTrack? midiTrack = SubtitleTracks.FirstOrDefault(static track => track.DisplayName == MidiLyricsDisplayName);
            if (midiTrack == null)
                return;

            if (value)
                _ = _session.Subtitle?.SelectTrack(midiTrack.Id);
            else if (SelectedSubtitleTrackId == midiTrack.Id)
                _ = _session.Subtitle?.SelectTrack(null);
        }
    } = true;
    public double AudioVideoOffsetSeconds => _session.Sync.AudioVideoOffsetSeconds;
    public double CdgOffsetSeconds => _session.Sync.CdgOffsetSeconds;
    public double SubtitleOffsetSeconds => _session.Sync.SubtitleOffsetSeconds;
    public IVideoEngine? Video => _session.Video?.IsOpen == true ? _session.Video : null;

    public bool LoopPlayback
    {
        get => _session.Video?.LoopPlayback == true;
        set => _session.Video?.LoopPlayback = value;
    }

    /// <summary>
    /// Supplies the actual monitor refresh rate to the underlying video pipeline regardless of
    /// whether a file is currently open, since the pipeline instance itself is stable for the
    /// coordinator's lifetime. See IVideoEngine.SetDisplayRefreshRate for details.
    /// </summary>
    public void SetDisplayRefreshRate(double? refreshHz) => _session.Video?.SetDisplayRefreshRate(refreshHz);
    public IAudioEngine ActiveAudio => _session.Video?.IsOpen == true
        ? _session.Video.HasAudio ? _session.Video.Audio! : _session.ClockAudio
        : _session.Audio?.IsOpen == true
            ? _session.Audio.Engine
            : _session.Cdg?.IsOpen == true ? _session.ClockAudio : _session.Audio?.Engine ?? _session.ClockAudio;

    public double DurationSeconds
    {
        get
        {
            var duration = 0.0;
            if (_session.Audio?.IsOpen == true)
                duration = Math.Max(duration, _session.Audio.DurationSeconds);
            if (_session.Cdg?.IsOpen == true)
                duration = Math.Max(duration, _session.Cdg.DurationSeconds);
            if (_session.Video?.IsOpen == true)
                duration = Math.Max(duration, _session.Video.Duration);
            if (duration <= 0)
                duration = _session.Clock.DurationSeconds;
            return duration;
        }
    }

    public double PositionSeconds
    {
        get => _session.Video?.IsOpen == true
              ? _session.Video.Position
              : _session.Audio?.IsOpen == true ? _session.Audio.PositionSeconds : _session.Clock.PositionSeconds;
        set => Seek(value);
    }

    public bool OpenAudio(string path) => _session.Audio?.Open(path) == true && TryLoadMidiLyrics(path);

    public bool OpenAudio(IArchiveEntryInfo archiveEntry) =>
        _session.Audio?.Open(archiveEntry) == true && TryLoadMidiLyrics(archiveEntry.Key);

    public bool OpenAudioCdg(string audioPath, string cdgPath)
    {
        if (_session.Audio?.Open(audioPath, ownState: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgPath, _session.Audio.PlayHandle);
        _session.State.SetOpened(MediaContent.Audio | MediaContent.Cdg, audioPath, DurationSeconds);
        return true;
    }

    public bool OpenAudioCdg(IArchiveEntryInfo audioEntry, IArchiveEntryInfo cdgEntry)
    {
        if (_session.Audio?.Open(audioEntry, ownState: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgEntry, _session.Audio.PlayHandle);
        _session.State.SetOpened(MediaContent.Audio | MediaContent.Cdg | MediaContent.Archive, audioEntry.Key, DurationSeconds);
        return true;
    }

    public bool OpenStandaloneCdg(string cdgPath)
    {
        if (_session.Cdg == null)
            return false;

        _session.Cdg.OpenStandalone(cdgPath);
        return true;
    }

    public bool OpenStandaloneCdg(IArchiveEntryInfo cdgEntry)
    {
        if (_session.Cdg == null)
            return false;

        _session.Cdg.OpenStandalone(cdgEntry);
        return true;
    }

    public bool OpenVideo(string videoPath)
    {
        _session.Subtitle?.Close();
        if (_session.Video?.Open(videoPath) != true)
            return false;

        TryLoadEmbeddedSubtitles(videoPath);
        return true;
    }

    public bool OpenVideo(string videoPath, string subtitlePath)
    {
        if (!OpenVideo(videoPath))
            return false;

        // Embedded subtitles take precedence; the sidecar remains selectable. A malformed or unreadable sidecar must not prevent
        // the independently valid video from opening.
        _ = _session.Subtitle?.Open(subtitlePath);
        return true;
    }

    public bool OpenVideo(IArchiveEntryInfo videoEntry) => _session.Video?.Open(videoEntry) == true;

    public bool Play()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Play();

        var hasAudio = _session.Audio?.IsOpen == true;
        var hasCdg = _session.Cdg?.IsOpen == true;
        var success = false;

        if (hasAudio)
            success |= _session.Audio!.Play();
        if (hasCdg)
            success |= _session.Cdg!.Play();

        return success;
    }

    public bool Pause()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Pause();

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Pause();
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Pause();
        return success;
    }

    public bool Stop()
    {
        if (_session.Video?.IsOpen == true)
            return _session.Video.Stop();

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Stop();
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Stop();
        return success;
    }

    public bool Seek(double positionSeconds)
    {
        var target = SeekHelpers.NormalizeSeekTarget(positionSeconds, DurationSeconds);

        if (_session.Video?.IsOpen == true)
            return _session.Video.Seek(target);

        var success = false;
        if (_session.Audio?.IsOpen == true)
            success |= _session.Audio.Seek(target);
        if (_session.Cdg?.IsOpen == true)
            success |= _session.Cdg.Seek(target);
        return success;
    }

    public bool SelectSubtitleTrack(string? trackId) => _session.Subtitle?.SelectTrack(trackId) == true;

    public double SetAudioVideoOffset(double seconds) => _session.Sync.SetAudioVideoOffset(seconds);
    public double SetCdgOffset(double seconds) => _session.Sync.SetCdgOffset(seconds);
    public double SetSubtitleOffset(double seconds) => _session.Sync.SetSubtitleOffset(seconds);
    public double AdjustAudioVideoOffset(double seconds) => _session.Sync.AdjustAudioVideoOffset(seconds);
    public double AdjustCdgOffset(double seconds) => _session.Sync.AdjustCdgOffset(seconds);
    public double AdjustSubtitleOffset(double seconds) => _session.Sync.AdjustSubtitleOffset(seconds);
    public void ResetSyncOffsets() => _session.Sync.Reset();

    public void Close() => _session.Clear();

    public void Dispose()
    {
        _session.Audio?.PlaybackEnded -= OnPlaybackEnded;
        _session.Cdg?.PlaybackEnded -= OnPlaybackEnded;
        _session.Video?.PlaybackEnded -= OnPlaybackEnded;

        _session.Dispose();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) => PlaybackEnded?.Invoke(this, EventArgs.Empty);

    private void TryLoadEmbeddedSubtitles(string videoPath)
    {
        try
        {
            IReadOnlyList<EmbeddedSubtitleTrack> tracks = EmbeddedSubtitleDecoder.DecodeTracks(videoPath);
            if (tracks.Count > 0)
                _ = _session.Subtitle?.LoadEmbeddedTracks(tracks);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Subtitle", $"Failed to decode embedded subtitles: {ex.Message}", sourcePath: videoPath, innerException: ex);
        }
    }

    private bool TryLoadMidiLyrics(string audioPath)
    {
        if (!_midiLyricProvider.IsMidiFile(audioPath))
            return true;

        try
        {
            var sourceHandle = _session.Audio?.Engine.SourceHandle ?? 0;
            IReadOnlyList<SubtitleCue>? cues = _midiLyricProvider.ExtractLyrics(
                sourceHandle,
                audioPath,
                _karaokeTransitionDuration);
            if (cues is { Count: > 0 })
            {
                _ = _session.Subtitle?.LoadEmbeddedTracks(
                [
                    new EmbeddedSubtitleTrack(0, "MIDI Lyrics", cues),
                ],
                selectFirstTrack: ShowMidiLyrics);
            }
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MIDI", $"Failed to extract MIDI lyrics: {ex.Message}", sourcePath: audioPath, innerException: ex);
        }

        return true;
    }
}
