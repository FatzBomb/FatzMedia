using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Threading;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

public partial class PlayerCore
{
    public event EventHandler? MediaEnded;

    public event EventHandler? MediaStarted;

    public event EventHandler? PresentationFramePresented;

    public event EventHandler? MediaStopped;

    public event EventHandler<MediaErrorEventArgs>? MediaError;

    public event EventHandler<MediaDiagnosticEventArgs>? MediaDiagnostic;

    public event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;

    public event EventHandler<MediaLoadedEventArgs>? MediaLoaded;

    public event EventHandler<MediaClosedEventArgs>? MediaClosed;

    public event EventHandler<MediaPausedEventArgs>? MediaPaused;

    public event EventHandler<MediaLoopedEventArgs>? MediaLooped;

    public event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    public event EventHandler<MediaPositionChangedEventArgs>? PositionChanged;

    public event EventHandler<MediaSeekCompletedEventArgs>? SeekCompleted;

    public event EventHandler<MediaValueChangedEventArgs<double>>? VolumeChanged;

    public event EventHandler<MediaValueChangedEventArgs<int>>? PitchChanged;

    public event EventHandler<MediaValueChangedEventArgs<int>>? TempoChanged;

    public event EventHandler<MediaNormalizeChangedEventArgs>? NormalizeChanged;

    public event EventHandler<MediaValueChangedEventArgs<bool>>? LoopMediaChanged;

    public event EventHandler<MediaValueChangedEventArgs<AudioEqualizerSettings>>? EqualizerChanged;

    public event EventHandler<SubtitleTracksChangedEventArgs>? SubtitleTracksChanged;

    /// <remarks>
    /// Raised by the engine's embedded subtitle scan (ENG-039 P5-1) on its own thread; delivered
    /// through the control queue, so hosts see it after the load's <c>MediaLoaded</c>.
    /// </remarks>
    void OnEngineSubtitleTracksChanged(object? sender, EventArgs e) =>
        RaiseOnControlQueue(() =>
        {
            // Queued behind a Close, the change is stale (a newer load drops it in the engine).
            if (!_mediaEngine.IsOpen)
                return;

            // A paused video shows the newly selected track at once.
            RequestPresentationRefresh();
            HostEventDispatcher.Raise(
                this,
                SubtitleTracksChanged,
                new SubtitleTracksChangedEventArgs(_mediaEngine.SubtitleTracks, _mediaEngine.SelectedSubtitleTrackId),
                nameof(SubtitleTracksChanged));
        });

    void OnControlsMediaEnded(object? sender, EventArgs e)
    {
        ClearPendingSeek();
        if (!Controls.LoopMedia)
            _positionNotifier.Stop();

        var handler = MediaEnded;
        if (handler != null)
            _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, handler, nameof(MediaEnded)));

        ScheduleAutomaticPreparedNextTransition();
    }

    void OnControlsMediaStarted(object? sender, EventArgs e)
    {
        HostEventDispatcher.Raise(this, MediaStarted, nameof(MediaStarted));
        _positionNotifier.Start();
    }

    void OnControlsMediaStopped(object? sender, EventArgs e)
    {
        ClearPendingSeek();
        _positionNotifier.Stop();
        var handler = MediaStopped;
        if (handler == null)
            return;

        // The item a load replaces: raised now, before that load's MediaClosed and MediaLoaded
        // (queued, it arrived after them and auto-hide hid the new item).
        if (File.IsStoppingReplacedMedia)
            HostEventDispatcher.Raise(this, handler, nameof(MediaStopped));
        else
            _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, handler, nameof(MediaStopped)));
    }

    void OnFileMediaLoaded(object? sender, MediaLoadedEventArgs e)
    {
        Controls.CancelScrub();
        ScrubAudioMuter.OnScrubEnded();
        SubscribeSeekCompletionSources();
        HostEventDispatcher.Raise(this, MediaLoaded, e, nameof(MediaLoaded));
        if (!Controls.ReportCurrentState())
            QueuePositionSnapshotAfter(Task.CompletedTask);
    }

    void OnFileMediaClosed(object? sender, MediaClosedEventArgs e)
    {
        Controls.CancelScrub();
        ScrubAudioMuter.OnScrubEnded();
        _positionNotifier.Stop();
        ClearPendingSeek();
        UnsubscribeSeekCompletionSources();
        HostEventDispatcher.Raise(this, MediaClosed, e, nameof(MediaClosed));
        if (!Controls.ReportCurrentState())
            QueuePositionSnapshotAfter(Task.CompletedTask);
    }

    void OnControlsMediaPaused(object? sender, MediaPausedEventArgs e)
    {
        _positionNotifier.Stop();
        HostEventDispatcher.Raise(this, MediaPaused, e, nameof(MediaPaused));
    }

    void OnControlsMediaLooped(object? sender, MediaLoopedEventArgs e)
    {
        var delivery = _controlOperations.Enqueue(() =>
            HostEventDispatcher.Raise(this, MediaLooped, e, nameof(MediaLooped)));
        QueuePositionSnapshotAfter(delivery);
    }

    void OnControlsStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        var delivery = _controlOperations.Enqueue(() =>
            HostEventDispatcher.Raise(this, StateChanged, e, nameof(StateChanged)));
        if (e.CurrentState is MediaPlayerState.Stopped or MediaPlayerState.Null)
            QueuePositionSnapshotAfter(delivery);
    }

    void OnPositionNotifierChanged(object? sender, MediaPositionChangedEventArgs e) =>
        HostEventDispatcher.Raise(this, PositionChanged, e, nameof(PositionChanged));

    void OnEffectsVolumeChanged(object? sender, MediaValueChangedEventArgs<double> e) =>
        HostEventDispatcher.Raise(this, VolumeChanged, e, nameof(VolumeChanged));

    void OnEffectsPitchChanged(object? sender, MediaValueChangedEventArgs<int> e) =>
        HostEventDispatcher.Raise(this, PitchChanged, e, nameof(PitchChanged));

    void OnEffectsTempoChanged(object? sender, MediaValueChangedEventArgs<int> e) =>
        HostEventDispatcher.Raise(this, TempoChanged, e, nameof(TempoChanged));

    void OnEffectsNormalizeChanged(object? sender, MediaNormalizeChangedEventArgs e) =>
        HostEventDispatcher.Raise(this, NormalizeChanged, e, nameof(NormalizeChanged));

    void OnEffectsEqualizerChanged(object? sender, MediaValueChangedEventArgs<AudioEqualizerSettings> e) =>
        HostEventDispatcher.Raise(this, EqualizerChanged, e, nameof(EqualizerChanged));

    void OnControlsLoopMediaChanged(object? sender, MediaValueChangedEventArgs<bool> e) =>
        HostEventDispatcher.Raise(this, LoopMediaChanged, e, nameof(LoopMediaChanged));

    // Raised on the render thread: a throwing handler must not escape into the render loop.
    void OnPresentationFramePresented(object? sender, EventArgs e) =>
        HostEventDispatcher.Raise(this, PresentationFramePresented, nameof(PresentationFramePresented));

    void OnEngineErrorOccurred(MediaErrorInfo info)
    {
        var handler = MediaError;
        if (handler == null)
            return;

        var args = new MediaErrorEventArgs(info.Code, info.Message, info.SourcePath, info.InnerException);
        _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, handler, args, nameof(MediaError)));
    }

    void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        // Another player's diagnostics are not this player's (process-wide ones are everyone's).
        var handler = MediaDiagnostic;
        if (handler == null || !info.IsFor(this))
            return;

        var args = new MediaDiagnosticEventArgs(info.Level, info.Category, info.Message, info.SourcePath, info.InnerException);
        // Delivered in order on the player's own diagnostic worker, not the control queue: a
        // diagnostic raised during a long operation (a network retry during a load) reaches the
        // host while the operation runs. A failing diagnostic handler is not reported as a
        // diagnostic: that would raise it again.
        _ = _diagnosticEvents.Post(() => HostEventDispatcher.Raise(this, handler, args, nameof(MediaDiagnostic), reportFailures: false));
    }

    void OnAudioDeviceMonitorChanged(object? sender, AudioDeviceChangeInfo info) =>
        _ = _controlOperations.Enqueue(() => RaiseAudioDeviceChangeNotice(info));

    void RaiseAudioDeviceChangeNotice(AudioDeviceChangeInfo info)
    {
        if (_disposed)
            return;

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "AudioDeviceMonitor",
            info.FailureMessage is { Length: > 0 }
                ? info.FailureMessage
                : $"Audio device change detected: kind={info.Kind}, name={info.DeviceName ?? "(unknown)"}; the audio engine and the system handle playback.",
            innerException: null);

        RaiseAudioDeviceChanged(info);
    }

    void RaiseAudioDeviceChanged(AudioDeviceChangeInfo info)
    {
        if (_disposed)
            return;

        var handler = AudioDeviceChanged;
        if (handler != null)
            HostEventDispatcher.Raise(this, handler, new AudioDeviceChangedEventArgs(info), nameof(AudioDeviceChanged));
    }

    PlaybackPositionNotifier CreatePositionNotifier()
    {
        var notifier = new PlaybackPositionNotifier(
            () => _mediaEngine.Position,
            () => _mediaEngine.Duration);
        notifier.PositionChanged += OnPositionNotifierChanged;
        return notifier;
    }

    void SubscribeNewSessionEvents()
    {
        File.MediaLoaded += OnFileMediaLoaded;
        File.MediaClosed += OnFileMediaClosed;
        Controls.MediaPaused += OnControlsMediaPaused;
        Controls.MediaLooped += OnControlsMediaLooped;
        Controls.StateChanged += OnControlsStateChanged;
        Controls.LoopMediaChanged += OnControlsLoopMediaChanged;
        Effects.VolumeChanged += OnEffectsVolumeChanged;
        Effects.PitchChanged += OnEffectsPitchChanged;
        Effects.TempoChanged += OnEffectsTempoChanged;
        Effects.NormalizeChanged += OnEffectsNormalizeChanged;
        Effects.EqualizerChanged += OnEffectsEqualizerChanged;
        if (_mediaEngine is ISubtitleTrackSource subtitleTracks)
            subtitleTracks.SubtitleTracksChanged += OnEngineSubtitleTracksChanged;
    }

    void UnsubscribeNewSessionEvents()
    {
        File.MediaLoaded -= OnFileMediaLoaded;
        File.MediaClosed -= OnFileMediaClosed;
        Controls.MediaPaused -= OnControlsMediaPaused;
        Controls.MediaLooped -= OnControlsMediaLooped;
        Controls.StateChanged -= OnControlsStateChanged;
        Controls.LoopMediaChanged -= OnControlsLoopMediaChanged;
        Effects.VolumeChanged -= OnEffectsVolumeChanged;
        Effects.PitchChanged -= OnEffectsPitchChanged;
        Effects.TempoChanged -= OnEffectsTempoChanged;
        Effects.NormalizeChanged -= OnEffectsNormalizeChanged;
        Effects.EqualizerChanged -= OnEffectsEqualizerChanged;
        if (_mediaEngine is ISubtitleTrackSource subtitleTracks)
            subtitleTracks.SubtitleTracksChanged -= OnEngineSubtitleTracksChanged;
    }

    void SubscribeSeekCompletionSources()
    {
        UnsubscribeSeekCompletionSources();
        _seekVideoEngine = _mediaEngine.Video;
        _seekCdgEngine = _mediaEngine.Cdg;
        _seekVideoEngine?.PositionChanged += OnSeekVideoPositionChanged;
        _seekCdgEngine?.FrameInvalidated += OnSeekCdgFrameInvalidated;
    }

    void UnsubscribeSeekCompletionSources()
    {
        _seekVideoEngine?.PositionChanged -= OnSeekVideoPositionChanged;
        _seekCdgEngine?.FrameInvalidated -= OnSeekCdgFrameInvalidated;
        _seekVideoEngine = null;
        _seekCdgEngine = null;
    }

    void OnSeekVideoPositionChanged(object? sender, double position) =>
        TryCompletePendingSeek(position);

    void OnSeekCdgFrameInvalidated(object? sender, EventArgs e) =>
        TryCompletePendingSeek(_mediaEngine.Position);

    void TryCompletePendingSeek(
        double resultingPosition,
        double toleranceSeconds = SeekSettleToleranceSeconds,
        bool isSettlement = true)
    {
        if (Volatile.Read(ref _hasPendingSeek) == 0)
            return;

        PendingSeek? pending;
        lock (_seekCompletionGate)
        {
            pending = _pendingSeek;
            // Only a settlement notification for a seek made during a scrub may land on the
            // previewed frame; the immediate check right after Seek stays strict, because the
            // video seek has only been queued and the position is still the old one.
            var tolerance = isSettlement && pending is { DuringScrub: true }
                ? Math.Max(toleranceSeconds, ScrubSettleToleranceSeconds)
                : toleranceSeconds;
            if (pending == null || Math.Abs(CleanPosition(resultingPosition) - pending.Value.TargetSeconds) > tolerance)
                return;

            _pendingSeek = null;
            Volatile.Write(ref _hasPendingSeek, 0);
        }

        _ = _controlOperations.Enqueue(() =>
            RaiseSeekCompleted(pending.Value.RequestedSeconds, resultingPosition));
    }

    void RaiseSeekCompleted(double requestedSeconds, double resultingSeconds)
    {
        HostEventDispatcher.Raise(
            this,
            SeekCompleted,
            new MediaSeekCompletedEventArgs(
                TimeSpan.FromSeconds(CleanPosition(requestedSeconds)),
                TimeSpan.FromSeconds(CleanPosition(resultingSeconds))),
            nameof(SeekCompleted));
        QueuePositionSnapshotAfter(Task.CompletedTask);
    }

    void QueuePositionSnapshotAfter(Task prerequisite) =>
        _ = PublishPositionSnapshotAfterAsync(prerequisite);

    async Task PublishPositionSnapshotAfterAsync(Task prerequisite)
    {
        // Always off the caller's thread, and never posted to its SynchronizationContext (a sync
        // call from a UI thread). Position delivery is independent of lifecycle handler failures.
        await prerequisite.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.SuppressThrowing);
        _positionNotifier.PublishNow();
    }

    void ClearPendingSeek()
    {
        lock (_seekCompletionGate)
        {
            _pendingSeek = null;
            Volatile.Write(ref _hasPendingSeek, 0);
        }
    }

    static double CleanPosition(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
