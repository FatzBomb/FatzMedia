
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed partial class PlaybackCoordinator : IDisposable
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
        bool ownsAudioEngine = false,
        bool audioAvailable = true,
        Func<string, CancellationToken, IReadOnlyList<EmbeddedSubtitleTrack>>? embeddedSubtitleDecoder = null)
    {
        ArgumentNullException.ThrowIfNull(videoEngineFactory);
        _decodeEmbeddedSubtitles = embeddedSubtitleDecoder ?? EmbeddedSubtitleDecoder.DecodeTracks;
        _midiLyricProvider = midiLyricProvider ?? throw new ArgumentNullException(nameof(midiLyricProvider));
        _session = new MediaSession();
        _karaokeTransitionDuration = TimeSpan.FromMilliseconds(configuration.KaraokeStyle.Normalize().LineTransitionMilliseconds);

        // Stage 043A: each mode attaches only the pipelines it plays. Background is video and
        // images on the clock (no audio, CD+G or subtitles); Audio is audio files plus video
        // soundtracks (no video, CD+G or subtitles).
        MediaPlayerMode mode = configuration.Mode;
        if (mode != MediaPlayerMode.Background)
        {
            var audio = new AudioPipeline(audioEngine, _session.State, ownsAudioEngine);
            audio.PlaybackEnded += OnPlaybackEnded;
            _session.AttachAudio(audio);
        }

        // The soundtrack engine (FFmpeg decode into the audio output) plays Audio mode's video
        // soundtracks and, in Full and Audio mode, network audio that opens through FFmpeg (https
        // and HLS, ENG-034). Without BASS (ENG-033) there is none: those loads are refused first.
        if (mode != MediaPlayerMode.Background && audioAvailable)
        {
            var bassDeviceIndex = audioEngine is IAudioOutputDeviceProvider deviceProvider
                ? deviceProvider.BassDeviceIndex
                : configuration.AudioOutputDeviceIndex;
            var soundtrack = new AudioPipeline(
                new SoundtrackAudioEngine(new BassOutputLatencyProvider(configuration.EnableUsbBluetoothLatencyHeuristic), bassDeviceIndex),
                _session.State,
                ownsAudioEngine: true);
            soundtrack.PlaybackEnded += OnPlaybackEnded;
            if (soundtrack.Engine is SoundtrackAudioEngine soundtrackEngine)
            {
                soundtrackEngine.NetworkBufferingChanged += OnNetworkBufferingChanged;
                soundtrackEngine.StreamMetadataChanged += OnNetworkStreamMetadataChanged;
            }

            _session.AttachSoundtrack(soundtrack);
        }

        if (mode == MediaPlayerMode.Full)
        {
            var cdg = new CdgPipeline(cdgEngine, _session.Clock, _session.State);
            cdg.PlaybackEnded += OnPlaybackEnded;
            _session.AttachCdg(cdg);
            _session.AttachSubtitle(new SubtitlePipeline(subtitleEngine, _session.State));
        }

        if (mode != MediaPlayerMode.Audio)
        {
            var video = videoEngineFactory.Create(_session.Clock, _session.State, audioEngine, configuration, _session.Sync);
            video.PlaybackEnded += OnPlaybackEnded;
            if (video is IMediaLoopEventSource loopEventSource)
                loopEventSource.MediaLooped += OnPlaybackLooped;
            if (video is INetworkVideoEngine networkVideo)
                networkVideo.NetworkBufferingChanged += OnNetworkBufferingChanged;
            _session.AttachVideo(video);
        }
    }

    public MediaState State => _session.State;
    public IClockSource ClockSource => _session.Clock;
    public event EventHandler? PlaybackEnded;
    public event EventHandler? PlaybackLooped;

    /// <summary>Buffering changes of a network video or network soundtrack (Stage 039A), from a timer thread.</summary>
    public event EventHandler<MediaBufferingChangedEventArgs>? NetworkBufferingChanged;

    /// <summary>In-stream titles of network audio played through FFmpeg (ENG-034), from a timer thread.</summary>
    public event EventHandler<MediaStreamMetadataChangedEventArgs>? NetworkStreamMetadataChanged;


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
        get => field;
        set
        {
            field = value;
            if (_session.FileAudio?.Engine is IAudioLoopControl audioLoopControl)
                audioLoopControl.LoopPlayback = value;
            if (_session.Soundtrack?.Engine is IAudioLoopControl soundtrackLoopControl)
                soundtrackLoopControl.LoopPlayback = value;
            if (_session.Video != null)
                _session.Video.LoopPlayback = value;
        }
    }

    public bool HandlesLoopInternally =>
        _session.Video?.IsOpen == true
        || (_session.Audio?.IsOpen == true
            && _session.Audio.Engine is IAudioLoopControl { LoopPlayback: true });

    /// <summary>
    /// Supplies the actual monitor refresh rate to the underlying video pipeline regardless of
    /// whether a file is currently open, since the pipeline instance itself is stable for the
    /// coordinator's lifetime. See IVideoEngine.SetDisplayRefreshRate for details.
    /// </summary>
    public void SetDisplayRefreshRate(double? refreshHz) => _session.Video?.SetDisplayRefreshRate(refreshHz);

    /// <summary>Video frame timing (Stage 017A slice 4), applied to the stable video pipeline.</summary>
    public VideoFrameTiming VideoFrameTiming
    {
        get => (_session.Video as IVideoFrameTimingControl)?.FrameTiming ?? VideoFrameTiming.SourceCadence;
        set
        {
            if (_session.Video is IVideoFrameTimingControl control)
                control.FrameTiming = value;
        }
    }
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
        _session.Audio?.Open(archiveEntry) == true && TryLoadMidiLyrics(archiveEntry.Key, archiveEntry);

    /// <summary>
    /// Audio mode (043A): plays the soundtrack of a video file as plain audio, with no video
    /// decoder. Fails when the file has no audio stream.
    /// </summary>
    public bool OpenSoundtrack(string videoPath) => OpenSoundtrack(pipeline => pipeline.Open(videoPath, ownState: true, analyzeSilence: false));

    /// <inheritdoc cref="OpenSoundtrack(string)"/>
    public bool OpenSoundtrack(IArchiveEntryInfo videoEntry) => OpenSoundtrack(pipeline => pipeline.Open(videoEntry));

    private bool OpenSoundtrack(Func<AudioPipeline, bool> open)
    {
        if (_session.Soundtrack is not { } soundtrack)
            return false;

        _session.UseSoundtrack(true);
        if (open(soundtrack))
            return true;

        _session.UseSoundtrack(false);
        return false;
    }

    /// <summary>Whether the soundtrack engine is there (Full and Audio mode, with BASS): network audio through FFmpeg can play.</summary>
    public bool CanOpenSoundtrack => _session.Soundtrack is not null;

    public bool OpenNetworkAudio(Uri uri) => _session.Audio?.OpenUrl(uri.AbsoluteUri) == true;

    /// <summary>Gets the network capabilities of the open audio source, or <see langword="null"/>.</summary>
    public MediaSourceCapabilities? NetworkAudioCapabilities =>
        _session.Audio?.IsOpen == true && _session.Audio.Engine is INetworkAudioSource network
            ? network.NetworkCapabilities
            : null;

    /// <summary>
    /// Makes one attempt to open a network video (Stage 039A). No embedded-subtitle scan: it
    /// would read the whole stream a second time.
    /// </summary>
    public bool OpenNetworkVideo(Uri uri, FfmpegNetworkOptions options)
    {
        CancelEmbeddedSubtitleScan();
        _session.Subtitle?.Close();
        return _session.Video is INetworkVideoEngine video && video.OpenNetwork(uri.AbsoluteUri, options);
    }

    /// <summary>
    /// Makes one attempt to open a network video's soundtrack (Audio mode, Stage 039A) or, with
    /// <paramref name="audioSource"/>, network audio through FFmpeg (ENG-034).
    /// </summary>
    public bool OpenNetworkSoundtrack(Uri uri, FfmpegNetworkOptions options, bool audioSource = false) =>
        OpenSoundtrack(pipeline => pipeline.OpenNetworkSoundtrack(uri.AbsoluteUri, options, audioSource));

    /// <summary>Gets the capability row of the open network video or soundtrack, or <see langword="null"/>.</summary>
    public MediaSourceCapabilities? NetworkVideoCapabilities =>
        _session.Video is INetworkVideoEngine { NetworkCapabilities: { } video } && _session.Video.IsOpen
            ? video
            : _session.Audio?.IsOpen == true && _session.Audio.Engine is SoundtrackAudioEngine soundtrack
                ? soundtrack.NetworkCapabilities
                : null;

    /// <summary>
    /// Gets FFmpeg's reason for the last failed network open of the soundtrack engine
    /// (<paramref name="soundtrack"/>) or of the video pipeline.
    /// </summary>
    public string? GetLastNetworkError(bool soundtrack) => soundtrack
        ? (_session.Soundtrack?.Engine as SoundtrackAudioEngine)?.LastNetworkError
        : (_session.Video as INetworkVideoEngine)?.LastNetworkError;

    public bool OpenAudioCdg(string audioPath, string cdgPath)
    {
        if (_session.Audio?.Open(audioPath, ownState: false, analyzeSilence: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgPath);
        _session.State.SetOpened(MediaContent.Audio | MediaContent.Cdg, audioPath, DurationSeconds);
        return true;
    }

    public bool OpenAudioCdg(IArchiveEntryInfo audioEntry, IArchiveEntryInfo cdgEntry)
    {
        if (_session.Audio?.Open(audioEntry, ownState: false) != true || _session.Cdg == null)
            return false;

        _session.Cdg.OpenPaired(cdgEntry);
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

    public bool OpenVideo(string videoPath) => OpenVideo(videoPath, subtitlePath: null);

    /// <summary>
    /// Opens a local video and its sidecar subtitle file, if any. The embedded text subtitle tracks
    /// arrive later from a background scan (ENG-039 P5-1), raising <see cref="SubtitleTracksChanged"/>.
    /// </summary>
    public bool OpenVideo(string videoPath, string? subtitlePath)
    {
        CancelEmbeddedSubtitleScan();
        _session.Subtitle?.Close();
        if (_session.Video?.Open(videoPath) != true)
            return false;

        if (_session.Subtitle == null)
            return true;

        // Embedded subtitles take precedence when they arrive; the sidecar remains selectable. A
        // malformed or unreadable sidecar must not prevent the independently valid video from
        // opening. The scan starts after the sidecar is attached, so the two never race.
        if (subtitlePath != null)
            _ = _session.Subtitle.Open(subtitlePath);
        StartEmbeddedSubtitleScan(videoPath);
        return true;
    }

    public bool OpenVideo(IArchiveEntryInfo videoEntry)
    {
        CancelEmbeddedSubtitleScan();
        return _session.Video?.Open(videoEntry) == true;
    }

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

    public double SetAudioVideoOffset(double seconds) => _session.Sync.SetAudioVideoOffset(seconds);
    public double SetCdgOffset(double seconds) => _session.Sync.SetCdgOffset(seconds);
    public double SetSubtitleOffset(double seconds) => _session.Sync.SetSubtitleOffset(seconds);
    public double AdjustAudioVideoOffset(double seconds) => _session.Sync.AdjustAudioVideoOffset(seconds);
    public double AdjustCdgOffset(double seconds) => _session.Sync.AdjustCdgOffset(seconds);
    public double AdjustSubtitleOffset(double seconds) => _session.Sync.AdjustSubtitleOffset(seconds);
    public void ResetSyncOffsets() => _session.Sync.Reset();

    public void Close()
    {
        CancelEmbeddedSubtitleScan();
        _session.Clear();
    }

    public void Dispose()
    {
        CancelEmbeddedSubtitleScan();
        _session.FileAudio?.PlaybackEnded -= OnPlaybackEnded;
        _session.Soundtrack?.PlaybackEnded -= OnPlaybackEnded;
        _session.Cdg?.PlaybackEnded -= OnPlaybackEnded;
        _session.Video?.PlaybackEnded -= OnPlaybackEnded;
        if (_session.Video is IMediaLoopEventSource loopEventSource)
            loopEventSource.MediaLooped -= OnPlaybackLooped;
        if (_session.Video is INetworkVideoEngine networkVideo)
            networkVideo.NetworkBufferingChanged -= OnNetworkBufferingChanged;
        if (_session.Soundtrack?.Engine is SoundtrackAudioEngine soundtrackEngine)
        {
            soundtrackEngine.NetworkBufferingChanged -= OnNetworkBufferingChanged;
            soundtrackEngine.StreamMetadataChanged -= OnNetworkStreamMetadataChanged;
        }

        _session.Dispose();
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) => PlaybackEnded?.Invoke(this, EventArgs.Empty);

    private void OnPlaybackLooped(object? sender, EventArgs e) => PlaybackLooped?.Invoke(this, EventArgs.Empty);

    private void OnNetworkBufferingChanged(object? sender, MediaBufferingChangedEventArgs e) => NetworkBufferingChanged?.Invoke(this, e);

    private void OnNetworkStreamMetadataChanged(object? sender, MediaStreamMetadataChangedEventArgs e) => NetworkStreamMetadataChanged?.Invoke(this, e);

    private bool TryLoadMidiLyrics(string audioPath, IArchiveEntryInfo? archiveEntry = null)
    {
        if (_session.Subtitle == null || !_midiLyricProvider.IsMidiFile(audioPath))
            return true;

        try
        {
            // 037A slice 1: the built-in provider reads MIDI through an engine-neutral reader; a
            // host-supplied provider still gets the BASS source handle.
            IAudioEngine? engine = _session.Audio?.Engine;
            IReadOnlyList<SubtitleCue>? cues = _midiLyricProvider is IMidiLyricReaderProvider readerProvider
                ? readerProvider.ExtractLyrics(
                    GetMidiEventReader(engine),
                    audioPath,
                    _karaokeTransitionDuration)
                : _midiLyricProvider.ExtractLyrics(
                    engine?.SourceHandle ?? 0,
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

    /// <summary>
    /// The MIDI events of the open audio: from the engine when it reads them itself (the BASS
    /// engines, on their source channel as before); for any other engine, its BASS source handle
    /// as before this seam (null for 0). A FatzAudio engine will implement the reader itself.
    /// </summary>
    internal static IMidiEventReader? GetMidiEventReader(IAudioEngine? engine) => engine switch
    {
        null => null,
        IMidiEventReaderSource source => source.GetMidiEventReader(),
        _ => BassMidiEventReader.ForHandle(engine.SourceHandle),
    };
}
