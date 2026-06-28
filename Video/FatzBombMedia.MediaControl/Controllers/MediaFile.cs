using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Handles file loading and management operations.
/// </summary>
public sealed class MediaFile
{
    private readonly IMediaEngine _mediaEngine;
    private readonly MediaRenderer? _mediaRenderer;
    private readonly MediaMetadata? _mediaMetadata;
    private readonly MediaControls? _mediaControls;
    private readonly MediaEffects? _mediaEffects;
    private readonly CdgVisuals? _cdgVisuals;
    private readonly IArchiveService? _archiveService;

    internal MediaFile(IMediaEngine mediaEngine, MediaRenderer? mediaRenderer = null, MediaMetadata? mediaMetadata = null, MediaControls? mediaControls = null, MediaEffects? mediaEffects = null, CdgVisuals? cdgVisuals = null, IArchiveService? archiveService = null)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        _mediaRenderer = mediaRenderer;
        _mediaMetadata = mediaMetadata;
        _mediaControls = mediaControls;
        _mediaEffects = mediaEffects;
        _cdgVisuals = cdgVisuals;
        _archiveService = archiveService;
    }

    /// <summary>
    /// Gets or sets whether to automatically start playback after loading.
    /// </summary>
    public bool PlayOnLoad { get; set; } = false;

    /// <summary>
    /// Loads a media file from the specified path.
    /// </summary>
    /// <param name="path">Path to the media file.</param>
    /// <returns>True if the file was loaded successfully.</returns>
    public async Task<bool> LoadAsync(string path)
    {
        var success = _mediaEngine.Open(path);
        if (success)
        {
            ApplyAutoBackground(path);

            // Refresh renderer attachments when new media is loaded
            _mediaRenderer?.RefreshAttachments();

            // Refresh metadata when new media is loaded
            _mediaMetadata?.Refresh();

            // Re-apply effect settings if this media selected a different audio engine.
            _mediaEffects?.RefreshActiveEngine();
            _cdgVisuals?.Apply();

            // Auto-play with proper normalization timing if enabled
            if (PlayOnLoad)
            {
                await HandlePlayOnLoadAsync();
            }
        }
        return success;
    }

    /// <summary>
    /// Loads a media file from the specified path (synchronous version).
    /// </summary>
    /// <param name="path">Path to the media file.</param>
    /// <returns>True if the file was loaded successfully.</returns>
    public bool Load(string path)
    {
        System.Diagnostics.Debug.WriteLine($"[MediaFile] Loading: {path}");

        // Force stop any currently playing media to ensure clean state
        if (_mediaEngine.IsOpen)
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] Stopping current media before loading new media");
            _ = (_mediaControls?.Stop());
        }

        var success = _mediaEngine.Open(path);
        if (success)
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] Media opened successfully, refreshing attachments");

            ApplyAutoBackground(path);

            // Refresh renderer attachments when new media is loaded
            _mediaRenderer?.RefreshAttachments();

            // Refresh metadata when new media is loaded
            _mediaMetadata?.Refresh();

            // Re-apply effect settings if this media selected a different audio engine.
            _mediaEffects?.RefreshActiveEngine();
            _cdgVisuals?.Apply();

            // Auto-play with proper normalization timing if enabled
            if (PlayOnLoad)
            {
                // Fire and forget for auto-play with normalization wait
                _ = Task.Run(HandlePlayOnLoadAsync);
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[MediaFile] Failed to load: {path}");
        }
        return success;
    }

    /// <summary>
    /// Handles PlayOnLoad with proper normalization timing.
    /// Waits for quick normalization before starting playback to avoid audio jumps.
    /// </summary>
    private async Task HandlePlayOnLoadAsync()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Waiting for quick normalization...");

            // Wait for quick normalization to complete (max 2 seconds)
            await WaitForQuickNormalizationAsync(TimeSpan.FromSeconds(2));

            System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Quick normalization complete, starting playback");

            // Start through MediaControls so renderer coordination and loop monitoring are active.
            var playSuccess = _mediaControls?.Play() ?? _mediaEngine.Play();
            if (playSuccess)
            {
                System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Playback started successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MediaFile] PlayOnLoad: Failed to start playback");
            }
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
            {
                await Task.Delay(50); // Check every 50ms
            }

            if (!gainChanged)
            {
                System.Diagnostics.Debug.WriteLine("[MediaFile] Quick normalization timeout - proceeding with playback");
            }
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
        _mediaRenderer?.StopRendering();
        _mediaEngine.Close();
        _mediaRenderer?.ClearAutoBackground();
        _mediaRenderer?.RefreshAttachments();
        _mediaMetadata?.Refresh();
    }

    /// <summary>
    /// Gets the type of content currently loaded (Audio, Video, CDG, etc.).
    /// </summary>
    public MediaContent ContentType => _mediaEngine.Content;

    /// <summary>
    /// Gets whether any media is currently loaded.
    /// </summary>
    public bool IsLoaded => _mediaEngine.IsOpen;

    private void ApplyAutoBackground(string path)
    {
        if (_mediaRenderer == null)
        {
            return;
        }

        if (!_mediaRenderer.BackgroundAutoDetect || _archiveService == null)
        {
            _mediaRenderer.ClearAutoBackground();
            return;
        }

        MediaBackgroundCandidate? background = MediaBackgroundResolver.Resolve(path, _archiveService, _mediaEngine.Content);
        _mediaRenderer.ApplyAutoBackground(background);
    }
}
