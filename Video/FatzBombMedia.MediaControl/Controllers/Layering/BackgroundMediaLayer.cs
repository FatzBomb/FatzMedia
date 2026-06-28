using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FatzBombMedia.MediaControl.Controllers.Layering;

/// <summary>
/// Layer 1 for overlay sessions: owns image/video media that sits behind a foreground overlay.
/// Background media is role-driven, not frame-driven: video backgrounds are silent,
/// looped, full-viewport, and UniformToFill regardless of overlay invalidation cadence.
/// </summary>
internal sealed class BackgroundMediaLayer : IDisposable
{
    private readonly BackgroundVideoRenderer _videoRenderer;
    private ImageSource? _imageSource;
    private bool _disposed;

    public BackgroundMediaLayer(MediaPlayerConfiguration configuration)
    {
        _videoRenderer = new BackgroundVideoRenderer(configuration);
        _videoRenderer.ImageSourceChanged += (_, __) => BackgroundChanged?.Invoke(this, EventArgs.Empty);
        _videoRenderer.FramePresented += (_, __) => FramePresented?.Invoke(this, EventArgs.Empty);
        _videoRenderer.FrameReady += (_, e) => VideoFrameReady?.Invoke(this, e);
    }

    public event EventHandler? BackgroundChanged;
    public event EventHandler? FramePresented;
    public event EventHandler<VideoFrameEventArgs>? VideoFrameReady;

    public MediaBackgroundKind Kind { get => _videoRenderer.IsOpen ? MediaBackgroundKind.Video : field; private set; } = MediaBackgroundKind.None;
    public bool HasMedia => Kind != MediaBackgroundKind.None;
    public bool HasVideo => _videoRenderer.IsOpen;
    public ImageSource? ImageSource => _videoRenderer.ImageSource ?? _imageSource;

    /// <summary>
    /// When false, the video background decodes silently and raises VideoFrameReady only.
    /// Native composition uses that frame as Layer 1 instead of showing the helper renderer's
    /// own WPF/D3DImage presentation.
    /// </summary>
    public bool UseOwnVideoPresentation
    {
        get => _videoRenderer.UseOwnPresentation;
        set => _videoRenderer.UseOwnPresentation = value;
    }

    public bool Apply(MediaBackgroundCandidate? candidate)
    {
        Clear();
        if (candidate == null)
        {
            return false;
        }

        switch (candidate.Kind)
        {
            case MediaBackgroundKind.Video:
                if (_videoRenderer.Open(candidate))
                {
                    Kind = MediaBackgroundKind.Video;
                    BackgroundChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                break;

            case MediaBackgroundKind.Image:
                _imageSource = LoadBackgroundImage(candidate);
                if (_imageSource != null)
                {
                    Kind = MediaBackgroundKind.Image;
                    BackgroundChanged?.Invoke(this, EventArgs.Empty);
                    return true;
                }
                break;
            case MediaBackgroundKind.None:
                break;
            default:
                break;
        }

        Clear();
        return false;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _videoRenderer.Start();
    }

    public void Stop(bool clearFrame)
    {
        if (_disposed)
        {
            return;
        }

        _videoRenderer.Stop(clearFrame);
    }

    public void Clear()
    {
        _videoRenderer.Clear();
        _imageSource = null;
        Kind = MediaBackgroundKind.None;
        BackgroundChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _videoRenderer.Dispose();
    }

    private static BitmapImage? LoadBackgroundImage(MediaBackgroundCandidate background)
    {
        if (background.Kind != MediaBackgroundKind.Image)
        {
            return null;
        }

        using MemoryStream? stream = background.Data is { Length: > 0 } data
            ? new MemoryStream(data)
            : null;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;

        if (!string.IsNullOrWhiteSpace(background.Path))
        {
            image.UriSource = new Uri(Path.GetFullPath(background.Path), UriKind.Absolute);
        }
        else if (stream != null)
        {
            image.StreamSource = stream;
        }
        else
        {
            return null;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }
}
