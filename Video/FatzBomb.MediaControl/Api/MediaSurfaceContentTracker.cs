using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Decides whether an adapter's media surface shows its content (Stage 046A): the surface
/// auto-hide rules of Stage 029C, moved out of the WPF adapter so every adapter hides and shows
/// the same way, plus the session's <see cref="IMediaControlSession.HasPresentation"/> signal
/// (Audio mode presents nothing, so a surface shows its background, not the last frame).
/// </summary>
/// <remarks>
/// <para>
/// Auto-hide follows the session's lifecycle events and uses
/// <see cref="IMediaControlSession.PresentationFramePresented"/> as the proof of visual output,
/// never file extensions: a load only arms the surface, the first presented frame shows it;
/// stop, close, error and a natural end without looping hide it; pause and looping keep it.
/// A frame presented before the load notice arrives (a static image presents once) is latched
/// and replayed when the load arms the surface; the latch is cleared on hide, close and session
/// change, and a present while the current item has no visual content
/// (<see cref="IMediaControlSession.HasVisualContent"/>) never shows the surface. A stop or end
/// that arrives while the session already plays again (the internal stop of a replacement) is
/// ignored.
/// </para>
/// <para>
/// Threading: set <see cref="Session"/> and <see cref="AutoHide"/> on the adapter's UI thread.
/// Session events arrive on background threads (the present notice on the render thread); the
/// tracker applies them under a lock and raises <see cref="Changed"/> on the thread that changed
/// the state, outside the lock. A handler marshals to its UI thread and reads the properties
/// there: they are the current state, not a snapshot of the change, so the handler may coalesce.
/// </para>
/// </remarks>
public sealed class MediaSurfaceContentTracker : IDisposable
{
    private readonly Lock _lock = new();
    private readonly SurfaceAutoHidePolicy _policy = new();
    private IMediaControlSession? _session;
    private bool _autoHide;
    private bool _framePresented;
    private bool _hasVisualContent;
    private bool _lifecycleSeen;
    private bool _disposed;

    /// <param name="autoHide">The initial <see cref="AutoHide"/> (default <see langword="true"/>).</param>
    public MediaSurfaceContentTracker(bool autoHide = true) => _autoHide = autoHide;

    /// <summary>
    /// Raised when <see cref="IsContentVisible"/>, <see cref="ShowsContent"/> or
    /// <see cref="HasPresentation"/> may have changed, on the thread that changed it (often a
    /// background thread). Marshal, then read the properties.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets or sets the session to follow (UI thread). A change resets the auto-hide state: an
    /// open session arms the surface until its next presented frame.
    /// </summary>
    public IMediaControlSession? Session
    {
        get => Volatile.Read(ref _session);
        set => SetSession(value);
    }

    /// <summary>
    /// Gets or sets whether the surface hides its content while no frame of the current media is
    /// shown (default <see langword="true"/>, standard media-player behaviour). <see langword="false"/>
    /// shows the surface whenever the session presents. Set it on the UI thread.
    /// </summary>
    public bool AutoHide
    {
        get
        {
            lock (_lock)
                return _autoHide;
        }
        set => SetAutoHide(value);
    }

    /// <summary>
    /// Gets whether the session presents to its surfaces (<see cref="IMediaControlSession.HasPresentation"/>,
    /// read now); <see langword="true"/> without a session.
    /// </summary>
    public bool HasPresentation => Volatile.Read(ref _session)?.HasPresentation ?? true;

    /// <summary>
    /// Gets whether auto-hide shows the content now: <see cref="AutoHide"/> is on, a frame of the
    /// current media was presented, and the session presents. Always <see langword="false"/>
    /// while <see cref="AutoHide"/> is off.
    /// </summary>
    public bool IsContentVisible
    {
        get
        {
            bool visible;
            lock (_lock)
                visible = _autoHide && _policy.IsContentVisible;
            return visible && HasPresentation;
        }
    }

    /// <summary>
    /// Gets whether the adapter shows its presentation host now: the session presents, and
    /// either <see cref="AutoHide"/> is off, the content is visible, or a load armed the surface
    /// for media with visual content (so its first frame can arrive).
    /// </summary>
    public bool ShowsContent
    {
        get
        {
            bool allowed;
            lock (_lock)
                allowed = AutoHideAllowsContent();
            return allowed && HasPresentation;
        }
    }

    /// <summary>Stops following the session.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        SetSession(null);
        lock (_lock)
            _disposed = true;
        Changed = null;
    }

    private bool AutoHideAllowsContent() =>
        !_autoHide
        || _policy.IsContentVisible
        || (_policy.State == SurfaceAutoHideState.PendingShow && _hasVisualContent);

    private (bool Visible, bool Allowed) Snapshot() => (_autoHide && _policy.IsContentVisible, AutoHideAllowsContent());

    private void SetSession(IMediaControlSession? session)
    {
        ObjectDisposedException.ThrowIf(_disposed && session is not null, this);
        IMediaControlSession? previous;
        lock (_lock)
        {
            if (ReferenceEquals(session, _session))
                return;

            previous = _session;
            Volatile.Write(ref _session, session);
            _framePresented = false;
            _hasVisualContent = false;
            _lifecycleSeen = false;
            _ = _policy.Reset();
        }

        if (previous is not null)
            Unsubscribe(previous);

        if (session is not null)
        {
            Subscribe(session);

            // Read after subscribing: an event raised meanwhile is applied as well, and this read
            // never undoes it.
            var isOpen = session.IsOpen;
            var hasVisualContent = session.HasVisualContent;
            lock (_lock)
            {
                if (ReferenceEquals(session, _session) && !_lifecycleSeen)
                {
                    _hasVisualContent = hasVisualContent;
                    if (_autoHide && isOpen)
                        ArmForCurrentMedia();
                }
            }
        }

        RaiseChanged();
    }

    private void SetAutoHide(bool autoHide)
    {
        IMediaControlSession? session = Volatile.Read(ref _session);
        var isOpen = session?.IsOpen == true;
        lock (_lock)
        {
            if (_autoHide == autoHide)
                return;

            _autoHide = autoHide;
            _ = _policy.Reset();
            if (autoHide && isOpen)
                ArmForCurrentMedia();
        }

        RaiseChanged();
    }

    // Under the lock: a load arms; a frame latched before the load notice shows at once.
    private void ArmForCurrentMedia()
    {
        _ = _policy.MediaLoaded();
        if (_hasVisualContent && _framePresented)
            _ = _policy.FramePresented();
    }

    private void Subscribe(IMediaControlSession session)
    {
        session.MediaLoaded += OnMediaLoaded;
        session.MediaClosed += OnMediaClosed;
        session.MediaError += OnMediaError;
        session.MediaStopped += OnMediaStopped;
        session.MediaEnded += OnMediaEnded;
        session.MediaStarted += OnMediaStarted;
        session.MediaLooped += OnMediaLooped;
        session.PresentationFramePresented += OnPresentationFramePresented;
        session.ModeChanged += OnModeChanged;
        session.HasPresentationChanged += OnHasPresentationChanged;
    }

    private void Unsubscribe(IMediaControlSession session)
    {
        session.MediaLoaded -= OnMediaLoaded;
        session.MediaClosed -= OnMediaClosed;
        session.MediaError -= OnMediaError;
        session.MediaStopped -= OnMediaStopped;
        session.MediaEnded -= OnMediaEnded;
        session.MediaStarted -= OnMediaStarted;
        session.MediaLooped -= OnMediaLooped;
        session.PresentationFramePresented -= OnPresentationFramePresented;
        session.ModeChanged -= OnModeChanged;
        session.HasPresentationChanged -= OnHasPresentationChanged;
    }

    // Under the lock. An event of a replaced session (raised before it was unsubscribed) is dropped.
    private bool IsCurrent(object? sender) => sender is null || ReferenceEquals(sender, _session);

    private IMediaControlSession? SessionOf(object? sender) => sender as IMediaControlSession ?? Volatile.Read(ref _session);

    private void OnMediaLoaded(object? sender, MediaLoadedEventArgs e)
    {
        var hasVisualContent = SessionOf(sender)?.HasVisualContent == true;
        Apply(sender, () =>
        {
            _hasVisualContent = hasVisualContent;
            if (_autoHide)
                ArmForCurrentMedia();
        });
    }

    private void OnMediaClosed(object? sender, MediaClosedEventArgs e) => Hide(sender, static policy => policy.MediaClosed());

    private void OnMediaError(object? sender, MediaErrorEventArgs e) => Hide(sender, static policy => policy.MediaError());

    private void Hide(object? sender, Func<SurfaceAutoHidePolicy, bool> transition) => Apply(sender, () =>
    {
        _hasVisualContent = false;
        _framePresented = false;
        if (_autoHide)
            _ = transition(_policy);
    });

    private void OnMediaStopped(object? sender, EventArgs e)
    {
        var stale = IsStaleTerminalEvent(SessionOf(sender));
        Apply(sender, () =>
        {
            if (stale && _autoHide)
                return;

            _framePresented = false;
            if (_autoHide)
                _ = _policy.MediaStopped();
        });
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        IMediaControlSession? session = SessionOf(sender);
        var stale = IsStaleTerminalEvent(session);
        var isLooping = session is { IsOpen: true, LoopMedia: true };
        Apply(sender, () =>
        {
            if (stale && _autoHide)
                return;

            if (!isLooping)
                _framePresented = false;
            if (_autoHide)
                _ = _policy.MediaEnded(isLooping);
        });
    }

    // A replacement stops the previous item internally; by the time that stop or end arrives the
    // session may already play the new one, which must not be hidden.
    private static bool IsStaleTerminalEvent(IMediaControlSession? session) =>
        session is { State: MediaPlayerState.Playing or MediaPlayerState.Paused or MediaPlayerState.Buffering };

    private void OnMediaStarted(object? sender, EventArgs e) => ApplyTransport(sender, static policy => policy.MediaStarted());

    private void OnMediaLooped(object? sender, MediaLoopedEventArgs e) => ApplyTransport(sender, static policy => policy.MediaLooped());

    private void ApplyTransport(object? sender, Func<SurfaceAutoHidePolicy, bool> transition)
    {
        (bool Visible, bool Allowed) before;
        (bool Visible, bool Allowed) after;
        lock (_lock)
        {
            if (_disposed || !IsCurrent(sender) || !_autoHide)
                return;

            before = Snapshot();
            _ = transition(_policy);

            // A frame of this media presented while hidden (the position after a stop) needs no
            // second present to show.
            if (_policy.State == SurfaceAutoHideState.PendingShow && _hasVisualContent && _framePresented)
                _ = _policy.FramePresented();
            after = Snapshot();
        }

        if (before != after)
            RaiseChanged();
    }

    // The render thread, once per presented frame: no allocation, and Changed only on a change.
    private void OnPresentationFramePresented(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed || !IsCurrent(sender))
                return;

            _framePresented = true;

            // A present of a replaced item (no visual content known yet) must not reveal a new
            // audio-only item; MediaLoaded replays the latch once it knows.
            if (!_autoHide || !_hasVisualContent || _policy.State != SurfaceAutoHideState.PendingShow)
                return;

            _ = _policy.FramePresented();
        }

        RaiseChanged();
    }

    private void OnModeChanged(object? sender, MediaValueChangedEventArgs<MediaPlayerMode> e) => OnPresentationAvailabilityChanged(sender);

    private void OnHasPresentationChanged(object? sender, MediaValueChangedEventArgs<bool> e) => OnPresentationAvailabilityChanged(sender);

    // HasPresentation is read live, so a handler that marshals sees the session's current mode.
    private void OnPresentationAvailabilityChanged(object? sender)
    {
        lock (_lock)
        {
            if (_disposed || !IsCurrent(sender))
                return;
        }

        RaiseChanged();
    }

    private void Apply(object? sender, Action change)
    {
        (bool Visible, bool Allowed) before;
        (bool Visible, bool Allowed) after;
        lock (_lock)
        {
            if (_disposed || !IsCurrent(sender))
                return;

            _lifecycleSeen = true;
            before = Snapshot();
            change();
            after = Snapshot();
        }

        if (before != after)
            RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
