using FatzBombMedia.MediaControl.Core.Abstractions;


namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Controls media playback operations.
/// </summary>
public sealed class MediaControls
{
    private readonly IMediaEngine _mediaEngine;
    private readonly IMediaRenderCoordinator? _mediaRenderer;
    private bool _handlingMediaEnd;
    private bool _disposed;

    internal MediaControls(IMediaEngine mediaEngine, IMediaRenderCoordinator? mediaRenderer = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _mediaRenderer = mediaRenderer;
        _mediaEngine.MediaEnded += OnMediaEnded;
    }

    /// <summary>
    /// Gets or sets whether media should loop continuously.
    /// </summary>
    public bool LoopMedia { get; set; } = false;


    /// <summary>
    /// Event raised when media reaches the end.
    /// </summary>
    public event EventHandler? MediaEnded;

    /// <summary>
    /// Event raised when media playback stops.
    /// </summary>
    public event EventHandler? MediaStopped;

    /// <summary>
    /// Starts media playback.
    /// </summary>
    /// <returns>True if playback started successfully.</returns>
    public bool Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var success = _mediaEngine.Play();
        if (success)
        {
            _handlingMediaEnd = false;

            // Start rendering for video/CDG sync
            StartRendering();

            System.Diagnostics.Debug.WriteLine("[MediaControls] Playback started");
        }
        return success;
    }

    /// <summary>
    /// Pauses media playback.
    /// </summary>
    /// <returns>True if playback was paused successfully.</returns>
    public bool Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var success = _mediaEngine.Pause();
        if (success)
        {
            _handlingMediaEnd = false;

            // Stop rendering when paused
            StopRendering();
        }
        return success;
    }

    /// <summary>
    /// Stops media playback and resets position to the beginning.
    /// </summary>
    /// <returns>True if playback was stopped successfully.</returns>
    public bool Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var success = _mediaEngine.Stop();
        if (success)
        {
            _handlingMediaEnd = false;

            // Stop rendering when stopped
            StopRendering();

            // Fire stopped event
            MediaStopped?.Invoke(this, EventArgs.Empty);
        }
        return success;
    }

    /// <summary>
    /// Seeks to the specified playback position.
    /// </summary>
    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    /// <summary>
    /// Seeks to the specified playback position in seconds.
    /// </summary>
    public bool Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mediaEngine.Seek(seconds);
    }

    /// <summary>
    /// Gets or sets the current playback position.
    /// </summary>
    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_mediaEngine.Position);
        set => Seek(value);
    }

    /// <summary>
    /// Gets the total loaded media duration.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_mediaRenderer is not null)
        {
            _mediaRenderer.RunOnRenderThread(HandleMediaEnded);
            return;
        }

        HandleMediaEnded();
    }

    private void HandleMediaEnded()
    {
        try
        {
            if (_disposed || _handlingMediaEnd || !_mediaEngine.IsOpen) return;

            var position = _mediaEngine.Position;
            var duration = _mediaEngine.Duration;
            _handlingMediaEnd = true;
            System.Diagnostics.Debug.WriteLine($"[MediaControls] Media end detected: {position:F2}s / {duration:F2}s");

            MediaEnded?.Invoke(this, EventArgs.Empty);

            if (LoopMedia)
            {
                System.Diagnostics.Debug.WriteLine("[MediaControls] Looping media");

                _mediaEngine.Position = 0;
                if (_mediaEngine.Play())
                {
                    StartRendering();
                }
            }
            else
            {
                StopRendering();
            }

            _handlingMediaEnd = false;
        }
        catch (Exception ex)
        {
            _handlingMediaEnd = false;
            System.Diagnostics.Debug.WriteLine($"[MediaControls] Error handling media end: {ex.Message}");
        }
    }

    private void StartRendering() => _mediaRenderer?.StartRendering();

    private void StopRendering() => _mediaRenderer?.StopRendering();

    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    public MediaPlayerState State => _mediaEngine.State;

    /// <summary>
    /// Gets whether media is currently open.
    /// </summary>
    public bool IsOpen => _mediaEngine.IsOpen;

    /// <summary>
    /// Gets the type of content currently loaded (Audio, Video, CDG, etc.).
    /// </summary>
    public MediaContent Content => _mediaEngine.Content;

    /// <summary>
    /// Gets whether the media has video content.
    /// </summary>
    public bool HasVideo => _mediaEngine.Content.HasFlag(MediaContent.Video);

    /// <summary>
    /// Gets whether the media has audio content.
    /// </summary>
    public bool HasAudio => _mediaEngine.Content.HasFlag(MediaContent.Audio);

    /// <summary>
    /// Gets whether the media has CDG content.
    /// </summary>
    public bool HasCdg => _mediaEngine.Content.HasFlag(MediaContent.Cdg);

    /// <summary>
    /// Disposes resources used by MediaControls.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _mediaEngine.MediaEnded -= OnMediaEnded;
    }
}
