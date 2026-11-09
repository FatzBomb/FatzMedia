using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// Self-contained 4:3 karaoke player for CD+G (graphics alone, audio + CD+G, archives) and
/// MIDI/KAR media (Stage 046A, the Avalonia counterpart of the WPF and WinForms
/// <c>KaraokeSurface</c>). It creates its own player from <see cref="PlatformFactory"/>, rejects
/// video and plain audio after inspection (Core's <see cref="LightweightPlayerPolicy"/>), and shows
/// the karaoke in a centred 4:3 <see cref="MediaSurface"/> on <see cref="CanvasColor"/>.
/// </summary>
/// <remarks>
/// The player is created on the first load and released when the control leaves the visual tree;
/// attached again, it loads <see cref="Source"/> again. Instead of the WPF <c>PresentationMode</c>,
/// the surface's path is chosen as on any Avalonia <see cref="MediaSurface"/>
/// (<see cref="PreferGpuSharedSurface"/>). Events are raised on the UI thread.
/// </remarks>
public class KaraokeSurface : Panel, IDisposable
{
    /// <summary>The diagnostic category of this control's own notices.</summary>
    public const string DiagnosticCategory = "Avalonia.KaraokeSurface";

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<KaraokeSurface, string?>(nameof(Source));

    public static readonly StyledProperty<bool> AutoPlayProperty =
        AvaloniaProperty.Register<KaraokeSurface, bool>(nameof(AutoPlay), true);

    public static readonly StyledProperty<bool> AutoHideProperty =
        AvaloniaProperty.Register<KaraokeSurface, bool>(nameof(AutoHide), true);

    public static readonly StyledProperty<Color> CanvasColorProperty =
        AvaloniaProperty.Register<KaraokeSurface, Color>(nameof(CanvasColor), Colors.Black);

    public static readonly StyledProperty<bool> PreferGpuSharedSurfaceProperty =
        AvaloniaProperty.Register<KaraokeSurface, bool>(nameof(PreferGpuSharedSurface), true);

    public static readonly DirectProperty<KaraokeSurface, IPlatformFactory?> PlatformFactoryProperty =
        AvaloniaProperty.RegisterDirect<KaraokeSurface, IPlatformFactory?>(
            nameof(PlatformFactory),
            static surface => surface.PlatformFactory,
            static (surface, value) => surface.PlatformFactory = value);

    public static readonly DirectProperty<KaraokeSurface, bool> IsContentVisibleProperty =
        AvaloniaProperty.RegisterDirect<KaraokeSurface, bool>(nameof(IsContentVisible), static surface => surface.IsContentVisible);

    readonly MediaSurface _surface = new();
    readonly LightweightSurfaceHost _host;
    // The player holds a song the Source loaded (not one LoadAsync loaded directly).
    bool _sourceLoaded;

    public KaraokeSurface()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Colors.Black);
        _host = new LightweightSurfaceHost(_surface, LightweightPlayerKind.Karaoke, DiagnosticCategory, ReloadSourceAsync);
        _host.ContentVisibilityChanged += OnContentVisibilityChanged;
        Children.Add(_surface);
    }

    /// <param name="platformFactory">The platform the control's player is created with.</param>
    public KaraokeSurface(IPlatformFactory platformFactory)
        : this()
    {
        PlatformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
    }

    /// <summary>Raised on the UI thread when <see cref="IsContentVisible"/> changes.</summary>
    public event EventHandler? ContentVisibilityChanged;

    /// <summary>Gets the control's player once created (diagnostics, events), else <see langword="null"/>.</summary>
    public IPlayerSession? Session => _host.Controller.Session;

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
    public IPlatformFactory? PlatformFactory
    {
        get => _host.PlatformFactory;
        set
        {
            var old = _host.PlatformFactory;
            _host.PlatformFactory = value;
            RaisePropertyChanged(PlatformFactoryProperty, old, value);
        }
    }

    /// <summary>Gets or sets whether accepted media starts by itself (default <see langword="true"/>).</summary>
    public bool AutoPlay
    {
        get => GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    /// <summary>Gets or sets whether the surface hides until the first frame and after stop (default <see langword="true"/>).</summary>
    public bool AutoHide
    {
        get => GetValue(AutoHideProperty);
        set => SetValue(AutoHideProperty, value);
    }

    /// <summary>Gets whether the karaoke is shown now (see <see cref="MediaSurface.IsContentVisible"/>).</summary>
    public bool IsContentVisible => _host.IsContentVisible;

    /// <summary>Gets or sets whether the surface may use the shared GPU image path (see <see cref="MediaSurface.PreferGpuSharedSurface"/>).</summary>
    public bool PreferGpuSharedSurface
    {
        get => GetValue(PreferGpuSharedSurfaceProperty);
        set => SetValue(PreferGpuSharedSurfaceProperty, value);
    }

    /// <summary>Gets or sets the karaoke file to load (while in the visual tree); a change reloads.</summary>
    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Gets or sets the opaque colour around the 4:3 karaoke area (default black).</summary>
    public Color CanvasColor
    {
        get => GetValue(CanvasColorProperty);
        set => SetValue(CanvasColorProperty, value);
    }

    /// <summary>The inner surface (tests).</summary>
    internal MediaSurface Surface => _surface;

    /// <summary>
    /// Loads <paramref name="path"/>: <see langword="true"/> when it is karaoke and this load is still
    /// the latest; anything else is closed.
    /// </summary>
    public Task<bool> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // The controller raises PlayerChanged where this call resumes: it must be the UI thread.
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);
        return _host.Controller.LoadAsync(path, PlatformFactory);
    }

    public Task<bool> PlayAsync() => _host.Controller.PlayAsync();

    public Task<bool> PauseAsync() => _host.Controller.PauseAsync();

    public Task<bool> StopAsync() => _host.Controller.StopAsync();

    /// <summary>Releases the player (UI thread); the control cannot load afterwards.</summary>
    public void Dispose() => _host.Dispose();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            _host.QueueReload();
        else if (change.Property == AutoPlayProperty)
            _host.Controller.AutoPlay = change.NewValue is true;
        else if (change.Property == AutoHideProperty)
            _surface.AutoHide = change.NewValue is true;
        else if (change.Property == PreferGpuSharedSurfaceProperty)
            _surface.PreferGpuSharedSurface = change.NewValue is true;
        else if (change.Property == CanvasColorProperty && change.NewValue is Color color)
            Background = new SolidColorBrush(Color.FromArgb(byte.MaxValue, color.R, color.G, color.B));
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        _ = base.ArrangeOverride(finalSize);
        (var x, var y, var width, var height) = KaraokeLayout.Fit(finalSize.Width, finalSize.Height);
        _surface.Arrange(new Rect(x, y, width, height));
        return finalSize;
    }

    // Clearing the source releases the player, so the previous song does not keep playing; a
    // reload without one (attaching) leaves a song LoadAsync loaded directly alone.
    Task ReloadSourceAsync()
    {
        if (!string.IsNullOrWhiteSpace(Source))
        {
            _sourceLoaded = true;
            return _host.Controller.LoadAsync(Source, PlatformFactory);
        }

        if (_sourceLoaded)
        {
            _sourceLoaded = false;
            _host.Controller.ReleasePlayer();
        }

        return Task.CompletedTask;
    }

    void OnContentVisibilityChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.VerifyAccess();
        var isVisible = _host.IsContentVisible;
        RaisePropertyChanged(IsContentVisibleProperty, !isVisible, isVisible);
        ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
