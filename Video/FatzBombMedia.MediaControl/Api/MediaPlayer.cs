using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Controllers;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows.Media;

#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Main API entry point for FatzBombMedia Media Player.
/// Provides a clean, user-friendly interface for media playback with automatic best-quality configuration.
/// GPU acceleration and video decoding methods are automatically detected for optimal performance.
/// </summary>
public sealed class MediaPlayer : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMediaEngine _mediaEngine;
    private bool _disposed;

    // Controller properties - these are the main API surface
    public MediaControls Controls { get; }
    public MediaFile File { get; }
    public MediaEffects Effects { get; }
    public CdgVisuals CdgVisuals { get; }
    public MediaRenderer Renderer { get; }
    public MediaMetadata Metadata { get; }

    /// <summary>
    /// Gets the media extensions accepted by this runtime after native libraries are loaded.
    /// </summary>
    public AcceptedMediaTypes AcceptedTypes { get; }

    /// <summary>
    /// Gets readable file picker filter groups accepted by this runtime.
    /// </summary>
    public IReadOnlyList<MediaFileFilter> Filters => AcceptedTypes.Filters;

    /// <summary>
    /// Gets a Windows OpenFileDialog-compatible filter string accepted by this runtime.
    /// </summary>
    public string OpenFileDialogFilter => AcceptedTypes.OpenFileDialogFilter;

    /// <summary>
    /// Gets the most recent native DLL loading message, including missing required files.
    /// </summary>
    public static string LastNativeLoadMessage => Bootstrap.Message;

    /// <summary>
    /// Gets the most recent readable native DLL loading details.
    /// </summary>
    public static IReadOnlyList<string> LastNativeLoadMessages => Bootstrap.NativeLoadMessages;

    /// <summary>
    /// Gets the configuration used by this MediaPlayer instance.
    /// </summary>
    public MediaPlayerConfiguration Configuration { get; }

    /// <summary>
    /// Gets the actual video decoding method resolved for this instance.
    /// </summary>
    public VideoDecodingMethod ActualVideoDecoding { get; private set; }

    /// <summary>
    /// Gets the actual rendering method resolved for this instance.
    /// </summary>
    public RenderingMethod ActualRendering { get; private set; }

    #region Constructors

    /// <summary>
    /// Creates a new MediaPlayer with default optimal configuration.
    /// Uses automatic detection for best quality hardware acceleration.
    /// </summary>
    public MediaPlayer() : this(new MediaPlayerConfiguration(), DLLPath.GetConfiguredNativeLibraryPaths()) { }

    /// <summary>
    /// Creates a new MediaPlayer with custom configuration.
    /// </summary>
    /// <param name="configuration">Configuration determining GPU/CPU usage and quality settings</param>
    public MediaPlayer(MediaPlayerConfiguration configuration) : this(configuration, DLLPath.GetConfiguredNativeLibraryPaths()) { }

    /// <summary>
    /// Creates a new MediaPlayer with default configuration and custom library path.
    /// </summary>
    /// <param name="userLibraryPath">Path to native libraries (null for default location)</param>
    public MediaPlayer(string? userLibraryPath) : this(new MediaPlayerConfiguration(), userLibraryPath) { }

    /// <summary>
    /// Creates a new MediaPlayer with default configuration and host-provided native library paths.
    /// </summary>
    /// <param name="nativeLibraryPaths">One or more host-owned folders containing native libraries.</param>
    public MediaPlayer(IEnumerable<string> nativeLibraryPaths) : this(new MediaPlayerConfiguration(), nativeLibraryPaths) { }

    /// <summary>
    /// Creates a new MediaPlayer with custom configuration and library path.
    /// This is the most flexible constructor allowing full control over all settings.
    /// </summary>
    /// <param name="configuration">Configuration determining GPU/CPU usage and quality settings</param>
    /// <param name="userLibraryPath">Path to native libraries (null for default location)</param>
    public MediaPlayer(MediaPlayerConfiguration configuration, string? userLibraryPath)
        : this(configuration, ToNativeLibraryPathArrayOrDefault(userLibraryPath))
    {
    }

    /// <summary>
    /// Creates a new MediaPlayer with custom configuration and host-provided native library paths.
    /// This is the most flexible constructor allowing full control over all settings.
    /// </summary>
    /// <param name="configuration">Configuration determining GPU/CPU usage and quality settings</param>
    /// <param name="nativeLibraryPaths">One or more host-owned folders containing native libraries.</param>
    public MediaPlayer(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths)
    {
        Configuration = NormalizeConfiguration(configuration);

        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        
        // Create service provider with configuration and library path
        _serviceProvider = MediaPlayerServiceProvider.Create(Configuration, resolvedNativeLibraryPaths);
    
        // Resolve actual methods using automatic capability detection.
        ResolveActualMethods();

        // Get media engine from DI - this contains all engines
        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        var archiveService = _serviceProvider.GetRequiredService<IArchiveService>();
        AcceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();

        // Initialize components in dependency order
        Renderer = new MediaRenderer(_mediaEngine, Configuration);
        Metadata = new Controllers.MediaMetadata(_mediaEngine);
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        
        // Initialize Controls and wire up renderer coordination
        Controls = new MediaControls(_mediaEngine, Renderer);
        File = new MediaFile(_mediaEngine, Renderer, Metadata, Controls, Effects, CdgVisuals, archiveService);
        
        // Wire up events from Controls
        WireUpControlsEvents();

        if (Configuration.EnableLogging) System.Diagnostics.Debug.WriteLine($"[MediaPlayer] Created with config: Video={ActualVideoDecoding}, Render={ActualRendering}");
    }

    private static IReadOnlyList<string> ResolveNativeLibraryPaths(IEnumerable<string> nativeLibraryPaths)
    {
        var resolvedPaths = nativeLibraryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var path in resolvedPaths)
        {
            if (!Directory.Exists(path))
                throw new DirectoryNotFoundException($"Native library path does not exist: {path}");
        }

        return resolvedPaths;
    }

    private static IReadOnlyList<string> ToNativeLibraryPathArrayOrDefault(string? nativeLibraryPath)
    {
        return string.IsNullOrWhiteSpace(nativeLibraryPath)
            ? DLLPath.GetConfiguredNativeLibraryPaths()
            : [nativeLibraryPath];
    }

    private static MediaPlayerConfiguration NormalizeConfiguration(MediaPlayerConfiguration configuration)
    {
        // Auto remains the default through MediaPlayerConfiguration.Auto, but explicit
        // Hardware/Software choices must survive so the host can test and force a decode path.
        return configuration;
    }

    #endregion

    #region Static Factory Methods (Optional - for convenience)

    /// <summary>
    /// Creates a MediaPlayer with automatic best-quality configuration.
    /// Convenience method - same as new MediaPlayer().
    /// </summary>
    /// <param name="userLibraryPath">Optional custom path to native libraries</param>
    /// <returns>A MediaPlayer instance configured for best quality</returns>
    public static MediaPlayer Create(string? userLibraryPath = null) =>
        new(userLibraryPath);

    /// <summary>
    /// Creates a MediaPlayer with custom configuration.
    /// Convenience method - same as new MediaPlayer(configuration).
    /// </summary>
    /// <param name="configuration">Custom configuration</param>
    /// <param name="userLibraryPath">Optional custom path to native libraries</param>
    /// <returns>A MediaPlayer instance with specified configuration</returns>
    public static MediaPlayer Create(MediaPlayerConfiguration configuration, string? userLibraryPath = null) =>
        new(configuration, userLibraryPath);

    /// <summary>
    /// Creates a MediaPlayer with host-provided native library paths.
    /// </summary>
    /// <param name="nativeLibraryPaths">One or more host-owned folders containing native libraries.</param>
    /// <returns>A MediaPlayer instance configured for best quality</returns>
    public static MediaPlayer Create(IEnumerable<string> nativeLibraryPaths) =>
        new(nativeLibraryPaths);

    #endregion

    /// <summary>
    /// Resolves the actual methods that will be used based on system capabilities.
    /// Always aims for best quality with automatic fallbacks.
    /// </summary>
    private void ResolveActualMethods()
    {
        var capabilities = Core.Graphics.GpuRenderingCapabilities.Capabilities;

        ActualVideoDecoding = capabilities.SupportsD3D11
            ? VideoDecodingMethod.Hardware
            : VideoDecodingMethod.Software;

        ActualRendering = capabilities.SupportsD3D11
            ? RenderingMethod.DirectX11
            : RenderingMethod.Software;
    }

    /// <summary>
    /// Wires up events from MediaControls to MediaPlayer events.
    /// </summary>
    private void WireUpControlsEvents()
    {
        Controls.MediaEnded += (_, __) => MediaEnded?.Invoke(this, EventArgs.Empty);
        Controls.MediaStopped += (_, __) => MediaStopped?.Invoke(this, EventArgs.Empty);
    }

    #region Public Properties

    /// <summary>
    /// Gets or sets whether to automatically start playback when a file is loaded.
    /// When enabled, waits for quick normalization before starting to prevent audio jumps.
    /// </summary>
    public bool PlayOnLoad
    {
        get => File.PlayOnLoad;
        set => File.PlayOnLoad = value;
    }

    /// <summary>
    /// Gets or sets whether media should loop continuously.
    /// When enabled, media will restart from the beginning when it reaches the end.
    /// </summary>
    public bool LoopMedia
    {
        get => Controls.LoopMedia;
        set => Controls.LoopMedia = value;
    }

    /// <summary>
    /// Gets or sets the optional background image shown behind the media render surface.
    /// </summary>
    public ImageSource? BackgroundSource
    {
        get => Renderer.BackgroundSource;
        set => Renderer.BackgroundSource = value;
    }

    /// <summary>
    /// Gets or sets the background color shown behind media and optional background images.
    /// Defaults to transparent.
    /// </summary>
    public Color BackgroundColor
    {
        get => Renderer.BackgroundColor;
        set => Renderer.BackgroundColor = value;
    }

    /// <summary>
    /// Gets or sets how the optional background image fills the host background surface.
    /// Defaults to Uniform.
    /// </summary>
    public Stretch BackgroundStretch
    {
        get => Renderer.BackgroundStretch;
        set => Renderer.BackgroundStretch = value;
    }

    /// <summary>
    /// Gets or sets how primary video content is displayed inside the viewport.
    /// Defaults to <see cref="VideoDisplayMode.Zoom"/>, matching the previous aspect-preserving behavior.
    /// Background media behind CDG/future overlays always uses zoom-to-fill regardless of this setting.
    /// </summary>
    public VideoDisplayMode VideoDisplayMode
    {
        get => Renderer.VideoDisplayMode;
        set => Renderer.VideoDisplayMode = value;
    }

    /// <summary>
    /// Gets or sets whether karaoke backgrounds should be detected from sidecar or archive media.
    /// Defaults to true.
    /// </summary>
    public bool BackgroundAutoDetect
    {
        get => Renderer.BackgroundAutoDetect;
        set => Renderer.BackgroundAutoDetect = value;
    }

    /// <summary>
    /// Gets or sets whether the last background video frame remains visible after rendering stops.
    /// Defaults to true. Decoding is stopped either way.
    /// </summary>
    public bool BackgroundVisibleWhenStopped
    {
        get => Renderer.BackgroundVisibleWhenStopped;
        set => Renderer.BackgroundVisibleWhenStopped = value;
    }

    /// <summary>
    /// Gets or sets whether audio normalization is enabled.
    /// When enabled, performs quick analysis for immediate playback and deep analysis for optimal gain.
    /// </summary>
    public bool Normalize
    {
        get => Effects.Normalize;
        set => Effects.Normalize = value;
    }

    /// <summary>
    /// Gets the current normalization gain value in dB.
    /// This represents the gain being applied to normalize the audio.
    /// </summary>
    public double NormalizationGain => Effects.NormalizationGain;

    /// <summary>
    /// Gets whether any media is currently open.
    /// </summary>
    public bool IsOpen => _mediaEngine.IsOpen;

    /// <summary>
    /// Gets the current playback state.
    /// </summary>
    public MediaPlayerState State => _mediaEngine.State;

    /// <summary>
    /// Gets the type of content currently loaded (Audio, Video, CDG, etc.).
    /// </summary>
    public MediaContent Content => _mediaEngine.Content;

    /// <summary>
    /// Gets whether the media has video content.
    /// </summary>
    public bool HasVideo => Content.HasFlag(MediaContent.Video);

    /// <summary>
    /// Gets whether the media has audio content.
    /// </summary>
    public bool HasAudio => Content.HasFlag(MediaContent.Audio);

    /// <summary>
    /// Gets whether the media has CDG content.
    /// </summary>
    public bool HasCdg => Content.HasFlag(MediaContent.Cdg);

    /// <summary>
    /// Gets the video engine for direct access (null if no video or not available).
    /// </summary>
    public IVideoEngine? Video => _mediaEngine.Video;

    /// <summary>
    /// Gets the audio engine for direct access.
    /// </summary>
    public IAudioEngine Audio => _mediaEngine.Audio;

    /// <summary>
    /// Gets the CDG engine for direct access (null if no CDG or not available).
    /// </summary>
    public ICdgEngine? Cdg => _mediaEngine.Cdg;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_mediaEngine.Position);
        set => Seek(value);
    }

    /// <summary>
    /// Gets the total duration of the loaded media as a TimeSpan.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);


    /// <summary>
    /// Seeks to the specified playback position.
    /// </summary>
    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    /// <summary>
    /// Seeks to the specified playback position in seconds.
    /// </summary>
    public bool Seek(double seconds)
    {
        try
        {
            return _mediaEngine.Seek(seconds);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asynchronously seeks to the specified playback position.
    /// </summary>
    public Task<bool> SeekAsync(TimeSpan position) => SeekAsync(position.TotalSeconds);

    /// <summary>
    /// Asynchronously seeks to the specified playback position in seconds.
    /// </summary>
    public async Task<bool> SeekAsync(double seconds)
    {
        try
        {
            return await _mediaEngine.SeekAsync(seconds).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    #endregion

    #region Playback Control Methods

    /// <summary>
    /// Loads a media file from the specified path.
    /// </summary>
    /// <param name="path">Path to the media file.</param>
    /// <returns>True if the file was loaded successfully.</returns>
    public bool Load(string path) => File.Load(path);

    /// <summary>
    /// Loads a media file from the specified path asynchronously.
    /// </summary>
    /// <param name="path">Path to the media file.</param>
    /// <returns>True if the file was loaded successfully.</returns>
    public async Task<bool> LoadAsync(string path) => await File.LoadAsync(path);

    /// <summary>
    /// Starts media playback.
    /// </summary>
    /// <returns>True if playback started successfully.</returns>
    public bool Play() => Controls.Play();

    /// <summary>
    /// Pauses media playback.
    /// </summary>
    /// <returns>True if playback was paused successfully.</returns>
    public bool Pause() => Controls.Pause();

    /// <summary>
    /// Stops media playback and resets position to the beginning.
    /// </summary>
    /// <returns>True if playback was stopped successfully.</returns>
    public bool Stop() => Controls.Stop();

    /// <summary>
    /// Closes the currently loaded media file.
    /// </summary>
    public void Close() => File.Close();

    #endregion

    #region Events

    /// <summary>
    /// Raised when the normalization gain changes (after analysis).
    /// </summary>
    public event EventHandler<double>? NormalizationGainChanged
    {
        add => Effects.NormalizationGainChanged += value;
        remove => Effects.NormalizationGainChanged -= value;
    }

    /// <summary>
    /// Raised when media reaches the end.
    /// </summary>
    public event EventHandler? MediaEnded;

    /// <summary>
    /// Raised when media playback stops.
    /// </summary>
    public event EventHandler? MediaStopped;

    #endregion

    #region IDisposable Implementation

    /// <summary>
    /// Disposes of all resources used by the MediaPlayer.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                // Stop playback first
                Stop();
                
                // Dispose components in reverse dependency order
                File?.Close(); // Close any open files
                Renderer?.Dispose(); // Stop natural window refresh first
                Controls?.Dispose(); // Stop loop monitoring
                
                // Dispose core engine (this disposes all internal engines)
                _mediaEngine?.Dispose();
                
                // Dispose service provider if it's disposable
                (_serviceProvider as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaPlayer] Error during disposal: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }

    #endregion
}
