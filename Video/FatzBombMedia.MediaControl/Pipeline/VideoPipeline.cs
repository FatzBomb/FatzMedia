
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Audio;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Threading;
using FatzBombMedia.MediaControl.Core.Video;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed partial class VideoPipeline(PlaybackClock clock, MediaState state, MediaPlayerConfiguration configuration) : IVideoEngine
{
    private readonly PlaybackClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly MediaState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly MediaPlayerConfiguration _configuration = configuration;
    private readonly AudioPresentationClock _presentationClock = new();
    private readonly Lock _decodeLock = new();
    private readonly PlaybackScheduler _decodeScheduler = new("VideoDecode");
    private readonly PlaybackScheduler _presentationScheduler = new("VideoPresentation");
    private VideoDecoder? _videoDecoder;
    private VideoAudioEngine? _audioEngine;
    private readonly Lock _presentationLock = new();
    private VideoFrameEventArgs? _loopStartFrameSnapshot;
    private double? _overrideRefreshHz;
    private VideoFrameData? _currentFrame;
    private volatile bool _isOpen;
    private volatile bool _isPlaying;
    private volatile bool _hostRenderEventDrivenPresentation;
    private volatile int _generation;

    // Seek calls are accepted as fast as the host sends them, but the pipeline processes them with
    // a latest-wins pump. The first request in a burst runs immediately; further requests only
    // replace the pending target until the pump interval elapses. This keeps progress-bar scrubbing
    // responsive without requiring host apps to understand mouse down/up, preview, or final commit.
    private readonly Lock _transportGate = new();
    private readonly Lock _seekPumpLock = new();
    private readonly AutoResetEvent _seekSignal = new(false);
    private Thread? _seekThread;
    private volatile bool _seekWorkerShutdown;
    private double? _pendingSeekSeconds;
    private double _activeSeekTargetSeconds = double.NaN;
    private int _playbackEndSignaled;
    private string? _tempPath;
    private const int MaxBufferedVideoFrames = 12;
    private const int MinBufferedVideoFrames = 4;
    private const long MaxBufferedVideoBytes = 96L * 1024 * 1024;
    private const int MaxVideoPacketsPerDecodePass = 3;
    private const int PresentationDiagnosticsFrameInterval = 120;
    private const double DefaultPresentationLeadRefreshIntervals = 0.75;
    private const double MinimumPresentationLeadRefreshIntervals = 0.50;
    private const double MaximumPresentationLeadRefreshIntervals = 1.25;
    private const double PresentationLeadIncreaseStepRefreshIntervals = 0.125;
    private const double PresentationLeadDecreaseStepRefreshIntervals = 0.0625;
    private const double PresentationLeadClockLateIncreaseThresholdRefreshes = 0.65;
    private const double PresentationLeadEarlyDecreaseThresholdRefreshes = 1.75;
    private const double PresentationLeadGoodClockLateThresholdRefreshes = 0.35;
    private const int PresentationLeadLateSampleIncreaseThreshold = 8;
    private const int PostSeekRecoveryMilliseconds = 450;
    private const int PostSeekUnderfilledRecoveryMilliseconds = 700;
    private const int PostSeekEmptyQueueRecoveryMilliseconds = 900;
    private const int PostSeekWarmupDecodeBudgetMilliseconds = 260;
    private const int PostSeekWarmupRetryBudgetMilliseconds = 220;
    private const int PostSeekMinimumBufferedFrames = 3;
    private const int PostSeekAdaptiveSuppressionWindows = 2;
    private double _adaptiveVideoSeekProcessingMilliseconds;
    private double _minimumCommittedVideoPresentationSeconds = double.NaN;
    private const int DefaultVideoSeekIntervalMilliseconds = 75;
    private const int MinVideoSeekIntervalMilliseconds = 75;
    private const int MaxVideoSeekIntervalMilliseconds = 300;
    private const double VideoSeekIntervalMultiplier = 1.5;
    private const double VideoSeekIntervalPaddingMilliseconds = 25.0;
    private const double VideoSeekProcessingEmaPreviousWeight = 0.75;
    private const double VideoSeekProcessingEmaCurrentWeight = 0.25;
    private const double VideoSeekTargetChangeThresholdSeconds = 0.025;
    private const double MaxPresentLatenessSeconds = 0.18;
    private const double TinyVideoSeekThresholdSeconds = 0.25;
    private const double PreviewMaxFrameDistanceSeconds = 2.0;
    private const double SettleMaxFrameDistanceSeconds = 0.50;
    private const int HardwarePreviewDecodeBudgetMilliseconds = 85;
    private const int HardwareSettleDecodeBudgetMilliseconds = 125;
    private const int SoftwarePreviewDecodeBudgetMilliseconds = 65;
    private const int SoftwareSettleDecodeBudgetMilliseconds = 95;
    private const int HardwarePreviewDecodeMaxAttempts = 32;
    private const int HardwareSettleDecodeMaxAttempts = 72;
    private const int SoftwarePreviewDecodeMaxAttempts = 20;
    private const int SoftwareSettleDecodeMaxAttempts = 44;
    private int _presentationDiagnosticFrames;
    private int _presentationDiagnosticDroppedFrames;
    private int _presentationDiagnosticSelectionSkippedFrames;
    private int _presentationDiagnosticLateDroppedFrames;
    private double _presentationDiagnosticMaxClockLateMs;
    private double _presentationDiagnosticMaxTargetLateMs;
    private double _presentationDiagnosticMaxEarlyMs;
    private double _presentationDiagnosticLeadMs;
    private int _presentationDiagnosticClockLateSamples;
    private double _activePresentationLeadRefreshIntervals = double.NaN;
    private long _postSeekRecoveryUntilTimestamp;
    private int _postSeekAdaptiveSuppressionWindowsRemaining;

    public IAudioEngine? Audio => _audioEngine;
    public bool HasAudio => _audioEngine?.IsOpen == true;
    public bool LoopPlayback { get; set; }
    public bool IsOpen => _isOpen;
    public double Duration { get; private set; }
    public double FramesPerSecond => _videoDecoder?.FramesPerSecond ?? 0;
    public int Width => _videoDecoder?.Width ?? 0;
    public int Height => _videoDecoder?.Height ?? 0;
    public string? CodecName => _videoDecoder?.CodecName;
    public bool IsHardwareAccelerated => _videoDecoder?.IsHardwareAccelerated == true;
    public string? HardwareAccelerationMethod => _videoDecoder?.HardwareAccelerationMethod;


    public void SetDisplayRefreshRate(double? refreshHz) => _overrideRefreshHz = refreshHz is > 0 ? refreshHz : null;

    public void SetHostRenderEventDrivenPresentation(bool enabled)
    {
        if (_hostRenderEventDrivenPresentation == enabled)
        {
            return;
        }

        _hostRenderEventDrivenPresentation = enabled;

        if (!_isOpen)
        {
            return;
        }

        if (enabled)
        {
            StopPresentationLoop();
            ResetPresentationDiagnostics();
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine("[VideoPipeline] Host render-event presentation now drives UI-backed video presentation.");
            }
        }
        else if (_isPlaying)
        {
            ResetPresentationDiagnostics();
            StartPresentationLoop();
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine("[VideoPipeline] Independent video presentation loop enabled.");
            }
        }
    }

    public double Position
    {
        get => HasAudio ? _audioEngine!.Position : _clock.PositionSeconds;
        set => Seek(value);
    }

    public event EventHandler<VideoFrameEventArgs>? FrameReady;
    public event EventHandler<VideoEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;

    public bool Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        Close();
        _generation++;
        _loopStartFrameSnapshot = null;
        _videoDecoder = VideoDecoder.FromConfiguration(_configuration);
        if (!_videoDecoder.Open(path))
        {
            Close();
            return false;
        }

        _audioEngine = new VideoAudioEngine();
        if (!_audioEngine.Open(path))
        {
            _audioEngine.Dispose();
            _audioEngine = null;
            if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] Opened video without audio stream; using playback clock.");
        }

        Duration = Math.Max(_videoDecoder.DurationSeconds, _audioEngine?.Duration ?? 0);
        _clock.Stop();
        _clock.DurationSeconds = Duration;
        _presentationClock.Reset(0);
        ResetPresentationTimingController();
        _isOpen = true;
        _isPlaying = false;
        _state.SetOpened(MediaContent.Video | (HasAudio ? MediaContent.Audio : 0), path, Duration);
        RaiseState(VideoEngineState.Ready);
        Opened?.Invoke(this, EventArgs.Empty);
        lock (_decodeLock)
        {
            if (!DecodeUntilFrameAvailable())
                _ = TryFallbackToSoftwareVideo(path);

            _ = PresentFrameForCurrentPosition();
        }

        ResetAdaptiveVideoSeekTiming();
        StartSeekWorker();
        return true;
    }

    private bool OpenBytes(byte[] data)
    {
        var tempPath = CreateTempMediaPath(data);
        var success = Open(tempPath);
        if (success)
            _tempPath = tempPath;
        else
            TryDeleteTempPath(tempPath);

        return success;
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);
        using var ms = new MemoryStream((int)Math.Min(archiveEntry.Size ?? 0, int.MaxValue));
        archiveEntry.WriteTo(ms);
        return OpenBytes(ms.ToArray());
    }

    public bool Play()
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            if (_isPlaying)
                return true;

            _ = TryPrepareLoopRestartAtBeginning();

            if (HasAudio)
            {
                if (!_audioEngine!.Play() && _configuration.EnableLogging)
                    Debug.WriteLine("[VideoPipeline] audio Play() returned false during transport start");
                _presentationClock.Start(_audioEngine.Position);
                if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] Playback started using BASS video-audio clock.");
            }
            else
            {
                _clock.Start(_clock.PositionSeconds);
                if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] Playback started using virtual video clock.");
            }

            StartDecodeLoop();
            if (!_hostRenderEventDrivenPresentation)
                StartPresentationLoop();
            _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);
            _isPlaying = true;
            _state.SetPlaybackState(MediaPlayerState.Playing);
            RaiseState(VideoEngineState.Playing);
            return true;
        }
    }

    private bool TryPrepareLoopRestartAtBeginning()
    {
        if (Duration <= 0)
            return false;

        var currentPosition = GetCurrentTransportPosition();
        return currentPosition >= Duration - GetEndToleranceSeconds() && TryRestartLoopPlayback(currentPosition, "transport start");
    }

    private double GetCurrentTransportPosition() => HasAudio ? _audioEngine!.Position : _clock.PositionSeconds;

    private bool TryRestartLoopPlayback(double currentPosition, string reason)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null || Duration <= 0)
            return false;

        var loopBoundary = GetLoopBoundarySeconds(decoder);
        var loopTarget = GetLoopOverflowPosition(currentPosition, loopBoundary);

        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = null;
            _minimumCommittedVideoPresentationSeconds = double.NaN;
        }

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine(
                $"[VideoPipeline:LoopRestart] {reason}: current={currentPosition:F3}s, boundary={loopBoundary:F3}s, target={loopTarget:F3}s, " +
                $"duration={Duration:F3}s, eof={decoder.EndOfStream}, queued={decoder.FrameCount}, fps={FramesPerSecond:F2}, " +
                $"hw={IsHardwareAccelerated}, codec={CodecName}");

        var presentedSnapshot = TryPresentLoopStartSnapshot(loopTarget);
        VideoFrameData? firstLoopFrame;
        lock (_decodeLock)
        {
            // Keep the retained current frame alive until a replacement is presented. Releasing it
            // before seek can blank hardware-backed presentation surfaces at loop boundaries.
            if (!decoder.Seek(loopTarget, exact: false))
            {
                if (_configuration.EnableLogging)
                    Debug.WriteLine("[VideoPipeline:LoopRestart] Decoder seek failed.");

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", "Decoder seek failed during loop restart");
                return false;
            }

            _ = DecodeUntilFrameAvailable();
            firstLoopFrame = decoder.GetNextFrame();
        }

        if (firstLoopFrame != null)
            PresentFrame(firstLoopFrame, forceReissue: true);

        if (HasAudio)
        {
            _ = _audioEngine!.Seek(loopTarget, resumePlayback: false);
            if (_isPlaying)
            {
                if (!_audioEngine.Play() && _configuration.EnableLogging)
                    Debug.WriteLine("[VideoPipeline:LoopRestart] audio Play() returned false during loop restart");

                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", "Audio Play() returned false during loop restart");
                _presentationClock.Start(_audioEngine.Position);
            }
            else
                _presentationClock.Reset(loopTarget);
        }
        else
        {
            if (_isPlaying)
                _clock.Start(loopTarget);
            else
                _clock.Seek(loopTarget);
        }

        BeginPostSeekPresentationRecovery();
        _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);
        _state.SetPosition(loopTarget);
        PositionChanged?.Invoke(this, loopTarget);
        if (firstLoopFrame == null && !presentedSnapshot)
            _ = PresentFrameForCurrentPosition(forceReissue: true);

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine(
                $"[VideoPipeline:LoopRestart] Restart prepared; queued={decoder.FrameCount}/{GetTargetBufferedVideoFrames()}, " +
                $"snapshot={presentedSnapshot}, decodedFirst={firstLoopFrame != null}.");

        return true;
    }

    public bool Pause()
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            _isPlaying = false;
            StopDecodeLoopAndWait();
            StopPresentationLoopAndWait();
            WaitForPresentationIdle();
            if (HasAudio)
            {
                _ = _audioEngine!.Pause();
                _presentationClock.Pause(_audioEngine.Position);
            }
            else
                _clock.Pause();

            _state.SetPlaybackState(MediaPlayerState.Paused);
            RaiseState(VideoEngineState.Paused);
            return true;
        }
    }

    public bool Stop()
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            _isPlaying = false;
            StopDecodeLoopAndWait();
            StopPresentationLoopAndWait();
            WaitForPresentationIdle();
            _ = (_audioEngine?.Stop());
            _clock.Stop();
            _presentationClock.Reset(0);
            lock (_decodeLock)
            {
                ReleaseCurrentFrame();
                _ = (_videoDecoder?.Seek(0));
                _ = DecodeUntilFrameAvailable();
                lock (_presentationLock)
                    _ = PresentFrameForCurrentPosition();
            }

            _state.SetPosition(0);
            _state.SetPlaybackState(MediaPlayerState.Stopped);
            RaiseState(VideoEngineState.Stopped);
            return true;
        }
    }

    /// <summary>
    /// Requests a seek to <paramref name="seconds"/>. This is non-blocking: the actual seek runs on
    /// a dedicated background worker that always honors the most recent target, so dragging a scrub
    /// slider never stalls the calling (UI) thread. Returns true if the request was accepted.
    /// </summary>
    /// <remarks>
    /// Because the seek now runs off the caller's thread, <see cref="PositionChanged"/> (and other
    /// playback events) may be raised on a background thread for seeks, just as they already can be
    /// during playback. Handlers that update UI must marshal to the appropriate UI thread.
    /// </remarks>
    public bool Seek(double seconds)
    {
        if (!_isOpen)
            return false;

        var target = NormalizeSeekTarget(seconds);
        lock (_seekPumpLock)
            _pendingSeekSeconds = target;

        _ = _seekSignal.Set();
        return true;
    }

    /// <summary>
    /// Performs the actual seek. Runs on the seek worker thread and is serialized against the other
    /// transport operations by <see cref="_transportGate"/> so pipeline state is never mutated
    /// concurrently.
    /// </summary>
    private void SeekCore(double seconds, bool asPreview)
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return;

            var target = NormalizeSeekTarget(seconds);
            SetActiveSeekTarget(target);
            try
            {
                var wasPlaying = _isPlaying;
                if (!asPreview)
                    SetMinimumCommittedVideoPresentationTarget(target);

                if (target < Duration - 0.05)
                    _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);

                _generation++;
                StopDecodeLoopAndWait();
                StopPresentationLoopAndWait();
                WaitForPresentationIdle();

                if (HasAudio)
                {
                    _ = _audioEngine!.Seek(target, resumePlayback: false);
                    _presentationClock.Reset(target);
                }
                else
                {
                    if (wasPlaying)
                        _clock.Pause();

                    _clock.Seek(target);
                }

                var videoSeekProcessingStopwatch = Stopwatch.StartNew();
                lock (_decodeLock)
                    _ = SeekVideoNearTarget(target, asPreview);

                videoSeekProcessingStopwatch.Stop();
                RecordAdaptiveVideoSeekProcessingTime(videoSeekProcessingStopwatch.Elapsed);

                if (HasSupersedingSeekRequest())
                    return;

                _state.SetPosition(target);
                PositionChanged?.Invoke(this, target);

                if (asPreview)
                    return;

                PreparePostSeekVideoRecovery(target);

                if (HasAudio && wasPlaying)
                {
                    _ = _audioEngine!.PrebufferAudio();
                    if (!_audioEngine.Play() && _configuration.EnableLogging)
                        Debug.WriteLine("[VideoPipeline] audio Play() returned false during post-seek resume");
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", "Audio Play() returned false during post-seek resume");
                    _presentationClock.Start(_audioEngine.Position);
                }
                else if (!HasAudio && wasPlaying)
                    _clock.Start(target);

                if (wasPlaying)
                {
                    BeginPostSeekPresentationRecovery();
                    StartDecodeLoop();
                    if (!_hostRenderEventDrivenPresentation)
                        StartPresentationLoop();
                }
            }
            finally
            {
                ClearActiveSeekTarget(target);
            }
        }
    }


    private void StartSeekWorker()
    {
        StopSeekWorker();
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = null;
            _activeSeekTargetSeconds = double.NaN;
            _minimumCommittedVideoPresentationSeconds = double.NaN;
        }

        _seekWorkerShutdown = false;
        _seekThread = new Thread(SeekWorkerLoop)
        {
            IsBackground = true,
            Name = "VideoSeek"
        };
        _seekThread.Start();
    }

    private void StopSeekWorker()
    {
        Thread? thread = _seekThread;
        if (thread == null)
            return;

        // Signal and join without holding _transportGate: an in-flight SeekCore holds that gate, so
        // joining here lets it finish, and Close re-acquires the gate afterwards before disposing.
        _seekWorkerShutdown = true;
        _ = _seekSignal.Set();
        try
        {
            _ = thread.Join(2000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoPipeline] Seek worker join failed: {ex.Message}");
        }

        _seekThread = null;
        lock (_seekPumpLock)
        {
            _pendingSeekSeconds = null;
            _activeSeekTargetSeconds = double.NaN;
            _minimumCommittedVideoPresentationSeconds = double.NaN;
        }
    }

    private void SeekWorkerLoop()
    {
        while (!_seekWorkerShutdown)
        {
            _ = _seekSignal.WaitOne();

            var lastPreviewTarget = double.NaN;
            while (!_seekWorkerShutdown)
            {
                if (!TryTakePendingSeek(out var target))
                    break;

                var normalizedTarget = NormalizeSeekTarget(target);
                if (!IsMeaningfullyDifferentSeekTarget(normalizedTarget, lastPreviewTarget))
                {
                    if (!HasPendingSeekRequest())
                    {
                        SettleSeekTarget(normalizedTarget);
                        break;
                    }

                    continue;
                }

                // Process the active drag/click target as a preview first. Preview mode is allowed
                // to present a nearby frame quickly, so scrubbing gives visible feedback even on
                // long-GOP or software-decoded media. When the pump goes quiet, we settle the same
                // latest target with Balanced mode so playback resumes from the requested timeline
                // rather than from the nearby preview frame.
                try
                {
                    SeekCore(normalizedTarget, asPreview: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VideoPipeline] Seek preview pump error: {ex.Message}");
                }

                lastPreviewTarget = normalizedTarget;
                WaitForSeekPumpInterval();
                if (!HasPendingSeekRequest())
                {
                    SettleSeekTarget(normalizedTarget);
                    break;
                }
            }
        }
    }

    private void SettleSeekTarget(double target)
    {
        if (_seekWorkerShutdown || HasSupersedingSeekRequest())
            return;

        try
        {
            SeekCore(target, asPreview: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoPipeline] Seek settle pump error: {ex.Message}");
        }
    }

    private bool TryTakePendingSeek(out double target)
    {
        lock (_seekPumpLock)
        {
            if (!_pendingSeekSeconds.HasValue)
            {
                target = 0;
                return false;
            }

            target = _pendingSeekSeconds.Value;
            _pendingSeekSeconds = null;
            return true;
        }
    }

    private void SetActiveSeekTarget(double target)
    {
        lock (_seekPumpLock)
            _activeSeekTargetSeconds = target;
    }

    private void ClearActiveSeekTarget(double target)
    {
        lock (_seekPumpLock)
            if (!double.IsNaN(_activeSeekTargetSeconds) && Math.Abs(_activeSeekTargetSeconds - target) <= VideoSeekTargetChangeThresholdSeconds)
                _activeSeekTargetSeconds = double.NaN;
    }

    private void SetMinimumCommittedVideoPresentationTarget(double target)
    {
        lock (_seekPumpLock)
            _minimumCommittedVideoPresentationSeconds = target;
    }

    private double GetMinimumCommittedVideoPresentationTarget()
    {
        lock (_seekPumpLock)
            return _minimumCommittedVideoPresentationSeconds;
    }

    private bool HasSupersedingSeekRequest()
    {
        if (_seekWorkerShutdown)
            return true;

        lock (_seekPumpLock)
            return _pendingSeekSeconds.HasValue && (double.IsNaN(_activeSeekTargetSeconds)
                || IsMeaningfullyDifferentSeekTarget(_pendingSeekSeconds.Value, _activeSeekTargetSeconds));
    }

    private bool HasPendingSeekRequest()
    {
        lock (_seekPumpLock)
            return _pendingSeekSeconds.HasValue;
    }

    private double NormalizeSeekTarget(double seconds) =>
        !double.IsFinite(seconds) || seconds <= 0 ? 0 : Duration > 0 ? Math.Clamp(seconds, 0, Duration) : seconds;

    private static bool IsMeaningfullyDifferentSeekTarget(double target, double previousTarget) =>
        double.IsNaN(previousTarget) || Math.Abs(target - previousTarget) > VideoSeekTargetChangeThresholdSeconds;

    private void WaitForSeekPumpInterval()
    {
        var intervalMilliseconds = GetAdaptiveVideoSeekIntervalMilliseconds();
        if (intervalMilliseconds <= 0)
            return;

        var started = Stopwatch.GetTimestamp();
        while (!_seekWorkerShutdown && GetElapsedMilliseconds(started, Stopwatch.GetTimestamp()) < intervalMilliseconds)
            _ = _seekSignal.WaitOne(TimeSpan.FromMilliseconds(10));
    }

    private int GetAdaptiveVideoSeekIntervalMilliseconds()
    {
        double measuredMilliseconds;
        lock (_seekPumpLock)
            measuredMilliseconds = _adaptiveVideoSeekProcessingMilliseconds;

        if (measuredMilliseconds <= 0 || !double.IsFinite(measuredMilliseconds))
            return DefaultVideoSeekIntervalMilliseconds;

        var intervalMilliseconds = (measuredMilliseconds * VideoSeekIntervalMultiplier)
            + VideoSeekIntervalPaddingMilliseconds;

        return (int)Math.Round(Math.Clamp(
            intervalMilliseconds,
            MinVideoSeekIntervalMilliseconds,
            MaxVideoSeekIntervalMilliseconds));
    }

    private void RecordAdaptiveVideoSeekProcessingTime(TimeSpan elapsed)
    {
        var elapsedMilliseconds = elapsed.TotalMilliseconds;
        if (elapsedMilliseconds <= 0 || !double.IsFinite(elapsedMilliseconds))
            return;

        lock (_seekPumpLock)
            _adaptiveVideoSeekProcessingMilliseconds = _adaptiveVideoSeekProcessingMilliseconds <= 0
                ? elapsedMilliseconds
                : (_adaptiveVideoSeekProcessingMilliseconds * VideoSeekProcessingEmaPreviousWeight) + (elapsedMilliseconds * VideoSeekProcessingEmaCurrentWeight);
    }

    private void ResetAdaptiveVideoSeekTiming()
    {
        lock (_seekPumpLock)
        {
            _adaptiveVideoSeekProcessingMilliseconds = 0;
            _minimumCommittedVideoPresentationSeconds = double.NaN;
        }
    }

    private static double GetElapsedMilliseconds(long startTimestamp, long endTimestamp) => (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void Close()
    {
        // Retire the seek worker first (without holding the gate) so no seek can be in flight when
        // we dispose the decoder below. StopSeekWorker joins the worker; the gate then guarantees
        // exclusivity for the teardown itself.
        StopSeekWorker();

        lock (_transportGate)
        {
            _generation++;
            _isPlaying = false;
            _isOpen = false;
            StopDecodeLoopAndWait();
            StopPresentationLoopAndWait();
            WaitForPresentationIdle();

            _audioEngine?.Dispose();
            _audioEngine = null;

            lock (_decodeLock)
            {
                ReleaseCurrentFrame();
                _videoDecoder?.Dispose();
                _videoDecoder = null;
            }

            _clock.Stop();
            _presentationClock.Reset(0);
            ResetPresentationTimingController();
            Duration = 0;
            _loopStartFrameSnapshot = null;
            CleanupTempPath();
            RaiseState(VideoEngineState.Closed);
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        Close();
        _decodeScheduler.Dispose();
        _presentationScheduler.Dispose();
        _seekSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void RenderCurrentFrame(bool allowWhilePlaying = false, bool forceReissue = false)
    {
        if (!_isOpen || (!allowWhilePlaying && _isPlaying))
            return;

        // During normal playback the background decode loop owns packet/frame filling. Do not
        // decode on the host UI render thread; the shared-surface interop path should only choose and
        // present the newest ready frame during CompositionTarget.Rendering. For paused/stopped
        // preview, it is still useful to decode one frame if the queue is empty.
        if (!_isPlaying)
        {
            if (!_decodeLock.TryEnter())
                return;

            try
            {
                if (!_isOpen || _videoDecoder == null)
                    return;

                _ = DecodeUntilFrameAvailable();
            }
            finally
            {
                _decodeLock.Exit();
            }
        }

        lock (_presentationLock)
        {
            if (!_isOpen || _videoDecoder is null)
                return;

            _ = PresentFrameForCurrentPosition(forceReissue);
        }
    }

    private void StartDecodeLoop()
    {
        StopDecodeLoop();
        var generation = _generation;
        _decodeScheduler.Start(token => DecodeLoop(generation, token));
    }

    private void StopDecodeLoop() => _decodeScheduler.Stop();

    private void StopDecodeLoopAndWait() => _decodeScheduler.StopAndWait();

    /// <summary>
    /// Starts a dedicated presentation thread that selects and pushes video frames on its own
    /// schedule, independent of the host's CompositionTarget.Rendering tick. the host render-event
    /// scheduler does not reliably fire at the true display refresh rate when driving a UI-backed interop surface
    /// surface (observed gaps of 40-90ms+ against an expected ~16.7ms at 60Hz, with no host-level
    /// fix available), so frame presentation timing is handled here instead, on a steady interval
    /// independent of host UI scheduling.
    /// </summary>
    private void StartPresentationLoop()
    {
        StopPresentationLoop();
        var generation = _generation;
        _presentationScheduler.Start(token => PresentationLoop(generation, token));
    }

    private void StopPresentationLoop() =>
        _presentationScheduler.Stop();

    private void StopPresentationLoopAndWait() =>
        _presentationScheduler.StopAndWait();

    private void PresentationLoop(int generation, CancellationToken cancellationToken)
    {
        // Target interval is read fresh each tick so source-FPS or monitor changes take effect
        // on the next tick. Stage 11 paces primary video at source cadence when FPS is known
        // instead of polling at display refresh for 24/30fps content.
        const int diagnosticInterval = 120;
        using var waiter = new PrecisionWaiter();
        var stopwatch = Stopwatch.StartNew();
        var nextTick = stopwatch.Elapsed.TotalSeconds;
        var lastTickSeconds = nextTick;
        var diagnosticTicks = 0;
        var diagnosticMaxGapMs = 0.0;
        var diagnosticSumGapMs = 0.0;
        var lastTargetIntervalSeconds = GetPrimaryPresentationIntervalSeconds();

        if (_configuration.EnableLogging && !waiter.IsHighResolution)
            Trace.WriteLine("[VideoPipeline] High-resolution presentation timer unavailable; using coarse wait fallback.");

        while (!cancellationToken.IsCancellationRequested && generation == _generation)
        {
            var targetIntervalSeconds = GetPrimaryPresentationIntervalSeconds();
            if (Math.Abs(targetIntervalSeconds - lastTargetIntervalSeconds) > 0.0001)
            {
                if (_configuration.EnableLogging)
                {
                    Trace.WriteLine(
                        $"[VideoPipeline] Presentation target interval changed: {lastTargetIntervalSeconds * 1000.0:F2}ms -> " +
                        $"{targetIntervalSeconds * 1000.0:F2}ms (source FPS or refresh rate change detected)");
                }
                lastTargetIntervalSeconds = targetIntervalSeconds;
            }

            var tickNow = stopwatch.Elapsed.TotalSeconds;
            var gapMs = (tickNow - lastTickSeconds) * 1000.0;
            lastTickSeconds = tickNow;
            diagnosticMaxGapMs = Math.Max(diagnosticMaxGapMs, gapMs);
            diagnosticSumGapMs += gapMs;
            diagnosticTicks++;
            if (diagnosticTicks >= diagnosticInterval)
            {
                var targetMs = targetIntervalSeconds * 1000.0;
                if (_configuration.EnablePerformanceLogging
                    && (_configuration.EnableVerboseLogging || diagnosticMaxGapMs > targetMs * 1.75))
                {
                    var displayMs = GetDisplayRefreshIntervalSeconds() * 1000.0;
                    var frameMs = FramesPerSecond > 0 ? 1000.0 / FramesPerSecond : 0.0;
                    var avgGapMs = diagnosticSumGapMs / diagnosticTicks;
                    Trace.WriteLine(
                        $"[VideoPipeline:Loop] avgTickGap={avgGapMs:F2}ms, maxTickGap={diagnosticMaxGapMs:F1}ms, " +
                        $"target={targetMs:F1}ms, sourceFrame={frameMs:F1}ms, displayRefresh={displayMs:F1}ms");
                }
                diagnosticTicks = 0;
                diagnosticMaxGapMs = 0;
                diagnosticSumGapMs = 0;
            }

            if (_isPlaying)
            {
                var restartedLoop = false;
                lock (_presentationLock)
                {
                    if (generation == _generation && _isPlaying)
                    {
                        var currentPosition = PresentFrameForCurrentPosition();
                        if (ShouldRestartLoopPlayback(currentPosition))
                        {
                            restartedLoop = TryRestartLoopPlayback(currentPosition, "presentation eof");
                        }
                        else if (HasReachedPlaybackEnd(currentPosition))
                        {
                            CompletePlaybackAtEnd();
                        }
                    }
                }

                if (restartedLoop)
                    nextTick = stopwatch.Elapsed.TotalSeconds;
            }

            if (IsSourceCadencePlayback(FramesPerSecond))
            {
                var waitCpuStart = stopwatch.Elapsed.TotalSeconds;
                var waitSeconds = GetClockLockedPresentationWaitSeconds(targetIntervalSeconds);
                waitSeconds -= stopwatch.Elapsed.TotalSeconds - waitCpuStart;

                if (waitSeconds <= 0)
                    continue;

                waiter.Wait(TimeSpan.FromSeconds(waitSeconds), cancellationToken.WaitHandle);
                nextTick = stopwatch.Elapsed.TotalSeconds;
                continue;
            }

            nextTick += targetIntervalSeconds;
            var now = stopwatch.Elapsed.TotalSeconds;
            var accumulatorWaitSeconds = nextTick - now;

            if (accumulatorWaitSeconds <= 0)
            {
                // Fell behind (e.g. a slow frame); resync instead of free-running catch-up ticks.
                nextTick = now;
                continue;
            }

            waiter.Wait(TimeSpan.FromSeconds(accumulatorWaitSeconds), cancellationToken.WaitHandle);
        }
    }

    private double GetClockLockedPresentationWaitSeconds(double fallbackIntervalSeconds)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return fallbackIntervalSeconds;

        VideoFrameData? nextFrame = decoder.PeekNextFrame();
        if (nextFrame == null)
            return Math.Min(fallbackIntervalSeconds, 0.005);

        var clockPosition = GetPresentationPosition();
        var selectionLeadSeconds = GetFrameSelectionLeadLimit(decoder.FramesPerSecond);
        var waitSeconds = nextFrame.PresentationTime - selectionLeadSeconds - clockPosition;
        return !double.IsFinite(waitSeconds)
            ? fallbackIntervalSeconds
            : waitSeconds <= 0 ? 0 : Math.Min(waitSeconds, Math.Max(0.001, fallbackIntervalSeconds));
    }

    private double GetPrimaryPresentationIntervalSeconds() =>
        VideoPresentationTiming.GetPrimaryPresentationInterval(FramesPerSecond, GetDisplayRefreshIntervalSeconds());

    /// <summary>
    /// Resolves the presentation tick interval: prefers an explicitly supplied monitor refresh
    /// rate (see SetDisplayRefreshRate), since DWM's global composition timing does not reliably
    /// reflect any one specific monitor on multi-monitor, mixed-refresh-rate setups. Falls back
    /// to DWM auto-detection, then to 60Hz, if no override has been supplied.
    /// </summary>
    private double GetDisplayRefreshIntervalSeconds() =>
        _overrideRefreshHz is { } overrideHz && double.IsFinite(overrideHz) && overrideHz > 0
            ? 1.0 / overrideHz
            : GetDwmDisplayRefreshIntervalSeconds();

    /// <summary>
    /// Queries DWM's global composition refresh interval as a fallback when no explicit
    /// per-monitor refresh rate has been supplied. Falls back to 60Hz if the query fails,
    /// returns implausible values, or this is a multi-monitor setup where the global DWM
    /// value may not match the monitor actually displaying this video.
    /// </summary>
    private static double GetDwmDisplayRefreshIntervalSeconds()
    {
        const double fallbackSeconds = 1.0 / 60.0;

        try
        {
            var timingInfo = new DWM_TIMING_INFO
            {
                cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>()
            };

            if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref timingInfo) != 0)
                return fallbackSeconds;

            if (timingInfo.rateRefresh.uiDenominator == 0 || timingInfo.rateRefresh.uiNumerator == 0)
                return fallbackSeconds;

            var refreshHz = (double)timingInfo.rateRefresh.uiNumerator / timingInfo.rateRefresh.uiDenominator;

            // Guard against bogus values from virtual displays/RDP sessions or driver quirks.
            return !double.IsFinite(refreshHz) || refreshHz < 23 || refreshHz > 600 ? fallbackSeconds : 1.0 / refreshHz;
        }
        catch
        {
            return fallbackSeconds;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNSIGNED_RATIO
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public ulong qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

    private void DecodeLoop(int generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && generation == _generation)
        {
            var decoded = false;
            decoded |= DecodeVideoBatch();

            if (_audioEngine is not null)
                decoded |= _audioEngine.DecodeAndPushAudio();

            _ = cancellationToken.WaitHandle.WaitOne(decoded ? 1 : 10);
        }
    }

    private bool DecodeVideoBatch()
    {
        var decoded = false;
        var packets = 0;

        while (packets++ < MaxVideoPacketsPerDecodePass)
        {
            lock (_decodeLock)
            {
                var targetFrames = GetTargetBufferedVideoFrames();
                if (_videoDecoder == null || _videoDecoder.FrameCount >= targetFrames)
                    return decoded;

                decoded |= _videoDecoder.DecodeNextFrame();
            }

            _ = Thread.Yield();
        }

        return decoded;
    }

    private bool DecodeUntilFrameAvailable(Func<bool>? shouldAbort = null)
    {
        if (_videoDecoder == null)
            return false;

        var attempts = 0;
        while (_videoDecoder.FrameCount == 0 && attempts++ < 240)
        {
            if (shouldAbort?.Invoke() == true)
                return false;

            _ = _videoDecoder.DecodeNextFrame();
        }

        return _videoDecoder.FrameCount > 0;
    }

    private bool TryFallbackToSoftwareVideo(string path)
    {
        if (_videoDecoder == null || string.IsNullOrWhiteSpace(_videoDecoder.HardwareAccelerationMethod))
            return false;

        if (_configuration.EnableLogging) Debug.WriteLine($"[VideoPipeline] Hardware video decode produced no usable startup frame; falling back to software for {Path.GetFileName(path)}.");
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", $"Hardware video decode produced no usable startup frame; falling back to software for {Path.GetFileName(path)}", sourcePath: path);
        _videoDecoder.Dispose();
        _videoDecoder = new VideoDecoder(VideoDecodingMethod.Software, true, _configuration.EnableLogging, _configuration.EnablePerformanceLogging, _configuration.EnableVerboseLogging);
        if (!_videoDecoder.Open(path))
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] Software video fallback failed.");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoPipeline", $"Software video fallback failed for {Path.GetFileName(path)}", sourcePath: path);
            _videoDecoder.Dispose();
            _videoDecoder = null;
            return false;
        }

        Duration = Math.Max(_videoDecoder.DurationSeconds, _audioEngine?.Duration ?? 0);
        _clock.DurationSeconds = Duration;
        _presentationClock.Reset(HasAudio ? _audioEngine!.Position : _clock.PositionSeconds);
        return DecodeUntilFrameAvailable();
    }

    private double PresentFrameForCurrentPosition(bool forceReissue = false)
    {
        var clockPosition = GetPresentationPosition();
        var targetPosition = GetDisplayAdjustedPresentationPosition(clockPosition);
        PresentFrameForPosition(targetPosition, clockPosition, forceReissue);
        return clockPosition;
    }

    private double GetPresentationPosition()
    {
        VideoAudioEngine? audioEngine = _audioEngine;
        return audioEngine is not null && audioEngine.TryGetPositionSnapshot(out var audioPosition)
            ? _isPlaying
                ? _presentationClock.GetPosition(audioPosition, Duration)
                : audioPosition
            : _clock.PositionSeconds;
    }

    private double GetDisplayAdjustedPresentationPosition(double clockPosition)
    {
        if (!_isPlaying)
        {
            _presentationDiagnosticLeadMs = 0;
            return clockPosition;
        }

        var leadSeconds = GetPresentationLeadSeconds();
        _presentationDiagnosticLeadMs = leadSeconds * 1000.0;
        if (leadSeconds <= 0)
        {
            return clockPosition;
        }

        var targetPosition = clockPosition + leadSeconds;
        return Duration > 0 ? Math.Min(Duration, targetPosition) : targetPosition;
    }

    private double GetPresentationLeadSeconds()
    {
        var refreshInterval = GetDisplayRefreshIntervalSeconds();
        if (!double.IsFinite(refreshInterval) || refreshInterval <= 0)
            refreshInterval = 1.0 / 60.0;

        var frameDuration = FramesPerSecond > 0 ? 1.0 / FramesPerSecond : 1.0 / 30.0;
        var maxLead = Math.Clamp(frameDuration * 0.45, 0.0, 0.030);
        return Math.Clamp(refreshInterval * GetActivePresentationLeadRefreshIntervals(), 0.0, maxLead);
    }

    private double GetActivePresentationLeadRefreshIntervals()
    {
        var active = _activePresentationLeadRefreshIntervals;
        if (double.IsFinite(active) && active > 0)
            return active;

        active = GetInitialPresentationLeadRefreshIntervals();
        _activePresentationLeadRefreshIntervals = active;
        return active;
    }

    private double GetInitialPresentationLeadRefreshIntervals()
    {
        var configured = _configuration.PresentationLeadRefreshIntervals;
        if (!double.IsFinite(configured) || configured <= 0)
            configured = DefaultPresentationLeadRefreshIntervals;

        return Math.Clamp(configured, MinimumPresentationLeadRefreshIntervals, MaximumPresentationLeadRefreshIntervals);
    }

    private void ResetPresentationTimingController()
    {
        _activePresentationLeadRefreshIntervals = GetInitialPresentationLeadRefreshIntervals();
        _postSeekRecoveryUntilTimestamp = 0;
        _postSeekAdaptiveSuppressionWindowsRemaining = 0;
        ResetPresentationDiagnostics();
    }

    private bool IsPostSeekRecoveryActive()
    {
        var recoveryUntil = Interlocked.Read(ref _postSeekRecoveryUntilTimestamp);
        return recoveryUntil > 0 && Stopwatch.GetTimestamp() < recoveryUntil;
    }

    private void BeginPostSeekPresentationRecovery()
    {
        var bufferedFrames = _videoDecoder?.FrameCount ?? 0;
        var recoveryMilliseconds = bufferedFrames <= 0
            ? PostSeekEmptyQueueRecoveryMilliseconds
            : bufferedFrames < PostSeekMinimumBufferedFrames
                ? PostSeekUnderfilledRecoveryMilliseconds
                : PostSeekRecoveryMilliseconds;

        var until = Stopwatch.GetTimestamp() + (long)(recoveryMilliseconds / 1000.0 * Stopwatch.Frequency);
        _ = Interlocked.Exchange(ref _postSeekRecoveryUntilTimestamp, until);
        _ = Interlocked.Exchange(ref _postSeekAdaptiveSuppressionWindowsRemaining, PostSeekAdaptiveSuppressionWindows);
        ResetPresentationDiagnostics();
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine(
                $"[VideoPipeline:SeekRecovery] Started {recoveryMilliseconds}ms recovery window; " +
                $"buffered={bufferedFrames}/{GetTargetBufferedVideoFrames()}, lead={GetActivePresentationLeadRefreshIntervals():F3} refreshes, " +
                $"adaptiveSuppress={PostSeekAdaptiveSuppressionWindows} windows.");
        }
    }

    private void PreparePostSeekVideoRecovery(double target)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        var targetBufferedFrames = Math.Min(GetTargetBufferedVideoFrames(), Math.Max(PostSeekMinimumBufferedFrames, MinBufferedVideoFrames));
        var decodedFrames = 0;
        int firstPassDecodedFrames;

        lock (_decodeLock)
        {
            firstPassDecodedFrames = DecodePostSeekWarmupFrames(
                decoder,
                targetBufferedFrames,
                PostSeekWarmupDecodeBudgetMilliseconds);

            decodedFrames += firstPassDecodedFrames;

            // A queue of zero after seek is the condition that produced the visible post-seek hitch.
            // Give the decoder a short second chance before audio/presentation resume. This is still
            // bounded so scrub/seek remains responsive, but avoids restarting playback from an empty
            // video queue whenever FFmpeg needs a few more packets after a long-GOP seek.
            if (!HasSupersedingSeekRequest() && decoder.FrameCount < targetBufferedFrames)
            {
                decodedFrames += DecodePostSeekWarmupFrames(
                    decoder,
                    targetBufferedFrames,
                    PostSeekWarmupRetryBudgetMilliseconds);
            }
        }

        if (_configuration.EnableLogging)
        {
            Debug.WriteLine(
                $"[VideoPipeline:SeekRecovery] Warmed video queue after seek to {target:F3}s: " +
                $"decoded={decodedFrames} (firstPass={firstPassDecodedFrames}), queued={decoder.FrameCount}/{targetBufferedFrames}, " +
                $"budget={PostSeekWarmupDecodeBudgetMilliseconds}+{PostSeekWarmupRetryBudgetMilliseconds}ms.");
        }
    }

    private int DecodePostSeekWarmupFrames(VideoDecoder decoder, int targetBufferedFrames, int budgetMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        var decodedFrames = 0;

        while (decoder.FrameCount < targetBufferedFrames && stopwatch.ElapsedMilliseconds < budgetMilliseconds)
        {
            if (HasSupersedingSeekRequest())
                break;

            if (decoder.DecodeNextFrame())
            {
                decodedFrames++;
                continue;
            }

            _ = Thread.Yield();
        }

        return decodedFrames;
    }

    private bool HasReachedNaturalEnd(double position) =>
        _isPlaying && Duration > 0 && position >= Duration - GetEndToleranceSeconds();

    private double GetEndToleranceSeconds() => FramesPerSecond > 0
            ? Math.Clamp(1.0 / FramesPerSecond, 0.02, 0.12)
            : 0.05;

    private bool ShouldRestartLoopPlayback(double currentPosition)
    {
        if (!LoopPlayback || !_isPlaying || Duration <= 0)
            return false;

        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return false;

        var tolerance = GetEndToleranceSeconds();
        var loopBoundary = GetLoopBoundarySeconds(decoder);
        if (decoder.EndOfStream && decoder.FrameCount == 0 && currentPosition >= loopBoundary - tolerance)
            return true;

        // Metadata and EOF signaling can be imperfect. If playback has moved beyond the nominal
        // boundary with no queued frames, restart rather than freezing forever on the retained frame.
        return decoder.FrameCount == 0 && currentPosition >= loopBoundary + tolerance;
    }

    private bool HasReachedPlaybackEnd(double currentPosition)
    {
        if (HasReachedNaturalEnd(currentPosition))
            return true;

        VideoDecoder? decoder = _videoDecoder;
        return decoder != null && decoder.EndOfStream && decoder.FrameCount <= 0 && (!HasAudio || currentPosition >= GetLoopBoundarySeconds(decoder) - GetEndToleranceSeconds());
    }

    private double GetLoopBoundarySeconds(VideoDecoder decoder)
    {
        var videoDuration = decoder.DurationSeconds;
        return double.IsFinite(videoDuration) && videoDuration > 0
            ? Duration > 0 ? Math.Min(Duration, videoDuration) : videoDuration
            : Duration;
    }

    private double GetLoopOverflowPosition(double currentPosition, double loopBoundary)
    {
        if (!double.IsFinite(currentPosition) || !double.IsFinite(loopBoundary) || loopBoundary <= 0)
            return 0;

        var overflow = currentPosition - loopBoundary;
        return overflow <= 0 ? 0 : overflow >= loopBoundary ? overflow % loopBoundary : overflow;
    }

    private void CompletePlaybackAtEnd()
    {
        if (Interlocked.Exchange(ref _playbackEndSignaled, 1) != 0)
            return;

        _isPlaying = false;
        StopDecodeLoop();
        StopPresentationLoop();

        if (HasAudio)
        {
            _ = _audioEngine!.Pause();
            _presentationClock.Pause(Duration);
        }
        else
            _clock.Pause();

        _state.SetPosition(Duration);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        RaiseState(VideoEngineState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private bool SeekVideoNearTarget(double target, bool asPreview)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return false;

        // Video is intentionally the flexible stream during SmartSeek. Audio/CDG/clock state is
        // already moved to the requested target above; video output is allowed to remain frozen if
        // getting a nearby frame would be expensive. Keeping _currentFrame alive here also keeps any
        // hardware frame backing the last visible image alive until a replacement is accepted.
        if (asPreview && IsTinyVideoSeek(target))
            return true;

        // Preview seeks may show a nearby keyframe quickly while the user is still dragging.
        // Commit/final seeks must not resume video from the previous keyframe, though: that made
        // playback continue seconds or even minutes away from the visual seek target on long-GOP
        // media. Balanced therefore still uses decoder discard-to-target, but with bounded decode
        // work so it is close-enough rather than frame-perfect.
        var discardBeforeTarget = !asPreview;
        if (!decoder.Seek(target, exact: discardBeforeTarget))
            return false;

        var allowSupersedingAbort = !asPreview;
        if (allowSupersedingAbort && HasSupersedingSeekRequest())
            return false;

        var maxFrameDistance = GetSeekMaxFrameDistanceSeconds(asPreview);
        var decodeBudgetMs = GetSeekDecodeBudgetMilliseconds(asPreview, decoder);
        var maxDecodeAttempts = GetSeekDecodeMaxAttempts(asPreview, decoder);

        DecodeForSeekTarget(
            target,
            maxFrameDistance,
            decodeBudgetMs,
            maxDecodeAttempts,
            discardBeforeTarget,
            allowSupersedingAbort);

        if (allowSupersedingAbort && HasSupersedingSeekRequest())
            return false;

        lock (_presentationLock)
            return TryPresentBestSeekFrame(
                target,
                maxFrameDistance,
                discardBeforeTarget,
                allowPreviewFallback: asPreview);
    }

    private bool IsTinyVideoSeek(double target)
    {
        VideoFrameData? currentFrame = _currentFrame;
        return currentFrame != null && Math.Abs(currentFrame.PresentationTime - target) <= TinyVideoSeekThresholdSeconds;
    }

    private void DecodeForSeekTarget(
        double target,
        double maxFrameDistance,
        int budgetMilliseconds,
        int maxDecodeAttempts,
        bool requireAtOrAfterTarget,
        bool allowSupersedingAbort)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        while (attempts++ < maxDecodeAttempts
            && stopwatch.ElapsedMilliseconds <= budgetMilliseconds
            && (!allowSupersedingAbort || !HasSupersedingSeekRequest()))
        {
            if (HasAcceptableSeekFrame(decoder, target, maxFrameDistance, requireAtOrAfterTarget))
                return;

            var decoded = decoder.DecodeNextFrame();
            if (!decoded && decoder.FrameCount > 0)
                return;

            if (!decoded)
                _ = Thread.Yield();
        }
    }

    private static bool HasAcceptableSeekFrame(VideoDecoder decoder, double target, double maxFrameDistance, bool requireAtOrAfterTarget)
    {
        foreach (VideoFrameData frame in decoder.EnumerateQueuedFrames())
            if (IsSeekFrameAcceptable(frame.PresentationTime, target, maxFrameDistance, requireAtOrAfterTarget))
                return true;

        return false;
    }

    private bool TryPresentBestSeekFrame(
        double target,
        double maxFrameDistance,
        bool requireAtOrAfterTarget,
        bool allowPreviewFallback)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return false;

        VideoFrameData? bestFrame = null;
        VideoFrameData? fallbackPreviewFrame = null;
        var bestDistance = double.MaxValue;
        var fallbackDistance = double.MaxValue;
        var examinedFrames = 0;

        while (decoder.GetNextFrame() is { } frame)
        {
            examinedFrames++;
            var distance = Math.Abs(frame.PresentationTime - target);
            if (distance <= bestDistance && IsSeekFrameAcceptable(frame.PresentationTime, target, maxFrameDistance, requireAtOrAfterTarget))
            {
                if (bestFrame != null)
                    decoder.ReturnFrame(bestFrame);

                bestFrame = frame;
                bestDistance = distance;
            }
            else if (allowPreviewFallback && distance < fallbackDistance)
            {
                if (fallbackPreviewFrame != null)
                    decoder.ReturnFrame(fallbackPreviewFrame);

                fallbackPreviewFrame = frame;
                fallbackDistance = distance;
            }
            else
                decoder.ReturnFrame(frame);
        }

        if (bestFrame == null && allowPreviewFallback && fallbackPreviewFrame != null)
        {
            bestFrame = fallbackPreviewFrame;
            bestDistance = fallbackDistance;
            fallbackPreviewFrame = null;
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine(
                    $"[VideoPipeline:SmartSeek] Preview fallback frame at {bestFrame.PresentationTime:F3}s " +
                    $"for target {target:F3}s, distance={bestDistance:F3}s.");
            }
        }

        if (fallbackPreviewFrame != null)
            decoder.ReturnFrame(fallbackPreviewFrame);

        if (bestFrame == null)
        {
            if (examinedFrames > 0)
                if (_configuration.EnableVerboseLogging)
                {
                    Debug.WriteLine(
                        $"[VideoPipeline:SmartSeek] No video frame close enough to {target:F3}s " +
                        $"within {maxFrameDistance:F3}s; keeping previous visual frozen.");
                }

            return false;
        }

        PresentFrame(bestFrame);
        return true;
    }

    private static bool IsSeekFrameAcceptable(double frameTime, double target, double maxFrameDistance, bool requireAtOrAfterTarget)
    {
        if (Math.Abs(frameTime - target) > maxFrameDistance)
            return false;

        // Preview mode can show a nearby frame on either side of the requested target. Commit
        // seeks must never accept an earlier keyframe as the playback start visual; otherwise the
        // decoder can resume far behind the requested target on long-GOP files.
        return !requireAtOrAfterTarget || frameTime + 0.001 >= target;
    }

    private static double GetSeekMaxFrameDistanceSeconds(bool asPreview) => asPreview
        ? PreviewMaxFrameDistanceSeconds
        : SettleMaxFrameDistanceSeconds;

    private static int GetSeekDecodeBudgetMilliseconds(bool asPreview, VideoDecoder decoder) => asPreview
        ? decoder.IsHardwareAccelerated
            ? HardwarePreviewDecodeBudgetMilliseconds
            : SoftwarePreviewDecodeBudgetMilliseconds
        : decoder.IsHardwareAccelerated
            ? HardwareSettleDecodeBudgetMilliseconds
            : SoftwareSettleDecodeBudgetMilliseconds;

    private static int GetSeekDecodeMaxAttempts(bool asPreview, VideoDecoder decoder) => asPreview
        ? decoder.IsHardwareAccelerated
            ? HardwarePreviewDecodeMaxAttempts
            : SoftwarePreviewDecodeMaxAttempts
        : decoder.IsHardwareAccelerated
            ? HardwareSettleDecodeMaxAttempts
            : SoftwareSettleDecodeMaxAttempts;

    private void DropFramesBeforeCommittedVideoTarget(VideoDecoder decoder)
    {
        var minimumPresentationTime = GetMinimumCommittedVideoPresentationTarget();
        if (!double.IsFinite(minimumPresentationTime))
            return;

        var threshold = Math.Max(0, minimumPresentationTime - 0.001);
        var droppedFrames = 0;
        while (decoder.PeekNextFrame() is { } nextFrame && nextFrame.PresentationTime < threshold)
        {
            VideoFrameData? staleFrame = decoder.GetNextFrame();
            if (staleFrame == null)
                break;

            decoder.ReturnFrame(staleFrame);
            droppedFrames++;
        }

        if (droppedFrames > 0)
            if (_configuration.EnableVerboseLogging) Debug.WriteLine($"[VideoPipeline:SmartSeek] Dropped {droppedFrames} stale post-seek video frame(s) before {minimumPresentationTime:F3}s.");
    }

    private bool ShouldSuppressLateFrameDrop() => IsPostSeekRecoveryActive() || Volatile.Read(ref _postSeekAdaptiveSuppressionWindowsRemaining) > 0;

    private void PresentFrameForPosition(double targetPosition, double clockPosition, bool forceReissue = false)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        DropFramesBeforeCommittedVideoTarget(decoder);

        var maxPresentationTime = Math.Min(
            targetPosition + GetFrameLeadTolerance(decoder.FramesPerSecond),
            clockPosition + GetFrameSelectionLeadLimit(decoder.FramesPerSecond));
        VideoFrameData? frameToPresent = null;
        var dequeuedFrames = 0;

        while (decoder.PeekNextFrame() is { } nextFrame)
        {
            if (nextFrame.PresentationTime > maxPresentationTime)
                break;

            if (frameToPresent != null
                && !ShouldAdvanceSelectionToFrame(decoder.FramesPerSecond, clockPosition, frameToPresent.PresentationTime, nextFrame.PresentationTime))
            {
                break;
            }

            VideoFrameData? candidateFrame = decoder.GetNextFrame();
            if (candidateFrame == null)
                break;

            if (frameToPresent != null)
                decoder.ReturnFrame(frameToPresent);

            frameToPresent = candidateFrame;
            dequeuedFrames++;
        }

        if (frameToPresent != null)
        {
            var lateness = targetPosition - frameToPresent.PresentationTime;
            if (!ShouldSuppressLateFrameDrop() && lateness > MaxPresentLatenessSeconds)
            {
                decoder.ReturnFrame(frameToPresent);
                var droppedFrames = Math.Max(1, dequeuedFrames);
                _presentationDiagnosticDroppedFrames += droppedFrames;
                _presentationDiagnosticLateDroppedFrames += droppedFrames;
                return;
            }

            PresentFrame(frameToPresent, forceReissue);
            RecordPresentationDiagnostics(dequeuedFrames, targetPosition, clockPosition, frameToPresent.PresentationTime, decoder.FrameCount);
            return;
        }

        // No newer frame was ready. When a host explicitly asks to refresh (attach/resize/display-mode
        // change), re-raise the retained current frame so a stopped/first-frame visual reaches a newly
        // attached surface even though its timestamp matches the last presented frame.
        if (forceReissue)
            ReissueCurrentFrame();
    }

    private static double GetFrameLeadTolerance(double fps) => VideoPresentationTiming.GetPrimaryFrameLeadTolerance(fps);

    private static double GetFrameSelectionLeadLimit(double fps) => VideoPresentationTiming.GetPrimaryFrameSelectionLeadLimit(fps);

    private bool ShouldAdvanceSelectionToFrame(double fps, double clockPosition, double selectedPresentationTime, double nextPresentationTime)
    {
        if (!IsSourceCadencePlayback(fps))
            return true;

        var frameDuration = 1.0 / fps;
        return clockPosition - selectedPresentationTime >= frameDuration && nextPresentationTime <= clockPosition;
    }

    private int GetTargetBufferedVideoFrames()
    {
        var frameBytes = Math.Max(1L, (long)Width * Height * 4);
        var memoryBoundFrames = (int)Math.Clamp(MaxBufferedVideoBytes / frameBytes, MinBufferedVideoFrames, MaxBufferedVideoFrames);
        var fps = FramesPerSecond;
        var timingBoundFrames = double.IsFinite(fps) && fps > 0
            ? Math.Clamp((int)Math.Ceiling(fps * 0.25), MinBufferedVideoFrames, MaxBufferedVideoFrames)
            : 10;

        return Math.Min(memoryBoundFrames, timingBoundFrames);
    }

    private void RecordPresentationDiagnostics(
        int dequeuedFrames,
        double targetPosition,
        double clockPosition,
        double framePresentationTime,
        int queuedFrames)
    {
        _presentationDiagnosticFrames++;
        if (dequeuedFrames > 1)
        {
            _presentationDiagnosticDroppedFrames += dequeuedFrames - 1;
            _presentationDiagnosticSelectionSkippedFrames += dequeuedFrames - 1;
        }

        var clockLateMs = Math.Max(0, clockPosition - framePresentationTime) * 1000.0;
        var targetLateMs = Math.Max(0, targetPosition - framePresentationTime) * 1000.0;
        var earlyMs = Math.Max(0, framePresentationTime - clockPosition) * 1000.0;
        var refreshIntervalMs = GetDisplayRefreshIntervalSeconds() * 1000.0;

        _presentationDiagnosticMaxClockLateMs = Math.Max(_presentationDiagnosticMaxClockLateMs, clockLateMs);
        _presentationDiagnosticMaxTargetLateMs = Math.Max(_presentationDiagnosticMaxTargetLateMs, targetLateMs);
        _presentationDiagnosticMaxEarlyMs = Math.Max(_presentationDiagnosticMaxEarlyMs, earlyMs);
        var clockLateThresholdMs = GetAdaptiveClockLateThresholdMilliseconds(refreshIntervalMs);
        if (double.IsFinite(clockLateThresholdMs) && clockLateThresholdMs > 0 && clockLateMs > clockLateThresholdMs)
            _presentationDiagnosticClockLateSamples++;

        if (_presentationDiagnosticFrames < PresentationDiagnosticsFrameInterval)
            return;

        var frameIntervalMs = FramesPerSecond > 0 ? 1000.0 / FramesPerSecond : 0.0;
        var activeLeadRefreshes = GetActivePresentationLeadRefreshIntervals();
        var inSeekRecovery = IsPostSeekRecoveryActive();
        var suppressedWindows = Volatile.Read(ref _postSeekAdaptiveSuppressionWindowsRemaining);
        string adaptiveAction;
        if (inSeekRecovery)
            adaptiveAction = "seek-recovery";
        else if (suppressedWindows > 0)
        {
            var remaining = Math.Max(0, Interlocked.Decrement(ref _postSeekAdaptiveSuppressionWindowsRemaining));
            adaptiveAction = $"post-seek-suppress remaining={remaining}";
        }
        else
            adaptiveAction = UpdateAdaptivePresentationLead(refreshIntervalMs);

        if (_configuration.EnablePerformanceLogging)
        {
            Trace.WriteLine(
                $"[VideoPipeline:Perf] presented={_presentationDiagnosticFrames}, dropped={_presentationDiagnosticDroppedFrames}, " +
                $"selectionSkips={_presentationDiagnosticSelectionSkippedFrames}, lateDrops={_presentationDiagnosticLateDroppedFrames}, " +
                $"maxClockLate={_presentationDiagnosticMaxClockLateMs:F1}ms, " +
                $"maxTargetLate={_presentationDiagnosticMaxTargetLateMs:F1}ms, maxEarly={_presentationDiagnosticMaxEarlyMs:F1}ms, " +
                $"clockLateSamples={_presentationDiagnosticClockLateSamples}, clockLateThreshold={clockLateThresholdMs:F1}ms, " +
                $"lead={_presentationDiagnosticLeadMs:F1}ms ({activeLeadRefreshes:F3}x refresh), adaptive={adaptiveAction}, " +
                $"queued={queuedFrames}/{GetTargetBufferedVideoFrames()}, fps={FramesPerSecond:F2}, " +
                $"videoFrame={frameIntervalMs:F1}ms, refresh={refreshIntervalMs:F1}ms");
        }

        ResetPresentationDiagnostics();
    }

    private string UpdateAdaptivePresentationLead(double refreshIntervalMs)
    {
        if (!double.IsFinite(refreshIntervalMs) || refreshIntervalMs <= 0)
            return "hold";

        if (!ShouldAdaptPresentationLead(refreshIntervalMs))
            return "hold-source-cadence";

        var active = GetActivePresentationLeadRefreshIntervals();
        var lateThresholdMs = GetAdaptiveClockLateThresholdMilliseconds(refreshIntervalMs);
        var goodLateThresholdMs = refreshIntervalMs * PresentationLeadGoodClockLateThresholdRefreshes;
        var earlyDecreaseThresholdMs = refreshIntervalMs * PresentationLeadEarlyDecreaseThresholdRefreshes;
        var previous = active;

        if (_presentationDiagnosticClockLateSamples >= PresentationLeadLateSampleIncreaseThreshold || _presentationDiagnosticMaxClockLateMs > lateThresholdMs * 2.0)
            active = Math.Min(MaximumPresentationLeadRefreshIntervals, active + PresentationLeadIncreaseStepRefreshIntervals);
        else if (_presentationDiagnosticClockLateSamples == 0 && _presentationDiagnosticMaxClockLateMs < goodLateThresholdMs && _presentationDiagnosticMaxEarlyMs > earlyDecreaseThresholdMs)
            active = Math.Max(MinimumPresentationLeadRefreshIntervals, active - PresentationLeadDecreaseStepRefreshIntervals);

        _activePresentationLeadRefreshIntervals = active;
        return Math.Abs(active - previous) < 0.0001
            ? "hold"
            : active > previous
                ? $"increase {previous:F3}->{active:F3}"
                : $"decrease {previous:F3}->{active:F3}";
    }

    private double GetAdaptiveClockLateThresholdMilliseconds(double refreshIntervalMs)
    {
        var refreshThresholdMs = double.IsFinite(refreshIntervalMs) && refreshIntervalMs > 0
            ? refreshIntervalMs * PresentationLeadClockLateIncreaseThresholdRefreshes
            : 0;
        var frameIntervalMs = FramesPerSecond > 0 ? 1000.0 / FramesPerSecond : 0;
        var sourceThresholdMs = frameIntervalMs > 0 ? frameIntervalMs * 0.75 : 0;

        return Math.Max(refreshThresholdMs, sourceThresholdMs);
    }

    private bool ShouldAdaptPresentationLead(double refreshIntervalMs)
    {
        var fps = FramesPerSecond;
        return !IsSourceCadencePlayback(fps, refreshIntervalMs);
    }

    private bool IsSourceCadencePlayback(double fps)
    {
        var refreshIntervalMs = GetDisplayRefreshIntervalSeconds() * 1000.0;
        return IsSourceCadencePlayback(fps, refreshIntervalMs);
    }

    private static bool IsSourceCadencePlayback(double fps, double refreshIntervalMs)
    {
        if (!double.IsFinite(fps) || fps <= 0)
            return false;

        var frameIntervalMs = 1000.0 / fps;
        return double.IsFinite(refreshIntervalMs)
            && refreshIntervalMs > 0
            && frameIntervalMs > refreshIntervalMs * 1.25;
    }

    private void ResetPresentationDiagnostics()
    {
        _presentationDiagnosticFrames = 0;
        _presentationDiagnosticDroppedFrames = 0;
        _presentationDiagnosticSelectionSkippedFrames = 0;
        _presentationDiagnosticLateDroppedFrames = 0;
        _presentationDiagnosticMaxClockLateMs = 0;
        _presentationDiagnosticMaxTargetLateMs = 0;
        _presentationDiagnosticMaxEarlyMs = 0;
        _presentationDiagnosticClockLateSamples = 0;
    }

    private void PresentFrame(VideoFrameData frame, bool forceReissue = false)
    {
        if (_currentFrame?.PresentationTime == frame.PresentationTime)
        {
            _videoDecoder?.ReturnFrame(frame);
            if (forceReissue)
                ReissueCurrentFrame();

            return;
        }

        VideoFrameData? previousFrame = _currentFrame;
        _currentFrame = frame;
        try
        {
            RaiseFrameReady(frame);
        }
        finally
        {
            _videoDecoder?.ReturnFrame(previousFrame);
        }
    }

    private void ReissueCurrentFrame()
    {
        if (_currentFrame != null)
            RaiseFrameReady(_currentFrame);
    }

    private bool TryPresentLoopStartSnapshot(double loopTarget)
    {
        VideoFrameEventArgs? snapshot = _loopStartFrameSnapshot;
        if (snapshot == null || loopTarget > GetEndToleranceSeconds())
            return false;

        FrameReady?.Invoke(this, snapshot);
        return true;
    }

    private void RaiseFrameReady(VideoFrameData frame)
    {
        CacheLoopStartFrameSnapshot(frame);
        FrameReady?.Invoke(this, CreateFrameEventArgs(frame));
    }

    private void CacheLoopStartFrameSnapshot(VideoFrameData frame)
    {
        if (_loopStartFrameSnapshot != null || frame.Data == null || frame.Length <= 0 || frame.PresentationTime > GetEndToleranceSeconds())
            return;

        var length = Math.Min(frame.Length, frame.Data.Length);
        var copy = new byte[length];
        Buffer.BlockCopy(frame.Data, 0, copy, 0, length);
        _loopStartFrameSnapshot = new VideoFrameEventArgs
        {
            PixelData = copy,
            PixelDataCanBeRetained = true,
            Width = frame.Width,
            Height = frame.Height,
            Stride = frame.Stride,
            PtsSeconds = frame.PresentationTime,
            FramesPerSecond = FramesPerSecond
        };
    }

    private VideoFrameEventArgs CreateFrameEventArgs(VideoFrameData frame) => new()
    {
        PixelData = frame.Data != null
            ? new ReadOnlyMemory<byte>(frame.Data, 0, frame.Length)
            : ReadOnlyMemory<byte>.Empty,
        Width = frame.Width,
        Height = frame.Height,
        Stride = frame.Stride,
        PtsSeconds = frame.PresentationTime,
        FramesPerSecond = FramesPerSecond,
        D3D11DevicePointer = frame.D3D11DevicePointer,
        D3D11DeviceContextPointer = frame.D3D11DeviceContextPointer,
        D3D11TexturePointer = frame.D3D11TexturePointer,
        D3D11ArraySlice = frame.D3D11ArraySlice
    };

    private void ReleaseCurrentFrame()
    {
        _videoDecoder?.ReturnFrame(_currentFrame);
        _currentFrame = null;
    }

    private void RaiseState(VideoEngineState state) => StateChanged?.Invoke(this, state);

    private void WaitForPresentationIdle()
    {
        lock (_presentationLock)
        {
            // Wait for any in-flight presentation callback before decoder/audio state changes.
        }
    }

    private void CleanupTempPath()
    {
        if (string.IsNullOrWhiteSpace(_tempPath))
            return;

        try
        {
            TryDeleteTempPath(_tempPath);
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging) Debug.WriteLine($"[VideoPipeline] Failed to delete temp video file: {ex.Message}");
        }
        finally
        {
            _tempPath = null;
        }
    }

    private static string CreateTempMediaPath(byte[] data)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FatzBombMediaControl");
        _ = Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".media");
        File.WriteAllBytes(tempPath, data);
        return tempPath;
    }

    private static void TryDeleteTempPath(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
