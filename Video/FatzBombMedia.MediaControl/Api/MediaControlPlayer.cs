using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.Graphics;
using FatzBombMedia.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Core-owned media-control session. Framework adapters supply surfaces; this object owns playback,
/// layer composition, native target fan-out, and presentation coordination.
/// </summary>
public class MediaControlPlayer : IMediaControlSession
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMediaEngine _mediaEngine;
    private readonly NativeMediaPresentationCoordinator _presentationCoordinator;
    private readonly MediaControlOperationQueue _controlOperations;
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _presentationSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public MediaControlPlayer()
        : this(new MediaPlayerConfiguration(), DLLPath.GetConfiguredNativeLibraryPaths())
    {
    }

    public MediaControlPlayer(MediaPlayerConfiguration configuration)
        : this(configuration, DLLPath.GetConfiguredNativeLibraryPaths())
    {
    }

    public MediaControlPlayer(string? userLibraryPath)
        : this(new MediaPlayerConfiguration(), ToNativeLibraryPathArrayOrDefault(userLibraryPath))
    {
    }

    public MediaControlPlayer(IEnumerable<string> nativeLibraryPaths)
        : this(new MediaPlayerConfiguration(), nativeLibraryPaths)
    {
    }

    public MediaControlPlayer(MediaPlayerConfiguration configuration, string? userLibraryPath)
        : this(configuration, ToNativeLibraryPathArrayOrDefault(userLibraryPath))
    {
    }

    public MediaControlPlayer(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths)
    {
        Configuration = configuration;
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        _serviceProvider = MediaPlayerServiceProvider.Create(Configuration, resolvedNativeLibraryPaths);
        ResolveActualMethods();

        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

        AcceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
        Metadata = new MediaMetadata(_mediaEngine);
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        _presentationCoordinator = new NativeMediaPresentationCoordinator(_mediaEngine, Configuration);
        Controls = new MediaControls(_mediaEngine, _presentationCoordinator);
        File = new MediaFile(_mediaEngine, _presentationCoordinator, Metadata, Controls, Effects, CdgVisuals, archiveService);
        _controlOperations = new MediaControlOperationQueue("MediaControl");

        Controls.MediaEnded += OnControlsMediaEnded;
        Controls.MediaStarted += OnControlsMediaStarted;
        Controls.MediaStopped += OnControlsMediaStopped;
        _presentationCoordinator.FramePresented += OnPresentationFramePresented;
    }

    public MediaControls Controls { get; }

    public MediaFile File { get; }

    public MediaEffects Effects { get; }

    public CdgVisuals CdgVisuals { get; }

    public MediaMetadata Metadata { get; }

    public AcceptedMediaTypes AcceptedTypes { get; }

    public IReadOnlyList<MediaFileFilter> Filters => AcceptedTypes.Filters;

    public string OpenFileDialogFilter => AcceptedTypes.OpenFileDialogFilter;

    public static string LastNativeLoadMessage => Bootstrap.Message;

    public static IReadOnlyList<string> LastNativeLoadMessages => Bootstrap.NativeLoadMessages;

    public MediaPlayerConfiguration Configuration { get; }

    public VideoDecodingMethod ActualVideoDecoding { get; private set; }

    public RenderingMethod ActualRendering { get; private set; }

    public string PresentationBackend => _presentationCoordinator.PresentationBackend;

    public bool PlayOnLoad
    {
        get => File.PlayOnLoad;
        set => File.PlayOnLoad = value;
    }

    public bool LoopMedia
    {
        get => Controls.LoopMedia;
        set => Controls.LoopMedia = value;
    }

    public VideoDisplayMode VideoDisplayMode
    {
        get => _presentationCoordinator.VideoDisplayMode;
        set => _presentationCoordinator.VideoDisplayMode = value;
    }

    public bool BackgroundAutoDetect
    {
        get => File.BackgroundAutoDetect;
        set => File.BackgroundAutoDetect = value;
    }

    public bool Normalize
    {
        get => Effects.Normalize;
        set => Effects.Normalize = value;
    }

    public double NormalizationGain => Effects.NormalizationGain;

    public bool IsOpen => _mediaEngine.IsOpen;

    public MediaPlayerState State => _mediaEngine.State;

    public MediaContent Content => _mediaEngine.Content;

    public bool HasVideo => Content.HasFlag(MediaContent.Video);

    public bool HasAudio => Content.HasFlag(MediaContent.Audio);

    public bool HasCdg => Content.HasFlag(MediaContent.Cdg);

    public IVideoEngine? Video => _mediaEngine.Video;

    public IAudioEngine Audio => _mediaEngine.Audio;

    public ICdgEngine? Cdg => _mediaEngine.Cdg;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_mediaEngine.Position);
        set => Seek(value);
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);

    public static MediaControlPlayer Create(string? userLibraryPath = null) => new(userLibraryPath);

    public static MediaControlPlayer Create(MediaPlayerConfiguration configuration, string? userLibraryPath = null) =>
        new(configuration, userLibraryPath);

    public static MediaControlPlayer Create(IEnumerable<string> nativeLibraryPaths) => new(nativeLibraryPaths);

    public static Task<MediaControlPlayer> CreateAsync(string? userLibraryPath = null) =>
        Task.Run(() => new MediaControlPlayer(userLibraryPath));

    public static Task<MediaControlPlayer> CreateAsync(MediaPlayerConfiguration configuration, string? userLibraryPath = null) =>
        Task.Run(() => new MediaControlPlayer(configuration, userLibraryPath));

    public static Task<MediaControlPlayer> CreateAsync(IEnumerable<string> nativeLibraryPaths) =>
        Task.Run(() => new MediaControlPlayer(nativeLibraryPaths));

    public static Task<MediaControlPlayer> CreateAsync(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths) =>
        Task.Run(() => new MediaControlPlayer(configuration, nativeLibraryPaths));

    public bool Load(string path) => File.Load(path);

    public bool Load(string path, bool ignoreSidecar = false) => File.Load(path, ignoreSidecar);

    public bool Load(MediaInputSource source, bool ignoreSidecar = false) => File.Load(source, ignoreSidecar);

    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) => File.Load(path, backgroundPath, ignoreSidecar);

    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        File.Load(source, backgroundSource, ignoreSidecar);

    public Task<bool> LoadAsync(string path) => _controlOperations.Enqueue(() => File.LoadAsync(path));

    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(path, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(source, ignoreSidecar));

    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(path, backgroundPath, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        _controlOperations.Enqueue(() => File.LoadAsync(source, backgroundSource, ignoreSidecar));

    public bool Play() => Controls.Play();

    public Task<bool> PlayAsync() => _controlOperations.Enqueue(Controls.Play);

    public bool Pause() => Controls.Pause();

    public Task<bool> PauseAsync() => _controlOperations.Enqueue(Controls.Pause);

    public bool Stop() => Controls.Stop();

    public Task<bool> StopAsync() => _controlOperations.Enqueue(Controls.Stop);

    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    public bool Seek(double seconds)
    {
        try
        {
            var success = Controls.Seek(seconds);
            if (success)
                _presentationCoordinator.RefreshCurrentPosition();

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    public Task<bool> SeekAsync(TimeSpan position) => SeekAsync(position.TotalSeconds);

    public async Task<bool> SeekAsync(double seconds)
    {
        try
        {
            var success = await _mediaEngine.SeekAsync(seconds).ConfigureAwait(false);
            if (success)
                _presentationCoordinator.RefreshCurrentPosition();

            return success;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    public void Close() => File.Close();

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid)
            return false;

        var attached = _presentationCoordinator.AttachPresentationSurface(surface);
        if (attached)
            _presentationSurfaces[surface.Id] = surface;

        return attached;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
            return false;

        if (!_presentationSurfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
            return false;

        if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
            return true;

        surface = surface.WithSize(pixelWidth, pixelHeight);
        var resized = _presentationCoordinator.ResizePresentationSurface(surface.Id, pixelWidth, pixelHeight);
        if (resized)
            _presentationSurfaces[surface.Id] = surface;

        return resized;
    }

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        return _presentationSurfaces.ContainsKey(surfaceId)
            && _presentationCoordinator.SetPresentationSurfaceLiveResize(surfaceId, isResizing);
    }

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        var detached = _presentationCoordinator.DetachPresentationSurface(surfaceId);
        if (detached)
            _ = _presentationSurfaces.Remove(surfaceId);

        return detached;
    }

    public event EventHandler<double>? NormalizationGainChanged
    {
        add => Effects.NormalizationGainChanged += value;
        remove => Effects.NormalizationGainChanged -= value;
    }

    public event EventHandler? MediaEnded;

    public event EventHandler? MediaStarted;

    public event EventHandler? PresentationFramePresented;

    public event EventHandler? MediaStopped;

    private void ResolveActualMethods()
    {
        GpuCapabilityInfo capabilities = GpuRenderingCapabilities.Capabilities;

        ActualVideoDecoding = capabilities.SupportsD3D11
            ? VideoDecodingMethod.Hardware
            : VideoDecodingMethod.Software;

        ActualRendering = capabilities.SupportsD3D11
            ? RenderingMethod.DirectX11
            : RenderingMethod.Software;
    }

    private void OnControlsMediaEnded(object? sender, EventArgs e)
    {
        EventHandler? handler = MediaEnded;
        if (handler == null)
            return;

        _ = _controlOperations.Enqueue(() => handler.Invoke(this, EventArgs.Empty));
    }

    private void OnControlsMediaStarted(object? sender, EventArgs e) => MediaStarted?.Invoke(this, EventArgs.Empty);

    private void OnControlsMediaStopped(object? sender, EventArgs e) => MediaStopped?.Invoke(this, EventArgs.Empty);

    private void OnPresentationFramePresented(object? sender, EventArgs e) => PresentationFramePresented?.Invoke(this, EventArgs.Empty);

    private static string[] ResolveNativeLibraryPaths(IEnumerable<string> nativeLibraryPaths)
    {
        var resolvedPaths = nativeLibraryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in resolvedPaths)
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Native library path does not exist: {path}");

        return resolvedPaths;
    }

    private static IReadOnlyList<string> ToNativeLibraryPathArrayOrDefault(string? nativeLibraryPath) =>
        string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? DLLPath.GetConfiguredNativeLibraryPaths()
            : [nativeLibraryPath];

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _ = Stop();
            File.Close();
            Controls.MediaEnded -= OnControlsMediaEnded;
            Controls.MediaStarted -= OnControlsMediaStarted;
            Controls.MediaStopped -= OnControlsMediaStopped;
            _presentationCoordinator.FramePresented -= OnPresentationFramePresented;
            Controls.Dispose();
            _presentationCoordinator.Dispose();
            _mediaEngine.Dispose();
            (_serviceProvider as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Dispose failed: {ex.Message}");
        }
        finally
        {
            _controlOperations.Dispose();
            _disposed = true;
        }
    }
}
