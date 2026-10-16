using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>
/// Avalonia media surface. It attaches one primary frame-sink surface to the bound
/// <see cref="IMediaControlSession"/>, keeps its pixel size in sync with layout bounds and render
/// scaling, and draws the final frames Core's selected renderer produces. It owns no media,
/// timing, composition, or renderer logic.
/// </summary>
public class MediaSurface : Control
{
    public static readonly StyledProperty<IMediaControlSession?> PlayerProperty =
        AvaloniaProperty.Register<MediaSurface, IMediaControlSession?>(nameof(Player));

    public static readonly StyledProperty<VideoDisplayMode> VideoDisplayModeProperty =
        AvaloniaProperty.Register<MediaSurface, VideoDisplayMode>(nameof(VideoDisplayMode), VideoDisplayMode.Zoom);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<MediaSurface, IBrush?>(nameof(Background), Brushes.Black);

    private AvaloniaFrameSink? _sink;
    private IMediaControlSession? _attachedSession;
    private (int Width, int Height) _attachedPixelSize;
    private bool _isAttachedToTree;

    static MediaSurface()
    {
        AffectsRender<MediaSurface>(BackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MediaSurface>(true);
    }

    /// <summary>Gets or sets the session this surface presents.</summary>
    public IMediaControlSession? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>Gets or sets how primary video maps into the surface (forwarded to the session).</summary>
    public VideoDisplayMode VideoDisplayMode
    {
        get => GetValue(VideoDisplayModeProperty);
        set => SetValue(VideoDisplayModeProperty, value);
    }

    /// <summary>Gets or sets the brush drawn where no frame has been presented yet.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Gets whether the primary surface is attached to a session.</summary>
    public bool IsPrimarySurfaceAttached => _attachedSession is not null;

    /// <summary>Gets the pixel size last sent to the session.</summary>
    public PixelSize AttachedPixelSize => new(_attachedPixelSize.Width, _attachedPixelSize.Height);

    internal AvaloniaFrameSink? FrameSink => _sink;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);

        if (_sink?.Bitmap is { } bitmap)
        {
            // The renderer already composed for this pixel size (letterboxing included), so the
            // bitmap fills the surface.
            context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToTree = true;
        _sink ??= new AvaloniaFrameSink(InvalidateVisual);
        AttachOrResize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToTree = false;
        Detach();
        _sink?.Dispose();
        _sink = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        AttachOrResize();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            Detach();
            if (change.NewValue is IMediaControlSession session)
                session.VideoDisplayMode = VideoDisplayMode;
            AttachOrResize();
        }
        else if (change.Property == VideoDisplayModeProperty && Player is { } session && change.NewValue is VideoDisplayMode mode)
        {
            session.VideoDisplayMode = mode;
        }
    }

    /// <summary>Pixel size for the current bounds and render scaling (at least 1x1).</summary>
    internal static (int Width, int Height) ToPixelSize(Size size, double scaling)
    {
        if (!double.IsFinite(scaling) || scaling <= 0)
            scaling = 1;

        return (
            Math.Max(1, (int)Math.Round(size.Width * scaling, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(size.Height * scaling, MidpointRounding.AwayFromZero)));
    }

    private void AttachOrResize()
    {
        IMediaControlSession? session = Player;
        if (!_isAttachedToTree || session is null || _sink is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        (int Width, int Height) pixelSize = ToPixelSize(Bounds.Size, scaling);
        if (ReferenceEquals(session, _attachedSession))
        {
            if (pixelSize != _attachedPixelSize
                && session.ResizePresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, pixelSize.Width, pixelSize.Height))
            {
                _attachedPixelSize = pixelSize;
            }

            return;
        }

        Detach();
        var attached = session.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            _sink.CreateSurfaceDescriptor(),
            pixelSize.Width,
            pixelSize.Height,
            scaling,
            scaling));
        if (attached)
        {
            _attachedSession = session;
            _attachedPixelSize = pixelSize;
        }
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        _attachedSession = null;
        _attachedPixelSize = default;
        if (session is not null)
            _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);

        _sink?.Clear();
    }
}
