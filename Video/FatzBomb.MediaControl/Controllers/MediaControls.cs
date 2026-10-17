using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Threading;


namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Controls media playback operations.
/// </summary>
public sealed class MediaControls
{
    private readonly IMediaEngine _mediaEngine;
    private readonly IMediaRenderCoordinator? _mediaRenderer;
    private readonly Action<Action>? _dispatchControlOperation;
    private MediaPlayerState _lastReportedState;
    private long _transportGeneration;
    private volatile bool _handlingMediaEnd;
    private volatile bool _disposed;
    private const double ScrubRepeatToleranceSeconds = 0.001;
    private readonly Lock _scrubGate = new();
    private bool _scrubbing;
    private double _lastScrubTarget = double.NaN;

    internal MediaControls(
        IMediaEngine mediaEngine,
        IMediaRenderCoordinator? mediaRenderer = null,
        Action<Action>? dispatchControlOperation = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _mediaRenderer = mediaRenderer;
        _dispatchControlOperation = dispatchControlOperation;
        _lastReportedState = mediaEngine.State;
        _mediaEngine.MediaEnded += OnMediaEnded;
        if (_mediaEngine is IMediaLoopEventSource loopEventSource)
            loopEventSource.MediaLooped += OnEngineMediaLooped;
    }

    /// <summary>
    /// Gets or sets whether media should loop continuously.
    /// </summary>
    public bool LoopMedia
    {
        get;
        set
        {
            if (field == value)
                return;

            var previous = field;
            field = value;
            if (_mediaEngine is ILoopPlaybackControl loopPlaybackControl)
                loopPlaybackControl.LoopPlayback = value;

            HostEventDispatcher.Raise(
                this,
                LoopMediaChanged,
                new MediaValueChangedEventArgs<bool>(previous, value),
                nameof(LoopMediaChanged));
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

    public event EventHandler<MediaPausedEventArgs>? MediaPaused;

    public event EventHandler<MediaLoopedEventArgs>? MediaLooped;

    public event EventHandler<MediaValueChangedEventArgs<bool>>? LoopMediaChanged;

    internal event EventHandler<MediaStateChangedEventArgs>? StateChanged;

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
            _ = Interlocked.Increment(ref _transportGeneration);
            _handlingMediaEnd = false;

            MediaStarted?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();

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
            _ = Interlocked.Increment(ref _transportGeneration);
            _handlingMediaEnd = false;

            HostEventDispatcher.Raise(
                this,
                MediaPaused,
                new MediaPausedEventArgs(Position),
                nameof(MediaPaused));
            RaiseStateChanged();

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
            _ = Interlocked.Increment(ref _transportGeneration);
            _handlingMediaEnd = false;

            // Stop rendering when stopped
            StopRendering();

            // Fire stopped event
            MediaStopped?.Invoke(this, EventArgs.Empty);
            RaiseStateChanged();
        }
        return success;
    }

    /// <summary>
    /// Seeks to the specified playback position in seconds.
    /// </summary>
    public bool Seek(double seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRepeatOfScrubTarget(seconds))
            return true;

        var success = _mediaEngine.Seek(seconds);
        if (success)
        {
            _ = Interlocked.Increment(ref _transportGeneration);
            _handlingMediaEnd = false;
            lock (_scrubGate)
                if (_scrubbing)
                    _lastScrubTarget = seconds;
        }

        return success;
    }

    /// <summary>
    /// Gets the current playback position.
    /// </summary>
    public TimeSpan Position => TimeSpan.FromSeconds(_mediaEngine.Position);

    /// <summary>
    /// Gets whether an interactive scrub started by <see cref="BeginScrub"/> is in progress.
    /// </summary>
    public bool IsScrubbing
    {
        get
        {
            lock (_scrubGate)
                return _scrubbing;
        }
    }

    /// <summary>
    /// Starts an interactive scrub, such as a seek-bar drag. Playback carries on as usual and
    /// each <see cref="Seek"/> moves it, but a seek to the same target as the previous seek of the
    /// scrub is ignored. A seek bar sends one more seek on release; at the last drag position
    /// that seek would restart playback from there, jumping back by however long it played while
    /// the button was held.
    /// </summary>
    /// <returns>True when media is open and the scrub started (or was already active).</returns>
    public bool BeginScrub()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mediaEngine.IsOpen)
            return false;

        lock (_scrubGate)
        {
            if (!_scrubbing)
            {
                _scrubbing = true;
                _lastScrubTarget = double.NaN;
            }
        }

        return true;
    }

    /// <summary>
    /// Ends a scrub started by <see cref="BeginScrub"/>. Send the release seek first; it is
    /// ignored when it repeats the last drag position.
    /// </summary>
    /// <returns>True when a scrub was active.</returns>
    public bool EndScrub()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_scrubGate)
        {
            if (!_scrubbing)
                return false;

            _scrubbing = false;
            _lastScrubTarget = double.NaN;
            return true;
        }
    }

    /// <summary>Ends any scrub, as when media is loaded or closed mid-drag.</summary>
    internal void CancelScrub()
    {
        lock (_scrubGate)
        {
            _scrubbing = false;
            _lastScrubTarget = double.NaN;
        }
    }

    /// <summary>True when a scrub is active and <paramref name="seconds"/> repeats its last seek.</summary>
    internal bool IsRepeatOfScrubTarget(double seconds)
    {
        lock (_scrubGate)
            return _scrubbing && !double.IsNaN(_lastScrubTarget) && Math.Abs(seconds - _lastScrubTarget) <= ScrubRepeatToleranceSeconds;
    }

    /// <summary>
    /// Gets the transport generation, which advances on every accepted Play, Pause, Stop, Seek,
    /// and loop restart so queued follow-up work can detect host intervention.
    /// </summary>
    internal long TransportGeneration => Volatile.Read(ref _transportGeneration);

    /// <summary>
    /// Gets the total loaded media duration.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        var transportGeneration = Volatile.Read(ref _transportGeneration);
        if (_dispatchControlOperation is not null)
        {
            _dispatchControlOperation(() => BeginMediaEnd(transportGeneration));
            return;
        }

        BeginMediaEnd(transportGeneration);
    }

    private void BeginMediaEnd(long transportGeneration)
    {
        try
        {
            if (_disposed
                || _handlingMediaEnd
                || !_mediaEngine.IsOpen
                || transportGeneration != Volatile.Read(ref _transportGeneration))
                return;

            var position = _mediaEngine.Position;
            var duration = _mediaEngine.Duration;
            _handlingMediaEnd = true;
            System.Diagnostics.Debug.WriteLine($"[MediaControls] Media end detected: {position:F2}s / {duration:F2}s");

            MediaEnded?.Invoke(this, EventArgs.Empty);

            if (_dispatchControlOperation is not null)
            {
                _dispatchControlOperation(() => CompleteMediaEnd(transportGeneration));
                return;
            }

            CompleteMediaEnd(transportGeneration);
        }
        catch (Exception ex)
        {
            if (transportGeneration == Volatile.Read(ref _transportGeneration))
                _handlingMediaEnd = false;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaControls", $"Error beginning media end: {ex.Message}", innerException: ex);
        }
    }

    private void CompleteMediaEnd(long transportGeneration)
    {
        try
        {
            if (_disposed
                || !_handlingMediaEnd
                || !_mediaEngine.IsOpen
                || transportGeneration != Volatile.Read(ref _transportGeneration))
                return;

            // Network sources never loop (a live stream cannot restart); their end is a stop.
            if (LoopMedia && !IsNetworkSource)
            {
                System.Diagnostics.Debug.WriteLine("[MediaControls] Looping media");

                if (_mediaEngine is ILoopPlaybackControl { HandlesLoopInternally: true })
                {
                    // Audio reports its internal wrap through PlaybackEnded. Video reports a
                    // successful wrap through IMediaLoopEventSource instead; an ended video did
                    // not loop successfully and must not synthesize MediaLooped here.
                    if (!_mediaEngine.Content.HasFlag(MediaContent.Video))
                        RaiseMediaLooped();
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
                    _ = Interlocked.Increment(ref _transportGeneration);
                    MediaStarted?.Invoke(this, EventArgs.Empty);
                    StartRendering();
                    RaiseMediaLooped();
                }
            }
            else
            {
                StopRendering();
                RaiseStateChanged();
            }

            _handlingMediaEnd = false;
        }
        catch (Exception ex)
        {
            if (transportGeneration == Volatile.Read(ref _transportGeneration))
                _handlingMediaEnd = false;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaControls", $"Error completing media end: {ex.Message}", innerException: ex);
        }
    }

    private bool IsNetworkSource =>
        _mediaEngine is IMediaSourceCapabilityProvider { CurrentCapabilities.SourceClass: MediaSourceClass.NetworkFile or MediaSourceClass.NetworkLive };

    private void StartRendering() => _mediaRenderer?.StartRendering();

    private void StopRendering() => _mediaRenderer?.StopRendering();

    private void OnEngineMediaLooped(object? sender, EventArgs e) => RaiseMediaLooped();

    private void RaiseMediaLooped() => HostEventDispatcher.Raise(
        this,
        MediaLooped,
        new MediaLoopedEventArgs(Position),
        nameof(MediaLooped));

    internal bool ReportCurrentState() => RaiseStateChanged();

    private bool RaiseStateChanged()
    {
        MediaPlayerState current = _mediaEngine.State;
        if (current == _lastReportedState)
            return false;

        var args = new MediaStateChangedEventArgs(_lastReportedState, current);
        _lastReportedState = current;
        HostEventDispatcher.Raise(this, StateChanged, args, nameof(StateChanged));
        return true;
    }

    /// <summary>
    /// Disposes resources used by MediaControls.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _mediaEngine.MediaEnded -= OnMediaEnded;
        if (_mediaEngine is IMediaLoopEventSource loopEventSource)
            loopEventSource.MediaLooped -= OnEngineMediaLooped;
    }
}
