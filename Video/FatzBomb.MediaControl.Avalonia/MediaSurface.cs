using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>
/// Avalonia media surface. It attaches one surface to the bound <see cref="IMediaControlSession"/>,
/// keeps its pixel size in sync with layout bounds and render scaling, and shows the final frames
/// Core's selected renderer produces. It owns no media, timing, composition, or renderer logic.
/// </summary>
/// <remarks>
/// <para>
/// At attach it chooses the shared GPU image path when <see cref="PreferGpuSharedSurface"/> is
/// set, Avalonia's compositor offers GPU interop with a handle kind it can import, and the
/// session's renderer accepts the surface; otherwise (and whenever that path later reports a
/// failure) it uses the managed frame sink. The choice and its reason are raised as an Info
/// diagnostic and exposed through <see cref="PresentationPath"/>.
/// </para>
/// <para>
/// <see cref="PresentationPath"/> is Core's <see cref="MediaSurfacePresentationPath"/> since
/// Stage 046A (it moved from this namespace to <c>FatzBomb.MediaControl</c>, shared by every
/// adapter). <see cref="AutoHide"/> hides the frames while no frame of the current media is shown,
/// with Core's <see cref="MediaSurfaceContentTracker"/>, like the WPF and WinForms surfaces.
/// </para>
/// <para>
/// Several surfaces can show one session (Stage 027A slice 5): the operator view as the
/// <see cref="MediaPresentationSurfaceRole.Primary"/> surface, and a singer or projector screen in
/// another window as a <see cref="MediaPresentationSurfaceRole.Projector"/> surface. Core renders
/// every frame once per surface at that surface's size; each surface picks its own path.
/// </para>
/// </remarks>
public class MediaSurface : Control
{
    /// <summary>The diagnostic category of the surface's path decisions.</summary>
    public const string DiagnosticCategory = "Avalonia.MediaSurface";

    /// <summary>The surface id a non-primary surface uses when <see cref="SurfaceId"/> is not set.</summary>
    public const string DefaultSecondarySurfaceId = "secondary";

    public static readonly StyledProperty<IMediaControlSession?> PlayerProperty =
        AvaloniaProperty.Register<MediaSurface, IMediaControlSession?>(nameof(Player));

    public static readonly StyledProperty<VideoDisplayMode> VideoDisplayModeProperty =
        AvaloniaProperty.Register<MediaSurface, VideoDisplayMode>(nameof(VideoDisplayMode), VideoDisplayMode.Zoom);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<MediaSurface, IBrush?>(nameof(Background), Brushes.Black);

    public static readonly StyledProperty<bool> PreferGpuSharedSurfaceProperty =
        AvaloniaProperty.Register<MediaSurface, bool>(nameof(PreferGpuSharedSurface), true);

    public static readonly StyledProperty<MediaPresentationSurfaceRole> RoleProperty =
        AvaloniaProperty.Register<MediaSurface, MediaPresentationSurfaceRole>(nameof(Role), MediaPresentationSurfaceRole.Primary);

    public static readonly StyledProperty<string?> SurfaceIdProperty =
        AvaloniaProperty.Register<MediaSurface, string?>(nameof(SurfaceId));

    public static readonly StyledProperty<bool> AutoHideProperty =
        AvaloniaProperty.Register<MediaSurface, bool>(nameof(AutoHide), true);

    public static readonly DirectProperty<MediaSurface, bool> IsContentVisibleProperty =
        AvaloniaProperty.RegisterDirect<MediaSurface, bool>(nameof(IsContentVisible), static surface => surface.IsContentVisible);

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
    private string _attachedSurfaceId = MediaPresentationSurfaceDescriptor.PrimarySurfaceId;
    private readonly MediaSurfaceContentTracker _contentTracker = new();
    private int _contentStateQueued;
    private bool _isPresentationSuspended;
    private bool _contentShown;
    private bool _isContentVisible;
    private bool _attachRefusalReported;

    static MediaSurface()
    {
        AffectsRender<MediaSurface>(BackgroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<MediaSurface>(true);
    }

    public MediaSurface() => _contentTracker.Changed += OnContentTrackerChanged;

    /// <summary>Raised on the UI thread when <see cref="PresentationPath"/> or its reason changes.</summary>
    public event EventHandler? PresentationPathChanged;

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    /// <summary>Gets or sets the session this surface presents.</summary>
    public IMediaControlSession? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    /// <summary>
    /// Gets or sets how primary video maps into the surface (forwarded to the session by a primary
    /// surface only, so a second screen does not change it).
    /// </summary>
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

    /// <summary>
    /// Gets or sets the surface's role (default <see cref="MediaPresentationSurfaceRole.Primary"/>).
    /// Use <see cref="MediaPresentationSurfaceRole.Projector"/> for a second screen of a session
    /// that already has a primary surface. A change re-attaches the surface.
    /// </summary>
    public MediaPresentationSurfaceRole Role
    {
        get => GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    /// <summary>
    /// Gets or sets the surface id sent to the session. Unset, a primary surface uses
    /// <see cref="MediaPresentationSurfaceDescriptor.PrimarySurfaceId"/> and any other role uses
    /// <see cref="DefaultSecondarySurfaceId"/>. Surfaces of one session need distinct ids. A change
    /// re-attaches the surface.
    /// </summary>
    public string? SurfaceId
    {
        get => GetValue(SurfaceIdProperty);
        set => SetValue(SurfaceIdProperty, value);
    }

    /// <summary>Gets the id this surface attaches with (<see cref="SurfaceId"/> or the role's default).</summary>
    public string EffectiveSurfaceId => string.IsNullOrWhiteSpace(SurfaceId)
        ? Role == MediaPresentationSurfaceRole.Primary ? MediaPresentationSurfaceDescriptor.PrimarySurfaceId : DefaultSecondarySurfaceId
        : SurfaceId.Trim();

    /// <summary>
    /// Gets or sets whether the surface hides its frames (shows only <see cref="Background"/>)
    /// while no frame of the current media is shown: before the first frame of a load, after
    /// stop, close, an error, or a natural end without looping (default <see langword="true"/>,
    /// as in the WPF and WinForms surfaces). <see langword="false"/> shows whatever the renderer
    /// last presented.
    /// </summary>
    public bool AutoHide
    {
        get => GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    /// <summary>Gets whether auto-hide shows the content now (always false while <see cref="AutoHide"/> is off).</summary>
    public bool IsContentVisible => _isContentVisible;

    /// <summary>Gets whether this surface is attached to a session, in any role.</summary>
    public bool IsSurfaceAttached => _attachedSession is not null;

    /// <summary>Gets whether this surface is attached to a session as its primary surface.</summary>
    public bool IsPrimarySurfaceAttached => _attachedSession is not null && Role == MediaPresentationSurfaceRole.Primary;

    /// <summary>
    /// Gets whether the surface shows only its <see cref="Background"/> because no renderer
    /// presents to it: the session is in <see cref="MediaPlayerMode.Audio"/> mode (Stage 043A),
    /// which keeps surfaces attached but has no presentation (the session's
    /// <see cref="IMediaControlSession.HasPresentation"/>, Stage 046A). The last video frame is not
    /// left on screen; frames show again once the session leaves Audio mode.
    /// </summary>
    public bool IsPresentationSuspended => _isPresentationSuspended;

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

        if (_contentShown && _sink?.Bitmap is { } bitmap)
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
        FollowSession(Player);
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
        FollowSession(null);
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
            // The rejection memo is per session; drop it so it does not keep an old session alive.
            if (!ReferenceEquals(change.NewValue, _gpuRejectedSession))
            {
                _gpuRejectedSession = null;
                _gpuRejectedReason = null;
            }

            Detach();
            _attachRefusalReported = false;
            if (_isAttachedToTree)
                FollowSession(change.NewValue as IMediaControlSession);
            if (change.NewValue is IMediaControlSession session && Role == MediaPresentationSurfaceRole.Primary)
                session.VideoDisplayMode = VideoDisplayMode;
            AttachOrResize();
        }
        else if (change.Property == RoleProperty || change.Property == SurfaceIdProperty)
        {
            Detach();
            if (change.Property == RoleProperty
                && change.NewValue is MediaPresentationSurfaceRole.Primary
                && Player is { } primarySession)
            {
                primarySession.VideoDisplayMode = VideoDisplayMode;
            }

            AttachOrResize();
        }
        else if (change.Property == VideoDisplayModeProperty
            && Role == MediaPresentationSurfaceRole.Primary
            && Player is { } session
            && change.NewValue is VideoDisplayMode mode)
        {
            session.VideoDisplayMode = mode;
        }
        else if (change.Property == AutoHideProperty)
        {
            _contentTracker.AutoHide = change.NewValue is true;
            UpdateContentState();
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
                && session.ResizePresentationSurface(_attachedSurfaceId, pixelSize.Width, pixelSize.Height))
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
            _attachedSurfaceId = EffectiveSurfaceId;
            _attachedPixelSize = pixelSize;
            _attachRefusalReported = false;
            SetPath(MediaSurfacePresentationPath.FrameSink, fallbackReason);
        }
        else if (!_attachRefusalReported)
        {
            // Layout retries the attach on every resize: report a refusal once per session.
            _attachRefusalReported = true;
            DiagnosticRouter.Raise(
                session,
                MediaDiagnosticLevel.Warning,
                DiagnosticCategory,
                MediaSurfacePresentationPathText.DescribeAttachRefused(DescribeSurface()));
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
        var gpuSurface = new AvaloniaSharedImageSurface(this, session, _compositor!, _gpuInterop!, request, OnGpuSurfaceUnavailable);
        gpuSurface.SetSize(Bounds.Size);
        gpuSurface.SetVisible(_contentShown);
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
            _attachedSurfaceId = EffectiveSurfaceId;
            _attachedPixelSize = pixelSize;
            SetPath(MediaSurfacePresentationPath.SharedGpuImage, $"the compositor imports {request}");
            fallbackReason = string.Empty;
            return true;
        }

        if (attached)
            _ = session.DetachPresentationSurface(EffectiveSurfaceId);

        gpuSurface.Dispose();
        fallbackReason = unavailable ?? "the session did not accept the shared GPU image surface";
        // Only a renderer that declined with a reason rules the path out for this session; a
        // refusal without one (renderer not initialized yet, session busy) is retried next attach.
        if (unavailable is not null)
        {
            _gpuRejectedSession = session;
            _gpuRejectedReason = fallbackReason;
        }

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

    private MediaPresentationSurfaceDescriptor CreateDescriptor(
        MediaNativeSurfaceDescriptor nativeSurface,
        (int Width, int Height) pixelSize,
        double scaling) => new(
            EffectiveSurfaceId,
            Role,
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
            DiagnosticRouter.Raise(
                _attachedSession,
                MediaDiagnosticLevel.Info,
                DiagnosticCategory,
                MediaSurfacePresentationPathText.DescribeChoice(DescribeSurface(), path, reason));
        }

        PresentationPathChanged?.Invoke(this, EventArgs.Empty);
    }

    private string DescribeSurface() => $"Surface #{_surfaceNumber} '{EffectiveSurfaceId}'";

    /// <summary>
    /// Follows the session while the control is in the visual tree (Stage 046A): Core's tracker
    /// decides auto-hide and reports Audio mode (Stage 043A), which keeps surfaces attached but
    /// presents nothing, so the last video frame would stay on screen.
    /// </summary>
    private void FollowSession(IMediaControlSession? session)
    {
        _contentTracker.Session = session;
        UpdateContentState();
    }

    // Raised on the thread that changed the state (a session event or render thread): marshal,
    // coalesce, then read the tracker's current state, so a quick Audio -> Full switch never
    // leaves the surface cleared.
    private void OnContentTrackerChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _contentStateQueued, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _contentStateQueued, 0);
            UpdateContentState();
        });
    }

    private void UpdateContentState()
    {
        IMediaControlSession? session = _contentTracker.Session;
        var suspended = session is not null && !_contentTracker.HasPresentation;
        if (suspended != _isPresentationSuspended)
        {
            _isPresentationSuspended = suspended;
            if (suspended)
            {
                // The renderer that drew the last frame is gone: show the background, not that frame.
                _sink?.Clear();
                DiagnosticRouter.Raise(
                    session,
                    MediaDiagnosticLevel.Info,
                    DiagnosticCategory,
                    MediaSurfacePresentationPathText.DescribeAudioModeCleared(DescribeSurface()));
            }
        }

        var wasShown = _contentShown;
        _contentShown = _contentTracker.ShowsContent;
        if (wasShown && !_contentShown)
        {
            // Hidden content is dropped, so a later load shows its own first frame, not this one.
            _sink?.Clear();
        }

        _gpuSurface?.SetVisible(_contentShown);
        InvalidateVisual();

        var isContentVisible = _contentTracker.IsContentVisible;
        if (isContentVisible != _isContentVisible)
        {
            var wasVisible = _isContentVisible;
            _isContentVisible = isContentVisible;
            RaisePropertyChanged(IsContentVisibleProperty, wasVisible, isContentVisible);
            ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Detach()
    {
        IMediaControlSession? session = _attachedSession;
        _attachedSession = null;
        _attachedPixelSize = default;
        if (session is not null)
            _ = session.DetachPresentationSurface(_attachedSurfaceId);

        _gpuSurface?.Dispose();
        _gpuSurface = null;
        _sink?.Clear();
        SetPath(MediaSurfacePresentationPath.None, null);
    }
}
