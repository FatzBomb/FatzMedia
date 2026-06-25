namespace FatzBombMedia.MediaControl.Pipeline;

using FatzBombMedia.MediaControl.Core.Abstractions;

internal sealed class AudioPipeline : IDisposable
{
    private readonly IAudioEngine _audioEngine;
    private readonly bool _ownsAudioEngine;
    private string? _sourcePath;
    private string? _pendingOpenPath;
    private bool _isOpen;
    private bool _disposed;

    public AudioPipeline(IAudioEngine audioEngine, MediaState? state = null, bool ownsAudioEngine = false)
    {
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        State = state ?? new MediaState();
        _ownsAudioEngine = ownsAudioEngine;

        _audioEngine.StateChanged += OnAudioStateChanged;
        _audioEngine.Opened += OnAudioOpened;
        _audioEngine.Closed += OnAudioClosed;
        _audioEngine.PositionChanged += OnAudioPositionChanged;
        _audioEngine.PlaybackEnded += OnAudioPlaybackEnded;
    }

    public MediaState State { get; }
    public IAudioEngine Engine => _audioEngine;
    public event EventHandler? PlaybackEnded;

    public bool IsOpen => _isOpen;
    public int PlayHandle => _audioEngine.PlayHandle;
    public double DurationSeconds => _audioEngine.Duration;

    public double PositionSeconds
    {
        get => _audioEngine.Position;
        set
        {
            ThrowIfDisposed();
            _audioEngine.Position = value;
            State.SetPosition(_audioEngine.Position);
        }
    }

    public bool Open(string path)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(path))
        {
            State.SetError("Audio path was empty.");
            return false;
        }

        Close();

        _pendingOpenPath = path;
        if (!_audioEngine.Open(path))
        {
            _pendingOpenPath = null;
            State.Reset();
            State.SetError($"Audio open failed: {path}");
            return false;
        }

        _sourcePath = path;
        _pendingOpenPath = null;
        _isOpen = true;
        State.SetOpened(MediaContent.Audio, path, _audioEngine.Duration);
        return true;
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(archiveEntry);

        Close();

        if (!_audioEngine.Open(archiveEntry))
        {
            State.Reset();
            State.SetError($"Archive audio open failed: {archiveEntry.Key}");
            return false;
        }

        _sourcePath = archiveEntry.Key;
        _isOpen = true;
        State.SetOpened(MediaContent.Audio, archiveEntry.Key, _audioEngine.Duration);
        return true;
    }

    public bool Play()
    {
        ThrowIfDisposed();

        if (!_audioEngine.Play())
        {
            return false;
        }

        State.SetPlaybackState(MediaPlayerState.Playing);
        return true;
    }

    public bool Pause()
    {
        ThrowIfDisposed();

        if (!_audioEngine.Pause())
        {
            return false;
        }

        State.SetPlaybackState(MediaPlayerState.Paused);
        return true;
    }

    public bool Stop()
    {
        ThrowIfDisposed();

        if (!_audioEngine.Stop())
        {
            return false;
        }

        State.SetPosition(_audioEngine.Position);
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
            : Math.Max(0, positionSeconds);

        _audioEngine.Position = targetPosition;
        State.SetPosition(_audioEngine.Position);
        return true;
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _audioEngine.Close();
        _sourcePath = null;
        _pendingOpenPath = null;
        _isOpen = false;
        State.Reset();
    }

    public bool SetVolume(double volume01)
    {
        ThrowIfDisposed();
        return _audioEngine.SetVolume(Math.Clamp(volume01, 0, 1));
    }

    public bool SetPitch(int semitones)
    {
        ThrowIfDisposed();
        return _audioEngine.SetPitch(semitones);
    }

    public bool SetTempo(int percent)
    {
        ThrowIfDisposed();
        return _audioEngine.SetTempo(percent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _audioEngine.StateChanged -= OnAudioStateChanged;
        _audioEngine.Opened -= OnAudioOpened;
        _audioEngine.Closed -= OnAudioClosed;
        _audioEngine.PositionChanged -= OnAudioPositionChanged;
        _audioEngine.PlaybackEnded -= OnAudioPlaybackEnded;

        _audioEngine.Close();
        if (_ownsAudioEngine)
        {
            _audioEngine.Dispose();
        }

        _disposed = true;
    }

    private void OnAudioStateChanged(object? sender, AudioEngineState state)
    {
        State.SetPlaybackState(MapState(state));
    }

    private void OnAudioOpened(object? sender, EventArgs e)
    {
        _sourcePath = _pendingOpenPath ?? _sourcePath ?? State.SourcePath;
        _isOpen = true;
        State.SetOpened(MediaContent.Audio, _sourcePath, _audioEngine.Duration);
    }

    private void OnAudioClosed(object? sender, EventArgs e)
    {
        _sourcePath = null;
        _isOpen = false;
        State.Reset();
    }

    private void OnAudioPositionChanged(object? sender, double positionSeconds)
    {
        State.SetPosition(positionSeconds);
    }

    private void OnAudioPlaybackEnded(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            return;
        }

        State.SetPosition(DurationSeconds);
        State.SetPlaybackState(MediaPlayerState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private static MediaPlayerState MapState(AudioEngineState state)
    {
        return state switch
        {
            AudioEngineState.Playing => MediaPlayerState.Playing,
            AudioEngineState.Paused => MediaPlayerState.Paused,
            AudioEngineState.Stopped => MediaPlayerState.Stopped,
            AudioEngineState.Stalled => MediaPlayerState.Buffering,
            _ => MediaPlayerState.Null
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
