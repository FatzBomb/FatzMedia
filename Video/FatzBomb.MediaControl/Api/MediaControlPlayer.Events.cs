using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Threading;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public partial class MediaControlPlayer
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

    private void OnControlsMediaEnded(object? sender, EventArgs e)
    {
        ClearPendingSeek();
        if (!Controls.LoopMedia)
            _positionNotifier.Stop();

        EventHandler? handler = MediaEnded;
        if (handler != null)
            _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));

        ScheduleAutomaticPreparedNextTransition();
    }

    private void OnControlsMediaStarted(object? sender, EventArgs e)
    {
        EventHandler? handler = MediaStarted;
        if (handler != null)
        {
            try
            {
                handler.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaControlPlayer", $"MediaStarted handler failed: {ex.Message}", innerException: ex);
            }
        }

        _positionNotifier.Start();
    }

    private void OnControlsMediaStopped(object? sender, EventArgs e)
    {
        ClearPendingSeek();
        _positionNotifier.Stop();
        EventHandler? handler = MediaStopped;
        if (handler != null)
            _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));
    }

    private void OnFileMediaLoaded(object? sender, MediaLoadedEventArgs e)
    {
        SubscribeSeekCompletionSources();
        HostEventDispatcher.Raise(this, MediaLoaded, e, nameof(MediaLoaded));
        if (!Controls.ReportCurrentState())
            QueuePositionSnapshotAfter(Task.CompletedTask);
    }

    private void OnFileMediaClosed(object? sender, MediaClosedEventArgs e)
    {
        _positionNotifier.Stop();
        ClearPendingSeek();
        UnsubscribeSeekCompletionSources();
        HostEventDispatcher.Raise(this, MediaClosed, e, nameof(MediaClosed));
        if (!Controls.ReportCurrentState())
            QueuePositionSnapshotAfter(Task.CompletedTask);
    }

    private void OnControlsMediaPaused(object? sender, MediaPausedEventArgs e)
    {
        _positionNotifier.Stop();
        HostEventDispatcher.Raise(this, MediaPaused, e, nameof(MediaPaused));
    }

    private void OnControlsMediaLooped(object? sender, MediaLoopedEventArgs e)
    {
        Task delivery = _controlOperations.Enqueue(() =>
            HostEventDispatcher.Raise(this, MediaLooped, e, nameof(MediaLooped)));
        QueuePositionSnapshotAfter(delivery);
    }

    private void OnControlsStateChanged(object? sender, MediaStateChangedEventArgs e)
    {
        Task delivery = _controlOperations.Enqueue(() =>
            HostEventDispatcher.Raise(this, StateChanged, e, nameof(StateChanged)));
        if (e.CurrentState is MediaPlayerState.Stopped or MediaPlayerState.Null)
            QueuePositionSnapshotAfter(delivery);
    }

    private void OnPositionNotifierChanged(object? sender, MediaPositionChangedEventArgs e) =>
        HostEventDispatcher.Raise(this, PositionChanged, e, nameof(PositionChanged));

    private void OnEffectsVolumeChanged(object? sender, MediaValueChangedEventArgs<double> e) =>
        HostEventDispatcher.Raise(this, VolumeChanged, e, nameof(VolumeChanged));

    private void OnEffectsPitchChanged(object? sender, MediaValueChangedEventArgs<int> e) =>
        HostEventDispatcher.Raise(this, PitchChanged, e, nameof(PitchChanged));

    private void OnEffectsTempoChanged(object? sender, MediaValueChangedEventArgs<int> e) =>
        HostEventDispatcher.Raise(this, TempoChanged, e, nameof(TempoChanged));

    private void OnEffectsNormalizeChanged(object? sender, MediaNormalizeChangedEventArgs e) =>
        HostEventDispatcher.Raise(this, NormalizeChanged, e, nameof(NormalizeChanged));

    private void OnEffectsEqualizerChanged(object? sender, MediaValueChangedEventArgs<AudioEqualizerSettings> e) =>
        HostEventDispatcher.Raise(this, EqualizerChanged, e, nameof(EqualizerChanged));

    private void OnControlsLoopMediaChanged(object? sender, MediaValueChangedEventArgs<bool> e) =>
        HostEventDispatcher.Raise(this, LoopMediaChanged, e, nameof(LoopMediaChanged));

    private void OnPresentationFramePresented(object? sender, EventArgs e) => PresentationFramePresented?.Invoke(this, EventArgs.Empty);

    private void OnEngineErrorOccurred(MediaErrorInfo info)
    {
        EventHandler<MediaErrorEventArgs>? handler = MediaError;
        if (handler == null)
            return;

        var args = new MediaErrorEventArgs(info.Code, info.Message, info.SourcePath, info.InnerException);
        _ = _controlOperations.Enqueue(() => handler.Invoke(this, args));
    }

    private void OnDiagnosticOccurred(MediaDiagnosticInfo info)
    {
        EventHandler<MediaDiagnosticEventArgs>? handler = MediaDiagnostic;
        if (handler == null)
            return;

        var args = new MediaDiagnosticEventArgs(info.Level, info.Category, info.Message, info.SourcePath, info.InnerException);
        _ = _controlOperations.Enqueue(() => handler.Invoke(this, args));
    }

    private void OnAudioDeviceMonitorChanged(object? sender, AudioDeviceChangeInfo info) =>
        _ = _controlOperations.Enqueue(() => RaiseAudioDeviceChangeNotice(info));

    private void RaiseAudioDeviceChangeNotice(AudioDeviceChangeInfo info)
    {
        if (_disposed)
            return;

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "AudioDeviceMonitor",
            info.FailureMessage is { Length: > 0 }
                ? info.FailureMessage
                : $"Audio device change detected: kind={info.Kind}, name={info.DeviceName ?? "(unknown)"}; leaving playback under BASS/Windows control.",
            innerException: null);

        try
        {
            EventHandler<AudioDeviceChangedEventArgs>? handler = AudioDeviceChanged;
            if (handler != null)
            {
                var args = new AudioDeviceChangedEventArgs(info);
                handler.Invoke(this, args);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] AudioDeviceChanged dispatch failed: {ex.Message}");
        }
    }

    private PlaybackPositionNotifier CreatePositionNotifier()
    {
        var notifier = new PlaybackPositionNotifier(
            () => _mediaEngine.Position,
            () => _mediaEngine.Duration);
        notifier.PositionChanged += OnPositionNotifierChanged;
        return notifier;
    }

    private void SubscribeNewSessionEvents()
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
    }

    private void UnsubscribeNewSessionEvents()
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
    }

    private void SubscribeSeekCompletionSources()
    {
        UnsubscribeSeekCompletionSources();
        _seekVideoEngine = _mediaEngine.Video;
        _seekCdgEngine = _mediaEngine.Cdg;
        if (_seekVideoEngine != null)
            _seekVideoEngine.PositionChanged += OnSeekVideoPositionChanged;
        if (_seekCdgEngine != null)
            _seekCdgEngine.FrameInvalidated += OnSeekCdgFrameInvalidated;
    }

    private void UnsubscribeSeekCompletionSources()
    {
        if (_seekVideoEngine != null)
            _seekVideoEngine.PositionChanged -= OnSeekVideoPositionChanged;
        if (_seekCdgEngine != null)
            _seekCdgEngine.FrameInvalidated -= OnSeekCdgFrameInvalidated;
        _seekVideoEngine = null;
        _seekCdgEngine = null;
    }

    private void OnSeekVideoPositionChanged(object? sender, double position) =>
        TryCompletePendingSeek(position);

    private void OnSeekCdgFrameInvalidated(object? sender, EventArgs e) =>
        TryCompletePendingSeek(_mediaEngine.Position);

    private void TryCompletePendingSeek(
        double resultingPosition,
        double toleranceSeconds = SeekSettleToleranceSeconds)
    {
        if (Volatile.Read(ref _hasPendingSeek) == 0)
            return;

        PendingSeek? pending;
        lock (_seekCompletionGate)
        {
            pending = _pendingSeek;
            if (pending == null || Math.Abs(CleanPosition(resultingPosition) - pending.Value.TargetSeconds) > toleranceSeconds)
                return;

            _pendingSeek = null;
            Volatile.Write(ref _hasPendingSeek, 0);
        }

        _ = _controlOperations.Enqueue(() =>
            RaiseSeekCompleted(pending.Value.RequestedSeconds, resultingPosition));
    }

    private void RaiseSeekCompleted(double requestedSeconds, double resultingSeconds)
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

    private void QueuePositionSnapshotAfter(Task prerequisite) =>
        _ = PublishPositionSnapshotAfterAsync(prerequisite);

    private async Task PublishPositionSnapshotAfterAsync(Task prerequisite)
    {
        try
        {
            await prerequisite.ConfigureAwait(false);
        }
        catch
        {
            // Position delivery is independent of lifecycle handler failures.
        }

        await Task.Yield();
        _positionNotifier.PublishNow();
    }

    private void ClearPendingSeek()
    {
        lock (_seekCompletionGate)
        {
            _pendingSeek = null;
            Volatile.Write(ref _hasPendingSeek, 0);
        }
    }

    private static double CleanPosition(double seconds) =>
        double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
