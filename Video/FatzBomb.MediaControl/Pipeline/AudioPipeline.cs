
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Media;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class AudioPipeline : IDisposable
{
    private readonly bool _ownsAudioEngine;
    private string? _sourcePath;
    private string? _pendingOpenPath;
    private bool _disposed;
    private bool _ownStateForCurrentOpen = true;
    private bool _analyzeSilenceForCurrentOpen;
    private MediaAnalysisResult? _trimWindow;

    public AudioPipeline(IAudioEngine audioEngine, MediaState? state = null, bool ownsAudioEngine = false)
    {
        Engine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        State = state ?? new MediaState();
        _ownsAudioEngine = ownsAudioEngine;

        Engine.StateChanged += OnAudioStateChanged;
        Engine.Opened += OnAudioOpened;
        Engine.Closed += OnAudioClosed;
        Engine.PositionChanged += OnAudioPositionChanged;
        Engine.PlaybackEnded += OnAudioPlaybackEnded;
    }

    public MediaState State { get; }
    public IAudioEngine Engine { get; }
    public event EventHandler? PlaybackEnded;

    public bool IsOpen { get; private set; }
    public int PlayHandle => Engine.PlayHandle;
    public double DurationSeconds => _trimWindow?.EffectiveDuration ?? Engine.Duration;

    public double PositionSeconds
    {
        get => SourceToEffective(Engine.Position);
        set
        {
            ThrowIfDisposed();
            Engine.Position = EffectiveToSource(value);
            State.SetPosition(SourceToEffective(Engine.Position));
        }
    }

    public bool Open(string path) => Open(path, ownState: true, analyzeSilence: true);

    internal bool Open(string path, bool ownState, bool analyzeSilence = true)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Close();

        _pendingOpenPath = path;
        _ownStateForCurrentOpen = ownState;
        _analyzeSilenceForCurrentOpen = analyzeSilence;
        var opened = Engine is IAudioAnalysisOpenControl analysisControl
            ? analysisControl.Open(path, analyzeSilence)
            : Engine.Open(path);
        if (!opened)
        {
            _pendingOpenPath = null;
            ResetTrimWindow();
            State.Reset();
            return false;
        }

        _sourcePath = path;
        _pendingOpenPath = null;
        if (!IsOpen)
        {
            IsOpen = true;
            RefreshTrimWindow();
            if (ownState)
            {
                State.SetOpened(MediaContent.Audio, path, DurationSeconds);
            }
        }
        return true;
    }

    /// <summary>
    /// Opens a direct HTTP/HTTPS audio source. Network sources never apply silence trim, and live
    /// sources report zero duration.
    /// </summary>
    internal bool OpenUrl(string url) =>
        Engine is INetworkAudioSource network && OpenNetwork(url, () => network.OpenUrl(url));

    /// <summary>
    /// Opens the soundtrack of a network video (Stage 039A, Audio mode): one attempt, as
    /// <see cref="OpenUrl"/> does for network audio.
    /// </summary>
    internal bool OpenNetworkSoundtrack(string url, FfmpegNetworkOptions options) =>
        Engine is SoundtrackAudioEngine soundtrack && OpenNetwork(url, () => soundtrack.OpenNetwork(url, options));

    private bool OpenNetwork(string url, Func<bool> open)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(url))
            return false;

        Close();

        _ownStateForCurrentOpen = true;
        _analyzeSilenceForCurrentOpen = false;
        if (!open())
        {
            State.Reset();
            return false;
        }

        _sourcePath = url;
        if (!IsOpen)
        {
            IsOpen = true;
            ResetTrimWindow();
            State.SetOpened(MediaContent.Audio, url, Engine.Duration);
        }
        return true;
    }

    public bool Open(IArchiveEntryInfo archiveEntry) => Open(archiveEntry, ownState: true);

    internal bool Open(IArchiveEntryInfo archiveEntry, bool ownState)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(archiveEntry);

        Close();

        _ownStateForCurrentOpen = ownState;
        _analyzeSilenceForCurrentOpen = false;
        if (!Engine.Open(archiveEntry))
        {
            State.Reset();
            return false;
        }

        _sourcePath = archiveEntry.Key;
        if (!IsOpen)
        {
            IsOpen = true;
            ResetTrimWindow();
            if (ownState)
            {
                State.SetOpened(MediaContent.Audio, archiveEntry.Key, Engine.Duration);
            }
        }
        return true;
    }

    public bool Play()
    {
        ThrowIfDisposed();

        if (_trimWindow is { EffectiveDuration: <= 0 })
        {
            return false;
        }

        if (!Engine.Play())
        {
            return false;
        }

        State.SetPlaybackState(MediaPlayerState.Playing);
        return true;
    }

    public bool Pause()
    {
        ThrowIfDisposed();

        if (!Engine.Pause())
        {
            return false;
        }

        State.SetPlaybackState(MediaPlayerState.Paused);
        return true;
    }

    public bool Stop()
    {
        ThrowIfDisposed();

        if (!Engine.Stop())
        {
            return false;
        }

        State.SetPosition(SourceToEffective(Engine.Position));
        State.SetPlaybackState(MediaPlayerState.Stopped);
        return true;
    }

    public bool Seek(double positionSeconds)
    {
        ThrowIfDisposed();

        if (!IsOpen)
        {
            return false;
        }

        var targetPosition = DurationSeconds > 0
            ? Math.Clamp(positionSeconds, 0, DurationSeconds)
            : _trimWindow is null ? Math.Max(0, positionSeconds) : 0;

        Engine.Position = EffectiveToSource(targetPosition);
        State.SetPosition(SourceToEffective(Engine.Position));
        return true;
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        Engine.Close();
        ResetTrimWindow();
        _sourcePath = null;
        _pendingOpenPath = null;
        IsOpen = false;
        State.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Engine.StateChanged -= OnAudioStateChanged;
        Engine.Opened -= OnAudioOpened;
        Engine.Closed -= OnAudioClosed;
        Engine.PositionChanged -= OnAudioPositionChanged;
        Engine.PlaybackEnded -= OnAudioPlaybackEnded;

        Engine.Close();
        if (_ownsAudioEngine)
        {
            Engine.Dispose();
        }

        _disposed = true;
    }

    private void OnAudioStateChanged(object? sender, AudioEngineState state) => State.SetPlaybackState(MapState(state));

    private void OnAudioOpened(object? sender, EventArgs e)
    {
        _sourcePath = _pendingOpenPath ?? _sourcePath ?? State.SourcePath;
        IsOpen = true;
        if (_ownStateForCurrentOpen)
        {
            RefreshTrimWindow();
            State.SetOpened(MediaContent.Audio, _sourcePath, DurationSeconds);
        }
    }

    private void OnAudioClosed(object? sender, EventArgs e)
    {
        _sourcePath = null;
        IsOpen = false;
        ResetTrimWindow();
        State.Reset();
    }

    private void OnAudioPositionChanged(object? sender, double positionSeconds) =>
        State.SetPosition(SourceToEffective(positionSeconds));

    private void OnAudioPlaybackEnded(object? sender, EventArgs e)
    {
        if (!IsOpen)
            return;

        if (Engine is IAudioLoopControl { LoopPlayback: true })
        {
            State.SetPosition(SourceToEffective(Engine.Position));
            State.SetPlaybackState(MediaPlayerState.Playing);
        }
        else
        {
            State.SetPosition(DurationSeconds);
            State.SetPlaybackState(MediaPlayerState.Stopped);
        }

        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private static MediaPlayerState MapState(AudioEngineState state) => state switch
    {
        AudioEngineState.Playing => MediaPlayerState.Playing,
        AudioEngineState.Paused => MediaPlayerState.Paused,
        AudioEngineState.Stopped => MediaPlayerState.Stopped,
        AudioEngineState.Stalled => MediaPlayerState.Buffering,
        _ => MediaPlayerState.Null
    };

    private void RefreshTrimWindow()
    {
        _trimWindow = _analyzeSilenceForCurrentOpen
            && Engine is IAudioTrimWindowProvider { ActiveTrimWindow: { } activeWindow }
            && MediaAnalysisResultValidator.IsValid(activeWindow)
                ? activeWindow
                : null;
    }

    private void ResetTrimWindow()
    {
        _trimWindow = null;
        _analyzeSilenceForCurrentOpen = false;
    }

    private double SourceToEffective(double sourcePosition)
    {
        if (_trimWindow is not { } trimWindow)
        {
            return sourcePosition;
        }

        return Math.Clamp(
            sourcePosition - trimWindow.PlayableStart,
            0,
            trimWindow.EffectiveDuration);
    }

    private double EffectiveToSource(double effectivePosition)
    {
        if (_trimWindow is not { } trimWindow)
        {
            return effectivePosition;
        }

        return Math.Clamp(
            trimWindow.PlayableStart + effectivePosition,
            trimWindow.PlayableStart,
            trimWindow.PlayableEnd);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
