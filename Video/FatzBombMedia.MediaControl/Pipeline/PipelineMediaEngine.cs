using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Files;
using FatzBombMedia.MediaControl.Core.Threading;
using FatzBombMedia.MediaControl.Core.Video;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class PipelineMediaEngine : IMediaEngine
{
    private readonly IAudioEngine _audioEngine;
    private readonly IArchiveService _archiveService;
    private readonly PlaybackCoordinator _coordinator;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly PlaybackScheduler _backgroundVideoScheduler = new("BackgroundVideoPresentation");
    private readonly Lock _backgroundGate = new();
    private VideoDecoder? _backgroundVideoDecoder;
    private string? _backgroundVideoTempPath;
    private VideoFrameEventArgs? _currentImageFrame;
    private volatile int _backgroundGeneration;
    private bool _disposed;

    public PipelineMediaEngine(
        IAudioEngine audioEngine,
        ICdgEngine cdgEngine,
        IArchiveService archiveService,
        MediaPlayerConfiguration configuration,
        bool ownsAudioEngine = false)
    {
        _audioEngine = audioEngine ?? throw new ArgumentNullException(nameof(audioEngine));
        _archiveService = archiveService ?? throw new ArgumentNullException(nameof(archiveService));
        _configuration = configuration;
        ArgumentNullException.ThrowIfNull(cdgEngine);
        _coordinator = new PlaybackCoordinator(_audioEngine, cdgEngine, configuration, ownsAudioEngine);
        _coordinator.PlaybackEnded += OnPlaybackEnded;
    }

    public IVideoEngine? Video => _coordinator.Video;
    public ICdgEngine? Cdg => _coordinator.Cdg;
    public IAudioEngine Audio => _coordinator.ActiveAudio;

    public void SetDisplayRefreshRate(double? refreshHz) => _coordinator.SetDisplayRefreshRate(refreshHz);
    public string? ImagePath { get; private set; }
    public bool BackgroundAutoDetect { get; set; } = true;
    public MediaBackgroundCandidate? Background { get; private set; }

    public MediaContent Content => _coordinator.State.Content;
    public MediaPlayerState State => _coordinator.State.PlaybackState;
    public bool IsOpen => _coordinator.State.IsOpen;
    public double Duration => _coordinator.DurationSeconds;


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
            return false;
        }

        using FileTree fileTree = FileTreeBuilder.GetFileTree(path, _archiveService);
        return fileTree.Type switch
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
            FileType.Video when !string.IsNullOrWhiteSpace(fileTree.VideoPath) =>
                _coordinator.OpenVideo(fileTree.VideoPath),
            FileType.VideoArchive when fileTree.VideoEntry != null =>
                _coordinator.OpenVideo(fileTree.VideoEntry),
            FileType.Image when !string.IsNullOrWhiteSpace(fileTree.ImagePath) =>
                OpenImage(fileTree.ImagePath),
            _ => RejectUnsupported(path, fileTree.Type)
        };
    }

    public bool Open(MediaInputSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);

        return source.Kind switch
        {
            MediaInputSourceKind.FilePath when !string.IsNullOrWhiteSpace(source.FilePath) => Open(source.FilePath),
            MediaInputSourceKind.Bgra32Image => OpenImage(source),
            _ => false
        };
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

        using VideoDecoder? decoder = OpenBackgroundDecoder(background, enableHardwareFrameOutput: false);
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

        VideoFrameData? firstFrame = DecodeFirstBackgroundFrame(decoder);
        if (firstFrame == null)
        {
            decoder.Dispose();
            return false;
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
        using var waiter = new PrecisionWaiter();
        var nextTick = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested && generation == _backgroundGeneration)
        {
            VideoDecoder? decoder = _backgroundVideoDecoder;
            if (decoder == null)
            {
                return;
            }

            VideoFrameData? frame = DecodeNextBackgroundVideoFrame(decoder);
            if (frame == null)
            {
                frame = RestartBackgroundVideo(decoder);
                nextTick = Stopwatch.GetTimestamp();
            }

            if (frame != null)
            {
                RaiseBackgroundFrame(decoder, frame, decoder.FramesPerSecond);
            }

            var interval = VideoPresentationTiming.GetBackgroundPresentationInterval(decoder.FramesPerSecond);
            nextTick += Math.Max(1L, (long)(interval * Stopwatch.Frequency));
            var delayTicks = nextTick - Stopwatch.GetTimestamp();
            if (delayTicks > 0)
            {
                waiter.Wait(TimeSpan.FromSeconds(delayTicks / (double)Stopwatch.Frequency), cancellationToken.WaitHandle);
            }
            else
            {
                nextTick = Stopwatch.GetTimestamp();
                waiter.Wait(TimeSpan.FromMilliseconds(1), cancellationToken.WaitHandle);
            }
        }
    }

    private VideoFrameData? DecodeNextBackgroundVideoFrame(VideoDecoder decoder)
    {
        for (var i = 0; i < 120; i++)
        {
            VideoFrameData? frame = decoder.GetNextFrame();
            if (frame != null)
                return frame;

            if (!decoder.DecodeNextFrame())
                break;
        }

        return decoder.GetNextFrame();
    }

    private static VideoFrameData? RestartBackgroundVideo(VideoDecoder decoder)
    {
        if (!decoder.Seek(0, exact: false))
        {
            return null;
        }

        return DecodeFirstBackgroundFrame(decoder);
    }

    private static VideoFrameData? DecodeFirstBackgroundFrame(VideoDecoder decoder)
    {
        for (var i = 0; i < 240; i++)
        {
            VideoFrameData? frame = decoder.GetNextFrame();
            if (frame != null)
            {
                return frame;
            }

            _ = decoder.DecodeNextFrame();
        }

        return decoder.GetNextFrame();
    }

    private VideoDecoder? OpenBackgroundDecoder(MediaBackgroundCandidate background, bool enableHardwareFrameOutput)
    {
        var path = background.Source?.FilePath ?? background.Path;
        if (string.IsNullOrWhiteSpace(path) && background.Data is { Length: > 0 })
        {
            path = CreateTempBackgroundPath(background);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var decoder = VideoDecoder.FromConfiguration(_configuration, enableHardwareFrameOutput);
        if (decoder.Open(path))
        {
            return decoder;
        }

        decoder.Dispose();
        return null;
    }

    private string CreateTempBackgroundPath(MediaBackgroundCandidate background)
    {
        var extension = Path.GetExtension(background.DisplayName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = background.Kind == MediaBackgroundKind.Video ? ".mp4" : ".png";
        }

        var path = Path.Combine(Path.GetTempPath(), $"fbm-bg-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, background.Data ?? []);
        _backgroundVideoTempPath = path;
        return path;
    }

    private void RaiseBackgroundFrame(VideoDecoder decoder, VideoFrameData frame, double framesPerSecond)
    {
        try
        {
            if (frame.IsD3D11HardwareFrame)
            {
                BackgroundFrameReady?.Invoke(this, new VideoFrameEventArgs
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    Stride = frame.Stride,
                    PtsSeconds = frame.PresentationTime,
                    FramesPerSecond = framesPerSecond,
                    D3D11DevicePointer = frame.D3D11DevicePointer,
                    D3D11DeviceContextPointer = frame.D3D11DeviceContextPointer,
                    D3D11TexturePointer = frame.D3D11TexturePointer,
                    D3D11ArraySlice = frame.D3D11ArraySlice
                });
                return;
            }

            if (frame.Data == null || frame.Length <= 0 || frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * 4)
            {
                return;
            }

            var length = Math.Min(frame.Length, frame.Data.Length);
            var copy = new byte[length];
            Buffer.BlockCopy(frame.Data, 0, copy, 0, length);
            BackgroundFrameReady?.Invoke(this, new VideoFrameEventArgs
            {
                PixelData = copy,
                PixelDataCanBeRetained = true,
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
            {
                return;
            }

            _backgroundGeneration++;
            var generation = _backgroundGeneration;
            _backgroundVideoScheduler.Start(token => BackgroundVideoLoop(generation, token));
        }
    }

    private void StopBackgroundVideoPlayback(bool wait)
    {
        if (wait)
        {
            _backgroundVideoScheduler.StopAndWait();
        }
        else
        {
            _backgroundVideoScheduler.Stop();
        }

        _backgroundGeneration++;
    }

    private void ResetBackgroundVideoToStart()
    {
        VideoDecoder? decoder;
        lock (_backgroundGate)
        {
            decoder = _backgroundVideoDecoder;
        }

        if (decoder == null)
        {
            return;
        }

        VideoFrameData? frame = RestartBackgroundVideo(decoder);
        if (frame != null)
        {
            RaiseBackgroundFrame(decoder, frame, decoder.FramesPerSecond);
        }
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
            TryDeleteTempPath(_backgroundVideoTempPath);
            _backgroundVideoTempPath = null;
        }

        Background = null;
        if (raiseEvent)
        {
            BackgroundCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    private static void TryDeleteTempPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for archive-backed temporary background media.
        }
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        StopBackgroundVideoPlayback(wait: true);
        ResetBackgroundVideoToStart();
        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private static bool RejectUnsupported(string path, FileType fileType)
    {
        Debug.WriteLine($"[PipelineMediaEngine] Current pipeline supports audio, CDG, video, image, and memory-backed archive entries. Rejected {Path.GetFileName(path)} ({fileType}).");
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
            return false;
        }

        using VideoDecoder? decoder = OpenImageDecoder(imagePath);
        if (decoder == null)
        {
            return false;
        }

        VideoFrameData? frame = DecodeFirstBackgroundFrame(decoder);
        if (frame == null)
        {
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
        {
            return false;
        }

        ImagePath = null;
        _coordinator.Close();
        _coordinator.State.SetOpened(MediaContent.Image, source.DisplayName, 0);
        return RaiseImageFrame(source);
    }

    private VideoDecoder? OpenImageDecoder(string imagePath)
    {
        var decoder = VideoDecoder.FromConfiguration(_configuration, enableHardwareFrameOutput: false);
        if (decoder.Open(imagePath))
        {
            return decoder;
        }

        decoder.Dispose();
        return null;
    }

    private bool RaiseImageFrame(MediaInputSource source)
    {
        if (!source.IsBgraImage || source.BgraPixels == null)
        {
            return false;
        }

        var length = checked(source.Stride * source.Height);
        if (source.BgraPixels.Length < length)
        {
            return false;
        }

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
        {
            return;
        }

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
        {
            return false;
        }

        var length = checked(source.Stride * source.Height);
        if (source.BgraPixels.Length < length)
        {
            return false;
        }

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
