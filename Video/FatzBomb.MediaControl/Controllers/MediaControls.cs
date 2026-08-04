using FatzBomb.MediaControl.Core.Abstractions;


namespace FatzBomb.MediaControl.Controllers;

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
    public bool LoopMedia
    {
        get;
        set
        {
            field = value;
            if (_mediaEngine is ILoopPlaybackControl loopPlaybackControl)
                loopPlaybackControl.LoopPlayback = value;
        }
    }


    /// <summary>
    /// Event raised when media reaches the end.
    /// </summary>
    public event EventHandler? MediaEnded;

    /// <summary>
    /// Event raised when media playback starts.
    /// </summary>
    public event EventHandler? MediaStarted;

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

            MediaStarted?.Invoke(this, EventArgs.Empty);

            // Notify adapters before the first playback presentation so they can reveal
            // retained one-shot visuals such as explicitly supplied image backgrounds.
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
    /// Seeks to the specified playback position in seconds.
    /// </summary>
    public bool Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mediaEngine.Seek(seconds);
    }

    /// <summary>
    /// Gets the current playback position.
    /// </summary>
    public TimeSpan Position => TimeSpan.FromSeconds(_mediaEngine.Position);

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

                if (_mediaEngine is ILoopPlaybackControl { HandlesLoopInternally: true })
                {
                    _handlingMediaEnd = false;
                    return;
                }

                // Video owns an atomic loop restart in VideoPipeline.Play(). Queueing the generic
                // async seek first lets the seek pump race the restart and can blank the native video
                // surface. Non-video pipelines still need the existing seek-before-play behavior.
                if (!_mediaEngine.Content.HasFlag(MediaContent.Video))
                    _mediaEngine.Position = 0;

                if (_mediaEngine.Play())
                {
                    MediaStarted?.Invoke(this, EventArgs.Empty);
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
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaControls", $"Error handling media end: {ex.Message}", innerException: ex);
        }
    }

    private void StartRendering() => _mediaRenderer?.StartRendering();

    private void StopRendering() => _mediaRenderer?.StopRendering();

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
