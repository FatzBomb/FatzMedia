using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// Self-contained background for solid colours, images and silent looping video (Stage 046A, the
/// Avalonia counterpart of the WPF and WinForms <c>BackgroundSurface</c>). Media plays in a player
/// of its own in <see cref="Core.Configuration.MediaPlayerMode.Background"/> mode (no audio engine), created from
/// <see cref="PlatformFactory"/>; audio, CD+G and lyrics are rejected (Core's
/// <see cref="LightweightPlayerPolicy"/>). A solid colour needs no player: the surface shows
/// <see cref="BackgroundColor"/>. <see cref="OverlayContent"/> is presented above the background.
/// </summary>
/// <remarks>
/// <see cref="Source"/> takes <see langword="null"/> (shows <see cref="BackgroundColor"/>), a
/// <see cref="Color"/> or <see cref="ISolidColorBrush"/>, a file path, an Avalonia
/// <see cref="Bitmap"/> (converted with <see cref="AvaloniaMediaInputSources.FromBitmap"/>) or a
/// <see cref="MediaInputSource"/>. The player is released when the control leaves the visual tree;
/// attached again, it loads the source again. Instead of the WPF <c>PresentationMode</c>, the
/// surface's path is chosen as on any Avalonia <see cref="MediaSurface"/>
/// (<see cref="PreferGpuSharedSurface"/>); the overlay is composited above either path. Events are
/// raised on the UI thread.
/// </remarks>
public class BackgroundSurface : Panel, IDisposable
{
    /// <summary>The diagnostic category of this control's own notices.</summary>
    public const string DiagnosticCategory = "Avalonia.BackgroundSurface";

    public static readonly StyledProperty<object?> SourceProperty =
        AvaloniaProperty.Register<BackgroundSurface, object?>(nameof(Source));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<BackgroundSurface, bool>(nameof(AutoPlay), true);

    public static readonly StyledProperty<bool> AutoHideProperty =
        AvaloniaProperty.Register<BackgroundSurface, bool>(nameof(AutoHide), true);

    public static readonly StyledProperty<Color> BackgroundColorProperty =
        AvaloniaProperty.Register<BackgroundSurface, Color>(nameof(BackgroundColor), Colors.Black);

    public static readonly StyledProperty<bool> PreferGpuSharedSurfaceProperty =
        AvaloniaProperty.Register<BackgroundSurface, bool>(nameof(PreferGpuSharedSurface), true);

    public static readonly StyledProperty<object?> OverlayContentProperty =
        AvaloniaProperty.Register<BackgroundSurface, object?>(nameof(OverlayContent));

    public static readonly StyledProperty<bool> OverlayHitTestVisibleProperty =
        AvaloniaProperty.Register<BackgroundSurface, bool>(nameof(OverlayHitTestVisible));

    public static readonly DirectProperty<BackgroundSurface, IMediaControlPlatformFactory?> PlatformFactoryProperty =
        AvaloniaProperty.RegisterDirect<BackgroundSurface, IMediaControlPlatformFactory?>(
            nameof(PlatformFactory),
            static surface => surface.PlatformFactory,
            static (surface, value) => surface.PlatformFactory = value);

    public static readonly DirectProperty<BackgroundSurface, bool> IsContentVisibleProperty =
        AvaloniaProperty.RegisterDirect<BackgroundSurface, bool>(nameof(IsContentVisible), static surface => surface.IsContentVisible);

    private readonly MediaSurface _surface = new() { Background = new SolidColorBrush(Colors.Black) };
    private readonly ContentPresenter _overlayPresenter = new() { IsHitTestVisible = false, IsVisible = false };
    private readonly LightweightSurfaceHost _host;

    public BackgroundSurface()
    {
        ClipToBounds = true;
        _host = new LightweightSurfaceHost(_surface, LightweightPlayerKind.Background, DiagnosticCategory, ReloadSourceAsync);
        _host.ContentVisibilityChanged += OnContentVisibilityChanged;
        Children.Add(_surface);
        Children.Add(_overlayPresenter);
    }

    /// <param name="platformFactory">The platform the control's player is created with.</param>
    public BackgroundSurface(IMediaControlPlatformFactory platformFactory)
        : this()
    {
        PlatformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
    }

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    /// <summary>Gets the control's player once created (diagnostics, events), else <see langword="null"/>.</summary>
    public IMediaControlSession? Session => _host.Controller.Session;

    /// <summary>
    /// Gets or sets the native library folders (BASS, FFmpeg) the control's player is created with;
    /// <see langword="null"/> (the default) uses <see cref="DLLPath"/> and the platform's default
    /// folders. Read when the player is created.
    /// </summary>
    public IReadOnlyList<string>? NativeLibraryPaths
    {
        get => _host.Controller.NativeLibraryPaths;
        set => _host.Controller.NativeLibraryPaths = value;
    }

    /// <summary>Gets or sets the platform the player is created with; it cannot change once the player exists.</summary>
    public IMediaControlPlatformFactory? PlatformFactory
    {
        get => _host.PlatformFactory;
        set
        {
            IMediaControlPlatformFactory? old = _host.PlatformFactory;
            _host.PlatformFactory = value;
            RaisePropertyChanged(PlatformFactoryProperty, old, value);
        }
    }

    /// <summary>Gets or sets whether a loaded video starts by itself (default <see langword="true"/>).</summary>
    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    /// <summary>Gets or sets whether the media hides until its first frame (default <see langword="true"/>).</summary>
    public bool AutoHide
    {
        get => GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    /// <summary>Gets whether media is shown now (false for a solid colour).</summary>
    public bool IsContentVisible => _host.IsContentVisible;

    /// <summary>Gets or sets whether the surface may use the shared GPU image path (see <see cref="MediaSurface.PreferGpuSharedSurface"/>).</summary>
    public bool PreferGpuSharedSurface
    {
        get => GetValue(PreferGpuSharedSurfaceProperty);
        set => SetValue(PreferGpuSharedSurfaceProperty, value);
    }

    /// <summary>Gets or sets what the background shows (see the remarks); a change reloads while in the visual tree.</summary>
    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Gets or sets the opaque colour shown without media, and behind it (default black).</summary>
    public Color BackgroundColor
    {
        get => GetValue(BackgroundColorProperty);
        set => SetValue(BackgroundColorProperty, value);
    }

    /// <summary>Gets or sets content presented above the background (application UI).</summary>
    public object? OverlayContent
    {
        get => GetValue(OverlayContentProperty);
        set => SetValue(OverlayContentProperty, value);
    }

    /// <summary>Gets or sets whether <see cref="OverlayContent"/> takes pointer input (default <see langword="false"/>).</summary>
    public bool OverlayHitTestVisible
    {
        get => GetValue(OverlayHitTestVisibleProperty);
        set => SetValue(OverlayHitTestVisibleProperty, value);
    }

    /// <summary>The inner surface (tests).</summary>
    internal MediaSurface Surface => _surface;

    /// <summary>The overlay presenter (tests).</summary>
    internal ContentPresenter OverlayPresenter => _overlayPresenter;

    /// <summary>Shows a solid colour: sets <see cref="BackgroundColor"/> and <see cref="Source"/>.</summary>
    public void SetColor(Color color)
    {
        SetCurrentValue(BackgroundColorProperty, color);
        SetCurrentValue(SourceProperty, color);
    }

    /// <summary>Loads an image or video file; <see langword="true"/> when accepted and still the latest load.</summary>
    public Task<bool> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadMediaAsync(MediaInputSource.FromFile(path));
    }

    /// <summary>Loads an Avalonia bitmap (copied with <see cref="AvaloniaMediaInputSources.FromBitmap"/>).</summary>
    public Task<bool> LoadAsync(Bitmap bitmap) => LoadMediaAsync(AvaloniaMediaInputSources.FromBitmap(bitmap));

    /// <summary>Loads a Core source (a file, a BGRA image such as <see cref="MediaInputSource.FromColor"/>, network video).</summary>
    public Task<bool> LoadAsync(MediaInputSource source) => LoadMediaAsync(source);

    public Task<bool> PlayAsync() => _host.Controller.PlayAsync();

    public Task<bool> PauseAsync() => _host.Controller.PauseAsync();

    public Task<bool> StopAsync() => _host.Controller.StopAsync();

    /// <summary>Releases the player (UI thread); the control cannot load afterwards.</summary>
    public void Dispose() => _host.Dispose();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _host.QueueReload();
        }
        else if (change.Property == AutoPlayProperty)
        {
            _host.Controller.AutoPlay = change.NewValue is true;
        }
        else if (change.Property == AutoHideProperty)
        {
            _surface.AutoHide = change.NewValue is true;
        }
        else if (change.Property == PreferGpuSharedSurfaceProperty)
        {
            _surface.PreferGpuSharedSurface = change.NewValue is true;
        }
        else if (change.Property == BackgroundColorProperty && change.NewValue is Color color)
        {
            _surface.Background = new SolidColorBrush(Color.FromArgb(byte.MaxValue, color.R, color.G, color.B));
        }
        else if (change.Property == OverlayContentProperty)
        {
            _overlayPresenter.Content = change.NewValue;
            _overlayPresenter.IsVisible = change.NewValue is not null;
        }
        else if (change.Property == OverlayHitTestVisibleProperty)
        {
            _overlayPresenter.IsHitTestVisible = change.NewValue is true;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _host.OnAttachedToVisualTree();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _host.OnDetachedFromVisualTree();
        base.OnDetachedFromVisualTree(e);
    }

    private Task<bool> LoadMediaAsync(MediaInputSource source)
    {
        // The controller raises PlayerChanged where this call resumes: it must be the UI thread.
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);
        return _host.Controller.LoadAsync(source, PlatformFactory);
    }

    // A newer Source abandons a load in progress: Core's controller checks after every await, and
    // a solid colour releases the player.
    private Task ReloadSourceAsync() => Source switch
    {
        null => ShowSolidColor(BackgroundColor),
        Color color => ShowSolidColor(color),
        ISolidColorBrush brush => ShowSolidColor(brush.Color),
        string path => LoadAsync(path),
        Bitmap bitmap => LoadAsync(bitmap),
        MediaInputSource source => LoadAsync(source),
        _ => throw new NotSupportedException($"Background source type {Source.GetType().Name} is not supported."),
    };

    private Task ShowSolidColor(Color color)
    {
        SetCurrentValue(BackgroundColorProperty, Color.FromArgb(byte.MaxValue, color.R, color.G, color.B));
        _host.Controller.ReleasePlayer();
        return Task.CompletedTask;
    }

    private void OnContentVisibilityChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        var isVisible = _host.IsContentVisible;
        RaisePropertyChanged(IsContentVisibleProperty, !isVisible, isVisible);
        ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
