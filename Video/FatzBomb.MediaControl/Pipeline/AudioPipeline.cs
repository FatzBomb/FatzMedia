
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class AudioPipeline : IDisposable
{
    private readonly bool _ownsAudioEngine;
    private string? _sourcePath;
    private string? _pendingOpenPath;
    private bool _disposed;
    private bool _ownStateForCurrentOpen = true;

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
    public double DurationSeconds => Engine.Duration;

    public double PositionSeconds
    {
        get => Engine.Position;
        set
        {
            ThrowIfDisposed();
            Engine.Position = value;
            State.SetPosition(Engine.Position);
        }
    }

    public bool Open(string path) => Open(path, ownState: true);

    internal bool Open(string path, bool ownState)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Close();

        _pendingOpenPath = path;
        _ownStateForCurrentOpen = ownState;
        if (!Engine.Open(path))
        {
            _pendingOpenPath = null;
            State.Reset();
            return false;
        }

        _sourcePath = path;
        _pendingOpenPath = null;
        IsOpen = true;
        if (ownState)
        {
            State.SetOpened(MediaContent.Audio, path, Engine.Duration);
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
        if (!Engine.Open(archiveEntry))
        {
            State.Reset();
            return false;
        }

        _sourcePath = archiveEntry.Key;
        IsOpen = true;
        if (ownState)
        {
            State.SetOpened(MediaContent.Audio, archiveEntry.Key, Engine.Duration);
        }
        return true;
    }

    public bool Play()
    {
        ThrowIfDisposed();

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

        State.SetPosition(Engine.Position);
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

        Engine.Position = targetPosition;
        State.SetPosition(Engine.Position);
        return true;
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        Engine.Close();
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
            State.SetOpened(MediaContent.Audio, _sourcePath, Engine.Duration);
        }
    }

    private void OnAudioClosed(object? sender, EventArgs e)
    {
        _sourcePath = null;
        IsOpen = false;
        State.Reset();
    }

    private void OnAudioPositionChanged(object? sender, double positionSeconds) => State.SetPosition(positionSeconds);

    private void OnAudioPlaybackEnded(object? sender, EventArgs e)
    {
        if (!IsOpen)
            return;

        State.SetPosition(DurationSeconds);
        State.SetPlaybackState(MediaPlayerState.Stopped);
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
