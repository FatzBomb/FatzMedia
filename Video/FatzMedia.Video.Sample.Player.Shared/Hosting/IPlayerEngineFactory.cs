using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>
/// Creates the engine for one player. The host owns the renderer, the platform package and the
/// native library paths; the view model owns the configuration and everything after creation.
/// </summary>
public interface IPlayerEngineFactory
{
    /// <summary>Gets the native library directories passed to the engine, highest priority first.</summary>
    IReadOnlyList<string> NativeLibraryPaths { get; }

    /// <summary>
    /// Gets the native library report of the last <see cref="CreatePlayerAsync"/> that failed, when
    /// the platform package could check the libraries before the failure; otherwise null.
    /// </summary>
    NativeDependencyReport? NativeDependenciesAfterFailure { get; }

    /// <summary>
    /// Chooses the renderer. It may probe the GPU, so it should not block the UI thread; the window
    /// shows while it runs.
    /// </summary>
    Task<PlayerRendererInfo> ResolveRendererAsync();

    /// <summary>
    /// Creates the engine for <paramref name="renderer"/> off the UI thread (native loading blocks).
    /// Throws when the engine cannot start (for example missing native libraries).
    /// </summary>
    Task<MediaControlPlayer> CreatePlayerAsync(PlayerRendererInfo renderer, MediaPlayerConfiguration configuration);
}

/// <summary>What the host's renderer choice means for the view model.</summary>
/// <param name="Summary">One line for the status line, for example "Direct3D11 GPU (forced)".</param>
/// <param name="VideoDecoding">How the engine decodes video with this renderer.</param>
/// <param name="RequiresFrameSinkSurface">Whether the renderer presents through frame sinks only (no native windows).</param>
/// <param name="LogLines">What was probed and chosen, for the Log panel (category <c>Renderer</c>).</param>
public sealed record PlayerRendererInfo(
    string Summary,
    VideoDecodingMethod VideoDecoding,
    bool RequiresFrameSinkSurface,
    IReadOnlyList<(MediaDiagnosticLevel Level, string Message)> LogLines);

/// <summary>Everything a host supplies to a <see cref="ViewModels.PlayerViewModel"/>.</summary>
/// <param name="Dispatcher">The UI thread and its timers.</param>
/// <param name="Engine">Creates the engine (renderer, platform package, native paths).</param>
/// <param name="Clipboard">The clipboard, or null when the host has none.</param>
/// <param name="FilePicker">The file picker, or null when the host has none.</param>
/// <param name="LogFilePath">The debug log file every diagnostic is also written to (see <see cref="PlayerDebugLog"/>), or null.</param>
public sealed record PlayerHost(
    IPlayerDispatcher Dispatcher,
    IPlayerEngineFactory Engine,
    IPlayerClipboard? Clipboard = null,
    IPlayerFilePicker? FilePicker = null,
    string? LogFilePath = null);
