
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Files;
using System.Diagnostics;
using System.IO;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class PipelineMediaEngine : IMediaEngine
{
    private readonly IAudioEngine _audioEngine;
    private readonly IArchiveService _archiveService;
    private readonly PlaybackCoordinator _coordinator;
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
        ArgumentNullException.ThrowIfNull(cdgEngine);
        _coordinator = new PlaybackCoordinator(_audioEngine, cdgEngine, configuration, ownsAudioEngine);
        _coordinator.PlaybackEnded += OnPlaybackEnded;
    }

    public IVideoEngine? Video => _coordinator.Video;
    public ICdgEngine? Cdg => _coordinator.Cdg;
    public IAudioEngine Audio => _coordinator.ActiveAudio;

    public void SetDisplayRefreshRate(double? refreshHz) => _coordinator.SetDisplayRefreshRate(refreshHz);
    public string? ImagePath { get; private set; }

    public MediaContent Content => _coordinator.State.Content;
    public MediaPlayerState State => _coordinator.State.PlaybackState;
    public bool IsOpen => _coordinator.State.IsOpen;
    public double Duration => _coordinator.DurationSeconds;


    public event EventHandler? MediaEnded;

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

    public bool Play()
    {
        ThrowIfDisposed();
        return _coordinator.Play();
    }

    public bool Pause()
    {
        ThrowIfDisposed();
        return _coordinator.Pause();
    }

    public bool Stop()
    {
        ThrowIfDisposed();
        return _coordinator.Stop();
    }

    public void Close()
    {
        if (_disposed)
        {
            return;
        }

        _coordinator.Close();
        ImagePath = null;
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
        {
            return;
        }

        _coordinator.PlaybackEnded -= OnPlaybackEnded;
        _coordinator.Dispose();
        _disposed = true;
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) => MediaEnded?.Invoke(this, EventArgs.Empty);

    private static bool RejectUnsupported(string path, FileType fileType)
    {
        Debug.WriteLine($"[PipelineMediaEngine] Current pipeline supports audio, CDG, video, image, and memory-backed archive entries. Rejected {Path.GetFileName(path)} ({fileType}).");
        return false;
    }

    private bool OpenImage(string imagePath)
    {
        ImagePath = imagePath;
        _coordinator.Close();
        ImagePath = imagePath;
        _coordinator.State.SetOpened(MediaContent.Image, imagePath, 0);
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
