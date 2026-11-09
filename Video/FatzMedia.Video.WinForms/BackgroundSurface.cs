using System.ComponentModel;

namespace FatzMedia.Video.WinForms;

/// <summary>
/// Self-contained background for solid colours, images and silent looping video (Stage 046A, the
/// WinForms counterpart of the WPF and Avalonia <c>BackgroundSurface</c>). Media plays in a player
/// of its own in <see cref="Core.Configuration.MediaPlayerMode.Background"/> mode (no audio engine), created from
/// <see cref="PlatformFactory"/>; audio, CD+G and lyrics are rejected (Core's
/// <see cref="LightweightPlayerPolicy"/>). A solid colour needs no player: the surface shows
/// <see cref="BackgroundColor"/>. <see cref="OverlayContent"/> shows host controls above it.
/// </summary>
/// <remarks>
/// <see cref="Source"/> takes <see langword="null"/> (shows <see cref="BackgroundColor"/>), a
/// <see cref="Color"/>, a file path, a <see cref="Bitmap"/> (converted with
/// <see cref="WinFormsMediaInputSources.FromBitmap"/>) or a <see cref="MediaInputSource"/>. The
/// player is released when the control's handle is destroyed; a new handle loads the source again.
/// Instead of the WPF <c>PresentationMode</c>, the surface's path is chosen as on any WinForms
/// <see cref="MediaSurface"/> (<see cref="PreferNativeWindow"/>). Events are raised on the UI thread.
/// </remarks>
public class BackgroundSurface : Control
{
    /// <summary>The diagnostic category of this control's own notices.</summary>
    public const string DiagnosticCategory = "WinForms.BackgroundSurface";

    private readonly MediaSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly LightweightSurfaceHost _host;
    private object? _source;
    private Color _backgroundColor = Color.Black;

    public BackgroundSurface()
    {
        BackColor = Color.Black;
        _host = new LightweightSurfaceHost(this, _surface, LightweightPlayerKind.Background, DiagnosticCategory, ReloadSourceAsync);
        _host.ContentVisibilityChanged += (_, _) => ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_surface);
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
    [Browsable(false)]
    public IMediaControlSession? Session => _host.Controller.Session;

    /// <summary>
    /// Gets or sets the native library folders (BASS, FFmpeg) the control's player is created with;
    /// <see langword="null"/> (the default) uses <see cref="DLLPath"/> and the platform's default
    /// folders. Read when the player is created.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string>? NativeLibraryPaths
    {
        get => _host.Controller.NativeLibraryPaths;
        set => _host.Controller.NativeLibraryPaths = value;
    }

    /// <summary>Gets or sets the platform the player is created with; it cannot change once the player exists.</summary>
    [DefaultValue(null)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IMediaControlPlatformFactory? PlatformFactory
    {
        get => _host.PlatformFactory;
        set => _host.PlatformFactory = value;
    }

    /// <summary>Gets or sets whether a loaded video starts by itself (default <see langword="true"/>).</summary>
    [DefaultValue(true)]
    public bool AutoPlay
    {
        get => _host.Controller.AutoPlay;
        set => _host.Controller.AutoPlay = value;
    }

    /// <summary>Gets or sets whether the media hides until its first frame (default <see langword="true"/>).</summary>
    [DefaultValue(true)]
    public bool AutoHide
    {
        get => _surface.AutoHide;
        set => _surface.AutoHide = value;
    }

    /// <summary>Gets whether media is shown now (false for a solid colour).</summary>
    [Browsable(false)]
    public bool IsContentVisible => _host.IsContentVisible;

    /// <summary>Gets or sets whether the renderer may present into a child window (see <see cref="MediaSurface.PreferNativeWindow"/>).</summary>
    [DefaultValue(true)]
    public bool PreferNativeWindow
    {
        get => _surface.PreferNativeWindow;
        set => _surface.PreferNativeWindow = value;
    }

    /// <summary>Gets or sets what the background shows (see the remarks); a change reloads once the control has a handle.</summary>
    [DefaultValue(null)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? Source
    {
        get => _source;
        set
        {
            if (Equals(_source, value))
                return;

            _source = value;
            _host.QueueReload();
        }
    }

    /// <summary>Gets or sets the opaque colour shown without media, and behind it (default black).</summary>
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            _backgroundColor = Color.FromArgb(byte.MaxValue, value);
            BackColor = _backgroundColor;
            _surface.BackColor = _backgroundColor;
        }
    }

    /// <summary>Gets or sets host controls shown above the background (see <see cref="MediaSurface.OverlayContent"/>).</summary>
    [DefaultValue(null)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? OverlayContent
    {
        get => _surface.OverlayContent;
        set => _surface.OverlayContent = value;
    }

    /// <summary>Gets or sets whether <see cref="OverlayContent"/> takes mouse input (default <see langword="false"/>).</summary>
    [DefaultValue(false)]
    public bool OverlayHitTestVisible
    {
        get => _surface.OverlayHitTestVisible;
        set => _surface.OverlayHitTestVisible = value;
    }

    /// <summary>The inner surface (tests).</summary>
    internal MediaSurface Surface => _surface;

    /// <summary>Shows a solid colour: sets <see cref="BackgroundColor"/> and <see cref="Source"/>.</summary>
    public void SetColor(Color color)
    {
        BackgroundColor = color;
        Source = color;
    }

    /// <summary>Loads an image or video file; <see langword="true"/> when accepted and still the latest load.</summary>
    public Task<bool> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadMediaAsync(MediaInputSource.FromFile(path));
    }

    /// <summary>Loads a bitmap (copied with <see cref="WinFormsMediaInputSources.FromBitmap"/>).</summary>
    public Task<bool> LoadAsync(Bitmap bitmap) => LoadMediaAsync(WinFormsMediaInputSources.FromBitmap(bitmap));

    /// <summary>Loads a Core source (a file, a BGRA image such as <see cref="MediaInputSource.FromColor"/>, network video).</summary>
    public Task<bool> LoadAsync(MediaInputSource source) => LoadMediaAsync(source);

    public Task<bool> PlayAsync() => _host.Controller.PlayAsync();

    public Task<bool> PauseAsync() => _host.Controller.PauseAsync();

    public Task<bool> StopAsync() => _host.Controller.StopAsync();

    private bool ShouldSerializeBackgroundColor() => _backgroundColor != Color.Black;

    private void ResetBackgroundColor() => BackgroundColor = Color.Black;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _host.OnHandleCreated();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _host.OnHandleDestroyed();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host.Dispose();

        base.Dispose(disposing);
    }

    private Task<bool> LoadMediaAsync(MediaInputSource source)
    {
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);
        return _host.Controller.LoadAsync(source, PlatformFactory);
    }

    // A newer Source abandons a load in progress: Core's controller checks after every await, and
    // a solid colour releases the player.
    private Task ReloadSourceAsync() => _source switch
    {
        null => ShowSolidColor(_backgroundColor),
        Color color => ShowSolidColor(color),
        string path => LoadAsync(path),
        Bitmap bitmap => LoadAsync(bitmap),
        MediaInputSource source => LoadAsync(source),
        _ => throw new NotSupportedException($"Background source type {_source.GetType().Name} is not supported."),
    };

    private Task ShowSolidColor(Color color)
    {
        BackgroundColor = color;
        _host.Controller.ReleasePlayer();
        return Task.CompletedTask;
    }
}
