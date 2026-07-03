using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Handles file loading and management operations.
/// </summary>
public sealed class MediaFile
{
    private readonly IMediaEngine _mediaEngine;
    private readonly IMediaRenderCoordinator? _mediaRenderer;
    private readonly MediaControls? _mediaControls;
    private readonly MediaEffects? _mediaEffects;
    private readonly CdgVisuals? _cdgVisuals;
    private readonly IArchiveService? _archiveService;
    private int _loadGeneration;

    internal MediaFile(IMediaEngine mediaEngine, IMediaRenderCoordinator? mediaRenderer = null, MediaControls? mediaControls = null, MediaEffects? mediaEffects = null, CdgVisuals? cdgVisuals = null, IArchiveService? archiveService = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _mediaRenderer = mediaRenderer;
        _mediaControls = mediaControls;
        _mediaEffects = mediaEffects;
        _cdgVisuals = cdgVisuals;
        _archiveService = archiveService;
    }

    /// <summary>
    /// Gets or sets whether to automatically start playback after loading.
    /// </summary>
    public bool PlayOnLoad { get; set; } = false;

    /// <summary>Gets or sets whether Core should automatically load matching sidecar/archive backgrounds for overlay media.</summary>
    public bool BackgroundAutoDetect
    {
        get => _mediaEngine.BackgroundAutoDetect;
        set => _mediaEngine.BackgroundAutoDetect = value;
    }

    /// <summary>
    /// Loads a media file from the specified path.
    /// </summary>
    public Task<bool> LoadAsync(string path) => LoadAsync(path, ignoreSidecar: false);

    /// <summary>
    /// Loads a media file from the specified path, optionally ignoring sidecar/archive backgrounds.
    /// </summary>
    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) =>
        LoadAsync(MediaInputSource.FromFile(path), backgroundSource: null, ignoreSidecar);

    /// <summary>
    /// Loads media from a neutral Core source.
    /// </summary>
    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) =>
        LoadAsync(source, backgroundSource: null, ignoreSidecar);

    /// <summary>
    /// Loads a media file with an optional explicit background. Unless ignored, a discovered sidecar/archive
    /// background takes precedence over the explicit background path.
    /// </summary>
    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        LoadAsync(
            MediaInputSource.FromFile(path),
            string.IsNullOrWhiteSpace(backgroundPath) ? null : MediaInputSource.FromFile(backgroundPath),
            ignoreSidecar);

    /// <summary>
    /// Loads a media file with one randomly selected explicit background path. Unless ignored, a
    /// discovered sidecar/archive background takes precedence over the explicit background paths.
    /// </summary>
    public Task<bool> LoadAsync(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        LoadAsync(
            MediaInputSource.FromFile(path),
            SelectExplicitBackground(backgroundPaths),
            ignoreSidecar);

    /// <summary>
    /// Loads media from a neutral Core source with an optional explicit neutral background source.
    /// </summary>
    public async Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false)
    {
        var loadGeneration = BeginLoadOperation();
        var success = LoadCore(source, backgroundSource, ignoreSidecar);
        if (success && PlayOnLoad)
            await HandlePlayOnLoadAsync(loadGeneration).ConfigureAwait(false);

        return success;
    }

    /// <summary>
    /// Loads a media file from the specified path (synchronous version).
    /// </summary>
    public bool Load(string path) => Load(path, ignoreSidecar: false);

    /// <summary>
    /// Loads a media file from the specified path, optionally ignoring sidecar/archive backgrounds.
    /// </summary>
    public bool Load(string path, bool ignoreSidecar = false) =>
        Load(MediaInputSource.FromFile(path), backgroundSource: null, ignoreSidecar);

    /// <summary>
    /// Loads media from a neutral Core source.
    /// </summary>
    public bool Load(MediaInputSource source, bool ignoreSidecar = false) =>
        Load(source, backgroundSource: null, ignoreSidecar);

    /// <summary>
    /// Loads a media file with an optional explicit background. Unless ignored, a discovered sidecar/archive
    /// background takes precedence over the explicit background path.
    /// </summary>
    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        Load(
            MediaInputSource.FromFile(path),
            string.IsNullOrWhiteSpace(backgroundPath) ? null : MediaInputSource.FromFile(backgroundPath),
            ignoreSidecar);

    /// <summary>
    /// Loads a media file with one randomly selected explicit background path. Unless ignored, a
    /// discovered sidecar/archive background takes precedence over the explicit background paths.
    /// </summary>
    public bool Load(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        Load(
            MediaInputSource.FromFile(path),
            SelectExplicitBackground(backgroundPaths),
            ignoreSidecar);

    /// <summary>
    /// Loads media from a neutral Core source with an optional explicit neutral background source.
    /// </summary>
    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false)
    {
        var loadGeneration = BeginLoadOperation();
        var success = LoadCore(source, backgroundSource, ignoreSidecar);
        if (success && PlayOnLoad)
            _ = Task.Run(() => HandlePlayOnLoadAsync(loadGeneration));

        return success;
    }

    /// <summary>
    /// Handles PlayOnLoad with proper normalization timing.
    /// Waits for quick normalization before starting playback to avoid audio jumps.
    /// </summary>
    private async Task HandlePlayOnLoadAsync(int loadGeneration)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Waiting for quick normalization...");

            // Wait for quick normalization to complete (max 2 seconds)
            await WaitForQuickNormalizationAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            if (!IsCurrentLoadOperation(loadGeneration))
            {
                System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Skipped because media changed before playback could start");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Quick normalization complete, starting playback");

            // Start through MediaControls so renderer coordination and loop monitoring are active.
            var playSuccess = _mediaControls?.Play() ?? _mediaEngine.Play();
            if (playSuccess)
                System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Playback started successfully");
            else
                System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Failed to start playback");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaFile] PlayOnLoad error: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for quick normalization analysis to complete.
    /// This ensures we don't get sudden audio level changes when auto-playing.
    /// </summary>
    private async Task WaitForQuickNormalizationAsync(TimeSpan timeout)
    {
        IAudioEngine audioEngine = _mediaEngine.Audio;
        if (audioEngine == null || !audioEngine.Normalize) return;
        if (audioEngine.IsNormalizationReady) return;

        DateTime startTime = DateTime.UtcNow;
        var gainChanged = false;

        // Subscribe to gain change event
        void gainHandler(object? _, double newGain)
        {
            gainChanged = true;
            System.Diagnostics.Debug.WriteLine($"[MediaFile] Quick normalization ready: gain {newGain:F3}");
        }

        audioEngine.NormalizationGainChanged += gainHandler;

        try
        {
            // Wait for gain change or timeout
            while (!gainChanged && !audioEngine.IsNormalizationReady && DateTime.UtcNow - startTime < timeout)
                await Task.Delay(50).ConfigureAwait(false); // Check every 50ms

            if (!gainChanged)
                System.Diagnostics.Debug.WriteLine("[MediaFile] Quick normalization timeout - proceeding with playback");
        }
        finally
        {
            audioEngine.NormalizationGainChanged -= gainHandler;
        }
    }

    /// <summary>
    /// Closes the currently loaded media file.
    /// </summary>
    public void Close()
    {
        InvalidatePendingPlayOnLoad();
        _mediaRenderer?.StopRendering();
        _mediaEngine.Close();
        _mediaEngine.ClearBackground();
        _mediaRenderer?.RefreshAttachments();
    }

    private int BeginLoadOperation() => Interlocked.Increment(ref _loadGeneration);

    private void InvalidatePendingPlayOnLoad() => _ = Interlocked.Increment(ref _loadGeneration);

    private bool IsCurrentLoadOperation(int loadGeneration) =>
        Volatile.Read(ref _loadGeneration) == loadGeneration && _mediaEngine.IsOpen;

    private bool LoadCore(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar)
    {
        ArgumentNullException.ThrowIfNull(source);
        System.Diagnostics.Debug.WriteLine($"[MediaFile] Loading: {source.DisplayName}");

        if (_mediaEngine.IsOpen)
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] Stopping current media before loading new media");
            if (!(_mediaControls?.Stop() ?? false))
                System.Diagnostics.Debug.WriteLine("[MediaFile] Stop failed or returned false");
        }

        _mediaEngine.ClearBackground();
        var success = _mediaEngine.Open(source);
        if (!success)
        {
            System.Diagnostics.Debug.WriteLine($"[MediaFile] Failed to load: {source.DisplayName}");
            return false;
        }

        System.Diagnostics.Debug.WriteLine("[MediaFile] Media opened successfully, refreshing attachments");
        _mediaRenderer?.RefreshAttachments();
        ApplyCoreBackground(source, backgroundSource, ignoreSidecar);

        _mediaEffects?.RefreshActiveEngine();
        _cdgVisuals?.Apply();
        return true;
    }

    private void ApplyCoreBackground(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar)
    {
        MediaBackgroundCandidate? background = ResolveCoreBackground(source, backgroundSource, ignoreSidecar);
        if (background == null)
            return;

        var setBackgroundResult = _mediaEngine.SetBackground(background);
        if (!setBackgroundResult)
            System.Diagnostics.Debug.WriteLine("[MediaFile] SetBackground failed");
    }

    private MediaBackgroundCandidate? ResolveCoreBackground(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar)
    {
        if (_mediaEngine.Content.HasFlag(MediaContent.Video) || _mediaEngine.Content.HasFlag(MediaContent.Image))
            return null;

        MediaBackgroundCandidate? sidecar = null;
        if (!ignoreSidecar && source.IsFile && _mediaEngine.BackgroundAutoDetect && _archiveService != null)
            sidecar = MediaBackgroundResolver.Resolve(source.FilePath!, _archiveService, _mediaEngine.Content);

        return sidecar ?? ResolveExplicitBackground(backgroundSource);
    }

    private static MediaBackgroundCandidate? ResolveExplicitBackground(MediaInputSource? backgroundSource)
    {
        if (backgroundSource == null)
            return null;

        if (backgroundSource.IsFile)
        {
            var path = backgroundSource.FilePath;
            return !File.Exists(path)
                ? null
                : Core.Files.FileTypes.IsVideo(path)
                    ? MediaBackgroundCandidate.FromPath(MediaBackgroundKind.Video, path)
                    : Core.Files.FileTypes.IsImage(path)
                        ? MediaBackgroundCandidate.FromPath(MediaBackgroundKind.Image, path)
                        : null;
        }

        return backgroundSource.IsBgraImage
            ? MediaBackgroundCandidate.FromSource(MediaBackgroundKind.Image, backgroundSource)
            : null;
    }

    private static MediaInputSource? SelectExplicitBackground(IReadOnlyList<string> backgroundPaths)
    {
        ArgumentNullException.ThrowIfNull(backgroundPaths);
        List<string> candidates = [];
        foreach (var path in backgroundPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;

            if (Core.Files.FileTypes.IsVideo(path) || Core.Files.FileTypes.IsImage(path))
                candidates.Add(path);
        }

        return candidates.Count == 0
            ? null
            : MediaInputSource.FromFile(candidates[Random.Shared.Next(candidates.Count)]);
    }
}
