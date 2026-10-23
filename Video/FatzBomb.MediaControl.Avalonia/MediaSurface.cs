using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>How a <see cref="MediaSurface"/> shows the renderer's frames.</summary>
public enum MediaSurfacePresentationPath
{
    /// <summary>No surface is attached.</summary>
    None,

    /// <summary>The renderer reads each frame back to the CPU and the surface uploads it as a bitmap.</summary>
    FrameSink,

    /// <summary>
    /// The renderer exports double-buffered GPU images that Avalonia's compositor imports and
    /// shows directly (Stage 027A slice 2): no CPU copy.
    /// </summary>
    SharedGpuImage,
}

/// <summary>
/// Avalonia media surface. It attaches one primary surface to the bound
/// <see cref="IMediaControlSession"/>, keeps its pixel size in sync with layout bounds and render
/// scaling, and shows the final frames Core's selected renderer produces. It owns no media,
/// timing, composition, or renderer logic.
/// </summary>
/// <remarks>
/// At attach it chooses the shared GPU image path when <see cref="PreferGpuSharedSurface"/> is
/// set, Avalonia's compositor offers GPU interop with a handle kind it can import, and the
/// session's renderer accepts the surface; otherwise (and whenever that path later reports a
/// failure) it uses the managed frame sink. The choice and its reason are raised as an Info
/// diagnostic and exposed through <see cref="PresentationPath"/>.
/// </remarks>
public class MediaSurface : Control
{
    /// <summary>The diagnostic category of the surface's path decisions.</summary>
    public const string DiagnosticCategory = "Avalonia.MediaSurface";

    public static readonly StyledProperty<IMediaControlSession?> PlayerProperty =
        AvaloniaProperty.Register<MediaSurface, IMediaControlSession?>(nameof(Player));

    public static readonly StyledProperty<VideoDisplayMode> VideoDisplayModeProperty =
        AvaloniaProperty.Register<MediaSurface, VideoDisplayMode>(nameof(VideoDisplayMode), VideoDisplayMode.Zoom);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<MediaSurface, IBrush?>(nameof(Background), Brushes.Black);

    public static readonly StyledProperty<bool> PreferGpuSharedSurfaceProperty =
        AvaloniaProperty.Register<MediaSurface, bool>(nameof(PreferGpuSharedSurface), true);

    private static int _nextSurfaceNumber;

    private readonly int _surfaceNumber = Interlocked.Increment(ref _nextSurfaceNumber);
    private AvaloniaFrameSink? _sink;
    private AvaloniaSharedImageSurface? _gpuSurface;
    private IMediaControlSession? _attachedSession;
    private (int Width, int Height) _attachedPixelSize;
    private bool _isAttachedToTree;
    private TopLevel? _topLevel;
    private Compositor? _compositor;
    private GpuInteropCapabilities? _gpuInterop;
    private bool _gpuInteropResolved;
    private int _gpuInteropQuery;
    private IMediaControlSession? _gpuRejectedSession;
    private string? _gpuRejectedReason;

    static MediaSurface()
    {
        AffectsRender<MediaSurface>(BackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MediaSurface>(true);
    }

    /// <summary>Raised on the UI thread when <see cref="PresentationPath"/> or its reason changes.</summary>
    public event EventHandler? PresentationPathChanged;

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

    /// <summary>
    /// Gets or sets whether the surface uses the shared GPU image path when the compositor and the
    /// renderer support it (default <see langword="true"/>). <see langword="false"/> always uses the
    /// frame sink. A change re-attaches the surface.
    /// </summary>
    public bool PreferGpuSharedSurface
    {
        get => GetValue(PreferGpuSharedSurfaceProperty);
        set => SetValue(PreferGpuSharedSurfaceProperty, value);
    }

    /// <summary>Gets whether the primary surface is attached to a session.</summary>
    public bool IsPrimarySurfaceAttached => _attachedSession is not null;

    /// <summary>Gets the pixel size last sent to the session.</summary>
    public PixelSize AttachedPixelSize => new(_attachedPixelSize.Width, _attachedPixelSize.Height);

    /// <summary>Gets how frames reach the screen now.</summary>
    public MediaSurfacePresentationPath PresentationPath { get; private set; }

    /// <summary>Gets why the current path was chosen (for example why the GPU path was not used).</summary>
    public string? PresentationPathReason { get; private set; }

    internal AvaloniaFrameSink? FrameSink => _sink;

    /// <summary>Test seam: how the compositor's GPU interop is obtained.</summary>
    internal Func<Compositor, ValueTask<GpuInteropCapabilities?>> GpuInteropProvider { get; set; } = GpuInteropCapabilities.QueryAsync;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);

        if (_sink?.Bitmap is { } bitmap)
        {
            // The renderer already composed for this pixel size (letterboxing included), so the
            // bitmap fills the surface. The shared-image path draws in a child composition visual.
            context.DrawImage(bitmap, new Rect(bitmap.Size), bounds);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToTree = true;
        _sink ??= new AvaloniaFrameSink(InvalidateVisual);

        // Moving to a monitor with another scale keeps Bounds (DIPs) unchanged, so size changes
        // alone would miss the new pixel size.
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.ScalingChanged += OnTopLevelScalingChanged;

        _compositor = ElementComposition.GetElementVisual(this)?.Compositor;
        QueryGpuInterop();
        AttachOrResize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttachedToTree = false;
        if (_topLevel is not null)
        {
            _topLevel.ScalingChanged -= OnTopLevelScalingChanged;
            _topLevel = null;
        }

        Detach();
        _sink?.Dispose();
        _sink = null;
        _compositor = null;
        _gpuInterop = null;
        _gpuInteropResolved = false;
        _gpuInteropQuery++;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _gpuSurface?.SetSize(Bounds.Size);
        AttachOrResize();
    }

    private void OnTopLevelScalingChanged(object? sender, EventArgs e) => AttachOrResize();

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
        else if (change.Property == PreferGpuSharedSurfaceProperty)
        {
            // A host that turns the GPU path back on asks for a new attempt.
            _gpuRejectedSession = null;
            _gpuRejectedReason = null;
            Detach();
            QueryGpuInterop();
            AttachOrResize();
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

    /// <summary>
    /// Asks the compositor for its GPU interop once per tree attachment. The answer is usually
    /// immediate; when it is not, the attach waits for it so the path is chosen once.
    /// </summary>
    private void QueryGpuInterop()
    {
        if (!_isAttachedToTree || _gpuInteropResolved || !PreferGpuSharedSurface)
            return;

        if (_compositor is null)
        {
            _gpuInteropResolved = true;
            return;
        }

        ValueTask<GpuInteropCapabilities?> query;
        try
        {
            query = GpuInteropProvider(_compositor);
        }
        catch (Exception)
        {
            _gpuInteropResolved = true;
            return;
        }

        if (query.IsCompleted)
        {
            _gpuInterop = query.IsCompletedSuccessfully ? query.Result : null;
            _gpuInteropResolved = true;
            return;
        }

        _ = AwaitGpuInteropAsync(query.AsTask(), ++_gpuInteropQuery);
    }

    private async Task AwaitGpuInteropAsync(Task<GpuInteropCapabilities?> query, int queryNumber)
    {
        GpuInteropCapabilities? interop = null;
        try
        {
            // Resumes on the UI thread.
            interop = await query;
        }
        catch (Exception)
        {
            // No interop: the frame sink is used.
        }

        if (queryNumber != _gpuInteropQuery || !_isAttachedToTree)
            return;

        _gpuInterop = interop;
        _gpuInteropResolved = true;
        AttachOrResize();
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
        string fallbackReason;
        switch (ChooseGpuPath(session, out MediaSharedGpuImageRequest? request, out fallbackReason))
        {
            case GpuPathChoice.Pending:
                // AwaitGpuInteropAsync attaches when the compositor answers.
                return;
            case GpuPathChoice.Use when TryAttachGpu(session, request!, pixelSize, scaling, out fallbackReason):
                return;
        }

        var attached = session.AttachPresentationSurface(CreateDescriptor(_sink.CreateSurfaceDescriptor(), pixelSize, scaling));
        if (attached)
        {
            _attachedSession = session;
            _attachedPixelSize = pixelSize;
            SetPath(MediaSurfacePresentationPath.FrameSink, fallbackReason);
        }
    }

    private enum GpuPathChoice
    {
        Use,
        Pending,
        FrameSink,
    }

    private GpuPathChoice ChooseGpuPath(IMediaControlSession session, out MediaSharedGpuImageRequest? request, out string reason)
    {
        request = null;
        if (!PreferGpuSharedSurface)
        {
            reason = "PreferGpuSharedSurface is off";
            return GpuPathChoice.FrameSink;
        }

        if (ReferenceEquals(session, _gpuRejectedSession))
        {
            reason = _gpuRejectedReason ?? "the shared GPU image path failed for this session";
            return GpuPathChoice.FrameSink;
        }

        if (_compositor is null)
        {
            reason = "the control has no compositor";
            return GpuPathChoice.FrameSink;
        }

        if (!_gpuInteropResolved)
        {
            reason = string.Empty;
            return GpuPathChoice.Pending;
        }

        if (_gpuInterop is null)
        {
            reason = "Avalonia's rendering backend offers no GPU interop (ICompositionGpuInterop)";
            return GpuPathChoice.FrameSink;
        }

        if (_gpuInterop.IsLost)
        {
            reason = "the compositor's GPU interop was lost";
            return GpuPathChoice.FrameSink;
        }

        request = _gpuInterop.CreateRequest();
        if (!request.IsValid)
        {
            reason = "the compositor imports no supported shared image handle kind (it offers: "
                + $"{string.Join(", ", _gpuInterop.ImageHandleTypes)})";
            request = null;
            return GpuPathChoice.FrameSink;
        }

        reason = string.Empty;
        return GpuPathChoice.Use;
    }

    private bool TryAttachGpu(
        IMediaControlSession session,
        MediaSharedGpuImageRequest request,
        (int Width, int Height) pixelSize,
        double scaling,
        out string fallbackReason)
    {
        var gpuSurface = new AvaloniaSharedImageSurface(this, _compositor!, _gpuInterop!, request, OnGpuSurfaceUnavailable);
        gpuSurface.SetSize(Bounds.Size);
        bool attached;
        try
        {
            attached = session.AttachPresentationSurface(CreateDescriptor(gpuSurface.CreateSurfaceDescriptor(), pixelSize, scaling));
        }
        catch
        {
            gpuSurface.Dispose();
            throw;
        }

        // A renderer that cannot serve the surface says why during the attach.
        var unavailable = gpuSurface.UnavailableReason;
        if (attached && unavailable is null)
        {
            _gpuSurface = gpuSurface;
            _attachedSession = session;
            _attachedPixelSize = pixelSize;
            SetPath(MediaSurfacePresentationPath.SharedGpuImage, $"the compositor imports {request}");
            fallbackReason = string.Empty;
            return true;
        }

        if (attached)
            _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);

        gpuSurface.Dispose();
        fallbackReason = unavailable ?? "the session did not accept the shared GPU image surface";
        _gpuRejectedSession = session;
        _gpuRejectedReason = fallbackReason;
        return false;
    }

    /// <summary>The renderer or the compositor gave up on the shared images: re-attach with the frame sink.</summary>
    private void OnGpuSurfaceUnavailable(AvaloniaSharedImageSurface gpuSurface, string reason)
    {
        if (!ReferenceEquals(gpuSurface, _gpuSurface) || _attachedSession is not { } session)
            return;

        _gpuRejectedSession = session;
        _gpuRejectedReason = reason;
        Detach();
        AttachOrResize();
    }

    private static MediaPresentationSurfaceDescriptor CreateDescriptor(
        MediaNativeSurfaceDescriptor nativeSurface,
        (int Width, int Height) pixelSize,
        double scaling) => new(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            nativeSurface,
            pixelSize.Width,
            pixelSize.Height,
            scaling,
            scaling);

    private void SetPath(MediaSurfacePresentationPath path, string? reason)
    {
        reason = string.IsNullOrEmpty(reason) ? null : reason;
        if (path == PresentationPath && reason == PresentationPathReason)
            return;

        PresentationPath = path;
        PresentationPathReason = reason;
        if (path != MediaSurfacePresentationPath.None)
        {
            var message = path == MediaSurfacePresentationPath.SharedGpuImage
                ? $"Surface #{_surfaceNumber}: shared GPU image path ({reason})"
                : $"Surface #{_surfaceNumber}: frame-sink path{(reason is null ? string.Empty : $" ({reason})")}";
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, DiagnosticCategory, message);
        }

        PresentationPathChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        _attachedSession = null;
        _attachedPixelSize = default;
        if (session is not null)
            _ = session.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);

        _gpuSurface?.Dispose();
        _gpuSurface = null;
        _sink?.Clear();
        SetPath(MediaSurfacePresentationPath.None, null);
    }
}
