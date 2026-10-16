using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Video;

namespace FatzBomb.MediaControl.Tests;

internal sealed class TestMediaEngine : IMediaEngine, ILoopPlaybackControl, IMediaLoopEventSource
{
    private double _position;

    public TestAudioEngine TestAudio { get; } = new();
    public TestVideoEngine? TestVideo { get; init; }
    public TestCdgEngine? TestCdg { get; init; }
    public IVideoEngine? Video => TestVideo;
    public ICdgEngine? Cdg => TestCdg;
    public ISubtitleEngine? Subtitle => null;
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => [];
    public string? SelectedSubtitleTrackId => null;
    public bool ShowMidiLyrics { get; set; }
    public double AudioVideoOffsetSeconds { get; private set; }
    public double CdgOffsetSeconds { get; private set; }
    public double SubtitleOffsetSeconds { get; private set; }
    public IAudioEngine Audio => TestAudio;
    public string? ImagePath => null;
    public bool BackgroundAutoDetect { get; set; }
    public MediaBackgroundCandidate? Background { get; private set; }
    public double Position
    {
        get => TestVideo is { IsOpen: true } ? TestVideo.Position : _position;
        set
        {
            _position = value;
            if (TestVideo is { IsOpen: true })
                TestVideo.Position = value;
        }
    }
    public double Duration { get; set; } = 120;
    public MediaPlayerState State { get; private set; } = MediaPlayerState.Null;
    public bool IsOpen { get; private set; }
    public MediaContent Content { get; set; } = MediaContent.Audio;
    public bool LoopPlayback { get; set; }
    public bool HandlesLoopInternally { get; set; }
    public bool OpenSucceeds { get; set; } = true;
    public Action? BeforeOpen { get; set; }
    public Action? BeforeStop { get; set; }
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }

    public event EventHandler? MediaEnded;
    public event EventHandler? MediaLooped;
    public event EventHandler<VideoFrameEventArgs>? ImageFrameReady;
    public event EventHandler<VideoFrameEventArgs>? BackgroundFrameReady;
    public event EventHandler? BackgroundCleared { add { } remove { } }

    public void SetDisplayRefreshRate(double? refreshHz)
    {
    }

    public bool SetBackground(MediaBackgroundCandidate? background)
    {
        Background = background;
        return true;
    }

    public void ClearBackground() => Background = null;

    public bool Open(string path) => Open(MediaInputSource.FromFile(path));

    public bool Open(MediaInputSource source)
    {
        BeforeOpen?.Invoke();
        if (!OpenSucceeds)
            return false;

        IsOpen = true;
        State = MediaPlayerState.Stopped;
        Position = 0;
        return true;
    }

    public bool Play()
    {
        State = MediaPlayerState.Playing;
        if (!IsOpen)
            return false;

        PlayCount++;
        return true;
    }

    public bool Pause()
    {
        State = MediaPlayerState.Paused;
        return IsOpen;
    }

    public bool Stop()
    {
        BeforeStop?.Invoke();
        State = MediaPlayerState.Stopped;
        Position = 0;
        if (!IsOpen)
            return false;

        StopCount++;
        return true;
    }

    public void Close()
    {
        IsOpen = false;
        State = MediaPlayerState.Null;
        Position = 0;
    }

    public bool Seek(double seconds)
    {
        if (TestVideo is { IsOpen: true } video)
            return video.Seek(seconds);

        Position = seconds;
        return IsOpen;
    }

    public bool SelectSubtitleTrack(string? trackId) => false;

    public double SetAudioVideoOffset(double seconds) => AudioVideoOffsetSeconds = seconds;
    public double SetCdgOffset(double seconds) => CdgOffsetSeconds = seconds;
    public double SetSubtitleOffset(double seconds) => SubtitleOffsetSeconds = seconds;
    public double AdjustAudioVideoOffset(double seconds) => AudioVideoOffsetSeconds += seconds;
    public double AdjustCdgOffset(double seconds) => CdgOffsetSeconds += seconds;
    public double AdjustSubtitleOffset(double seconds) => SubtitleOffsetSeconds += seconds;

    public void ResetSyncOffsets()
    {
        AudioVideoOffsetSeconds = 0;
        CdgOffsetSeconds = 0;
        SubtitleOffsetSeconds = 0;
    }

    public Task<bool> SeekAsync(double seconds, TimeSpan? timeout = null) =>
        Task.FromResult(Seek(seconds));

    public bool TryGetCurrentImageFrame(out VideoFrameEventArgs? frame)
    {
        frame = null;
        return false;
    }

    public void RaiseMediaEnded() => MediaEnded?.Invoke(this, EventArgs.Empty);
    public void RaiseBackgroundFrame(VideoFrameEventArgs frame) => BackgroundFrameReady?.Invoke(this, frame);
    public void RaiseImageFrame(VideoFrameEventArgs frame) => ImageFrameReady?.Invoke(this, frame);
    public void CompletePlayback()
    {
        State = MediaPlayerState.Stopped;
        Position = Duration;
        RaiseMediaEnded();
    }

    public void RaiseMediaLooped() => MediaLooped?.Invoke(this, EventArgs.Empty);
    public void Dispose()
    {
        DisposeCount++;
        Close();
    }
}

internal sealed class TestVideoEngine : IVideoEngine
{
    public bool IsOpen { get; set; } = true;
    public bool SeekUpdatesPosition { get; set; }
    public double Duration { get; set; } = 120;
    public double Position { get; set; }
    public double FramesPerSecond => 30;
    public IAudioEngine? Audio => null;
    public bool HasAudio => false;
    public bool LoopPlayback { get; set; }
    public int Width => 1920;
    public int Height => 1080;
    public string? CodecName => "test";
    public bool IsHardwareAccelerated => false;
    public string? HardwareAccelerationMethod => null;

    public event EventHandler<VideoFrameEventArgs>? FrameReady { add { } remove { } }
    public event EventHandler<VideoEngineState>? StateChanged { add { } remove { } }
    public event EventHandler? PlaybackEnded { add { } remove { } }
    public event EventHandler? Opened { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    public event EventHandler<double>? PositionChanged;

    public bool Open(string path)
    {
        IsOpen = true;
        return true;
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        IsOpen = true;
        return true;
    }

    public void Close() => IsOpen = false;
    public bool Play() => IsOpen;
    public bool Pause() => IsOpen;
    public bool Stop() => IsOpen;

    public bool Seek(double seconds)
    {
        if (!IsOpen)
            return false;

        if (SeekUpdatesPosition)
            RaisePositionChanged(seconds);
        return true;
    }

    public void SetDisplayRefreshRate(double? refreshHz) { }
    public void SetHostRenderEventDrivenPresentation(bool enabled) { }
    public void RenderCurrentFrame(bool allowWhilePlaying = false, bool forceReissue = false) { }

    public void RaisePositionChanged(double position)
    {
        Position = position;
        PositionChanged?.Invoke(this, position);
    }

    public void Dispose() => Close();
}

internal sealed class TestCdgEngine : ICdgEngine
{
    public int FrameWidth => 300;
    public int FrameHeight => 216;
    public uint BackgroundWashArgb => 0;
    public double TotalDurationSeconds => 120;
    public byte BackgroundOpacity { get; set; }
    public uint? BackgroundOverrideArgb { get; set; }

    public event EventHandler? FrameInvalidated;
    public event EventHandler? BackgroundWashChanged { add { } remove { } }
    public event EventHandler<CdgEngineState>? StateChanged { add { } remove { } }
    public event EventHandler? Opened { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    public event EventHandler? PlaybackStarted { add { } remove { } }

    public void LoadPaired(string filePath, int audioStreamHandle) { }
    public void LoadPaired(IArchiveEntryInfo archiveEntry, int audioStreamHandle) { }
    public void Load(string filePath) { }
    public void Load(IArchiveEntryInfo archiveEntry) { }
    public void Play() { }
    public void Pause() { }
    public void Stop() { }
    public void Reset() { }
    public void RenderAtSeconds(double seconds) { }
    public void RenderAtSeconds(double seconds, bool forceRebuild) { }
    public void RaiseFrameInvalidated() => FrameInvalidated?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }
}

internal sealed class TestAudioEngine : IAudioEngine, IAudioAnalysisOpenControl, IAudioLoopControl, IAudioTrimWindowProvider, FatzBomb.MediaControl.Core.Audio.Fade.IAudioFadeGainTarget, INetworkAudioSource, IAudioFilterHost
{
    public AudioEqualizerSettings Equalizer { get; private set; } = AudioEqualizerSettings.Disabled;
    public int EqualizerApplyCount { get; private set; }

    public bool SetEqualizer(AudioEqualizerSettings settings)
    {
        Equalizer = settings;
        EqualizerApplyCount++;
        return true;
    }

    public bool NetworkOpenSucceeds { get; set; } = true;
    public string? LastNetworkUrl { get; private set; }
    public MediaSourceCapabilities? NetworkCapabilities { get; set; }
    public event EventHandler<MediaBufferingChangedEventArgs>? BufferingChanged;
    public event EventHandler<MediaStreamMetadataChangedEventArgs>? StreamMetadataChanged;

    public bool OpenUrl(string url)
    {
        LastNetworkUrl = url;
        if (!NetworkOpenSucceeds)
            return false;

        SetState(AudioEngineState.Stopped);
        return true;
    }

    public void RaiseBuffering(bool isBuffering, double fill) =>
        BufferingChanged?.Invoke(this, new MediaBufferingChangedEventArgs(isBuffering, fill));

    public void RaiseStreamMetadata(string? title, string? artist) =>
        StreamMetadataChanged?.Invoke(this, new MediaStreamMetadataChangedEventArgs(title, artist, $"{artist} - {title}"));

    private int _fadeSession;

    public bool FadeAvailable { get; set; } = true;
    public System.Collections.Concurrent.ConcurrentQueue<(double Gain, TimeSpan Ramp)> FadeCalls { get; } = new();

    public int BeginFadeSession() => FadeAvailable ? Interlocked.Increment(ref _fadeSession) : 0;

    public bool SetFadeGain(int session, double gain, TimeSpan ramp)
    {
        if (session != Volatile.Read(ref _fadeSession))
            return false;

        FadeCalls.Enqueue((gain, ramp));
        return true;
    }

    public AudioEngineState State { get; private set; }
    public int PlayHandle => 0;
    public int SourceHandle => 0;
    public bool Normalize { get; set; } = true;
    public double NormalizationGain { get; set; } = 1;
    public bool IsNormalizationReady => true;
    public double Duration { get; set; }
    public double Position { get; set; }
    public int CurrentTempo { get; private set; }
    public double Volume { get; private set; } = 1;
    public int Pitch { get; private set; }
    public bool? LastAnalyzeSilence { get; private set; }
    public bool LoopPlayback { get; set; }
    public MediaAnalysisResult? ActiveTrimWindow { get; set; }

    public event EventHandler<double>? NormalizationGainChanged { add { } remove { } }
    public event EventHandler<AudioEngineState>? StateChanged;
    public event EventHandler? Opened { add { } remove { } }
    public event EventHandler? Closed { add { } remove { } }
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;

    public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => Task.FromResult(1d);
    public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => Task.FromResult(1d);
    public bool Init(int device = -1, int sampleRate = 44100) => true;
    public bool Open(string filePath) => true;
    bool IAudioAnalysisOpenControl.Open(string filePath, bool analyzeSilence)
    {
        LastAnalyzeSilence = analyzeSilence;
        return Open(filePath);
    }
    public bool Open(IArchiveEntryInfo archiveEntry) => true;
    public bool Play()
    {
        if (ActiveTrimWindow is { } trimWindow)
        {
            if (trimWindow.EffectiveDuration <= 0)
            {
                return false;
            }

            if (Position < trimWindow.PlayableStart || Position >= trimWindow.PlayableEnd)
            {
                Position = trimWindow.PlayableStart;
            }
        }

        SetState(AudioEngineState.Playing);
        return true;
    }

    public bool Pause() { SetState(AudioEngineState.Paused); return true; }
    public bool Stop() { SetState(AudioEngineState.Stopped); Position = 0; return true; }
    public void Close() => State = AudioEngineState.Null;
    public void SetVolume(double volume01) => Volume = volume01;
    public void SetPitch(int semitones) => Pitch = semitones;
    public void SetTempo(int percent) => CurrentTempo = percent;
    public void RaisePositionChanged(double sourcePosition)
    {
        Position = sourcePosition;
        PositionChanged?.Invoke(this, sourcePosition);
    }

    public void RaisePlaybackEnded()
    {
        if (ActiveTrimWindow is { } trimWindow)
        {
            Position = LoopPlayback ? trimWindow.PlayableStart : trimWindow.PlayableEnd;
        }
        else
        {
            Position = LoopPlayback ? 0 : Duration;
        }

        SetState(LoopPlayback ? AudioEngineState.Playing : AudioEngineState.Stopped);
        PositionChanged?.Invoke(this, Position);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private void SetState(AudioEngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose() => Close();
}
