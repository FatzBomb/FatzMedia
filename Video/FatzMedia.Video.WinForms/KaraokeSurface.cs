using System.ComponentModel;

namespace FatzMedia.Video.WinForms;

/// <summary>
/// Self-contained 4:3 karaoke player for CD+G (graphics alone, audio + CD+G, archives) and
/// MIDI/KAR media (Stage 046A, the WinForms counterpart of the WPF and Avalonia
/// <c>KaraokeSurface</c>). It creates its own player from <see cref="PlatformFactory"/>, rejects
/// video and plain audio after inspection (Core's <see cref="LightweightPlayerPolicy"/>), and shows
/// the karaoke in a centred 4:3 <see cref="MediaSurface"/> on <see cref="CanvasColor"/>.
/// </summary>
/// <remarks>
/// The player is created on the first load and released when the control's handle is destroyed;
/// a new handle loads <see cref="Source"/> again. Instead of the WPF <c>PresentationMode</c>, the
/// surface's path is chosen as on any WinForms <see cref="MediaSurface"/>
/// (<see cref="PreferNativeWindow"/>). Events are raised on the UI thread.
/// </remarks>
public class KaraokeSurface : Control
{
    /// <summary>The diagnostic category of this control's own notices.</summary>
    public const string DiagnosticCategory = "WinForms.KaraokeSurface";

    readonly MediaSurface _surface = new();
    readonly LightweightSurfaceHost _host;
    // The player holds a song the Source loaded (not one LoadAsync loaded directly).
    bool _sourceLoaded;
    string? _source;

    public KaraokeSurface()
    {
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _host = new LightweightSurfaceHost(this, _surface, LightweightPlayerKind.Karaoke, DiagnosticCategory, ReloadSourceAsync);
        _host.ContentVisibilityChanged += (_, _) => ContentVisibilityChanged?.Invoke(this, EventArgs.Empty);
        Controls.Add(_surface);
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
    [Browsable(false)]
    public IPlayerSession? Session => _host.Controller.Session;

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
    public IPlatformFactory? PlatformFactory
    {
        get => _host.PlatformFactory;
        set => _host.PlatformFactory = value;
    }

    /// <summary>Gets or sets whether accepted media starts by itself (default <see langword="true"/>).</summary>
    [DefaultValue(true)]
    public bool AutoPlay
    {
        get => _host.Controller.AutoPlay;
        set => _host.Controller.AutoPlay = value;
    }

    /// <summary>Gets or sets whether the surface hides until the first frame and after stop (default <see langword="true"/>).</summary>
    [DefaultValue(true)]
    public bool AutoHide
    {
        get => _surface.AutoHide;
        set => _surface.AutoHide = value;
    }

    /// <summary>Gets whether the karaoke is shown now (see <see cref="MediaSurface.IsContentVisible"/>).</summary>
    [Browsable(false)]
    public bool IsContentVisible => _host.IsContentVisible;

    /// <summary>Gets or sets whether the renderer may present into a child window (see <see cref="MediaSurface.PreferNativeWindow"/>).</summary>
    [DefaultValue(true)]
    public bool PreferNativeWindow
    {
        get => _surface.PreferNativeWindow;
        set => _surface.PreferNativeWindow = value;
    }

    /// <summary>Gets or sets the karaoke file to load (once the control has a handle); a change reloads.</summary>
    [DefaultValue(null)]
    public string? Source
    {
        get => _source;
        set
        {
            if (string.Equals(_source, value, StringComparison.Ordinal))
                return;

            _source = value;
            _host.QueueReload();
        }
    }

    /// <summary>Gets or sets the opaque colour around the 4:3 karaoke area (default black).</summary>
    public Color CanvasColor
    {
        get => BackColor;
        set => BackColor = Color.FromArgb(byte.MaxValue, value);
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
        ObjectDisposedException.ThrowIf(_host.IsDisposed, this);
        return _host.Controller.LoadAsync(path, PlatformFactory);
    }

    public Task<bool> PlayAsync() => _host.Controller.PlayAsync();

    public Task<bool> PauseAsync() => _host.Controller.PauseAsync();

    public Task<bool> StopAsync() => _host.Controller.StopAsync();

    bool ShouldSerializeCanvasColor() => BackColor != Color.Black;

    void ResetCanvasColor() => CanvasColor = Color.Black;

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

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        (var x, var y, var width, var height) = KaraokeLayout.Fit(ClientSize.Width, ClientSize.Height);
        _surface.Bounds = new Rectangle((int)x, (int)y, (int)width, (int)height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _host.Dispose();

        base.Dispose(disposing);
    }

    // Clearing the source releases the player, so the previous song does not keep playing; a
    // reload without one (attaching) leaves a song LoadAsync loaded directly alone.
    Task ReloadSourceAsync()
    {
        if (!string.IsNullOrWhiteSpace(_source))
        {
            _sourceLoaded = true;
            return _host.Controller.LoadAsync(_source, PlatformFactory);
        }

        if (_sourceLoaded)
        {
            _sourceLoaded = false;
            _host.Controller.ReleasePlayer();
        }

        return Task.CompletedTask;
    }
}
