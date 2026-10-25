
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Graphics.Cadence;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Network;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Core.Video;
using FFmpeg.AutoGen;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed partial class VideoPipeline(
    IClockSource clock,
    MediaState state,
    MediaPlayerConfiguration configuration,
    int bassDeviceIndex,
    PlaybackSyncState sync,
    IPrecisionWaiterFactory precisionWaiterFactory,
    IVideoPipelinePlatformServices platformServices,
    IVideoHardwareAccelerationServices hardwareAccelerationServices,
    bool usesAudio) : IVideoEngine, IMediaLoopEventSource, IRendererRefreshRateSink, INetworkVideoEngine
{
    private readonly IClockSource _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly MediaState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly MediaPlayerConfiguration _configuration = configuration;
    private readonly int _bassDeviceIndex = bassDeviceIndex;
    private readonly PlaybackSyncState _sync = sync ?? throw new ArgumentNullException(nameof(sync));
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
    private readonly IVideoPipelinePlatformServices _platformServices = platformServices ?? throw new ArgumentNullException(nameof(platformServices));
    private readonly IVideoHardwareAccelerationServices _hardwareAccelerationServices = hardwareAccelerationServices ?? throw new ArgumentNullException(nameof(hardwareAccelerationServices));
    // False in Background mode (043A) and without BASS (ENG-033): picture only, on the playback clock.
    private readonly bool _usesAudio = usesAudio;
    private readonly AudioPresentationClock _presentationClock = new();
    private readonly IOutputLatencyProvider _outputLatencyProvider = new BassOutputLatencyProvider(configuration.EnableUsbBluetoothLatencyHeuristic);
    private readonly Lock _decodeLock = new();
    private readonly PlaybackScheduler _decodeScheduler = new("VideoDecode");
    private readonly PlaybackScheduler _presentationScheduler = new("VideoPresentation");
    private readonly PlaybackScheduler _decoderRecoveryScheduler = new("DecoderDeviceLostRecovery");
    private string? _currentVideoPath;
    private int _decoderRecoveryInProgress;
    private volatile bool _hardwareDecodeDisabledByDeviceLoss;
    private VideoDecoder? _videoDecoder;
    private VideoAudioEngine? _audioEngine;
    private SharedDemuxer? _sharedDemuxer;
    private readonly Lock _presentationLock = new();
    private VideoFrameEventArgs? _loopStartFrameSnapshot;
    private bool _loopPreSeekActive;
    // Tracks the last VideoAudioEngine.CompletedLoopCount value the video side has caught up
    // to. When the audio engine's count advances (it wraps its own decoder and never stops
    // its BASS channel - see VideoAudioEngine.LoopPlayback), CommitAudioLoopWrap catches the
    // video side up with no audio calls at all.
    private int _lastObservedAudioLoopCount;
    private double? _overrideRefreshHz;
    private Func<double?>? _rendererRefreshRateSource;
    // Set on open; the first presentation-loop start reports the cadence, by which time the
    // presentation coordinator has handed over the primary display's refresh rate.
    private int _cadenceReportPending;
    private long _cadenceReportFirstAttempt;
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
    private int _playbackEndSignaled;
    private string? _tempPath;
    private const int MaxVideoPacketsPerDecodePass = 3;
    // A queued frame whose PTS is this far behind the presentation clock while a loop
    // pre-seek is pending is a next-lap frame (PTS restarted near 0), not a late frame.
    // Larger than any legitimate decode lateness, far smaller than a realistic loop length.
    private const double LoopWrapFrameGuardSeconds = 1.0;
    private const string DecoderDeviceLostCategory = "Decoder.DeviceLost";
    private const string DecoderDeviceLostRecoveryFailedCategory = "Decoder.DeviceLost.RecoveryFailed";
    private const int DecoderRecoveryMaxAttempts = 3;
    private static readonly TimeSpan[] DecoderRecoveryBackoffDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];
    private static readonly TimeSpan HardwareDecodeReprobeInterval = TimeSpan.FromSeconds(5);
    private const int PresentationDiagnosticsFrameInterval = 120;
    private readonly AdaptiveSeekPumpTiming _seekPumpTiming = new();
    private const double MaxPresentLatenessSeconds = 0.18;
    private const double DisplayLockedPtsToleranceSeconds = 0.0005;
    private const double TinyVideoSeekThresholdSeconds = 0.25;
    private const double PreviewMaxFrameDistanceSeconds = 2.0;
    private const double SettleMaxFrameDistanceSeconds = 0.50;
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
    private readonly PostSeekRecovery _postSeekRecovery = new();

    public IAudioEngine? Audio => _audioEngine;
    public bool HasAudio => _audioEngine?.IsOpen == true;
    /// <summary>
    /// Gets or sets whether playback loops. A network source loops only when it is finite and
    /// seekable (Stage 039A); the requested value is kept for the next source.
    /// </summary>
    public bool LoopPlayback
    {
        get => field && _sourceCanLoop;
        set
        {
            field = value;
            _ = (_audioEngine?.LoopPlayback = LoopPlayback);
        }
    }

    // Stage 039A network source state: the probed row, whether it can loop, the buffering
    // monitor, and FFmpeg's reason for the last failed open.
    private volatile bool _sourceCanLoop = true;
    private MediaSourceCapabilities? _networkCapabilities;
    private NetworkBufferingMonitor? _networkBufferingMonitor;

    public MediaSourceCapabilities? NetworkCapabilities => _isOpen ? Volatile.Read(ref _networkCapabilities) : null;

    public string? LastNetworkError { get; private set; }

    public event EventHandler<MediaBufferingChangedEventArgs>? NetworkBufferingChanged;
    public bool IsOpen => _isOpen;
    public double Duration { get; private set; }
    public double FramesPerSecond => _videoDecoder?.FramesPerSecond ?? 0;
    public int Width => _videoDecoder?.Width ?? 0;
    public int Height => _videoDecoder?.Height ?? 0;
    public string? CodecName => _videoDecoder?.CodecName;
    public bool IsHardwareAccelerated => _videoDecoder?.IsHardwareAccelerated == true;
    public string? HardwareAccelerationMethod => _videoDecoder?.HardwareAccelerationMethod;


    public void SetDisplayRefreshRate(double? refreshHz) => _overrideRefreshHz = refreshHz is > 0 ? refreshHz : null;

    void IRendererRefreshRateSink.SetRendererRefreshRateSource(Func<double?>? source) =>
        Volatile.Write(ref _rendererRefreshRateSource, source);

    /// <summary>
    /// Reports how the video's frame rate fits the display refresh rate (Stage 017A slice 2):
    /// smooth, judder, or skipped frames, and which refresh rates would be smooth. Raised once
    /// per open, when playback first starts. Diagnostic only; presentation timing is unchanged.
    /// </summary>
    /// <remarks>
    /// The presentation loop calls this every pass. Until the renderer reports the monitor's
    /// rate the report waits, up to <see cref="CadenceReportRendererWait"/>: the renderer's
    /// rate source can attach just after playback starts, and its first answer after an idle
    /// spell can be the value cached when the window was attached (empty if that query failed).
    /// Without the wait the once-per-open report could name the platform's 60 Hz fallback
    /// while timing moved to the monitor's rate a moment later.
    /// </remarks>
    private void ReportPendingDisplayCadence(bool canWait = true)
    {
        if (Volatile.Read(ref _cadenceReportPending) == 0)
            return;

        (var refreshIntervalSeconds, DisplayRefreshSource source) = ResolveDisplayRefresh();
        var now = Stopwatch.GetTimestamp();
        var firstAttempt = Interlocked.CompareExchange(ref _cadenceReportFirstAttempt, now, 0);
        var waited = firstAttempt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(firstAttempt, now);
        if (canWait && ShouldWaitForRendererRefreshRate(source, Volatile.Read(ref _rendererRefreshRateSource) is not null, waited))
            return;

        if (Interlocked.Exchange(ref _cadenceReportPending, 0) == 1)
            ReportDisplayCadence(_currentVideoPath, refreshIntervalSeconds, source);
    }

    /// <summary>How long the cadence report waits for the renderer's monitor rate.</summary>
    internal static readonly TimeSpan CadenceReportRendererWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether the cadence report should wait for the renderer's rate: only while the rate came
    /// from the platform, a renderer could still supply one, and the wait has not run out.
    /// </summary>
    internal static bool ShouldWaitForRendererRefreshRate(DisplayRefreshSource source, bool rendererSourceKnown, TimeSpan waited) =>
        source == DisplayRefreshSource.Platform
        && rendererSourceKnown
        && waited < CadenceReportRendererWait;

    private void ReportDisplayCadence(string? path, double refreshIntervalSeconds, DisplayRefreshSource source)
    {
        if (!double.IsFinite(refreshIntervalSeconds) || refreshIntervalSeconds <= 0)
            return;

        var description = CadenceAdvice.Describe(FramesPerSecond, 1.0 / refreshIntervalSeconds);
        if (description is null)
            return;

        // The platform rate can be a 60 Hz fallback, or another monitor's rate on Windows (DWM's
        // global composition rate), so the report says where it came from.
        var rateSource = source switch
        {
            DisplayRefreshSource.Host => "set by the host",
            DisplayRefreshSource.Renderer => "of the monitor showing the video",
            _ => "reported by the platform, or 60 Hz when it cannot tell",
        };
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "VideoCadence", $"{description} (Display rate {rateSource}.)", sourcePath: path);
    }

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
        get
        {
            // One read of the field: Close nulls it from another thread.
            VideoAudioEngine? audio = _audioEngine;
            return audio?.IsOpen == true ? audio.Position : _clock.PositionSeconds;
        }
        set => Seek(value);
    }

    public event EventHandler<VideoFrameEventArgs>? FrameReady;
    public event EventHandler<VideoEngineState>? StateChanged;
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? PlaybackEnded;
    public event EventHandler? MediaLooped;

    public bool Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        return OpenCore(path, network: null);
    }

    /// <summary>
    /// Opens an http/https video (Stage 039A): one attempt through FFmpeg's network path for the
    /// picture and, as for local video, a second connection for the audio (its own cursor). No
    /// archive, temp file, normalization analysis or embedded-subtitle scan.
    /// </summary>
    public bool OpenNetwork(string url, FfmpegNetworkOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(options);
        return OpenCore(url, options);
    }

    private bool OpenCore(string path, FfmpegNetworkOptions? network)
    {
        Close();
        LastNetworkError = null;
        _generation++;
        _loopStartFrameSnapshot = null;
        _sharedDemuxer = new SharedDemuxer();
        if (!(network is null ? _sharedDemuxer.Open(path) : _sharedDemuxer.Open(path, network)))
        {
            LastNetworkError = _sharedDemuxer.LastNetworkError;
            Close();
            return false;
        }

        _videoDecoder = VideoDecoder.FromConfiguration(_configuration, _sharedDemuxer, _hardwareAccelerationServices);
        if (!_videoDecoder.Open(path))
        {
            Close();
            return false;
        }

        // Keep audio on its own FFmpeg cursor. Routing codec priming/preroll packets through the
        // video cursor can offset startup until the first seek (reproduced with AV1 + E-AC-3).
        // SeekCore still repositions both cursors to the same target.
        // Background mode (043A), and any mode without BASS (ENG-033), decodes the picture only
        // and runs on the playback clock.
        _audioEngine = _usesAudio
            ? new VideoAudioEngine(_outputLatencyProvider, _bassDeviceIndex)
            : null;
        if (_audioEngine is null)
        {
            if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] No video audio (Background mode or no audio engine): picture only; using playback clock.");
        }
        else if (!(network is null ? _audioEngine.Open(path) : _audioEngine.OpenNetwork(path, network)))
        {
            if (network is not null && _audioEngine.LastNetworkError is { } audioError)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Network", $"Network video audio did not open ({audioError}); playing the picture on the playback clock.", sourcePath: path);
            _audioEngine.Dispose();
            _audioEngine = null;
            if (_configuration.EnableLogging) Debug.WriteLine("[VideoPipeline] Opened video without audio stream; using playback clock.");
        }

        if (network is not null)
        {
            MediaSourceCapabilities capabilities = NetworkVideoPolicy.Resolve(_sharedDemuxer.GetNetworkProbe());
            Volatile.Write(ref _networkCapabilities, capabilities);
            _sourceCanLoop = NetworkVideoPolicy.CanLoop(capabilities);
        }

        if (_audioEngine is not null)
            _audioEngine.LoopPlayback = LoopPlayback;

        _lastObservedAudioLoopCount = 0;

        Duration = Math.Max(_videoDecoder.DurationSeconds, _audioEngine?.Duration ?? 0);
        _clock.Stop();
        _clock.DurationSeconds = Duration;
        _presentationClock.Reset(0);
        ResetPresentationTimingController();
        _currentVideoPath = path;
        _ = Interlocked.Exchange(ref _cadenceReportFirstAttempt, 0);
        _ = Interlocked.Exchange(ref _cadenceReportPending, 1);
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
            if (_videoDecoder != null)
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "VideoDecoder", $"Decoding {_videoDecoder.DescribeDecodePath()}", sourcePath: path);
        }

        ResetAdaptiveVideoSeekTiming();
        StartSeekWorker();
        if (network is not null)
            StartNetworkBufferingMonitor();
        return true;
    }

    private void StartNetworkBufferingMonitor()
    {
        var generation = _generation;
        _networkBufferingMonitor = new NetworkBufferingMonitor(
            SampleNetworkBuffering,
            args =>
            {
                if (generation == _generation && _isOpen)
                    NetworkBufferingChanged?.Invoke(this, args);
            });
    }

    /// <summary>
    /// Samples the network readers for buffering: the longer of the picture and audio reads in
    /// progress, and the decoded frames queued against the decode target. Lock-free reads only:
    /// a stalled read holds the decode lock.
    /// </summary>
    private NetworkBufferingSample SampleNetworkBuffering()
    {
        SharedDemuxer? demuxer = _sharedDemuxer;
        VideoDecoder? decoder = _videoDecoder;
        VideoAudioEngine? audio = _audioEngine;
        if (demuxer is null || decoder is null)
            return default;

        TimeSpan stall = demuxer.NetworkReadStall;
        if (audio?.NetworkReadStall is { } audioStall && audioStall > stall)
            stall = audioStall;

        var target = Math.Max(1, VideoFrameBufferPolicy.MinBufferedFrames);
        var fill = Math.Clamp(decoder.FrameCount / (double)target, 0, 1);
        return new NetworkBufferingSample(_isPlaying, stall, fill, decoder.EndOfStream || demuxer.NetworkEndOfStream);
    }

    private bool OpenTempFile(string tempPath)
    {
        var success = Open(tempPath);
        if (success)
            _tempPath = tempPath;
        else
            TempMediaFile.TryDelete(tempPath);

        return success;
    }

    public bool Open(IArchiveEntryInfo archiveEntry)
    {
        ArgumentNullException.ThrowIfNull(archiveEntry);

        // Streamed straight to the scratch file: no in-memory copies of the whole video.
        return OpenTempFile(TempMediaFile.CreateFromArchiveEntry(archiveEntry));
    }

    public bool Play()
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            if (_isPlaying)
                return true;

            if (IsSeekSequenceActive())
            {
                // A seek is queued or settling. Starting now would play the old position until the
                // seek stops playback again; the settle resumes playback from the target instead.
                _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);
                _isPlaying = true;
                _state.SetPlaybackState(MediaPlayerState.Playing);
                RaiseState(VideoEngineState.Playing);
                return true;
            }

            _ = TryPrepareLoopRestartAtBeginning();

            if (HasAudio)
            {
                var presentationStartPosition = _audioEngine!.Position;
                if (!_audioEngine!.Play() && _configuration.EnableLogging)
                    Debug.WriteLine("[VideoPipeline] audio Play() returned false during transport start");
                _presentationClock.Start(presentationStartPosition);
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
            else
                ReportPendingDisplayCadence(canWait: false); // no presentation loop to report from
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

    // Reactive fallback restart: used when there was no proactive pre-seek (non-audio video,
    // where TryPreSeekLoopStart never runs) or as a safety net if the decoder queue somehow
    // drains before an audio-driven wrap is observed. For audio-backed video the normal path
    // is CommitAudioLoopWrap, triggered from PresentationLoop when VideoAudioEngine reports a
    // completed loop - this method seeks the decoder + audio + clock together and is
    // therefore not gapless, but it only runs when the proactive path didn't apply.
    private bool TryRestartLoopPlayback(double currentPosition, string reason)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null || Duration <= 0)
            return false;

        // This full restart supersedes any pending proactive pre-seek (it re-seeks the
        // decoder itself); a stale flag would keep the wrap-guard armed through the next
        // lap and stop TryPreSeekLoopStart from re-arming at the next EOF.
        _loopPreSeekActive = false;

        var loopBoundary = GetLoopBoundarySeconds(decoder);
        var loopTarget = GetLoopOverflowPosition(currentPosition, loopBoundary);

        SeekPump.CancelPendingRequest();

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
            if (!decoder.SeekSharedDemuxer(loopTarget) || !decoder.Seek(loopTarget, exact: false))
            {
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
                if (!_audioEngine!.Play())
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
        MediaLooped?.Invoke(this, EventArgs.Empty);
        if (firstLoopFrame == null && !presentedSnapshot)
            _ = PresentFrameForCurrentPosition(forceReissue: true);

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine(
                $"[VideoPipeline:LoopRestart] Restart prepared; queued={decoder.FrameCount}/{GetTargetBufferedVideoFrames()}, " +
                $"snapshot={presentedSnapshot}, decodedFirst={firstLoopFrame != null}.");

        return true;
    }

    // Gapless audio-backed loop wrap-commit. The audio engine already wrapped its own
    // decoder and never stopped its BASS channel (VideoAudioEngine.LoopPlayback /
    // CompletedLoopCount - the channel plays continuously across the boundary), so by the
    // time this fires the audio is already playing the next lap without interruption. This
    // just catches the video side up: flush any stale tail frames left in the queue by
    // TryPreSeekLoopStart's proactive pre-seek, present the first position-0 frame, and
    // re-anchor the presentation clock to the fresh (already-wrapped) audio position. There
    // are no audio Seek/Play calls and no decoder seek here - both already happened
    // (the decoder proactively in TryPreSeekLoopStart, the audio transparently inside
    // VideoAudioEngine) - so this is effectively instantaneous.
    private void CommitAudioLoopWrap(int newLoopCount)
    {
        _loopPreSeekActive = false;
        _lastObservedAudioLoopCount = newLoopCount;

        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        VideoFrameData? wrapFrame;
        lock (_decodeLock)
        {
            // Normal path: TryPreSeekLoopStart already repositioned the decoder and queued
            // position-0 frames behind the tail. Fallback: if the pre-seek never ran or
            // failed, the decoder is still at end-of-stream - seek it now so the video can
            // follow the audio, which has already wrapped and is playing the next lap.
            if (decoder.EndOfStream && (!decoder.SeekSharedDemuxer(0) || !decoder.Seek(0, exact: false)))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", "Decoder seek failed during audio loop wrap commit");

            DropStaleLoopTailFrames(decoder, GetEndToleranceSeconds());

            if (decoder.FrameCount == 0)
                _ = DecodeUntilFrameAvailable();

            wrapFrame = decoder.GetNextFrame();
        }

        if (wrapFrame != null)
            PresentFrame(wrapFrame, forceReissue: true);

        // The audio engine already wrapped its own position - read it fresh and re-anchor
        // the clock directly. AudioPresentationClock.GetPosition's own monotonic guard only
        // ever lets position move forward, so a plain GetPosition() call here would stay
        // stuck at the pre-wrap value; Start/Reset bypass that guard explicitly.
        var freshPosition = HasAudio ? _audioEngine!.Position : 0.0;
        if (HasAudio)
        {
            if (_isPlaying)
                _presentationClock.Start(freshPosition);
            else
                _presentationClock.Reset(freshPosition);
        }

        _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);
        _state.SetPosition(freshPosition);
        PositionChanged?.Invoke(this, freshPosition);
        MediaLooped?.Invoke(this, EventArgs.Empty);
        if (wrapFrame == null)
            _ = PresentFrameForCurrentPosition(forceReissue: true);

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine(
                $"[VideoPipeline:AudioLoopWrap] Commit #{newLoopCount}; position={freshPosition:F3}s, " +
                $"queued={decoder.FrameCount}/{GetTargetBufferedVideoFrames()}, decodedFirst={wrapFrame != null}.");
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

            _generation++;
            _isPlaying = false;
            _loopPreSeekActive = false;
            SeekPump.Reset();

            // Sync (not zero): the engine's CompletedLoopCount persists across Stop, so a
            // mismatched baseline would fire a spurious wrap commit on the next Play tick.
            _lastObservedAudioLoopCount = _audioEngine?.CompletedLoopCount ?? 0;
            StopDecodeLoopAndWait();
            StopPresentationLoopAndWait();
            WaitForPresentationIdle();
            _ = (_audioEngine?.Stop());
            _clock.Stop();
            _presentationClock.Reset(0);
            // Acquire presentationLock before decodeLock to keep the pipeline-wide ordering
            // (presentationLoop -> decodeLoop) consistent for stop-time frame presentation.
            lock (_presentationLock)
            {
                lock (_decodeLock)
                {
                    ReleaseCurrentFrame();
                    if (_videoDecoder is { } decoder)
                    {
                        _ = decoder.SeekSharedDemuxer(0);
                        _ = decoder.Seek(0);
                    }
                    _ = DecodeUntilFrameAvailable();
                    _ = PresentFrameForCurrentPosition();
                }
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

        // A network read or seek still running for an older position (a stalled segment) would
        // hold the seek worker or the decode loop for up to the read timeout: give it up. Taken
        // before the request, so the worker's I/O for this target is never cut (ENG-035).
        var requested = Stopwatch.GetTimestamp();
        SeekPump.Request(NormalizeSeekTarget(seconds));
        SupersedeNetworkOperationsStartedBefore(requested);
        return true;
    }

    /// <summary>
    /// Gives up the network reads and seeks of both cursors (picture and audio) that started
    /// before <paramref name="timestamp"/>; later ones are unaffected. Lock-free; local files
    /// are unaffected (ENG-035).
    /// </summary>
    private void SupersedeNetworkOperationsStartedBefore(long timestamp)
    {
        // One read of each field: Close nulls them from another thread.
        if (_sharedDemuxer is not { IsNetwork: true } demuxer)
            return;

        demuxer.SupersedeOperationsStartedBefore(timestamp);
        _audioEngine?.SupersedeNetworkOperationsStartedBefore(timestamp);
    }

    /// <summary>
    /// Performs the actual seek. Runs on the seek worker thread and is serialized against the other
    /// transport operations by <see cref="_transportGate"/> so pipeline state is never mutated
    /// concurrently.
    /// </summary>
    private bool SeekCore(double seconds, bool asPreview, bool allowPreviewFallback = false)
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            var target = NormalizeSeekTarget(seconds);
            var videoFramePresented = false;
            SetActiveSeekTarget(target);
            try
            {
                _loopPreSeekActive = false;
                _lastObservedAudioLoopCount = _audioEngine?.CompletedLoopCount ?? 0;
                var wasPlaying = _isPlaying;
                var resumeAudioPosition = target;
                if (!asPreview)
                    SetMinimumCommittedVideoPresentationTarget(target);

                if (target < Duration - 0.05)
                    _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);

                _generation++;
                // The decode loop may be blocked in a network read for the old position (one
                // that began after the request): give it up rather than wait for it (ENG-035).
                SupersedeNetworkOperationsStartedBefore(Stopwatch.GetTimestamp());
                StopDecodeLoopAndWait();
                StopPresentationLoopAndWait();
                WaitForPresentationIdle();

                // Reposition the video cursor before flushing its decoder. Audio owns a separate
                // cursor and is repositioned independently below.
                if (_sharedDemuxer != null && _videoDecoder != null)
                    _ = _videoDecoder.SeekSharedDemuxer(target);

                if (HasAudio)
                {
                    if (_audioEngine!.Seek(target, resumePlayback: false))
                        resumeAudioPosition = _audioEngine.Position;

                    _presentationClock.Reset(resumeAudioPosition);
                }
                else
                {
                    if (wasPlaying)
                        _clock.Pause();

                    _clock.Seek(target);
                }

                var videoSeekProcessingStopwatch = Stopwatch.StartNew();
                // Enforce the pipeline-wide presentationLock -> decodeLock ordering. The presentation
                // loop holds _presentationLock while driving TryPreSeekLoopStart /
                // TryCommitVideoOnlyLoopWrap (which take _decodeLock), so seeking must acquire
                // _presentationLock first to avoid the ABBA deadlock with a concurrent loop wrap.
                lock (_presentationLock)
                {
                    lock (_decodeLock)
                        videoFramePresented = SeekVideoNearTarget(target, asPreview, allowPreviewFallback);
                }

                videoSeekProcessingStopwatch.Stop();
                RecordAdaptiveVideoSeekProcessingTime(videoSeekProcessingStopwatch.Elapsed);

                if (HasSupersedingSeekRequest())
                    return videoFramePresented;

                _state.SetPosition(target);
                PositionChanged?.Invoke(this, target);

                if (asPreview)
                    return videoFramePresented;

                PreparePostSeekVideoRecovery(target);

                if (HasAudio && wasPlaying)
                {
                    _ = _audioEngine!.PrebufferAudio(maxDecodeAttempts: 4096);
                    if (!_audioEngine.Play())
                        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "VideoPipeline", "Audio Play() returned false during post-seek resume");
                    _presentationClock.Start(resumeAudioPosition);
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
                if (!asPreview)
                    EndSeekSequenceIfSettled();
            }

            return videoFramePresented;
        }
    }

    private double NormalizeSeekTarget(double seconds) => VideoSeekPolicy.NormalizeTarget(seconds, Duration);

    private void RecordAdaptiveVideoSeekProcessingTime(TimeSpan elapsed) => _seekPumpTiming.Record(elapsed);

    private void ResetAdaptiveVideoSeekTiming()
    {
        _seekPumpTiming.Reset();
        SeekPump.ClearMinimumCommittedTarget();
    }

    public void Close()
    {
        // A network read can block for the whole read timeout: abort it first, so the joins below
        // do not wait for it (Stage 039A). Aborted reads return end of file, so playback stops
        // first (under the presentation lock, so no pass is mid-way) and the presentation loop
        // cannot take them for the natural end. Local files are unaffected.
        _networkBufferingMonitor?.Dispose();
        _networkBufferingMonitor = null;
        if (_sharedDemuxer is { IsNetwork: true } networkDemuxer)
        {
            // A seek holds the presentation lock across its network reads: make it give up (it
            // checks for a superseding request, which shutdown is) and cut its reads short, or
            // Close would wait for a stalled segment (ENG-035).
            SeekPump.BeginShutdown();
            while (!_presentationLock.TryEnter(TimeSpan.FromMilliseconds(20)))
                SupersedeNetworkOperationsStartedBefore(Stopwatch.GetTimestamp());

            try
            {
                _isPlaying = false;
            }
            finally
            {
                _presentationLock.Exit();
            }

            networkDemuxer.Abort();
            _audioEngine?.AbortNetwork();
        }

        // Retire the seek worker first (without holding the gate) so no seek can be in flight when
        // we dispose the decoder below. StopSeekWorker joins the worker; the gate then guarantees
        // exclusivity for the teardown itself. Cancel any in-flight device-lost recovery the same way
        // (Stop only signals cancellation; so it cannot deadlock on the gate).
        StopSeekWorker();
        _decoderRecoveryScheduler.Stop();
        // Deterministic join before Dispose: the recovery worker may be in the middle of
        // HardwareDecodeDeviceAvailable (opening a throwaway VideoDecoder) and must not be torn
        // down mid-native-call. 2s mirrors the existing scheduler timeout pattern.
        _decoderRecoveryScheduler.StopAndWait();
        _hardwareDecodeDisabledByDeviceLoss = false;
        _ = Interlocked.Exchange(ref _decoderRecoveryInProgress, 0);

        lock (_transportGate)
        {
            _generation++;
            _isPlaying = false;
            _isOpen = false;
            _loopPreSeekActive = false;
            _lastObservedAudioLoopCount = 0;
            _currentVideoPath = null;
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
                _swapTimelineOrigin = null;
            }

            _sharedDemuxer?.Dispose();
            _sharedDemuxer = null;
            Volatile.Write(ref _networkCapabilities, null);
            _sourceCanLoop = true;

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
        _decoderRecoveryScheduler.Dispose();
        _seekPump?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void RenderCurrentFrame(bool allowWhilePlaying = false, bool forceReissue = false)
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

        // A seek holds the presentation lock while it decodes to its target (for a network source
        // that includes downloading segments, seconds when the server stalls) and presents that
        // frame itself: do not wait for it. Callers are the control queue (the refresh after a
        // seek) and the host's UI thread (a surface resize), which froze for the whole network
        // seek (ENG-035).
        if (!_presentationLock.TryEnter())
        {
            if (IsSeekSequenceActive())
                return;

            _presentationLock.Enter();
        }

        try
        {
            if (!_isOpen || _videoDecoder is null)
                return;

            _ = PresentFrameForCurrentPosition(forceReissue);
        }
        finally
        {
            _presentationLock.Exit();
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
        using var pacer = new MediaLoopPacer(_precisionWaiterFactory);
        var stopwatch = Stopwatch.StartNew();
        var lastTickSeconds = stopwatch.Elapsed.TotalSeconds;
        var diagnosticTicks = 0;
        var diagnosticMaxGapMs = 0.0;
        var diagnosticSumGapMs = 0.0;
        var lastTargetIntervalSeconds = GetPrimaryPresentationIntervalSeconds();

        if (_configuration.EnableLogging && !pacer.IsHighResolution)
            Trace.WriteLine("[VideoPipeline] High-resolution presentation timer unavailable; using coarse wait fallback.");

        while (!cancellationToken.IsCancellationRequested && generation == _generation)
        {
            ReportPendingDisplayCadence();
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

            // Display-locked timing (Stage 017A slice 4): wait for the moment just before the
            // display's next refresh, then present the frame for that refresh.
            DisplayLockedSlot? displayLockedSlot = null;
            DisplayLockedTarget? displayLockedTarget = null;
            if (_isPlaying && TryGetDisplayLockedSlot(out DisplayLockedSlot nextSlot, out DisplayLockedTarget? nextTarget))
            {
                var waitTicks = nextSlot.WakeTimestamp - Stopwatch.GetTimestamp();
                if (waitTicks > 0)
                    pacer.WaitFor(TimeSpan.FromSeconds(waitTicks / (double)Stopwatch.Frequency), cancellationToken.WaitHandle);

                if (cancellationToken.IsCancellationRequested || generation != _generation)
                    continue;

                displayLockedSlot = nextSlot;
                displayLockedTarget = nextTarget;
            }
            else if (!_isPlaying)
            {
                OnDisplayLockedIdle();
            }

            if (_isPlaying)
            {
                var restartedLoop = false;
                lock (_presentationLock)
                {
                    if (generation == _generation && _isPlaying)
                    {
                        // Proactive pre-seek: when the decoder reaches EOF with tail frames
                        // still queued, pre-seek to 0 so position-0 frames are ready before
                        // the audio-driven wrap commits below (audio-backed video only).
                        TryPreSeekLoopStart();

                        var currentPosition = displayLockedSlot is { } slot && displayLockedTarget is not null
                            ? PresentFrameForDisplayLockedSlot(slot, displayLockedTarget)
                            : PresentFrameForCurrentPosition();

                        // Gapless audio-backed loop: the audio engine wraps its own decoder
                        // and never stops its BASS channel (VideoAudioEngine.LoopPlayback /
                        // CompletedLoopCount). When it reports a new completed loop, catch the
                        // video side up - no audio Seek/Play calls needed. Gated on
                        // LoopPlayback (not _loopPreSeekActive) so a failed/missed video
                        // pre-seek can't strand the video on the old lap while the audio
                        // loops autonomously; CommitAudioLoopWrap handles that fallback.
                        var completedAudioLoops = LoopPlayback && HasAudio ? _audioEngine!.CompletedLoopCount : -1;
                        if (completedAudioLoops >= 0 && completedAudioLoops != _lastObservedAudioLoopCount)
                        {
                            CommitAudioLoopWrap(completedAudioLoops);
                            restartedLoop = true;
                        }
                        else if (!HasAudio && TryCommitVideoOnlyLoopWrap(currentPosition))
                        {
                            restartedLoop = true;
                        }
                        else if (ShouldRestartLoopPlayback(currentPosition))
                        {
                            restartedLoop = TryRestartLoopPlayback(currentPosition, "presentation eof");
                        }
                        else if (!LoopPlayback && HasReachedPlaybackEnd(currentPosition))
                        {
                            CompletePlaybackAtEnd();
                        }
                    }
                }

                if (restartedLoop)
                    pacer.Reset();
            }

            // The next pass waits for the following refresh.
            if (displayLockedSlot is not null)
                continue;

            if (IsSourceCadencePlayback(FramesPerSecond))
            {
                var waitSeconds = GetClockLockedPresentationWaitSeconds(targetIntervalSeconds);

                if (waitSeconds <= 0)
                    continue;

                pacer.WaitFor(TimeSpan.FromSeconds(waitSeconds), cancellationToken.WaitHandle);
                continue;
            }

            pacer.WaitNext(
                TimeSpan.FromSeconds(targetIntervalSeconds),
                cancellationToken.WaitHandle);
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

        var clockPosition = ApplyAudioVideoOffset(GetPresentationPosition());
        var selectionLeadSeconds = GetFrameSelectionLeadLimit(decoder.FramesPerSecond);

        // A loop pre-seek leaves the previous lap's tail ahead of position-zero frames.
        // Once the tail has presented, the next frame looks late against the still-unwrapped
        // clock; keep normal cadence until the audio or video-only wrap resets that clock.
        if (_loopPreSeekActive && nextFrame.PresentationTime < clockPosition - selectionLeadSeconds)
            return fallbackIntervalSeconds;

        var waitSeconds = nextFrame.PresentationTime - selectionLeadSeconds - clockPosition;
        return !double.IsFinite(waitSeconds)
            ? fallbackIntervalSeconds
            : waitSeconds <= 0 ? 0 : Math.Min(waitSeconds, Math.Max(0.001, fallbackIntervalSeconds));
    }

    private double GetPrimaryPresentationIntervalSeconds() =>
        VideoPresentationTiming.GetPrimaryPresentationInterval(FramesPerSecond, GetDisplayRefreshIntervalSeconds());

    private double GetDisplayRefreshIntervalSeconds() => ResolveDisplayRefresh().IntervalSeconds;

    // The presentation loop reads the display interval several times per frame (interval,
    // lead, cadence checks). Resolving it at most every 100 ms keeps the renderer and DWM
    // queries off the per-frame path (Stage 022A slice 2) and gives one frame one rate.
    private static readonly long DisplayRefreshMemoTicks = Stopwatch.Frequency / 10;
    private ResolvedDisplayRefresh? _resolvedDisplayRefresh;

    private sealed record ResolvedDisplayRefresh(double IntervalSeconds, DisplayRefreshSource Source, double? OverrideHz, long Timestamp);

    private (double IntervalSeconds, DisplayRefreshSource Source) ResolveDisplayRefresh()
    {
        var now = Stopwatch.GetTimestamp();
        var overrideHz = _overrideRefreshHz;
        ResolvedDisplayRefresh? cached = Volatile.Read(ref _resolvedDisplayRefresh);
        if (cached is not null && cached.OverrideHz == overrideHz && now - cached.Timestamp < DisplayRefreshMemoTicks)
            return (cached.IntervalSeconds, cached.Source);

        (var interval, DisplayRefreshSource source) = ResolveDisplayRefreshUncached(overrideHz);
        Volatile.Write(ref _resolvedDisplayRefresh, new ResolvedDisplayRefresh(interval, source, overrideHz, now));
        return (interval, source);
    }

    /// <summary>
    /// Resolves the presentation tick interval: prefers an explicitly supplied monitor refresh
    /// rate (see SetDisplayRefreshRate), then the rate the renderer reports for the monitor
    /// showing the primary surface (Stage 017A slice 3), since DWM's global composition timing
    /// does not reliably reflect any one monitor on mixed-refresh-rate setups. Falls back to the
    /// platform's detection (DWM on Windows), then to 60Hz.
    /// </summary>
    private (double IntervalSeconds, DisplayRefreshSource Source) ResolveDisplayRefreshUncached(double? overrideHz)
    {
        if (overrideHz is { } hostHz && double.IsFinite(hostHz) && hostHz > 0)
            return (1.0 / hostHz, DisplayRefreshSource.Host);

        if (Volatile.Read(ref _rendererRefreshRateSource)?.Invoke() is { } rendererHz && double.IsFinite(rendererHz) && rendererHz > 0)
            return (1.0 / rendererHz, DisplayRefreshSource.Renderer);

        return (_platformServices.GetPresentationRefreshIntervalSeconds(), DisplayRefreshSource.Platform);
    }

    internal enum DisplayRefreshSource
    {
        Host,
        Renderer,
        Platform,
    }

    private void DecodeLoop(int generation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && generation == _generation)
        {
            var decoded = false;
            decoded |= DecodeVideoBatch();

            if (_audioEngine is not null)
                decoded |= _audioEngine.DecodeAndPushAudio();

            if (!_hardwareDecodeDisabledByDeviceLoss)
                CheckForHardwareDecodeDeviceLoss();

            _ = cancellationToken.WaitHandle.WaitOne(decoded ? 1 : 10);
        }
    }

    /// <summary>
    /// Confirms and triggers recovery when the hardware-decode D3D11 device appears to have been
    /// lost. Runs on the decode thread; it only reads state and, at most, starts the dedicated
    /// recovery worker — it never performs the teardown/rebuild itself.
    /// </summary>
    private void CheckForHardwareDecodeDeviceLoss()
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder is null || !decoder.IsHardwareDecodeRepeatedlyFailing)
            return;

        // Repeated hardware-decode failure is only a hint; confirm the device is actually gone via
        // its device-removed reason before disrupting playback (avoids falling back on a bad stream).
        if (decoder.HardwareDevice is not { } hardwareDevice
            || !_hardwareAccelerationServices.IsDeviceLost(hardwareDevice))
            return;

        TriggerDecoderDeviceLostRecovery();
    }

    private void TriggerDecoderDeviceLostRecovery()
    {
        // Debounce: only one recovery worker at a time. Multiple failing decode iterations (or a
        // concurrent presentation-side device-lost) coalesce into a single recovery.
        if (Interlocked.CompareExchange(ref _decoderRecoveryInProgress, 1, 0) != 0)
            return;

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DecoderDeviceLostCategory, "Hardware video decode device lost; recovering via software fallback.");
        _decoderRecoveryScheduler.Start(RunDecoderDeviceLostRecovery);
    }

    /// <summary>
    /// Recovers hardware-decode device loss on a dedicated worker thread: first falls back to
    /// software decode at the current position (keeping audio/CDG uninterrupted and, as a side
    /// effect, stopping the emission of lost-device hardware frames so the presentation-side
    /// recovery can settle), then periodically re-probes for hardware and restores it when the
    /// device returns. Loading a new file restores hardware automatically via the normal open path.
    /// </summary>
    private void RunDecoderDeviceLostRecovery(CancellationToken cancellationToken)
    {
        try
        {
            // _currentVideoPath is written under _transportGate; this read is intentionally
            // unsynchronized because the string is immutable and SwapVideoDecoder re-validates
            // _isOpen under the gate before using it. Worst case: a stale-non-null path that
            // _isOpen then rejects.
            var path = _currentVideoPath;
            if (string.IsNullOrWhiteSpace(path) || !_isOpen)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DecoderDeviceLostRecoveryFailedCategory, "Cannot recover hardware-decode device loss: no reopenable source path.");
                RaiseState(VideoEngineState.Error);
                return;
            }

            if (!FallBackToSoftwareDecode(path, cancellationToken))
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, DecoderDeviceLostRecoveryFailedCategory, $"Software video fallback failed after {DecoderRecoveryMaxAttempts} attempts following device loss.");
                    RaiseState(VideoEngineState.Error);
                }
                return;
            }

            _hardwareDecodeDisabledByDeviceLoss = true;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DecoderDeviceLostCategory, "Video decode fell back to software after device loss; audio and CD+G overlay continue uninterrupted.");

            ReprobeAndRestoreHardware(path, cancellationToken);
        }
        finally
        {
            _ = Interlocked.Exchange(ref _decoderRecoveryInProgress, 0);
        }
    }

    private bool FallBackToSoftwareDecode(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= DecoderRecoveryMaxAttempts && !cancellationToken.IsCancellationRequested; attempt++)
        {
            if (attempt > 1)
            {
                TimeSpan delay = DecoderRecoveryBackoffDelays[Math.Min(attempt - 2, DecoderRecoveryBackoffDelays.Length - 1)];
                if (cancellationToken.WaitHandle.WaitOne(delay))
                    return false;
            }

            if (SwapVideoDecoder(VideoDecodingMethod.Software, path))
                return true;

            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, DecoderDeviceLostCategory, $"Software fallback attempt {attempt}/{DecoderRecoveryMaxAttempts} failed.");
        }

        return false;
    }

    private void ReprobeAndRestoreHardware(string path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _hardwareDecodeDisabledByDeviceLoss && _isOpen)
        {
            if (cancellationToken.WaitHandle.WaitOne(HardwareDecodeReprobeInterval))
                return;

            if (!HardwareDecodeDeviceAvailable(path))
                continue;

            if (SwapVideoDecoder(VideoDecodingMethod.Auto, path) && _videoDecoder?.IsHardwareAccelerated == true)
            {
                _hardwareDecodeDisabledByDeviceLoss = false;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DecoderDeviceLostCategory, "Hardware video decode restored after device recovery.");
                return;
            }
        }
    }

    /// <summary>
    /// Non-disruptive probe: opens a throwaway decoder that requires hardware and reports whether the
    /// hardware-decode D3D11 device could be created (i.e. the GPU is back). Does not touch the live
    /// decoder or playback.
    /// </summary>
    private bool HardwareDecodeDeviceAvailable(string path)
    {
        try
        {
            using var probe = new VideoDecoder(
                VideoDecodingMethod.Hardware,
                enableHardwareFrameOutput: false,
                enableDiagnostics: false,
                hardwareAccelerationServices: _hardwareAccelerationServices);
            return probe.Open(path) && probe.HardwareAccelerationMethod != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the live decoder with a new one using <paramref name="method"/>, reopened at the
    /// current transport position. Mirrors the seek stop-reposition-restart dance and is serialized
    /// through <see cref="_transportGate"/>, so it cannot race a user seek/transport op. Audio is
    /// deliberately left running — only the video decoder is swapped. Bumping the generation drops any
    /// in-flight frames that referenced the old (lost) device.
    /// </summary>
    // The timeline origin of the media's first decoder, carried across decoder swaps (and
    // across failed swap attempts) until the media closes.
    private double? _swapTimelineOrigin;

    private bool SwapVideoDecoder(VideoDecodingMethod method, string path)
    {
        lock (_transportGate)
        {
            if (!_isOpen)
                return false;

            var wasPlaying = _isPlaying;
            var position = GetCurrentTransportPosition();
            _generation++;
            StopDecodeLoopAndWait();
            if (!_hostRenderEventDrivenPresentation)
                StopPresentationLoopAndWait();
            WaitForPresentationIdle();

            var restartLoops = wasPlaying;
            try
            {
                return SwapVideoDecoderCore(method, path, position, wasPlaying);
            }
            catch
            {
                // Thrown mid-swap, the decoder may be disposed or half built: leave the loops
                // stopped and let the exception surface.
                restartLoops = false;
                throw;
            }
            finally
            {
                // Restarted even when the swap failed: the decode loop also pushes the audio, so
                // audio keeps playing between recovery attempts (and frozen video beats silence).
                if (restartLoops)
                {
                    StartDecodeLoop();
                    if (!_hostRenderEventDrivenPresentation)
                        StartPresentationLoop();
                }
            }
        }
    }

    private bool SwapVideoDecoderCore(VideoDecodingMethod method, string path, double position, bool wasPlaying)
    {
        // The presentation lock (taken before the decode lock, as in Stop and SeekCore): a host
        // render thread may be presenting, and both sides replace and pool-return the current frame.
        lock (_presentationLock)
            lock (_decodeLock)
            {
                // Kept in a field: a failed attempt leaves no decoder, and a retry must still
                // adopt the original origin.
                _swapTimelineOrigin = _videoDecoder?.TimelineOriginSeconds ?? _swapTimelineOrigin;
                var timelineOrigin = _swapTimelineOrigin;
                ReleaseCurrentFrame();
                _videoDecoder?.Dispose();
                _videoDecoder = new VideoDecoder(
                    method,
                    true,
                    _configuration.EnableLogging,
                    _configuration.EnablePerformanceLogging,
                    _configuration.EnableVerboseLogging,
                    sharedDemuxer: _sharedDemuxer,
                    hardwareAccelerationServices: _hardwareAccelerationServices,
                    hdrToneMapping: _configuration.HdrToneMapping);

                if (!_videoDecoder.Open(path))
                {
                    _videoDecoder.Dispose();
                    _videoDecoder = null;
                    return false;
                }

                if (timelineOrigin is { } origin)
                    _videoDecoder.AdoptTimelineOrigin(origin);

                Duration = Math.Max(_videoDecoder.DurationSeconds, _audioEngine?.Duration ?? 0);
                _clock.DurationSeconds = Duration;
                _ = _videoDecoder.SeekSharedDemuxer(position);
                _ = _videoDecoder.Seek(position, exact: false);
                if (wasPlaying && HasAudio && _audioEngine != null)
                    _presentationClock.Start(_audioEngine.Position);
                else
                    _presentationClock.Reset(position);
                _ = DecodeUntilFrameAvailable();
                _ = PresentFrameForCurrentPosition();
                return true;
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

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "VideoPipeline",
            $"Hardware video decode produced no usable startup frame; falling back to software for {Path.GetFileName(path)}. " +
            $"Stream: {_videoDecoder.DescribeDecodePath()}. Reason: {_videoDecoder.DescribeHardwareStartupFailure()}.",
            sourcePath: path);
        _videoDecoder.Dispose();
        _videoDecoder = new VideoDecoder(
            VideoDecodingMethod.Software,
            true,
            _configuration.EnableLogging,
            _configuration.EnablePerformanceLogging,
            _configuration.EnableVerboseLogging,
            sharedDemuxer: _sharedDemuxer,
            hardwareAccelerationServices: _hardwareAccelerationServices,
            hdrToneMapping: _configuration.HdrToneMapping);
        if (!_videoDecoder.Open(path))
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "VideoPipeline", $"Software video fallback failed for {Path.GetFileName(path)}", sourcePath: path);
            _videoDecoder.Dispose();
            _videoDecoder = null;
            return false;
        }

        Duration = Math.Max(_videoDecoder.DurationSeconds, _audioEngine?.Duration ?? 0);
        _clock.DurationSeconds = Duration;
        _ = _videoDecoder.SeekSharedDemuxer(0);
        _ = _videoDecoder.Seek(0, exact: false);
        _presentationClock.Reset(HasAudio ? _audioEngine!.Position : _clock.PositionSeconds);
        return DecodeUntilFrameAvailable();
    }

    private double PresentFrameForCurrentPosition(bool forceReissue = false)
    {
        var clockPosition = GetPresentationPosition();
        var targetPosition = ApplyAudioVideoOffset(GetDisplayAdjustedPresentationPosition(clockPosition));
        PresentFrameForPosition(targetPosition, ApplyAudioVideoOffset(clockPosition), forceReissue);
        return clockPosition;
    }

    private double ApplyAudioVideoOffset(double position)
    {
        var corrected = position + _sync.AudioVideoOffsetSeconds;
        return Duration > 0
            ? Math.Clamp(corrected, 0, Duration)
            : Math.Max(0, corrected);
    }

    private double GetPresentationPosition()
    {
        VideoAudioEngine? audioEngine = _audioEngine;
        if (audioEngine is not null && audioEngine.TryGetPositionSnapshot(out var audioPosition))
        {
            // audioPosition already wraps naturally back into [0, Duration) across a loop
            // boundary - VideoAudioEngine reports a continuous internal position minus
            // completed-loop offsets (see AudioLoopBoundaryTracker), so no override is
            // needed here for the loop case.
            return _isPlaying
                ? _presentationClock.GetPosition(audioPosition, Duration)
                : audioPosition;
        }

        return _clock.PositionSeconds;
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

    private double GetPresentationLeadSeconds() =>
        PresentationLeadController.LeadSeconds(GetDisplayRefreshIntervalSeconds(), FramesPerSecond, GetActivePresentationLeadRefreshIntervals());

    private double GetActivePresentationLeadRefreshIntervals()
    {
        var active = _activePresentationLeadRefreshIntervals;
        if (double.IsFinite(active) && active > 0)
            return active;

        active = GetInitialPresentationLeadRefreshIntervals();
        _activePresentationLeadRefreshIntervals = active;
        return active;
    }

    private double GetInitialPresentationLeadRefreshIntervals() =>
        PresentationLeadController.InitialLeadRefreshes(_configuration.PresentationLeadRefreshIntervals);

    private void ResetPresentationTimingController()
    {
        RequestDisplayLockedReset();
        _activePresentationLeadRefreshIntervals = GetInitialPresentationLeadRefreshIntervals();
        _postSeekRecovery.Reset();
        ResetPresentationDiagnostics();
    }

    private void BeginPostSeekPresentationRecovery()
    {
        RequestDisplayLockedReset();
        var bufferedFrames = _videoDecoder?.FrameCount ?? 0;
        var recoveryMilliseconds = _postSeekRecovery.Begin(bufferedFrames, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        ResetPresentationDiagnostics();
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine(
                $"[VideoPipeline:SeekRecovery] Started {recoveryMilliseconds}ms recovery window; " +
                $"buffered={bufferedFrames}/{GetTargetBufferedVideoFrames()}, lead={GetActivePresentationLeadRefreshIntervals():F3} refreshes, " +
                $"adaptiveSuppress={PostSeekRecovery.AdaptiveSuppressionWindows} windows.");
        }
    }

    private void PreparePostSeekVideoRecovery(double target)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        var targetBufferedFrames = PostSeekRecovery.WarmupTargetFrames(GetTargetBufferedVideoFrames(), VideoFrameBufferPolicy.MinBufferedFrames);
        var decodedFrames = 0;
        int firstPassDecodedFrames;

        lock (_decodeLock)
        {
            firstPassDecodedFrames = DecodePostSeekWarmupFrames(
                decoder,
                targetBufferedFrames,
                PostSeekRecovery.WarmupDecodeBudgetMilliseconds);

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
                    PostSeekRecovery.WarmupRetryBudgetMilliseconds);
            }

            // A shared seek starts at the preceding video keyframe. Long-GOP
            // software streams can need substantially more than the normal
            // recovery budget to decode through that preroll before any frame
            // reaches the requested timeline. Do that work before audio starts
            // its presentation clock, rather than resuming with an empty queue.
            if (!HasSupersedingSeekRequest() && _sharedDemuxer != null && decoder.FrameCount == 0)
            {
                decodedFrames += DecodePostSeekWarmupFrames(
                    decoder,
                    targetBufferedFrames,
                    PostSeekRecovery.SharedDemuxerPrerollBudgetMilliseconds);
            }
        }

        if (_configuration.EnableLogging)
        {
            Debug.WriteLine(
                $"[VideoPipeline:SeekRecovery] Warmed video queue after seek to {target:F3}s: " +
                $"decoded={decodedFrames} (firstPass={firstPassDecodedFrames}), queued={decoder.FrameCount}/{targetBufferedFrames}, " +
                $"budget={PostSeekRecovery.WarmupDecodeBudgetMilliseconds}+{PostSeekRecovery.WarmupRetryBudgetMilliseconds}ms.");
        }
    }

    private int DecodePostSeekWarmupFrames(VideoDecoder decoder, int targetBufferedFrames, int budgetMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        var decodedFrames = 0;

        while (decoder.FrameCount < targetBufferedFrames && stopwatch.ElapsedMilliseconds < budgetMilliseconds)
        {
            // A seek at or near the end leaves nothing to decode: stop instead of spinning out the
            // budget (up to about 3 s with the transport gate held).
            if (HasSupersedingSeekRequest() || decoder.EndOfStream)
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
        VideoLoopBoundary.HasReachedNaturalEnd(_isPlaying, Duration, position, GetEndToleranceSeconds());

    private double GetEndToleranceSeconds() => VideoLoopBoundary.EndToleranceSeconds(FramesPerSecond);

    private bool ShouldRestartLoopPlayback(double currentPosition)
    {
        if (!LoopPlayback || !_isPlaying || Duration <= 0)
            return false;

        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return false;

        return VideoLoopBoundary.ShouldRestartLoop(
            decoder.EndOfStream,
            decoder.FrameCount,
            currentPosition,
            GetLoopBoundarySeconds(decoder),
            GetEndToleranceSeconds());
    }

    // Proactively pre-seek the decoder while tail frames are still queued. The decoded tail stays
    // visible while position-zero frames are refilled behind it, allowing either the audio wrap or
    // the virtual video-only clock to commit the next lap without a full restart at EOF.
    private void TryPreSeekLoopStart()
    {
        if (_loopPreSeekActive || !LoopPlayback || !_isPlaying || Duration <= 0)
            return;

        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null || !decoder.EndOfStream || decoder.FrameCount <= 0)
            return;

        lock (_decodeLock)
        {
            if (_loopPreSeekActive || !decoder.EndOfStream || decoder.FrameCount <= 0)
                return;

            if (HasAudio)
            {
                // Drain remaining audio packets into the queue, then seek the shared cursor to 0.
                // This keeps BASS fed while position-zero video frames are prepared.
                _sharedDemuxer?.PrepareLoopWrap();
            }
            else if (_sharedDemuxer?.Seek(decoder.VideoStreamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            {
                return;
            }

            decoder.OnContainerSeeked(0, exact: false, preserveQueue: true);

            // Clear the committed presentation target so position-0 frames aren't dropped
            // as "stale" by DropFramesBeforeCommittedVideoTarget.
            SeekPump.ClearMinimumCommittedTarget();

            SeamlessLoopRestart.TopUpDecode(
                decoder,
                SeamlessLoopRestart.DefaultTopUpBatchSize,
                SeamlessLoopRestart.GetTopUpThreshold(GetTargetBufferedVideoFrames()));
            _loopPreSeekActive = true;
        }

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine($"[VideoPipeline:LoopPreSeek] Pre-seek to 0; audio={HasAudio}, queued={decoder.FrameCount}/{GetTargetBufferedVideoFrames()}");
    }

    private bool TryCommitVideoOnlyLoopWrap(double currentPosition)
    {
        if (!_loopPreSeekActive || HasAudio || !_isPlaying || _videoDecoder is not { } decoder)
            return false;

        var loopBoundary = GetLoopBoundarySeconds(decoder);
        if (currentPosition < loopBoundary - GetEndToleranceSeconds())
            return false;

        var loopTarget = GetLoopOverflowPosition(currentPosition, loopBoundary);
        VideoFrameData? wrapFrame;
        lock (_decodeLock)
        {
            DropStaleLoopTailFrames(decoder, GetEndToleranceSeconds());
            if (decoder.FrameCount == 0)
                _ = DecodeUntilFrameAvailable();

            wrapFrame = decoder.GetNextFrame();
        }

        if (wrapFrame != null)
            PresentFrame(wrapFrame, forceReissue: true);

        _loopPreSeekActive = false;
        _clock.Start(loopTarget);
        _state.SetPosition(loopTarget);
        PositionChanged?.Invoke(this, loopTarget);
        MediaLooped?.Invoke(this, EventArgs.Empty);
        _ = Interlocked.Exchange(ref _playbackEndSignaled, 0);

        if (wrapFrame == null)
            _ = PresentFrameForCurrentPosition(forceReissue: true);

        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine($"[VideoPipeline:VideoOnlyLoopWrap] Commit position={loopTarget:F3}s, queued={decoder.FrameCount}/{GetTargetBufferedVideoFrames()}, decodedFirst={wrapFrame != null}.");

        return true;
    }

    // Flush remaining tail frames (PTS > threshold) from the front of the
    // queue so position-0 frames are reachable after the clock resets to 0.
    private void DropStaleLoopTailFrames(VideoDecoder decoder, double threshold)
    {
        var dropped = 0;
        while (decoder.PeekNextFrame() is { } nextFrame && nextFrame.PresentationTime > threshold)
        {
            VideoFrameData? stale = decoder.GetNextFrame();
            if (stale == null)
                break;

            decoder.ReturnFrame(stale);
            dropped++;
        }

        if (dropped > 0 && _configuration.EnableVerboseLogging)
            Trace.WriteLine($"[VideoPipeline:LoopPhase2] Dropped {dropped} stale tail frame(s) above {threshold:F3}s");
    }

    private bool HasReachedPlaybackEnd(double currentPosition)
    {
        if (HasReachedNaturalEnd(currentPosition))
            return true;

        VideoDecoder? decoder = _videoDecoder;
        return decoder != null && VideoLoopBoundary.HasDrainedAtEnd(
            decoder.EndOfStream,
            decoder.FrameCount,
            HasAudio,
            currentPosition,
            GetLoopBoundarySeconds(decoder),
            GetEndToleranceSeconds());
    }

    private double GetLoopBoundarySeconds(VideoDecoder decoder) =>
        VideoLoopBoundary.LoopBoundarySeconds(decoder.DurationSeconds, Duration);

    private static double GetLoopOverflowPosition(double currentPosition, double loopBoundary) =>
        VideoLoopBoundary.LoopOverflowPosition(currentPosition, loopBoundary);

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
            _audioEngine.EndFadeSessionAtEnd();
            _presentationClock.Pause(Duration);
        }
        else
            _clock.Pause();

        _state.SetPosition(Duration);
        _state.SetPlaybackState(MediaPlayerState.Stopped);
        RaiseState(VideoEngineState.Stopped);
        PlaybackEnded?.Invoke(this, EventArgs.Empty);
    }

    private bool SeekVideoNearTarget(double target, bool asPreview, bool allowPreviewFallback)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return false;

        if (allowPreviewFallback && IsTinyVideoSeek(target))
            return true;

        // A lone click uses the exact settle rule. Active scrubbing can show a
        // nearby frame for feedback; the final settle seek always restores the
        // exact post-target playback start.
        var requireAtOrAfterTarget = !allowPreviewFallback;
        if (!decoder.Seek(target, exact: requireAtOrAfterTarget))
            return false;

        var allowSupersedingAbort = !allowPreviewFallback;
        if (allowSupersedingAbort && HasSupersedingSeekRequest())
            return false;

        var maxFrameDistance = allowPreviewFallback
            ? PreviewMaxFrameDistanceSeconds
            : SettleMaxFrameDistanceSeconds;
        var useFastPreviewBudget = asPreview && allowPreviewFallback;
        var decodeBudgetMs = GetSeekDecodeBudgetMilliseconds(useFastPreviewBudget, decoder);
        var maxDecodeAttempts = GetSeekDecodeMaxAttempts(useFastPreviewBudget, decoder);

        DecodeForSeekTarget(
            target,
            maxFrameDistance,
            decodeBudgetMs,
            maxDecodeAttempts,
            requireAtOrAfterTarget,
            allowSupersedingAbort);

        if (allowSupersedingAbort && HasSupersedingSeekRequest())
            return false;

        return TryPresentBestSeekFrame(
            target,
            maxFrameDistance,
            requireAtOrAfterTarget,
            allowPreviewFallback);
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
            {
                // A full queue with no acceptable frame yet (a preview seek far past a keyframe):
                // drop a frame that a later one replaces, so decoding can go on under the cap.
                if (decoder.FrameCount >= decoder.MaxFrames && TryDropSupersededSeekFrame(decoder, target))
                    continue;

                return;
            }

            if (!decoded)
                _ = Thread.Yield();
        }
    }

    private static bool TryDropSupersededSeekFrame(VideoDecoder decoder, double target)
    {
        using IEnumerator<VideoFrameData> queued = decoder.EnumerateQueuedFrames().GetEnumerator();
        if (!queued.MoveNext())
            return false;

        VideoFrameData head = queued.Current;
        if (!queued.MoveNext() || !VideoSeekPolicy.CanDropQueuedSeekFrame(head.PresentationTime, queued.Current.PresentationTime, target))
            return false;

        decoder.ReturnFrame(decoder.GetNextFrame());
        return true;
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

        if (requireAtOrAfterTarget && !allowPreviewFallback)
        {
            // SeekCore stops both loops before this point. Exact seeking drops all
            // pre-target frames, so the queue head is the visual playback start.
            // Preserve its successors for the restarted presentation loop.
            VideoFrameData? firstFrame = decoder.GetNextFrame();
            if (firstFrame == null)
                return false;

            if (!IsSeekFrameAcceptable(firstFrame.PresentationTime, target, maxFrameDistance, requireAtOrAfterTarget))
            {
                decoder.ReturnFrame(firstFrame);
                return false;
            }

            PresentFrame(firstFrame);
            return true;
        }

        VideoFrameData? bestFrame = null;
        VideoFrameData? fallbackFrame = null;
        var bestDistance = double.MaxValue;
        var fallbackDistance = double.MaxValue;
        while (decoder.GetNextFrame() is { } frame)
        {
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
                if (fallbackFrame != null)
                    decoder.ReturnFrame(fallbackFrame);

                fallbackFrame = frame;
                fallbackDistance = distance;
            }
            else
                decoder.ReturnFrame(frame);
        }

        var usedFallback = bestFrame == null && fallbackFrame != null;
        bestFrame ??= fallbackFrame;
        if (bestFrame == null)
            return false;

        if (bestFrame != fallbackFrame && fallbackFrame != null)
            decoder.ReturnFrame(fallbackFrame);

        if (usedFallback && _configuration.EnableVerboseLogging)
        {
            Debug.WriteLine(
                $"[VideoPipeline:SmartSeek] Preview fallback frame at {bestFrame.PresentationTime:F3}s " +
                $"for target {target:F3}s.");
        }

        PresentFrame(bestFrame);
        return true;
    }

    private bool IsTinyVideoSeek(double target)
    {
        VideoFrameData? currentFrame = _currentFrame;
        return currentFrame != null && Math.Abs(currentFrame.PresentationTime - target) <= TinyVideoSeekThresholdSeconds;
    }

    private static bool IsSeekFrameAcceptable(double frameTime, double target, double maxFrameDistance, bool requireAtOrAfterTarget) =>
        VideoSeekPolicy.IsFrameAcceptable(frameTime, target, maxFrameDistance, requireAtOrAfterTarget);

    private static int GetSeekDecodeBudgetMilliseconds(bool useFastPreviewBudget, VideoDecoder decoder) =>
        VideoSeekPolicy.GetDecodeBudgetMilliseconds(useFastPreviewBudget, decoder.IsHardwareAccelerated);

    private static int GetSeekDecodeMaxAttempts(bool useFastPreviewBudget, VideoDecoder decoder) =>
        VideoSeekPolicy.GetDecodeMaxAttempts(useFastPreviewBudget, decoder.IsHardwareAccelerated);

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

    private bool ShouldSuppressLateFrameDrop() => _postSeekRecovery.ShouldSuppressLateFrameDrop(Stopwatch.GetTimestamp());

    private void PresentFrameForPosition(double targetPosition, double clockPosition, bool forceReissue = false, bool displayLocked = false)
    {
        VideoDecoder? decoder = _videoDecoder;
        if (decoder == null)
            return;

        DropFramesBeforeCommittedVideoTarget(decoder);

        // Display-locked timing already asks for the refresh's own media time: the latest frame
        // due by then, with only enough slack for timestamp rounding.
        var maxPresentationTime = displayLocked
            ? targetPosition + DisplayLockedPtsToleranceSeconds
            : Math.Min(
                targetPosition + GetFrameLeadTolerance(decoder.FramesPerSecond),
                clockPosition + GetFrameSelectionLeadLimit(decoder.FramesPerSecond));
        VideoFrameData? frameToPresent = null;
        var dequeuedFrames = 0;

        while (decoder.PeekNextFrame() is { } nextFrame)
        {
            if (nextFrame.PresentationTime > maxPresentationTime)
                break;

            // Loop-wrap guard: while a pre-seeked loop restart is awaiting its audio-side
            // commit, the queue holds next-lap frames (PTS near 0) while the clock is still
            // near the end of the current lap. Those frames must be neither presented nor
            // late-dropped - they are the warm queue CommitAudioLoopWrap depends on. The
            // guard must fire whether the next-lap frame sits behind an already-selected
            // tail frame or is already at the head of the queue (the tail's real last frame
            // often lands before the audio boundary, exhausting the tail a few ticks early);
            // without the head case, the selector eats the next lap's opening frames as
            // "late", leaving the commit with nothing to present until the clock catches up
            // to whatever survived - or, if everything is eaten, letting the reactive
            // restart fire and destroy the gapless audio wrap.
            if (_loopPreSeekActive && clockPosition - nextFrame.PresentationTime > LoopWrapFrameGuardSeconds)
                break;

            if (frameToPresent != null
                && !displayLocked
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

    private int GetTargetBufferedVideoFrames() =>
        VideoFrameBufferPolicy.GetTargetBufferedFrames(Width, Height, FramesPerSecond);

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
        var adaptiveAction = _postSeekRecovery.NextWindow(Stopwatch.GetTimestamp(), out var remaining) switch
        {
            PostSeekRecovery.WindowAction.Recovering => "seek-recovery",
            PostSeekRecovery.WindowAction.Suppressed => $"post-seek-suppress remaining={remaining}",
            _ => UpdateAdaptivePresentationLead(refreshIntervalMs),
        };

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

        var previous = GetActivePresentationLeadRefreshIntervals();
        var active = PresentationLeadController.Adjust(
            previous,
            _presentationDiagnosticClockLateSamples,
            _presentationDiagnosticMaxClockLateMs,
            _presentationDiagnosticMaxEarlyMs,
            refreshIntervalMs,
            FramesPerSecond);

        _activePresentationLeadRefreshIntervals = active;
        return Math.Abs(active - previous) < 0.0001
            ? "hold"
            : active > previous
                ? $"increase {previous:F3}->{active:F3}"
                : $"decrease {previous:F3}->{active:F3}";
    }

    private double GetAdaptiveClockLateThresholdMilliseconds(double refreshIntervalMs) =>
        PresentationLeadController.ClockLateThresholdMilliseconds(refreshIntervalMs, FramesPerSecond);

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
        // Kept even when not looping (a CPU-frame copy, up to 33 MB at 4K): loop is often turned
        // on after playback starts, and the first wrap needs the snapshot to stay gapless.
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
        Targets = t_frameReadyTargets,
        PixelData = frame.Data != null
            ? new ReadOnlyMemory<byte>(frame.Data, 0, frame.Length)
            : ReadOnlyMemory<byte>.Empty,
        Width = frame.Width,
        Height = frame.Height,
        Stride = frame.Stride,
        PtsSeconds = frame.PresentationTime,
        FramesPerSecond = FramesPerSecond,
        NativeResource = frame.NativeResource
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

        TempMediaFile.TryDelete(_tempPath);
        _tempPath = null;
    }
}
