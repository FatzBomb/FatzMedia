using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;

#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Framework-neutral public playback/control contract for a FatzBombMedia media-control instance.
/// </summary>
/// <remarks>
/// UI adapters such as WPF, WinForms, WinUI, Avalonia, and MAUI should expose this session instead
/// of re-implementing playback/controller logic. Adapters may add tiny convenience pass-throughs,
/// but playback, rendering, retained layers, swap chains, and target fan-out remain owned by the
/// shared media-control implementation.
/// </remarks>
public interface IMediaControlSession : IDisposable
{
    /// <summary>Gets or sets whether loading media should automatically begin playback.</summary>
    bool PlayOnLoad { get; set; }

    /// <summary>Gets or sets whether playback loops when the end of media is reached.</summary>
    bool LoopMedia { get; set; }

    /// <summary>Gets whether media is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>Gets the current playback state.</summary>
    MediaPlayerState State { get; }

    /// <summary>Gets the content roles present in the loaded media.</summary>
    MediaContent Content { get; }

    /// <summary>Gets whether the loaded media has video content.</summary>
    bool HasVideo { get; }

    /// <summary>Gets whether the loaded media has audio content.</summary>
    bool HasAudio { get; }

    /// <summary>Gets whether the loaded media has CDG content.</summary>
    bool HasCdg { get; }

    /// <summary>Gets or sets how primary video content maps into the presentation surface.</summary>
    VideoDisplayMode VideoDisplayMode { get; set; }

    /// <summary>Gets or sets the current playback position.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Gets the loaded media duration.</summary>
    TimeSpan Duration { get; }

    /// <summary>Loads media from a file path.</summary>
    bool Load(string path);

    /// <summary>Loads media from a file path, optionally ignoring matching sidecar/archive backgrounds.</summary>
    bool Load(string path, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source.</summary>
    bool Load(MediaInputSource source, bool ignoreSidecar = false);

    /// <summary>Loads media with an optional explicit background. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    bool Load(string path, string? backgroundPath, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source.</summary>
    bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false);

    /// <summary>Loads media from a file path asynchronously.</summary>
    Task<bool> LoadAsync(string path);

    /// <summary>Loads media from a file path asynchronously, optionally ignoring matching sidecar/archive backgrounds.</summary>
    Task<bool> LoadAsync(string path, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source asynchronously.</summary>
    Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false);

    /// <summary>Loads media with an optional explicit background asynchronously. Unless ignored, a discovered sidecar/archive background takes precedence.</summary>
    Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false);

    /// <summary>Loads media from a framework-neutral Core source with an optional neutral background source asynchronously.</summary>
    Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false);

    /// <summary>Starts or resumes playback.</summary>
    bool Play();

    /// <summary>Starts or resumes playback asynchronously on the Core control queue.</summary>
    Task<bool> PlayAsync();

    /// <summary>Pauses playback.</summary>
    bool Pause();

    /// <summary>Pauses playback asynchronously on the Core control queue.</summary>
    Task<bool> PauseAsync();

    /// <summary>Stops playback and resets position.</summary>
    bool Stop();

    /// <summary>Stops playback asynchronously on the Core control queue.</summary>
    Task<bool> StopAsync();

    /// <summary>Seeks to a playback position.</summary>
    bool Seek(TimeSpan position);

    /// <summary>Seeks to a playback position in seconds.</summary>
    bool Seek(double seconds);

    /// <summary>Closes the currently loaded media.</summary>
    void Close();

    /// <summary>
    /// Stops all running processes, disposes internal resources, then reinitializes the entire
    /// engine with the supplied configuration and native-library paths. Existing presentation
    /// surface registrations are preserved and reattached. No media is loaded after reset; the
    /// caller is responsible for re-loading and resuming playback on the new device.
    /// </summary>
    /// <param name="configuration">New immutable configuration to apply (device, fallback mode, etc.).</param>
    /// <param name="nativeLibraryPaths">Native-library search paths (BASS/FFmpeg).</param>
    /// <returns>True if the engine was successfully reinitialized.</returns>
    bool Reset(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths);

    /// <summary>Attaches or updates a framework-provided presentation surface.</summary>
    bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface);

    /// <summary>Resizes a previously attached framework-provided presentation surface.</summary>
    bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight);

    /// <summary>Notifies Core that a framework-provided presentation surface is in an active host resize/move loop.</summary>
    bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing);

    /// <summary>Detaches a previously attached framework-provided presentation surface.</summary>
    bool DetachPresentationSurface(string surfaceId);

    /// <summary>Raised when media reaches the end.</summary>
    event EventHandler? MediaEnded;

    /// <summary>Raised when media playback starts or resumes.</summary>
    event EventHandler? MediaStarted;

    /// <summary>Raised after Core presents a frame to an attached presentation surface.</summary>
    event EventHandler? PresentationFramePresented;

    /// <summary>Raised when media playback is stopped.</summary>
    event EventHandler? MediaStopped;

    /// <summary>
    /// Raised when a high-value failure occurs inside the media engine. Carries typed
    /// error information so hosts can diagnose failures without relying on <c>false</c>
    /// returns or Debug-only output. May fire from background threads; hosts must marshal
    /// to the UI thread before touching view state.
    /// </summary>
    event EventHandler<MediaErrorEventArgs>? MediaError;

    /// <summary>
    /// Raised when an engine component produces a host-routable diagnostic. Carries
    /// a severity level and category so hosts can filter and route diagnostics without
    /// relying on Debug-only output. Contains non-error informational messages such as
    /// decoder configuration, native library load outcomes, and state transitions in
    /// addition to warning- and error-level diagnostics. Fires in both Debug and Release
    /// builds. May fire from background threads; hosts must marshal to the UI thread
    /// before touching view state.
    /// </summary>
    event EventHandler<MediaDiagnosticEventArgs>? MediaDiagnostic;

    /// <summary>
    /// Raised when the active audio output device is lost, the system default device changes,
    /// or the audio device state otherwise changes. Core does not change playback state from
    /// this notification; BASS/Windows remain responsible for active stream migration. Hosts
    /// should surface a user-visible diagnostic when needed. May fire from background threads;
    /// hosts must marshal to the UI thread before touching view state.
    /// </summary>
    event EventHandler<AudioDeviceChangedEventArgs>? AudioDeviceChanged;
}
