using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl;

/// <summary>
/// Core-owned public session facade for a FatzBombMedia media-control instance.
/// </summary>
/// <remarks>
/// Stage 10S makes this core type the session object that adapters expose to hosts.
/// During the boundary migration it delegates to the current media-control implementation;
/// later stages move that implementation fully into the core project. Framework adapters
/// should expose this session and provide only surface/lifecycle interop.
/// </remarks>
/// <remarks>
/// Creates a core session facade over the supplied media-control implementation.
/// </remarks>
/// <param name="implementation">The current media-control implementation.</param>
/// <param name="ownsImplementation">
/// True when disposing this session should dispose the wrapped implementation.
/// Adapter-owned controls should normally pass false so the framework control remains
/// responsible for its own lifetime.
/// </param>
public sealed class MediaControlSession(IMediaControlSession implementation, bool ownsImplementation = false) : IMediaControlSession
{
    private readonly IMediaControlSession _implementation = implementation ?? throw new ArgumentNullException(nameof(implementation));
    private bool _disposed;

    /// <summary>Gets or sets whether loading media should automatically begin playback.</summary>
    public bool PlayOnLoad
    {
        get => _implementation.PlayOnLoad;
        set => _implementation.PlayOnLoad = value;
    }

    /// <summary>Gets or sets whether playback loops when the end of media is reached.</summary>
    public bool LoopMedia
    {
        get => _implementation.LoopMedia;
        set => _implementation.LoopMedia = value;
    }

    /// <summary>Gets whether media is currently open.</summary>
    public bool IsOpen => _implementation.IsOpen;

    /// <summary>Gets the current playback state.</summary>
    public MediaPlayerState State => _implementation.State;

    /// <summary>Gets the content roles present in the loaded media.</summary>
    public MediaContent Content => _implementation.Content;

    /// <summary>Gets whether the loaded media has video content.</summary>
    public bool HasVideo => _implementation.HasVideo;

    /// <summary>Gets whether the loaded media has audio content.</summary>
    public bool HasAudio => _implementation.HasAudio;

    /// <summary>Gets whether the loaded media has CDG content.</summary>
    public bool HasCdg => _implementation.HasCdg;

    /// <summary>Gets or sets how primary video content maps into the presentation surface.</summary>
    public VideoDisplayMode VideoDisplayMode
    {
        get => _implementation.VideoDisplayMode;
        set => _implementation.VideoDisplayMode = value;
    }

    /// <summary>Gets or sets the current playback position.</summary>
    public TimeSpan Position
    {
        get => _implementation.Position;
        set => _implementation.Position = value;
    }

    /// <summary>Gets the loaded media duration.</summary>
    public TimeSpan Duration => _implementation.Duration;

    /// <summary>Loads media from a file path.</summary>
    public bool Load(string path) => _implementation.Load(path);

    /// <summary>Loads media from a file path, optionally ignoring matching sidecar/archive backgrounds.</summary>
    public bool Load(string path, bool ignoreSidecar = false) => _implementation.Load(path, ignoreSidecar);

    /// <summary>Loads media from a framework-neutral Core source.</summary>
    public bool Load(MediaInputSource source, bool ignoreSidecar = false) => _implementation.Load(source, ignoreSidecar);

    /// <summary>Loads media with an optional explicit background. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) => _implementation.Load(path, backgroundPath, ignoreSidecar);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source.</summary>
    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) => _implementation.Load(source, backgroundSource, ignoreSidecar);

    /// <summary>Loads media from a file path asynchronously.</summary>
    public Task<bool> LoadAsync(string path) => _implementation.LoadAsync(path);

    /// <summary>Loads media from a file path asynchronously, optionally ignoring matching sidecar/archive backgrounds.</summary>
    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) => _implementation.LoadAsync(path, ignoreSidecar);

    /// <summary>Loads media from a framework-neutral Core source asynchronously.</summary>
    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) => _implementation.LoadAsync(source, ignoreSidecar);

    /// <summary>Loads media with an optional explicit background asynchronously. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) => _implementation.LoadAsync(path, backgroundPath, ignoreSidecar);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source asynchronously.</summary>
    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) => _implementation.LoadAsync(source, backgroundSource, ignoreSidecar);

    /// <summary>Starts or resumes playback.</summary>
    public bool Play() => _implementation.Play();

    /// <summary>Starts or resumes playback asynchronously on the Core control queue.</summary>
    public Task<bool> PlayAsync() => _implementation.PlayAsync();

    /// <summary>Pauses playback.</summary>
    public bool Pause() => _implementation.Pause();

    /// <summary>Pauses playback asynchronously on the Core control queue.</summary>
    public Task<bool> PauseAsync() => _implementation.PauseAsync();

    /// <summary>Stops playback and resets position.</summary>
    public bool Stop() => _implementation.Stop();

    /// <summary>Stops playback asynchronously on the Core control queue.</summary>
    public Task<bool> StopAsync() => _implementation.StopAsync();

    /// <summary>Seeks to a playback position.</summary>
    public bool Seek(TimeSpan position) => _implementation.Seek(position);

    /// <summary>Seeks to a playback position in seconds.</summary>
    public bool Seek(double seconds) => _implementation.Seek(seconds);

    /// <summary>Closes the currently loaded media.</summary>
    public void Close() => _implementation.Close();

    /// <summary>Attaches or updates a framework-provided presentation surface.</summary>
    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => _implementation.AttachPresentationSurface(surface);

    /// <summary>Resizes a previously attached framework-provided presentation surface.</summary>
    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) =>
        _implementation.ResizePresentationSurface(surfaceId, pixelWidth, pixelHeight);

    /// <summary>Notifies Core that a framework-provided presentation surface is in an active host resize/move loop.</summary>
    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) =>
        _implementation.SetPresentationSurfaceLiveResize(surfaceId, isResizing);

    /// <summary>Detaches a previously attached framework-provided presentation surface.</summary>
    public bool DetachPresentationSurface(string surfaceId) => _implementation.DetachPresentationSurface(surfaceId);

    /// <summary>Raised when media reaches the end.</summary>
    public event EventHandler? MediaEnded
    {
        add => _implementation.MediaEnded += value;
        remove => _implementation.MediaEnded -= value;
    }

    /// <summary>Raised when media playback starts or resumes.</summary>
    public event EventHandler? MediaStarted
    {
        add => _implementation.MediaStarted += value;
        remove => _implementation.MediaStarted -= value;
    }

    /// <summary>Raised after Core presents a frame to an attached presentation surface.</summary>
    public event EventHandler? PresentationFramePresented
    {
        add => _implementation.PresentationFramePresented += value;
        remove => _implementation.PresentationFramePresented -= value;
    }

    /// <summary>Raised when media playback is stopped.</summary>
    public event EventHandler? MediaStopped
    {
        add => _implementation.MediaStopped += value;
        remove => _implementation.MediaStopped -= value;
    }

    /// <summary>
    /// Disposes the wrapped implementation only when this session was created as its owner.
    /// Framework adapter controls normally own their implementation lifetime separately.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (ownsImplementation)
        {
            _implementation.Dispose();
        }

        _disposed = true;
    }
}
