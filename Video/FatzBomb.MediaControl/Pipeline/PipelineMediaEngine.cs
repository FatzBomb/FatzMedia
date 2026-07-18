using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Core.Video;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class PipelineMediaEngine : IMediaEngine, ILoopPlaybackControl, IMediaErrorSource, IClockBackedMediaEngine
{
    private readonly IAudioEngine _audioEngine;
    private readonly IArchiveService _archiveService;
    private readonly PlaybackCoordinator _coordinator;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly IPrecisionWaiterFactory _precisionWaiterFactory;
    private readonly IVideoHardwareAccelerationServices _hardwareAccelerationServices;
    private readonly PlaybackScheduler _backgroundVideoScheduler = new("BackgroundVideoPresentation");
    private readonly Lock _backgroundGate = new();
    private VideoDecoder? _backgroundVideoDecoder;
    private string? _backgroundVideoTempPath;
    private VideoFrameEventArgs? _currentImageFrame;
    private volatile int _backgroundGeneration;
    private bool _disposed;

    event Action<MediaErrorInfo>? IMediaErrorSource.ErrorOccurred
    {
        add => ErrorOccurred += value;
        remove => ErrorOccurred -= value;
    }

    private event Action<MediaErrorInfo>? ErrorOccurred;

    private void RaiseError(MediaErrorCode code, string message, string? sourcePath = null, Exception? innerException = null)
    {
        Debug.WriteLine($"[PipelineMediaEngine] Error {code}: {message}");
        ErrorOccurred?.Invoke(new MediaErrorInfo(code, message, sourcePath, innerException));
    }

    public PipelineMediaEngine(
        IAudioEngine audioEngine,
        ICdgEngine cdgEngine,
        ISubtitleEngine subtitleEngine,
        IArchiveService archiveService,
        IVideoEngineFactory videoEngineFactory,
        IMidiLyricProvider midiLyricProvider,
        IPrecisionWaiterFactory precisionWaiterFactory,
        IVideoHardwareAccelerationServices hardwareAccelerationServices,
        MediaPlayerConfiguration configuration,
        bool ownsAudioEngine = false)
    {
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _archiveService = archiveService ?? throw new ArgumentNullException(nameof(archiveService));
        _configuration = configuration;
        _precisionWaiterFactory = precisionWaiterFactory ?? throw new ArgumentNullException(nameof(precisionWaiterFactory));
        _hardwareAccelerationServices = hardwareAccelerationServices ?? throw new ArgumentNullException(nameof(hardwareAccelerationServices));
        ArgumentNullException.ThrowIfNull(cdgEngine);
        ArgumentNullException.ThrowIfNull(subtitleEngine);
        _coordinator = new PlaybackCoordinator(_audioEngine, cdgEngine, subtitleEngine, videoEngineFactory, midiLyricProvider, configuration, ownsAudioEngine);
        _coordinator.PlaybackEnded += OnPlaybackEnded;
    }

    public IVideoEngine? Video => _coordinator.Video;
    public ICdgEngine? Cdg => _coordinator.Cdg;
    public ISubtitleEngine? Subtitle => _coordinator.Subtitle;
    public IReadOnlyList<SubtitleTrack> SubtitleTracks => _coordinator.SubtitleTracks;
    public string? SelectedSubtitleTrackId => _coordinator.SelectedSubtitleTrackId;
    public bool ShowMidiLyrics
    {
        get => _coordinator.ShowMidiLyrics;
        set => _coordinator.ShowMidiLyrics = value;
    }
    public double AudioVideoOffsetSeconds => _coordinator.AudioVideoOffsetSeconds;
    public double CdgOffsetSeconds => _coordinator.CdgOffsetSeconds;
    public double SubtitleOffsetSeconds => _coordinator.SubtitleOffsetSeconds;
    public IAudioEngine Audio => _coordinator.ActiveAudio;
    IClockSource IClockBackedMediaEngine.ClockSource => _coordinator.ClockSource;

    public void SetDisplayRefreshRate(double? refreshHz) => _coordinator.SetDisplayRefreshRate(refreshHz);
    public string? ImagePath { get; private set; }
    public bool BackgroundAutoDetect { get; set; } = true;
    public MediaBackgroundCandidate? Background { get; private set; }

    public MediaContent Content => _coordinator.State.Content;
    public MediaPlayerState State => _coordinator.State.PlaybackState;
    public bool IsOpen => _coordinator.State.IsOpen;
    public double Duration => _coordinator.DurationSeconds;

    public bool LoopPlayback
    {
        get => _coordinator.LoopPlayback;
        set => _coordinator.LoopPlayback = value;
    }


    public event EventHandler? MediaEnded;
    public event EventHandler<VideoFrameEventArgs>? ImageFrameReady;
    public event EventHandler<VideoFrameEventArgs>? BackgroundFrameReady;
    public event EventHandler? BackgroundCleared;

    public bool TryGetCurrentImageFrame(out VideoFrameEventArgs? frame)
    {
        frame = _currentImageFrame;
        return frame != null;
    }

    public double Position
    {
        get => _coordinator.PositionSeconds;
        set => Seek(value);
    }

    public bool Open(string path)
    {
        ThrowIfDisposed();

        if (IsOpen)
        {
            Close();
        }
        ImagePath = null;
        _currentImageFrame = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Debug.WriteLine($"[PipelineMediaEngine] File not found: {path}");
            RaiseError(MediaErrorCode.FileNotFound, $"File not found: {path}", path);
            return false;
        }

        using FileTree fileTree = FileTreeBuilder.GetFileTree(path, _archiveService);
        var opened = fileTree.Type switch
        {
            FileType.Audio when !string.IsNullOrWhiteSpace(fileTree.AudioPath) =>
                _coordinator.OpenAudio(fileTree.AudioPath),
            FileType.AudioCdg when !string.IsNullOrWhiteSpace(fileTree.AudioPath) && !string.IsNullOrWhiteSpace(fileTree.CdgPath) =>
                _coordinator.OpenAudioCdg(fileTree.AudioPath, fileTree.CdgPath),
            FileType.Cdg when !string.IsNullOrWhiteSpace(fileTree.CdgPath) =>
                _coordinator.OpenStandaloneCdg(fileTree.CdgPath),
            FileType.AudioArchive when fileTree.AudioEntry != null =>
                _coordinator.OpenAudio(fileTree.AudioEntry),
            FileType.AudioCdgArchive when fileTree.AudioEntry != null && fileTree.CdgEntry != null =>
                _coordinator.OpenAudioCdg(fileTree.AudioEntry, fileTree.CdgEntry),
            FileType.CdgArchive when fileTree.CdgEntry != null =>
                _coordinator.OpenStandaloneCdg(fileTree.CdgEntry),
            FileType.VideoSubtitle when !string.IsNullOrWhiteSpace(fileTree.VideoPath) && !string.IsNullOrWhiteSpace(fileTree.SubtitlePath) =>
                _coordinator.OpenVideo(fileTree.VideoPath, fileTree.SubtitlePath),
            FileType.Video when !string.IsNullOrWhiteSpace(fileTree.VideoPath) =>
                _coordinator.OpenVideo(fileTree.VideoPath),
            FileType.VideoArchive when fileTree.VideoEntry != null =>
                _coordinator.OpenVideo(fileTree.VideoEntry),
            FileType.Image when !string.IsNullOrWhiteSpace(fileTree.ImagePath) =>
                OpenImage(fileTree.ImagePath),
            _ => RejectUnsupported(path, fileTree.Type)
        };

        if (!opened)
        {
            MediaErrorCode code = fileTree.Type is FileType.Unsupported or FileType.UnsupportedArchive
                ? MediaErrorCode.UnsupportedFormat
                : MediaErrorCode.OpenFailed;
            RaiseError(code, $"Failed to open media: {Path.GetFileName(path)}", path);
        }

        return opened;
    }

    public bool Open(MediaInputSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        return source.Kind switch
        {
            MediaInputSourceKind.FilePath when !string.IsNullOrWhiteSpace(source.FilePath) => Open(source.FilePath),
            MediaInputSourceKind.Bgra32Image => OpenImage(source),
            _ => RejectUnknownSource(source)
        };
    }

    private bool RejectUnknownSource(MediaInputSource source)
    {
        RaiseError(MediaErrorCode.UnsupportedFormat, $"Unsupported media source kind: {source.Kind}", source.DisplayName);
        return false;
    }

    public bool SetBackground(MediaBackgroundCandidate? background)
    {
        ThrowIfDisposed();

        ClearBackgroundCore(raiseEvent: false);
        Background = background;

        if (background == null || background.Kind == MediaBackgroundKind.None)
        {
            BackgroundCleared?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return background.Kind switch
        {
            MediaBackgroundKind.Image => PresentBackgroundImage(background),
            MediaBackgroundKind.Video => StartBackgroundVideo(background),
            _ => false
        };
    }

    public void ClearBackground() => ClearBackgroundCore(raiseEvent: true);

    public bool Play()
    {
        ThrowIfDisposed();
        var success = _coordinator.Play();
        if (success)
        {
            StartBackgroundVideoPlayback();
        }

        return success;
    }

    public bool Pause()
    {
        ThrowIfDisposed();
        var success = _coordinator.Pause();
        if (success)
        {
            StopBackgroundVideoPlayback(wait: true);
        }

        return success;
    }

    public bool Stop()
    {
        ThrowIfDisposed();
        var success = _coordinator.Stop();
        StopBackgroundVideoPlayback(wait: true);
        ResetBackgroundVideoToStart();
        return success;
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        ClearBackgroundCore(raiseEvent: true);
        _coordinator.Close();
        ImagePath = null;
        _currentImageFrame = null;
    }

    public bool Seek(double seconds)
    {
        ThrowIfDisposed();
        return _coordinator.Seek(seconds);
    }

    public bool SelectSubtitleTrack(string? trackId)
    {
        ThrowIfDisposed();
        return _coordinator.SelectSubtitleTrack(trackId);
    }

    public double SetAudioVideoOffset(double seconds) => _coordinator.SetAudioVideoOffset(seconds);
    public double SetCdgOffset(double seconds) => _coordinator.SetCdgOffset(seconds);
    public double SetSubtitleOffset(double seconds) => _coordinator.SetSubtitleOffset(seconds);
    public double AdjustAudioVideoOffset(double seconds) => _coordinator.AdjustAudioVideoOffset(seconds);
    public double AdjustCdgOffset(double seconds) => _coordinator.AdjustCdgOffset(seconds);
    public double AdjustSubtitleOffset(double seconds) => _coordinator.AdjustSubtitleOffset(seconds);
    public void ResetSyncOffsets() => _coordinator.ResetSyncOffsets();

    public Task<bool> SeekAsync(double seconds, TimeSpan? timeout = null) => Task.FromResult(Seek(seconds));

    public void Dispose()
    {
        if (_disposed)
            return;

        ClearBackgroundCore(raiseEvent: false);
        _backgroundVideoScheduler.Dispose();
        _coordinator.PlaybackEnded -= OnPlaybackEnded;
        _coordinator.Dispose();
        _disposed = true;
    }

    private bool PresentBackgroundImage(MediaBackgroundCandidate background)
    {
        if (background.Source?.IsBgraImage == true)
        {
            return RaiseBackgroundFrame(background.Source);
        }

        using VideoDecoder? decoder = OpenBackgroundDecoder(background, enableHardwareFrameOutput: true);
        if (decoder == null)
            return false;

        VideoFrameData? frame = DecodeFirstBackgroundFrame(decoder);
        if (frame == null)
            return false;

        RaiseBackgroundFrame(decoder, frame, decoder.FramesPerSecond);
        return true;
    }

    private bool StartBackgroundVideo(MediaBackgroundCandidate background)
    {
        VideoDecoder? decoder = OpenBackgroundDecoder(background, enableHardwareFrameOutput: true);
        if (decoder == null)
        {
            return false;
        }

        if (_configuration.EnableVerboseLogging)
        {
            Trace.WriteLine(
                $"[BackgroundVideo] Opened sidecar video: duration={decoder.DurationSeconds:F3}s, fps={decoder.FramesPerSecond:F2}, " +
                $"codec={decoder.CodecName}, hwFrameOutput=true, isHardwareAccelerated={decoder.IsHardwareAccelerated}, " +
                $"width={decoder.Width}, height={decoder.Height}, source={background.DisplayName}");
        }

        VideoFrameData? firstFrame = DecodeFirstBackgroundFrame(decoder);
        if (firstFrame == null)
        {
            decoder.Dispose();
            return false;
        }

        if (_configuration.EnableVerboseLogging)
        {
            Trace.WriteLine(
                $"[BackgroundVideo] First frame: pts={firstFrame.PresentationTime:F3}s, hw={firstFrame.IsNativeHardwareFrame}, " +
                $"width={firstFrame.Width}, height={firstFrame.Height}");
        }

        RaiseBackgroundFrame(decoder, firstFrame, decoder.FramesPerSecond);

        lock (_backgroundGate)
        {
            _backgroundVideoDecoder = decoder;
        }

        if (State == MediaPlayerState.Playing)
        {
            StartBackgroundVideoPlayback();
        }

        return true;
    }

    private void BackgroundVideoLoop(int generation, CancellationToken cancellationToken)
    {
        using var pacer = new MediaLoopPacer(_precisionWaiterFactory);
        var startTimestamp = Stopwatch.GetTimestamp();
        var lastPresentedPts = double.NaN;
        var maxPresentedPts = double.NaN;
        var iterationCount = 0;
        var restartCount = 0;
        while (!cancellationToken.IsCancellationRequested && generation == _backgroundGeneration)
        {
            VideoDecoder? decoder = _backgroundVideoDecoder;
            if (decoder == null)
            {
                return;
            }

            VideoFrameData? frame = DecodeNextBackgroundVideoFrame(decoder, restartCount: restartCount, lastPresentedPts: lastPresentedPts, iterationCount: iterationCount, startTimestamp: startTimestamp);
            if (frame == null)
            {
                restartCount++;
                if (_configuration.EnableVerboseLogging)
                {
                    var elapsedSec = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                    Trace.WriteLine(
                        $"[BackgroundVideo] Restart #{restartCount} at {elapsedSec:F2}s, lastPts={lastPresentedPts:F3}s, maxPts={maxPresentedPts:F3}s, " +
                        $"iterations={iterationCount}, queueDepth={decoder.FrameCount}, fps={decoder.FramesPerSecond:F2}");
                }

                frame = RestartBackgroundVideo(decoder);
                pacer.Reset();
            }

            if (frame != null)
            {
                lastPresentedPts = frame.PresentationTime;
                if (double.IsNaN(maxPresentedPts) || frame.PresentationTime > maxPresentedPts)
                    maxPresentedPts = frame.PresentationTime;

                RaiseBackgroundFrame(decoder, frame, decoder.FramesPerSecond);

                if (_configuration.EnableVerboseLogging && iterationCount > 0 && iterationCount % 60 == 0)
                {
                    var elapsedSec = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                    Trace.WriteLine(
                        $"[BackgroundVideo] iter={iterationCount}, elapsed={elapsedSec:F2}s, pts={frame.PresentationTime:F3}s, " +
                        $"hw={frame.IsNativeHardwareFrame}, queueDepth={decoder.FrameCount}");
                }
            }

            // Pre-restart buffering + top-up via the shared seamless-loop primitive (also used
            // by the primary VideoPipeline). At EOF with tail frames still queued, seek the
            // decoder back to 0 preserving the queue, then decode a few packets inline so
            // position-0 frames are ready before the tail runs out. See SeamlessLoopRestart.
            if (SeamlessLoopRestart.TryPreSeekToStart(decoder) && _configuration.EnableVerboseLogging)
            {
                var elapsedSec = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                Trace.WriteLine(
                    $"[BackgroundVideo] Pre-seek to start at {elapsedSec:F2}s, queued={decoder.FrameCount}, " +
                    $"lastPts={lastPresentedPts:F3}s");
            }

            SeamlessLoopRestart.TopUpDecode(
                decoder,
                SeamlessLoopRestart.DefaultTopUpBatchSize,
                SeamlessLoopRestart.GetTopUpThreshold(decoder.MaxFrames));

            iterationCount++;
            var interval = VideoPresentationTiming.GetBackgroundPresentationInterval(decoder.FramesPerSecond);
            pacer.WaitNext(
                TimeSpan.FromSeconds(interval),
                cancellationToken.WaitHandle,
                lateYield: TimeSpan.FromMilliseconds(1));
        }
    }

    private VideoFrameData? DecodeNextBackgroundVideoFrame(VideoDecoder decoder, int restartCount, double lastPresentedPts, int iterationCount, long startTimestamp)
    {
        var attempts = 0;
        for (var i = 0; i < 120; i++)
        {
            VideoFrameData? frame = decoder.GetNextFrame();
            if (frame != null)
                return frame;

            if (!decoder.DecodeNextFrame())
            {
                // EndOfStream means the file is fully read (real EOF) — break so the caller
                // can restart the loop. Without EndOfStream, the false just means a video
                // packet was sent to the decoder but no frame was produced yet (normal for
                // B-frame / packed-bitstream video where the decoder pipelines several packets
                // before emitting output). Breaking here would cause a premature restart after
                // only the first few frames — continue the loop instead.
                if (decoder.EndOfStream)
                {
                    if (_configuration.EnableVerboseLogging)
                    {
                        var elapsedSec = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
                        Trace.WriteLine(
                            $"[BackgroundVideo] DecodeNextFrame returned false at iter={iterationCount}, elapsed={elapsedSec:F2}s, " +
                            $"lastPts={lastPresentedPts:F3}s, attempts={attempts}, queueDepth={decoder.FrameCount}");
                    }

                    break;
                }
            }
            else
            {
                attempts++;
            }
        }

        VideoFrameData? finalFrame = decoder.GetNextFrame();
        if (finalFrame == null && _configuration.EnableVerboseLogging)
        {
            var elapsedSec = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
            Trace.WriteLine(
                $"[BackgroundVideo] No frame produced before restart: restart#{restartCount}, elapsed={elapsedSec:F2}s, " +
                $"lastPts={lastPresentedPts:F3}s, queueDepth={decoder.FrameCount}");
        }

        return finalFrame;
    }

    private VideoFrameData? RestartBackgroundVideo(VideoDecoder decoder)
    {
        if (_configuration.EnableVerboseLogging)
            Trace.WriteLine(
                $"[BackgroundVideo] Seek(0) called, duration={decoder.DurationSeconds:F3}s, fps={decoder.FramesPerSecond:F2}, " +
                $"codec={decoder.CodecName}");

        return !decoder.Seek(0, exact: false) ? null : DecodeFirstBackgroundFrame(decoder);
    }

    private static VideoFrameData? DecodeFirstBackgroundFrame(VideoDecoder decoder)
    {
        for (var i = 0; i < 240; i++)
        {
            VideoFrameData? frame = decoder.GetNextFrame();
            if (frame != null)
                return frame;

            _ = decoder.DecodeNextFrame();
        }

        return decoder.GetNextFrame();
    }

    private VideoDecoder? OpenBackgroundDecoder(MediaBackgroundCandidate background, bool enableHardwareFrameOutput)
    {
        var path = background.Source?.FilePath ?? background.Path;
        if (string.IsNullOrWhiteSpace(path)
            && (background.Data is { Length: > 0 }
                || (!string.IsNullOrWhiteSpace(background.ArchivePath)
                    && !string.IsNullOrWhiteSpace(background.ArchiveEntryKey))))
            path = CreateTempBackgroundPath(background);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var decoder = VideoDecoder.FromConfiguration(
            _configuration,
            _hardwareAccelerationServices,
            enableHardwareFrameOutput,
            scaleMaxFramesByResolution: true);
        if (decoder.Open(path))
            return decoder;

        decoder.Dispose();
        return null;
    }

    private string? CreateTempBackgroundPath(MediaBackgroundCandidate background)
    {
        var extension = Path.GetExtension(background.DisplayName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = background.Kind == MediaBackgroundKind.Video ? ".mp4" : ".png";

        var path = Path.Combine(Path.GetTempPath(), $"fbm-bg-{Guid.NewGuid():N}{extension}");
        try
        {
            if (!string.IsNullOrWhiteSpace(background.ArchivePath)
                && !string.IsNullOrWhiteSpace(background.ArchiveEntryKey))
            {
                using IArchiveHandle archive = _archiveService.Open(background.ArchivePath);
                IArchiveEntryInfo? entry = archive.Entries.FirstOrDefault(candidate =>
                    string.Equals(candidate.Key, background.ArchiveEntryKey, StringComparison.Ordinal));
                if (entry == null)
                    return null;

                using FileStream stream = File.Create(path);
                entry.WriteTo(stream);
            }
            else
            {
                File.WriteAllBytes(path, background.Data ?? []);
            }

            _backgroundVideoTempPath = path;
            return path;
        }
        catch (Exception ex)
        {
            TempMediaFile.TryDelete(path);
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "Background",
                $"Failed to extract archive background: {ex.Message}",
                sourcePath: background.ArchivePath,
                innerException: ex);
            return null;
        }
    }

    private void RaiseBackgroundFrame(VideoDecoder decoder, VideoFrameData frame, double framesPerSecond)
    {
        try
        {
            if (frame.IsNativeHardwareFrame)
            {
                BackgroundFrameReady?.Invoke(this, new VideoFrameEventArgs
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    Stride = frame.Stride,
                    PtsSeconds = frame.PresentationTime,
                    FramesPerSecond = framesPerSecond,
                    NativeResource = frame.NativeResource
                });
                return;
            }

            if (frame.Data == null || frame.Length <= 0 || frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * 4)
                return;

            var length = Math.Min(frame.Length, frame.Data.Length);

            // Pass the pool-rented buffer directly. The consumer (SetCompositionLayer0Frame)
            // always copies pixels synchronously into its own master buffer before this call
            // returns, so the pool buffer is safe to return in the finally block below.
            // PixelDataCanBeRetained is false because the buffer is returned to the ArrayPool
            // immediately after the event handlers return.
            BackgroundFrameReady?.Invoke(this, new VideoFrameEventArgs
            {
                PixelData = new ReadOnlyMemory<byte>(frame.Data, 0, length),
                PixelDataCanBeRetained = false,
                Width = frame.Width,
                Height = frame.Height,
                Stride = frame.Stride,
                PtsSeconds = frame.PresentationTime,
                FramesPerSecond = framesPerSecond
            });
        }
        finally
        {
            decoder.ReturnFrame(frame);
        }
    }

    private void StartBackgroundVideoPlayback()
    {
        _backgroundVideoScheduler.StopAndWait();

        lock (_backgroundGate)
        {
            if (_backgroundVideoDecoder == null)
                return;

            _backgroundGeneration++;
            var generation = _backgroundGeneration;
            _backgroundVideoScheduler.Start(token => BackgroundVideoLoop(generation, token));
        }
    }

    private void StopBackgroundVideoPlayback(bool wait)
    {
        if (wait)
            _backgroundVideoScheduler.StopAndWait();
        else
            _backgroundVideoScheduler.Stop();

        _backgroundGeneration++;
    }

    private void ResetBackgroundVideoToStart()
    {
        VideoDecoder? decoder;
        lock (_backgroundGate)
            decoder = _backgroundVideoDecoder;

        if (decoder == null)
            return;

        VideoFrameData? frame = RestartBackgroundVideo(decoder);
        if (frame != null)
            RaiseBackgroundFrame(decoder, frame, decoder.FramesPerSecond);
    }

    private void ClearBackgroundCore(bool raiseEvent)
    {
        StopBackgroundVideoPlayback(wait: true);
        lock (_backgroundGate)
        {
            _backgroundVideoDecoder?.Dispose();
            _backgroundVideoDecoder = null;
        }

        if (!string.IsNullOrWhiteSpace(_backgroundVideoTempPath))
        {
            TempMediaFile.TryDelete(_backgroundVideoTempPath);
            _backgroundVideoTempPath = null;
        }

        Background = null;
        if (raiseEvent)
            BackgroundCleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        StopBackgroundVideoPlayback(wait: true);
        ResetBackgroundVideoToStart();
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private static bool RejectUnsupported(string path, FileType fileType)
    {
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "PipelineMediaEngine", $"Rejected {Path.GetFileName(path)} ({fileType})", sourcePath: path);
        return false;
    }

    private bool OpenImage(string imagePath)
    {
        var source = MediaInputSource.FromFile(imagePath);
        return OpenImage(source);
    }

    private bool OpenImage(MediaInputSource source) =>
        source.Kind == MediaInputSourceKind.FilePath ? OpenImageFile(source) : source.IsBgraImage && OpenImageBgra(source);

    private bool OpenImageFile(MediaInputSource source)
    {
        var imagePath = source.FilePath;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            RaiseError(MediaErrorCode.FileNotFound, $"Image file not found: {imagePath}", imagePath);
            return false;
        }

        using VideoDecoder? decoder = OpenImageDecoder(imagePath);
        if (decoder == null)
        {
            RaiseError(MediaErrorCode.OpenFailed, $"Failed to open image decoder: {imagePath}", imagePath);
            return false;
        }

        VideoFrameData? frame = DecodeFirstBackgroundFrame(decoder);
        if (frame == null)
        {
            RaiseError(MediaErrorCode.DecodeFailed, $"Failed to decode image frame: {imagePath}", imagePath);
            return false;
        }

        try
        {
            ImagePath = imagePath;
            _coordinator.Close();
            _coordinator.State.SetOpened(MediaContent.Image, imagePath, 0);
            RaiseImageFrame(decoder, frame);
            return true;
        }
        finally
        {
            decoder.ReturnFrame(frame);
        }
    }

    private bool OpenImageBgra(MediaInputSource source)
    {
        if (!source.IsBgraImage)
            return false;

        ImagePath = null;
        _coordinator.Close();
        _coordinator.State.SetOpened(MediaContent.Image, source.DisplayName, 0);
        return RaiseImageFrame(source);
    }

    private VideoDecoder? OpenImageDecoder(string imagePath)
    {
        var decoder = VideoDecoder.FromConfiguration(
            _configuration,
            _hardwareAccelerationServices,
            enableHardwareFrameOutput: false);
        if (decoder.Open(imagePath))
            return decoder;

        decoder.Dispose();
        return null;
    }

    private bool RaiseImageFrame(MediaInputSource source)
    {
        if (!source.IsBgraImage || source.BgraPixels == null)
            return false;

        var length = checked(source.Stride * source.Height);
        if (source.BgraPixels.Length < length)
            return false;

        var args = new VideoFrameEventArgs
        {
            PixelData = new ReadOnlyMemory<byte>(source.BgraPixels, 0, length),
            PixelDataCanBeRetained = true,
            Width = source.Width,
            Height = source.Height,
            Stride = source.Stride,
            PtsSeconds = 0,
            FramesPerSecond = 0
        };
        _currentImageFrame = args;
        ImageFrameReady?.Invoke(this, args);
        return true;
    }

    private void RaiseImageFrame(VideoDecoder decoder, VideoFrameData frame)
    {
        if (frame.Data == null || frame.Length <= 0 || frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * 4)
            return;

        var length = Math.Min(frame.Length, frame.Data.Length);
        var copy = new byte[length];
        Buffer.BlockCopy(frame.Data, 0, copy, 0, length);
        var args = new VideoFrameEventArgs
        {
            PixelData = copy,
            PixelDataCanBeRetained = true,
            Width = frame.Width,
            Height = frame.Height,
            Stride = frame.Stride,
            PtsSeconds = frame.PresentationTime,
            FramesPerSecond = decoder.FramesPerSecond
        };
        _currentImageFrame = args;
        ImageFrameReady?.Invoke(this, args);
    }

    private bool RaiseBackgroundFrame(MediaInputSource source)
    {
        if (!source.IsBgraImage || source.BgraPixels == null)
            return false;

        var length = checked(source.Stride * source.Height);
        if (source.BgraPixels.Length < length)
            return false;

        BackgroundFrameReady?.Invoke(this, new VideoFrameEventArgs
        {
            PixelData = new ReadOnlyMemory<byte>(source.BgraPixels, 0, length),
            PixelDataCanBeRetained = true,
            Width = source.Width,
            Height = source.Height,
            Stride = source.Stride,
            PtsSeconds = 0,
            FramesPerSecond = 0
        });
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
