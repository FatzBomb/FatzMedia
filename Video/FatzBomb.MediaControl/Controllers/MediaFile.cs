using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Threading;

namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Handles file loading and management operations.
/// </summary>
internal sealed class MediaFile
{
    private readonly IMediaEngine _mediaEngine;
    private readonly IMediaRenderCoordinator? _mediaRenderer;
    private readonly MediaControls? _mediaControls;
    private readonly MediaEffects? _mediaEffects;
    private readonly CdgVisuals? _cdgVisuals;
    private readonly IArchiveService? _archiveService;
    private MediaSourceInfo? _currentSource;
    private LoadRequest? _currentLoad;
    private int _loadGeneration;

    internal MediaFile(
        IMediaEngine mediaEngine,
        IMediaRenderCoordinator? mediaRenderer = null,
        MediaControls? mediaControls = null,
        MediaEffects? mediaEffects = null,
        CdgVisuals? cdgVisuals = null,
        IArchiveService? archiveService = null,
        bool resetPitchOnMediaLoad = false,
        bool resetTempoOnMediaLoad = false)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _mediaRenderer = mediaRenderer;
        _mediaControls = mediaControls;
        _mediaEffects = mediaEffects;
        _cdgVisuals = cdgVisuals;
        _archiveService = archiveService;
        ResetPitchOnMediaLoad = resetPitchOnMediaLoad;
        ResetTempoOnMediaLoad = resetTempoOnMediaLoad;
    }

    /// <summary>
    /// Gets or sets whether to automatically start playback after loading.
    /// </summary>
    public bool PlayOnLoad { get; set; } = false;

    /// <summary>Gets or sets whether pitch resets to zero after each successful load.</summary>
    public bool ResetPitchOnMediaLoad { get; set; }

    /// <summary>Gets or sets whether tempo resets to 100 percent after each successful load.</summary>
    public bool ResetTempoOnMediaLoad { get; set; }

    internal event EventHandler<MediaLoadedEventArgs>? MediaLoaded;

    internal event EventHandler<MediaClosedEventArgs>? MediaClosed;

    /// <summary>Gets or sets whether Core should automatically load matching sidecar/archive backgrounds for overlay media.</summary>
    public bool BackgroundAutoDetect
    {
        get => _mediaEngine.BackgroundAutoDetect;
        set => _mediaEngine.BackgroundAutoDetect = value;
    }

    /// <summary>
    /// Loads media and honors <see cref="PlayOnLoad"/>. <paramref name="cancellationToken"/> (ENG-024):
    /// cancelled before the load starts, nothing changes; cancelled during the open, the open stops
    /// where it can (network retry delays), the player ends closed and this throws
    /// <see cref="OperationCanceledException"/>; cancelled after the media loaded, the load stands
    /// and only the PlayOnLoad start is skipped.
    /// </summary>
    internal async Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loadGeneration = BeginLoadOperation();
        var success = LoadCore(source, backgroundSource, ignoreSidecar, cancellationToken);
        if (success && PlayOnLoad)
            await HandlePlayOnLoadAsync(loadGeneration, cancellationToken).ConfigureAwait(false);

        return success;
    }

    /// <summary>
    /// Loads media without honoring <see cref="PlayOnLoad"/>, for callers that must prepare the new
    /// media (for example its fade gain) before starting playback themselves.
    /// </summary>
    internal bool LoadWithoutAutoPlay(MediaInputSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = BeginLoadOperation();
        return LoadCore(source, backgroundSource: null, ignoreSidecar: false, cancellationToken);
    }

    /// <summary>
    /// Gets how the open media was loaded (source, explicit background, sidecar choice), or
    /// <see langword="null"/> when nothing is open. A mode switch (043A) reopens it in the new graph.
    /// </summary>
    internal LoadRequest? CurrentLoad => _currentLoad;

    /// <summary>
    /// Loads <paramref name="request"/> again as the same item: without honoring
    /// <see cref="PlayOnLoad"/>, <see cref="ResetPitchOnMediaLoad"/> or <see cref="ResetTempoOnMediaLoad"/>.
    /// </summary>
    internal bool Reopen(LoadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = BeginLoadOperation();
        return LoadCore(request.Source, request.BackgroundSource, request.IgnoreSidecar, cancellationToken, applyLoadReset: false);
    }

    /// <summary>
    /// The synchronous load; <paramref name="cancellationToken"/> as for
    /// <see cref="LoadAsync(MediaInputSource, MediaInputSource?, bool, CancellationToken)"/>. It never
    /// starts playback: with <see cref="PlayOnLoad"/> set, the caller runs
    /// <see cref="StartPlayOnLoadAsync"/> with <paramref name="loadGeneration"/> in order with the
    /// controls issued after the load (a Pause or Stop right after a load must win).
    /// </summary>
    internal bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar, CancellationToken cancellationToken, out int loadGeneration)
    {
        cancellationToken.ThrowIfCancellationRequested();
        loadGeneration = BeginLoadOperation();
        return LoadCore(source, backgroundSource, ignoreSidecar, cancellationToken);
    }

    /// <summary>
    /// Starts the media of a synchronous <see cref="Load(MediaInputSource, MediaInputSource?, bool, CancellationToken, out int)"/>
    /// as <see cref="PlayOnLoad"/> asks, unless another load replaced it meanwhile.
    /// </summary>
    internal Task StartPlayOnLoadAsync(int loadGeneration, CancellationToken cancellationToken) =>
        HandlePlayOnLoadAsync(loadGeneration, cancellationToken);

    /// <summary>
    /// Handles PlayOnLoad with proper normalization timing.
    /// Waits for quick normalization before starting playback to avoid audio jumps.
    /// </summary>
    private async Task HandlePlayOnLoadAsync(int loadGeneration, CancellationToken cancellationToken)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Waiting for quick normalization...");

            // Wait for quick normalization to complete (max 2 seconds)
            await WaitForQuickNormalizationAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested || !IsCurrentLoadOperation(loadGeneration))
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
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaFile", "PlayOnLoad: Failed to start playback");
        }
        catch (OperationCanceledException)
        {
            // The load was cancelled after it loaded: the media stays loaded, not started.
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "MediaFile", $"PlayOnLoad error: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>
    /// Waits for quick normalization analysis to complete.
    /// This ensures we don't get sudden audio level changes when auto-playing.
    /// </summary>
    private async Task WaitForQuickNormalizationAsync(TimeSpan timeout, CancellationToken cancellationToken)
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
                await Task.Delay(50, cancellationToken).ConfigureAwait(false); // Check every 50ms

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
        MediaSourceInfo? closedSource = _currentSource;
        _currentSource = null;
        _currentLoad = null;
        _mediaRenderer?.StopRendering();
        _mediaEngine.Close();
        _mediaEngine.ClearBackground();
        _mediaRenderer?.RefreshAttachments();
        if (closedSource != null)
        {
            HostEventDispatcher.Raise(
                this,
                MediaClosed,
                new MediaClosedEventArgs(closedSource),
                nameof(MediaClosed));
        }
    }

    internal int LoadGeneration => Volatile.Read(ref _loadGeneration);

    private int BeginLoadOperation() => Interlocked.Increment(ref _loadGeneration);

    private void InvalidatePendingPlayOnLoad() => _ = Interlocked.Increment(ref _loadGeneration);

    private bool IsCurrentLoadOperation(int loadGeneration) =>
        Volatile.Read(ref _loadGeneration) == loadGeneration && _mediaEngine.IsOpen;

    private bool LoadCore(
        MediaInputSource source,
        MediaInputSource? backgroundSource,
        bool ignoreSidecar,
        CancellationToken cancellationToken,
        bool applyLoadReset = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        System.Diagnostics.Debug.WriteLine($"[MediaFile] Loading: {source.DisplayName}");

        if (_mediaEngine.IsOpen)
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] Stopping current media before loading new media");
            if (!(_mediaControls?.Stop() ?? false))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaFile", "Stop failed or returned false");

            RaiseMediaClosed();
        }

        _mediaEngine.ClearBackground();
        bool success;
        using (MediaOpenCancellation.Enter(cancellationToken))
            success = _mediaEngine.Open(source);

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelled during the open (ENG-024): the previous media is already closed, so the
            // player ends closed rather than with media the caller no longer wants.
            if (success)
            {
                _mediaEngine.Close();
                _mediaEngine.ClearBackground();
                _mediaRenderer?.RefreshAttachments();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!success)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "MediaFile", $"Failed to load: {source.DisplayName}", sourcePath: source.FilePath);
            return false;
        }

        System.Diagnostics.Debug.WriteLine("[MediaFile] Media opened successfully, refreshing attachments");
        _mediaRenderer?.RefreshAttachments();
        ApplyCoreBackground(source, backgroundSource, ignoreSidecar);

        _mediaEffects?.RefreshActiveEngine();
        if (applyLoadReset)
            _mediaEffects?.ApplyLoadReset(ResetPitchOnMediaLoad, ResetTempoOnMediaLoad);
        _cdgVisuals?.Apply();
        _currentLoad = new LoadRequest(source, backgroundSource, ignoreSidecar);
        _currentSource = new MediaSourceInfo(
            source,
            _mediaEngine.Content,
            (_mediaEngine as IMediaSourceCapabilityProvider)?.CurrentCapabilities);
        HostEventDispatcher.Raise(
            this,
            MediaLoaded,
            new MediaLoadedEventArgs(_currentSource, TimeSpan.FromSeconds(_mediaEngine.Duration)),
            nameof(MediaLoaded));
        return true;
    }

    private void RaiseMediaClosed()
    {
        MediaSourceInfo? closedSource = _currentSource;
        if (closedSource == null)
            return;

        _currentSource = null;
        _currentLoad = null;
        HostEventDispatcher.Raise(
            this,
            MediaClosed,
            new MediaClosedEventArgs(closedSource),
            nameof(MediaClosed));
    }

    private void ApplyCoreBackground(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar)
    {
        MediaBackgroundCandidate? background = ResolveCoreBackground(source, backgroundSource, ignoreSidecar);
        if (background == null)
            return;

        var setBackgroundResult = _mediaEngine.SetBackground(background);
        if (!setBackgroundResult)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "MediaFile", "SetBackground failed");
    }

    private MediaBackgroundCandidate? ResolveCoreBackground(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar)
    {
        MediaContent content = _mediaEngine.Content;
        if (!CanUseExplicitBackground(content))
            return null;

        if (!ShouldAutoDetectBackground(content))
            return ResolveExplicitBackground(backgroundSource);

        MediaBackgroundCandidate? sidecar = null;
        IArchiveService? archiveService = _archiveService;
        if (archiveService != null && ShouldResolveAutomaticBackground(
            content,
            ignoreSidecar,
            _mediaEngine.BackgroundAutoDetect,
            source.IsFile,
            resolverAvailable: true))
        {
            sidecar = MediaBackgroundResolver.Resolve(source.FilePath!, archiveService, content);
        }

        return sidecar ?? ResolveExplicitBackground(backgroundSource);
    }

    internal static bool ShouldAutoDetectBackground(MediaContent content) =>
        !content.HasFlag(MediaContent.Video) && !content.HasFlag(MediaContent.Image);

    internal static bool CanUseExplicitBackground(MediaContent content) =>
        !content.HasFlag(MediaContent.Video);

    internal static bool ShouldResolveAutomaticBackground(
        MediaContent content,
        bool ignoreSidecar,
        bool backgroundAutoDetect,
        bool sourceIsFile,
        bool resolverAvailable) =>
        ShouldAutoDetectBackground(content)
        && !ignoreSidecar
        && backgroundAutoDetect
        && sourceIsFile
        && resolverAvailable;

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

    internal static MediaInputSource? SelectExplicitBackground(IReadOnlyList<string> backgroundPaths)
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

    /// <summary>The arguments of a successful load, kept so the media can be opened again.</summary>
    internal sealed record LoadRequest(MediaInputSource Source, MediaInputSource? BackgroundSource, bool IgnoreSidecar);
}
