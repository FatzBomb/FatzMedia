using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Core-owned media-control session. Framework adapters supply surfaces; this object owns playback,
/// layer composition, native target fan-out, and presentation coordination.
/// </summary>
public partial class MediaControlPlayer : IMediaControlSession
{
    private static readonly TimeSpan SyncAdjustmentStep = TimeSpan.FromMilliseconds(100);
    private const double SeekSettleToleranceSeconds = 0.25;
    private const double SeekImmediateCompletionToleranceSeconds = 0.001;
    // A scrub settles on the frame its last preview showed, at most VideoPipeline's preview frame
    // distance (2 s) from the target, plus the normal settle tolerance.
    private const double ScrubSettleToleranceSeconds = 2.0 + SeekSettleToleranceSeconds;
    private readonly IMediaControlPlatformFactory _platformFactory;
    private IServiceProvider _serviceProvider;
    private IMediaEngine _mediaEngine;
    private IMediaPresentationCoordinator _presentationCoordinator;
    private MediaControlOperationQueue _controlOperations;
    private PlaybackPositionNotifier _positionNotifier;
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _presentationSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _operationAdmissionGate = new();
    private readonly Lock _seekCompletionGate = new();
    private readonly Lock _queuedSeekGate = new();
    private QueuedSeek? _queuedSeek;
    private IAudioDeviceMonitor? _audioDeviceMonitor;
    private IVideoEngine? _seekVideoEngine;
    private ICdgEngine? _seekCdgEngine;
    private PendingSeek? _pendingSeek;
    private int _hasPendingSeek;
    private int _resetting;
    private bool _disposed;

    public MediaControlPlayer(
        IMediaControlPlatformFactory platformFactory,
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths)
    {
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        _serviceProvider = _platformFactory.CreateServiceProvider(configuration, resolvedNativeLibraryPaths);

        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

        _acceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        _presentationCoordinator = _platformFactory.CreatePresentationCoordinator(_mediaEngine, configuration);
        Controls = new MediaControls(_mediaEngine, _presentationCoordinator, DispatchControlOperation);
        File = new MediaFile(
            _mediaEngine,
            _presentationCoordinator,
            Controls,
            Effects,
            CdgVisuals,
            archiveService,
            configuration.ResetPitchOnMediaLoad,
            configuration.ResetTempoOnMediaLoad);
        _controlOperations = new MediaControlOperationQueue("MediaControl");
        _positionNotifier = CreatePositionNotifier();

        Controls.MediaEnded += OnControlsMediaEnded;
        Controls.MediaStarted += OnControlsMediaStarted;
        Controls.MediaStopped += OnControlsMediaStopped;
        SubscribeNewSessionEvents();
        SubscribeNetworkEvents();
        _presentationCoordinator.FramePresented += OnPresentationFramePresented;

        if (_mediaEngine is IMediaErrorSource errorSource)
            errorSource.ErrorOccurred += OnEngineErrorOccurred;

        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

        try
        {
            _audioDeviceMonitor = _serviceProvider.GetService<IAudioDeviceMonitor>();
            if (_audioDeviceMonitor != null)
            {
                _audioDeviceMonitor.DeviceChanged += OnAudioDeviceMonitorChanged;
                _audioDeviceMonitor.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Audio device monitor failed to start: {ex.Message}");
        }
    }

    public MediaControls Controls { get; private set; }

    private MediaFile File { get; set; }

    public MediaEffects Effects { get; private set; }

    public CdgVisuals CdgVisuals { get; private set; }

    private AcceptedMediaTypes _acceptedTypes;

    public string OpenFileDialogFilter => _acceptedTypes.OpenFileDialogFilter;

    public string LastNativeLoadMessage => _platformFactory.LastNativeLoadMessage;

    /// <summary>
    /// Gets which native libraries loaded at startup, with the exact file names and folders, so a
    /// host can report a missing or wrong-version file (see <see cref="NativeDependencyReport.Describe"/>).
    /// </summary>
    public NativeDependencyReport NativeDependencies => _platformFactory.NativeDependencies;

    public VideoDecodeCapabilities VideoDecodeCapabilities => _platformFactory.GetVideoDecodeCapabilities();

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

    public bool Normalize
    {
        get => Effects.Normalize;
        set => Effects.Normalize = value;
    }

    public AudioEqualizerSettings Equalizer
    {
        get => Effects.Equalizer;
        set => Effects.Equalizer = value;
    }

    public bool IsOpen => _mediaEngine.IsOpen;

    public MediaPlayerState State => _mediaEngine.State;

    public MediaContent Content => _mediaEngine.Content;

    public bool HasVideo => Content.HasFlag(MediaContent.Video);

    public bool HasAudio => Content.HasFlag(MediaContent.Audio);

    public bool HasCdg => Content.HasFlag(MediaContent.Cdg);

    public bool HasSubtitle => Content.HasFlag(MediaContent.Subtitle);

    public bool HasVisualContent =>
        (Content & (MediaContent.Video | MediaContent.Cdg | MediaContent.Image | MediaContent.Subtitle)) != 0
        || _mediaEngine.Background != null;

    public IReadOnlyList<SubtitleTrack> SubtitleTracks => _mediaEngine.SubtitleTracks;

    public string? SelectedSubtitleTrackId => _mediaEngine.SelectedSubtitleTrackId;

    public bool ShowMidiLyrics
    {
        get => _mediaEngine.ShowMidiLyrics;
        set
        {
            if (_mediaEngine.ShowMidiLyrics == value)
                return;

            _mediaEngine.ShowMidiLyrics = value;
            _presentationCoordinator.RefreshCurrentPosition();
        }
    }

    public TimeSpan AudioSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.AudioVideoOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetAudioVideoOffset(value.TotalSeconds));
    }

    public TimeSpan CdgSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.CdgOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetCdgOffset(value.TotalSeconds));
    }

    public TimeSpan SubtitleSyncOffset
    {
        get => TimeSpan.FromSeconds(_mediaEngine.SubtitleOffsetSeconds);
        set => ApplySyncOffset(_mediaEngine.SetSubtitleOffset(value.TotalSeconds));
    }

    public IVideoEngine? Video => _mediaEngine.Video;

    public IAudioEngine Audio => _mediaEngine.Audio;

    public ICdgEngine? Cdg => _mediaEngine.Cdg;

    public ISubtitleEngine? Subtitle => _mediaEngine.Subtitle;

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(_mediaEngine.Position);
        set => Seek(value);
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(_mediaEngine.Duration);

    public bool Load(string path) => ExecuteControlOperation(() => File.Load(path));

    public bool Load(string path, bool ignoreSidecar = false) => ExecuteControlOperation(() => File.Load(path, ignoreSidecar));

    public bool Load(MediaInputSource source, bool ignoreSidecar = false) => ExecuteControlOperation(() => File.Load(source, ignoreSidecar));

    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) => ExecuteControlOperation(() => File.Load(path, backgroundPath, ignoreSidecar));

    public bool Load(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) => ExecuteControlOperation(() => File.Load(path, backgroundPaths, ignoreSidecar));

    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        ExecuteControlOperation(() => File.Load(source, backgroundSource, ignoreSidecar));

    public Task<bool> LoadAsync(string path) => EnqueueControlOperation(() => File.LoadAsync(path));

    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) =>
        EnqueueControlOperation(() => File.LoadAsync(path, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) =>
        EnqueueControlOperation(() => File.LoadAsync(source, ignoreSidecar));

    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        EnqueueControlOperation(() => File.LoadAsync(path, backgroundPath, ignoreSidecar));

    public Task<bool> LoadAsync(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        EnqueueControlOperation(() => File.LoadAsync(path, backgroundPaths, ignoreSidecar));

    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        EnqueueControlOperation(() => File.LoadAsync(source, backgroundSource, ignoreSidecar));

    public bool Play() => ExecuteControlOperation(() => Controls.Play());

    public Task<bool> PlayAsync() => EnqueueControlOperation(() => Controls.Play());

    public bool Pause() => ExecuteControlOperation(() => Controls.Pause());

    public Task<bool> PauseAsync() => EnqueueControlOperation(() => Controls.Pause());

    public bool Stop() => ExecuteControlOperation(() => Controls.Stop());

    public Task<bool> StopAsync() => EnqueueControlOperation(() => Controls.Stop());

    public bool BeginScrub() => ExecuteControlOperation(() => Controls.BeginScrub());

    public bool EndScrub() => ExecuteControlOperation(() => Controls.EndScrub());

    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    public bool Seek(double seconds)
    {
        try
        {
            return ExecuteControlOperation(() => SeekOperation(seconds));
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Seek failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Seeks on the Core control queue, in order with every other control operation, so the
    /// caller's thread does none of the seek work (Stage 022A slice 4: seek-bar drags kept the
    /// host's UI thread busy for every mouse move). The newest request wins: a request made while
    /// another is still queued (not yet running) replaces its target, and both callers get the
    /// result of that one seek, so a host can call this for every mouse move.
    /// </summary>
    public Task<bool> SeekAsync(double seconds)
    {
        QueuedSeek queued;
        lock (_queuedSeekGate)
        {
            if (_queuedSeek is { } waiting)
            {
                waiting.Seconds = seconds;
                return waiting.Completion.Task;
            }

            queued = new QueuedSeek(seconds);
            _queuedSeek = queued;
        }

        // Queued outside _queuedSeekGate: a synchronous Seek holds the admission gate while it
        // waits for its turn, so taking that gate under ours could deadlock with the worker.
        Task<bool> enqueued;
        try
        {
            enqueued = EnqueueControlOperation(() => RunQueuedSeek(queued));
        }
        catch (Exception ex)
        {
            FailQueuedSeek(queued, ex);
            throw;
        }

        // RunQueuedSeek never throws, so a faulted task means the queue refused the operation
        // (disposed): fail it, or later requests would join a seek that never runs.
        _ = enqueued.ContinueWith(
            task => FailQueuedSeek(queued, task.Exception!.InnerException ?? task.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return queued.Completion.Task;
    }

    private void FailQueuedSeek(QueuedSeek queued, Exception exception)
    {
        lock (_queuedSeekGate)
        {
            if (ReferenceEquals(_queuedSeek, queued))
                _queuedSeek = null;
        }

        _ = queued.Completion.TrySetException(exception);
    }

    private bool RunQueuedSeek(QueuedSeek queued)
    {
        double seconds;
        lock (_queuedSeekGate)
        {
            seconds = queued.Seconds;
            if (ReferenceEquals(_queuedSeek, queued))
                _queuedSeek = null;
        }

        try
        {
            var result = SeekOperation(seconds);
            _ = queued.Completion.TrySetResult(result);
            return result;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            _ = queued.Completion.TrySetException(ex);
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Seek failed: {ex.Message}");
            _ = queued.Completion.TrySetResult(false);
            return false;
        }
    }

    private sealed class QueuedSeek(double seconds)
    {
        public double Seconds { get; set; } = seconds;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool SeekOperation(double seconds)
    {
        var requested = CleanPosition(seconds);
        var target = _mediaEngine.Duration > 0
            ? Math.Clamp(requested, 0, _mediaEngine.Duration)
            : requested;
        if (Controls.IsRepeatOfScrubTarget(seconds))
        {
            // The release seek of a drag at the last drag position: already there or
            // playing on from it, so nothing moves.
            RaiseSeekCompleted(requested, _mediaEngine.Position);
            return true;
        }

        var waitsForSettle = _mediaEngine.Video?.IsOpen == true || _mediaEngine.Cdg != null;
        if (waitsForSettle)
        {
            lock (_seekCompletionGate)
            {
                _pendingSeek = new PendingSeek(requested, target, Controls.IsScrubbing);
                Volatile.Write(ref _hasPendingSeek, 1);
            }
        }

        var success = Controls.Seek(seconds);
        if (!success)
        {
            ClearPendingSeek();
            return false;
        }

        _presentationCoordinator.RefreshCurrentPosition();
        if (!waitsForSettle)
        {
            RaiseSeekCompleted(requested, _mediaEngine.Position);
            return true;
        }

        TryCompletePendingSeek(_mediaEngine.Position, SeekImmediateCompletionToleranceSeconds, isSettlement: false);
        return true;
    }

    public bool SelectSubtitleTrack(string? trackId)
    {
        var selected = _mediaEngine.SelectSubtitleTrack(trackId);
        if (selected)
            _presentationCoordinator.RefreshCurrentPosition();

        return selected;
    }

    public TimeSpan AdjustAudioSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustAudioVideoOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public TimeSpan AdjustCdgSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustCdgOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public TimeSpan AdjustSubtitleSyncOffset(int steps) => TimeSpan.FromSeconds(ApplySyncOffset(_mediaEngine.AdjustSubtitleOffset(steps * SyncAdjustmentStep.TotalSeconds)));

    public void ResetSyncOffsets()
    {
        _mediaEngine.ResetSyncOffsets();
        _presentationCoordinator.RefreshCurrentPosition();
    }

    public void Close() => ExecuteControlOperation(() =>
    {
        File.Close();
        return true;
    });

    private T ExecuteControlOperation<T>(Func<T> operation)
    {
        ThrowIfResetting();
        lock (_operationAdmissionGate)
        {
            ThrowIfResetting();
            return _controlOperations.Execute(operation);
        }
    }

    private Task<T> EnqueueControlOperation<T>(Func<T> operation)
    {
        ThrowIfResetting();
        lock (_operationAdmissionGate)
        {
            ThrowIfResetting();
            return _controlOperations.Enqueue(operation);
        }
    }

    private Task<T> EnqueueControlOperation<T>(Func<Task<T>> operation)
    {
        ThrowIfResetting();
        lock (_operationAdmissionGate)
        {
            ThrowIfResetting();
            return _controlOperations.Enqueue(operation);
        }
    }

    private void DispatchControlOperation(Action operation) =>
        _ = _controlOperations.Enqueue(operation);

    private void ThrowIfResetting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _resetting) != 0)
            throw new InvalidOperationException("Media control operations are unavailable while the player is resetting.");
    }

    private double ApplySyncOffset(double seconds)
    {
        _presentationCoordinator.RefreshCurrentPosition();
        return seconds;
    }

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

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) =>
        !_disposed && !string.IsNullOrWhiteSpace(surfaceId) && _presentationSurfaces.ContainsKey(surfaceId) && _presentationCoordinator.SetPresentationSurfaceLiveResize(surfaceId, isResizing);

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        var detached = _presentationCoordinator.DetachPresentationSurface(surfaceId);
        if (detached)
            _ = _presentationSurfaces.Remove(surfaceId);

        return detached;
    }

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

    public bool Reset(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths)
    {
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        lock (_operationAdmissionGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MediaControlOperationQueue oldOperations = _controlOperations;
            if (oldOperations.IsExecutingOperation)
                throw new InvalidOperationException("Reset cannot run from inside a media control operation or event handler.");

            Volatile.Write(ref _resetting, 1);
            try
            {
                return oldOperations.Execute(() => ResetCore(configuration, resolvedNativeLibraryPaths));
            }
            finally
            {
                oldOperations.Dispose();
                Volatile.Write(ref _resetting, 0);
            }
        }
    }

    private bool ResetCore(MediaPlayerConfiguration configuration, IReadOnlyList<string> resolvedNativeLibraryPaths)
    {
        // 1. Discard the prepared-next item (its analysis ran on the old engine), stop playback,
        //    and close media
        try { ClearPreparedNextForLifecycle(); } catch { }
        try { _fadeRunner.Cancel(); } catch { }
        try { File.Close(); } catch { }

        // 2. Unsubscribe internal events
        try { Controls.MediaEnded -= OnControlsMediaEnded; } catch { }
        try { Controls.MediaStarted -= OnControlsMediaStarted; } catch { }
        try { Controls.MediaStopped -= OnControlsMediaStopped; } catch { }
        try { UnsubscribeNewSessionEvents(); } catch { }
        try { UnsubscribeNetworkEvents(); } catch { }
        try { UnsubscribeSeekCompletionSources(); } catch { }
        try { _positionNotifier.PositionChanged -= OnPositionNotifierChanged; } catch { }
        try { _positionNotifier.Dispose(); } catch { }
        ClearPendingSeek();
        try { _presentationCoordinator.FramePresented -= OnPresentationFramePresented; } catch { }
        try { DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred; } catch { }
        try
        {
            if (_mediaEngine is IMediaErrorSource errorSource)
                errorSource.ErrorOccurred -= OnEngineErrorOccurred;
        }
        catch { }

        // 3. Disconnect audio device monitor
        if (_audioDeviceMonitor != null)
        {
            try { _audioDeviceMonitor.DeviceChanged -= OnAudioDeviceMonitorChanged; } catch { }
            try { _audioDeviceMonitor.Stop(); } catch { }
            try { _audioDeviceMonitor.Dispose(); } catch { }
            _audioDeviceMonitor = null;
        }

        // 4. Preserve presentation surfaces for reattachment
        var surfaces = new Dictionary<string, MediaPresentationSurfaceDescriptor>(_presentationSurfaces, StringComparer.OrdinalIgnoreCase);
        _presentationSurfaces.Clear();

        // 5. Dispose old dependencies
        try { Controls.Dispose(); } catch { }
        try { _presentationCoordinator.Dispose(); } catch { }
        try { _mediaEngine.Dispose(); } catch { }
        try { (_serviceProvider as IDisposable)?.Dispose(); } catch { }

        // 6. Reinitialize with new configuration
        _serviceProvider = _platformFactory.CreateServiceProvider(configuration, resolvedNativeLibraryPaths);

        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

        _acceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        _presentationCoordinator = _platformFactory.CreatePresentationCoordinator(_mediaEngine, configuration);
        Controls = new MediaControls(_mediaEngine, _presentationCoordinator, DispatchControlOperation);
        File = new MediaFile(
            _mediaEngine,
            _presentationCoordinator,
            Controls,
            Effects,
            CdgVisuals,
            archiveService,
            configuration.ResetPitchOnMediaLoad,
            configuration.ResetTempoOnMediaLoad);
        _controlOperations = new MediaControlOperationQueue("MediaControl");
        _positionNotifier = CreatePositionNotifier();

        // 7. Re-subscribe internal events
        Controls.MediaEnded += OnControlsMediaEnded;
        Controls.MediaStarted += OnControlsMediaStarted;
        Controls.MediaStopped += OnControlsMediaStopped;
        SubscribeNewSessionEvents();
        SubscribeNetworkEvents();
        _presentationCoordinator.FramePresented += OnPresentationFramePresented;
        if (_mediaEngine is IMediaErrorSource reinitErrorSource)
            reinitErrorSource.ErrorOccurred += OnEngineErrorOccurred;
        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;

        // 8. Reattach preserved presentation surfaces
        foreach (KeyValuePair<string, MediaPresentationSurfaceDescriptor> kvp in surfaces)
            try { _ = AttachPresentationSurface(kvp.Value); } catch { }

        // 9. Reconnect audio device monitor
        try
        {
            _audioDeviceMonitor = _serviceProvider.GetService<IAudioDeviceMonitor>();
            if (_audioDeviceMonitor != null)
            {
                _audioDeviceMonitor.DeviceChanged += OnAudioDeviceMonitorChanged;
                _audioDeviceMonitor.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaControlPlayer] Audio device monitor failed to start during reset: {ex.Message}");
        }

        return true;
    }

    public void Dispose()
    {
        lock (_operationAdmissionGate)
        {
            if (_disposed)
                return;

            MediaControlOperationQueue operations = _controlOperations;
            if (operations.IsExecutingOperation)
                throw new InvalidOperationException("Dispose cannot run from inside a media control operation or event handler.");

            Volatile.Write(ref _resetting, 1);
            var teardownRan = false;
            try
            {
                _ = operations.Execute(() =>
                {
                    teardownRan = true;
                    DisposeCore();
                    return true;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControlPlayer] Dispose failed: {ex.Message}");
                if (!teardownRan)
                    DisposeCore();
            }
            finally
            {
                operations.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    private void DisposeCore()
    {
        try
        {
            if (_preparedNext.Snapshot.Source?.FilePath is { } preparedPath)
                FfmpegAudioFallback.Release(preparedPath);
        }
        catch { }
        try { _preparedNext.Dispose(); } catch { }
        try { _fadeRunner.Cancel(); } catch { }
        try { _ = Controls.Stop(); } catch { }
        try { File.Close(); } catch { }
        try { Controls.MediaEnded -= OnControlsMediaEnded; } catch { }
        try { Controls.MediaStarted -= OnControlsMediaStarted; } catch { }
        try { Controls.MediaStopped -= OnControlsMediaStopped; } catch { }
        try { UnsubscribeNewSessionEvents(); } catch { }
        try { UnsubscribeNetworkEvents(); } catch { }
        try { UnsubscribeSeekCompletionSources(); } catch { }
        try { _positionNotifier.PositionChanged -= OnPositionNotifierChanged; } catch { }
        try { _positionNotifier.Dispose(); } catch { }
        ClearPendingSeek();
        try { _presentationCoordinator.FramePresented -= OnPresentationFramePresented; } catch { }
        try { DiagnosticRouter.DiagnosticOccurred -= OnDiagnosticOccurred; } catch { }
        try
        {
            if (_mediaEngine is IMediaErrorSource errorSource)
                errorSource.ErrorOccurred -= OnEngineErrorOccurred;
        }
        catch { }

        if (_audioDeviceMonitor != null)
        {
            try { _audioDeviceMonitor.DeviceChanged -= OnAudioDeviceMonitorChanged; } catch { }
            try { _audioDeviceMonitor.Stop(); } catch { }
            try { _audioDeviceMonitor.Dispose(); } catch { }
            _audioDeviceMonitor = null;
        }

        try { Controls.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[MediaControlPlayer] Controls dispose failed: {ex.Message}"); }
        try { _presentationCoordinator.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[MediaControlPlayer] Presentation coordinator dispose failed: {ex.Message}"); }
        try { _mediaEngine.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[MediaControlPlayer] Media engine dispose failed: {ex.Message}"); }
        try { (_serviceProvider as IDisposable)?.Dispose(); } catch (Exception ex) { Debug.WriteLine($"[MediaControlPlayer] Service provider dispose failed: {ex.Message}"); }
    }

    /// <param name="DuringScrub">
    /// The seek was requested during a scrub, so a video settle may land on the previewed frame
    /// rather than the target (035A).
    /// </param>
    private readonly record struct PendingSeek(double RequestedSeconds, double TargetSeconds, bool DuringScrub);
}
