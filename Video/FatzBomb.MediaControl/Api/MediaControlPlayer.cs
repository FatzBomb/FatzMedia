using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

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
    // The host's surfaces. Guarded by _surfaceGate: host calls arrive on the UI thread while a
    // rebuild (Reset, SetModeAsync) holds and reattaches them on the control queue.
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _presentationSurfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _surfaceGate = new();
    private bool _surfacesHeld;
    // Held only while a call checks the lifecycle and takes its place in the control queue, never
    // for the operation itself (ENG-024).
    private readonly Lock _operationAdmissionGate = new();
    // Serializes Reset and Dispose with each other; held for their whole run.
    private readonly Lock _lifecycleGate = new();
    // Cancelled by Reset and Dispose so loads in progress or queued stop early; Reset replaces it.
    private volatile CancellationTokenSource _loadLifetime = new();
    private readonly Lock _seekCompletionGate = new();
    private readonly Lock _queuedSeekGate = new();
    private QueuedSeek? _queuedSeek;
    private readonly ScrubAudioMuter _scrubAudio;
    private VideoFrameTiming _videoFrameTiming = VideoFrameTiming.SourceCadence;
    private IAudioDeviceMonitor? _audioDeviceMonitor;
    private IVideoEngine? _seekVideoEngine;
    private ICdgEngine? _seekCdgEngine;
    private PendingSeek? _pendingSeek;
    private int _hasPendingSeek;
    private int _resetting;
    // A rebuild freed the graph and failed to build the next one: nothing in the fields is live.
    private bool _sessionGraphFreed;
    private volatile bool _disposed;

    public MediaControlPlayer(
        IMediaControlPlatformFactory platformFactory,
        MediaPlayerConfiguration configuration,
        IEnumerable<string> nativeLibraryPaths)
    {
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);

        // Diagnostics from this player's engine (its threads and tasks inherit the owner) reach
        // only this player's MediaDiagnostic event, not every player's in the process.
        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        try
        {
            CreateSessionGraph(configuration, resolvedNativeLibraryPaths);
        }
        catch
        {
            // Nothing else owns the half-built graph: release the native resources and the worker,
            // and the static diagnostic subscription that would keep this instance alive.
            DisposeCore();
            try { _controlOperations?.Dispose(); } catch { }
            throw;
        }

        // Reads Effects on every call: Reset replaces it.
        _scrubAudio = new ScrubAudioMuter((muted, ramp) => Effects.SetScrubMuted(muted, ramp));
        StartAudioDeviceMonitor();
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

    public MediaScrubAudio ScrubAudio
    {
        get => _scrubAudio.Mode;
        set => _scrubAudio.Mode = value;
    }

    internal ScrubAudioMuter ScrubAudioMuter => _scrubAudio;

    public VideoFrameTiming VideoFrameTiming
    {
        get => _videoFrameTiming;
        set
        {
            _videoFrameTiming = value;
            ApplyVideoFrameTiming();
        }
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

    public bool Load(string path) => Load(path, ignoreSidecar: false);

    public bool Load(string path, bool ignoreSidecar = false) =>
        LoadOnQueue(() => MediaInputSource.FromFile(path), static () => null, ignoreSidecar);

    public bool Load(MediaInputSource source, bool ignoreSidecar = false) =>
        LoadOnQueue(() => source, static () => null, ignoreSidecar);

    public bool Load(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        LoadOnQueue(() => MediaInputSource.FromFile(path), () => FileBackground(backgroundPath), ignoreSidecar);

    public bool Load(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        LoadOnQueue(() => MediaInputSource.FromFile(path), () => MediaFile.SelectExplicitBackground(backgroundPaths), ignoreSidecar);

    public bool Load(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        LoadOnQueue(() => source, () => backgroundSource, ignoreSidecar);

    public Task<bool> LoadAsync(string path) => LoadAsync(path, ignoreSidecar: false);

    public Task<bool> LoadAsync(string path, bool ignoreSidecar = false) =>
        EnqueueLoad(() => MediaInputSource.FromFile(path), static () => null, ignoreSidecar, CancellationToken.None);

    public Task<bool> LoadAsync(MediaInputSource source, bool ignoreSidecar = false) =>
        EnqueueLoad(() => source, static () => null, ignoreSidecar, CancellationToken.None);

    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar = false) =>
        EnqueueLoad(() => MediaInputSource.FromFile(path), () => FileBackground(backgroundPath), ignoreSidecar, CancellationToken.None);

    public Task<bool> LoadAsync(string path, IReadOnlyList<string> backgroundPaths, bool ignoreSidecar = false) =>
        EnqueueLoad(() => MediaInputSource.FromFile(path), () => MediaFile.SelectExplicitBackground(backgroundPaths), ignoreSidecar, CancellationToken.None);

    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar = false) =>
        EnqueueLoad(() => source, () => backgroundSource, ignoreSidecar, CancellationToken.None);

    /// <summary>
    /// Loads <paramref name="path"/> on the control queue; <paramref name="cancellationToken"/> cancels
    /// the load (ENG-024). See <see cref="LoadAsync(MediaInputSource, MediaInputSource?, bool, CancellationToken)"/>.
    /// </summary>
    public Task<bool> LoadAsync(string path, CancellationToken cancellationToken) =>
        LoadAsync(path, ignoreSidecar: false, cancellationToken);

    /// <inheritdoc cref="LoadAsync(string, CancellationToken)"/>
    public Task<bool> LoadAsync(string path, bool ignoreSidecar, CancellationToken cancellationToken) =>
        EnqueueLoad(() => MediaInputSource.FromFile(path), static () => null, ignoreSidecar, cancellationToken);

    /// <inheritdoc cref="LoadAsync(string, CancellationToken)"/>
    public Task<bool> LoadAsync(string path, string? backgroundPath, bool ignoreSidecar, CancellationToken cancellationToken) =>
        EnqueueLoad(() => MediaInputSource.FromFile(path), () => FileBackground(backgroundPath), ignoreSidecar, cancellationToken);

    /// <inheritdoc cref="LoadAsync(MediaInputSource, MediaInputSource?, bool, CancellationToken)"/>
    public Task<bool> LoadAsync(MediaInputSource source, CancellationToken cancellationToken) =>
        EnqueueLoad(() => source, static () => null, ignoreSidecar: false, cancellationToken);

    /// <summary>
    /// Loads media on the control queue with a cancellation token (ENG-024). Cancelled while it
    /// waits in the queue, it is skipped when its turn comes and nothing changes. Cancelled during
    /// the open, the open stops where it can (network retry delays end at once; a connect attempt
    /// in progress finishes first, at most <see cref="NetworkRetryPolicy.ConnectTimeout"/>), the
    /// previous media is already closed, and the player ends closed. Either way the task is
    /// cancelled. Cancelled after the media loaded, the load stands (the task returns
    /// <see langword="true"/>) and only the <see cref="PlayOnLoad"/> start is skipped.
    /// <see cref="Reset"/> and <see cref="Dispose()"/> cancel loads in progress or queued; such a load
    /// returns <see langword="false"/>, as from the overloads without a token.
    /// </summary>
    public Task<bool> LoadAsync(MediaInputSource source, MediaInputSource? backgroundSource, bool ignoreSidecar, CancellationToken cancellationToken) =>
        EnqueueLoad(() => source, () => backgroundSource, ignoreSidecar, cancellationToken);

    private static MediaInputSource? FileBackground(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : MediaInputSource.FromFile(path);

    /// <summary>A synchronous load: on the caller's thread in its queue turn, cancelled only by Reset or Dispose.</summary>
    private bool LoadOnQueue(Func<MediaInputSource> source, Func<MediaInputSource?> backgroundSource, bool ignoreSidecar) =>
        ExecuteControlOperation(() =>
        {
            CancellationToken lifetime = _loadLifetime.Token;
            try
            {
                return File.Load(source(), backgroundSource(), ignoreSidecar, lifetime);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return false;
            }
        });

    private Task<bool> EnqueueLoad(
        Func<MediaInputSource> source,
        Func<MediaInputSource?> backgroundSource,
        bool ignoreSidecar,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<bool>(cancellationToken);

        return EnqueueControlOperation(async () =>
        {
            // Read when the load runs: a Reset replaces the lifetime (and File) after cancelling it.
            CancellationToken lifetime = _loadLifetime.Token;
            using CancellationTokenSource? linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken)
                : null;
            try
            {
                return await File.LoadAsync(source(), backgroundSource(), ignoreSidecar, linked?.Token ?? lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lifetime.IsCancellationRequested)
            {
                // Cancelled by Reset or Dispose, not by the caller.
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        });
    }

    public bool Play() => ExecuteControlOperation(() => Controls.Play());

    public Task<bool> PlayAsync() => EnqueueControlOperation(() => Controls.Play());

    public bool Pause() => ExecuteControlOperation(() => Controls.Pause());

    public Task<bool> PauseAsync() => EnqueueControlOperation(() => Controls.Pause());

    public bool Stop() => ExecuteControlOperation(() => Controls.Stop());

    public Task<bool> StopAsync() => EnqueueControlOperation(() => Controls.Stop());

    public bool BeginScrub() => ExecuteControlOperation(() => Controls.BeginScrub());

    public bool EndScrub() => ExecuteControlOperation(() =>
    {
        var ended = Controls.EndScrub();
        _scrubAudio.OnScrubEnded();
        return ended;
    });

    public bool Seek(TimeSpan position) => Seek(position.TotalSeconds);

    public bool Seek(double seconds)
    {
        try
        {
            return ExecuteControlOperation(() => SeekOperation(seconds, refreshPresentation: true));
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

        // Queued outside _queuedSeekGate: the admission gate is never taken under it.
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
            // A drag seek leaves the frame to the seek pump, which presents the scrub preview
            // itself; re-rendering here held the video lock through a vsync present and stalled
            // that preview (owner capture cdg-2: 4.3 s of seek-pump waits per minute of dragging).
            var result = SeekOperation(seconds, refreshPresentation: !Controls.IsScrubbing);
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

    private bool SeekOperation(double seconds, bool refreshPresentation)
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

        // Silent scrubbing mutes before the seek moves the audio, so nothing of the new position
        // is heard while the drag keeps moving. A slow seek (a network stream) holds the unmute
        // until it returns, so the drag requests queued behind it are not heard either.
        var trackedScrubSeek = Controls.IsScrubbing && _scrubAudio.OnScrubSeek();
        bool success;
        try
        {
            success = Controls.Seek(seconds);
        }
        finally
        {
            if (trackedScrubSeek)
                _scrubAudio.OnScrubSeekFinished();
        }

        if (!success)
        {
            ClearPendingSeek();
            return false;
        }

        if (refreshPresentation)
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

    public void Close()
    {
        CancelPendingNetworkOpen();
        _ = ExecuteControlOperation(() =>
        {
            File.Close();
            return true;
        });
    }

    /// <summary>
    /// Runs a synchronous API call in its queue turn on the caller's thread. Only taking the turn is
    /// under the admission gate; the wait for it and the operation are not (ENG-024), so a long
    /// synchronous call never blocks asynchronous callers, which only queue.
    /// </summary>
    private T ExecuteControlOperation<T>(Func<T> operation)
    {
        MediaControlOperationQueue.ExclusiveTurn turn;
        lock (_operationAdmissionGate)
        {
            ThrowIfResetting();
            turn = _controlOperations.BeginExclusiveTurn();
        }

        return turn.Run(operation);
    }

    // The admission gate is held only to check the lifecycle and write to the queue, never for
    // the operation: an async call never waits for a long one.
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

        // Surface calls arrive on the host's UI thread and can create the device or start the
        // render loop: attribute what they raise (and start) to this player.
        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        lock (_surfaceGate)
        {
            if (_surfacesHeld)
            {
                // The graph is being rebuilt: kept, and attached to the new graph when it is ready.
                _presentationSurfaces[surface.Id] = surface;
                return true;
            }

            var attached = _presentationCoordinator.AttachPresentationSurface(surface);
            if (attached)
                _presentationSurfaces[surface.Id] = surface;

            return attached;
        }
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
            return false;

        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        lock (_surfaceGate)
        {
            if (!_presentationSurfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
                return false;

            if (surface.PixelWidth == pixelWidth && surface.PixelHeight == pixelHeight)
                return true;

            surface = surface.WithSize(pixelWidth, pixelHeight);
            if (_surfacesHeld)
            {
                // A held surface keeps its newest size for the reattach.
                _presentationSurfaces[surface.Id] = surface;
                return true;
            }

            var resized = _presentationCoordinator.ResizePresentationSurface(surface.Id, pixelWidth, pixelHeight);
            if (resized)
                _presentationSurfaces[surface.Id] = surface;

            return resized;
        }
    }

    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        lock (_surfaceGate)
        {
            return _presentationSurfaces.ContainsKey(surfaceId)
                && (_surfacesHeld || _presentationCoordinator.SetPresentationSurfaceLiveResize(surfaceId, isResizing));
        }
    }

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
            return false;

        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        lock (_surfaceGate)
        {
            if (_surfacesHeld)
                return _presentationSurfaces.Remove(surfaceId);

            var detached = _presentationCoordinator.DetachPresentationSurface(surfaceId);
            if (detached)
                _ = _presentationSurfaces.Remove(surfaceId);

            return detached;
        }
    }

    /// <summary>
    /// Before the graph is freed: the surfaces stay registered with the player but no longer reach
    /// a coordinator, so host calls during the rebuild only update them.
    /// </summary>
    private void HoldPresentationSurfaces()
    {
        lock (_surfaceGate)
            _surfacesHeld = true;
    }

    /// <summary>
    /// Attaches the held surfaces, at their latest size, to the new graph's coordinator. A surface
    /// the new coordinator refuses is dropped, as a refused attach is.
    /// </summary>
    private void ReattachHeldPresentationSurfaces()
    {
        lock (_surfaceGate)
        {
            // A Dispose that gave up waiting for this rebuild has released the surfaces already.
            if (_disposed)
                return;

            KeyValuePair<string, MediaPresentationSurfaceDescriptor>[] held = [.. _presentationSurfaces];
            _presentationSurfaces.Clear();
            _surfacesHeld = false;
            foreach (KeyValuePair<string, MediaPresentationSurfaceDescriptor> kvp in held)
            {
                try
                {
                    if (_presentationCoordinator.AttachPresentationSurface(kvp.Value))
                        _presentationSurfaces[kvp.Key] = kvp.Value;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MediaControlPlayer] Reattaching surface {kvp.Key} failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Detaches every surface from the coordinator and forgets it, as the host's own detach calls
    /// would (they are not serialized with control operations either).
    /// </summary>
    private void DetachPresentationSurfacesForDispose()
    {
        lock (_surfaceGate)
        {
            if (!_surfacesHeld)
            {
                foreach (var surfaceId in _presentationSurfaces.Keys.ToArray())
                {
                    try { _ = _presentationCoordinator.DetachPresentationSurface(surfaceId); }
                    catch (Exception ex) { Debug.WriteLine($"[MediaControlPlayer] Detaching surface {surfaceId} failed: {ex.Message}"); }
                }
            }

            _presentationSurfaces.Clear();
            _surfacesHeld = true;
        }
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

    /// <summary>
    /// How long <see cref="Reset"/> waits for the operation in progress (after cancelling loads)
    /// before it gives up and returns <see langword="false"/>.
    /// </summary>
    internal TimeSpan ResetWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long <see cref="Dispose()"/> waits for the operation in progress (after cancelling loads)
    /// before it returns and leaves the teardown to the control queue.
    /// </summary>
    internal TimeSpan DisposeWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Frees the session graph and builds a new one from <paramref name="configuration"/>; the
    /// surfaces are kept and reattached, nothing is loaded, and the runtime settings (volume, pitch,
    /// tempo, normalization, equalizer, loop, base visual transform, display options) start from their
    /// defaults again. Each of those that had changed raises its change event once, on the control
    /// queue after Reset returns (ENG-024).
    /// </summary>
    /// <remarks>
    /// Threading (ENG-024): blocks the caller. Loads in progress or queued are cancelled (they return
    /// <see langword="false"/>); Reset then waits for the operations queued before it, at most 5 s.
    /// If they have not finished by then (a network connect attempt in progress), Reset gives up,
    /// raises a Warning diagnostic and returns <see langword="false"/>: nothing was rebuilt and the
    /// player is usable. Calls made while it runs throw <see cref="InvalidOperationException"/>.
    /// It throws <see cref="InvalidOperationException"/> from inside a control operation or an event
    /// handler raised in one (it would wait for itself); schedule it after the handler returns.
    /// When the new graph cannot be built it throws, and the player stays rebuildable: control calls
    /// throw <see cref="ObjectDisposedException"/> until a later Reset succeeds.
    /// </remarks>
    public bool Reset(MediaPlayerConfiguration configuration, IEnumerable<string> nativeLibraryPaths)
    {
        var resolvedNativeLibraryPaths = ResolveNativeLibraryPaths(nativeLibraryPaths);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_controlOperations.IsExecutingOperation)
            throw new InvalidOperationException("Reset cannot run from inside a media control operation or event handler.");

        ResetChanges? changes;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MediaControlOperationQueue oldOperations;
            MediaControlOperationQueue.ExclusiveTurn turn;
            lock (_operationAdmissionGate)
            {
                oldOperations = _controlOperations;
                if (oldOperations.IsExecutingOperation)
                    throw new InvalidOperationException("Reset cannot run from inside a media control operation or event handler.");

                turn = oldOperations.BeginExclusiveTurn();
                Volatile.Write(ref _resetting, 1);
            }

            var turnTaken = false;
            try
            {
                _loadLifetime.Cancel();
                if (!turn.TryWait(ResetWaitTimeout))
                {
                    // Nothing was torn down. Loads queued from now on are not cancelled.
                    _loadLifetime = new CancellationTokenSource();
                    using DiagnosticRouter.OwnerScope owner = DiagnosticRouter.EnterOwner(this);
                    DiagnosticRouter.Raise(
                        MediaDiagnosticLevel.Warning,
                        "MediaControlPlayer",
                        $"Reset gave up after {ResetWaitTimeout.TotalSeconds:0.#} s waiting for the operation in progress; the player was not reset.");
                    return false;
                }

                turnTaken = true;
                changes = turn.Run(() => ResetCore(configuration, resolvedNativeLibraryPaths));
            }
            finally
            {
                if (turnTaken)
                    oldOperations.Dispose();
                Volatile.Write(ref _resetting, 0);
            }
        }

        RaiseResetChanges(changes);
        return true;
    }

    private ResetChanges? ResetCore(MediaPlayerConfiguration configuration, IReadOnlyList<string> resolvedNativeLibraryPaths)
    {
        // Discard the prepared-next item (its analysis ran on the old engine).
        try { ClearPreparedNextForLifecycle(); } catch { }
        // After a failed Reset there is no live graph to compare with.
        ResetChanges? before = _sessionGraphFreed ? null : TryCaptureResetChanges();
        // Loads queued behind Reset run on the new graph and must not inherit the cancellation.
        _loadLifetime = new CancellationTokenSource();
        RebuildSessionGraph(configuration, resolvedNativeLibraryPaths, keepOperationQueue: false, restoreSettings: null);
        return before;
    }

    /// <summary>
    /// Closes the media, frees the session graph and builds a new one from
    /// <paramref name="configuration"/> (Reset, and SetModeAsync with another mode). The host's
    /// surfaces are held meanwhile and reattached to the new graph.
    /// </summary>
    /// <param name="keepOperationQueue">
    /// Keeps the control queue: a mode switch runs on it, and the calls queued behind the switch
    /// must run after it. Reset replaces it.
    /// </param>
    /// <param name="restoreSettings">
    /// Applied to the new graph before its events are forwarded to the host.
    /// </param>
    private void RebuildSessionGraph(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> resolvedNativeLibraryPaths,
        bool keepOperationQueue,
        Action? restoreSettings)
    {
        // ENG-025: the new graph's threads and tasks (and the control queue a Reset creates)
        // inherit this player as their diagnostic owner.
        using DiagnosticRouter.OwnerScope diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        TearDownSessionGraph();

        MediaControlOperationQueue oldOperations = _controlOperations;
        (object Services, IMediaEngine Engine, IMediaPresentationCoordinator Coordinator, MediaControls Controls) freed =
            (_serviceProvider, _mediaEngine, _presentationCoordinator, Controls);
        try
        {
            CreateSessionGraph(configuration, resolvedNativeLibraryPaths, keepOperationQueue ? oldOperations : null, restoreSettings);
        }
        catch
        {
            // The old graph is gone. Keep the player rebuildable: the surfaces stay held (a retried
            // Reset reattaches them) and Reset gets a fresh operation queue (the old one is disposed
            // when Reset returns). Until then control calls fail with ObjectDisposedException.
            if (!keepOperationQueue && ReferenceEquals(_controlOperations, oldOperations))
                _controlOperations = new MediaControlOperationQueue("MediaControl");

            // Free what the failed build created; the rest was freed above, so neither a retried
            // rebuild nor Dispose frees it again (ENG-024).
            if (!ReferenceEquals(Controls, freed.Controls))
                try { Controls.Dispose(); } catch { }
            if (!ReferenceEquals(_presentationCoordinator, freed.Coordinator))
                try { _presentationCoordinator.Dispose(); } catch { }
            if (!ReferenceEquals(_mediaEngine, freed.Engine))
                try { _mediaEngine.Dispose(); } catch { }
            if (!ReferenceEquals(_serviceProvider, freed.Services))
                try { (_serviceProvider as IDisposable)?.Dispose(); } catch { }
            throw;
        }

        _sessionGraphFreed = false;

        ApplyVideoFrameTiming();
        ReattachHeldPresentationSurfaces();
        StartAudioDeviceMonitor();
    }

    /// <summary>Stops and closes the media, unsubscribes, and disposes the session graph.</summary>
    private void TearDownSessionGraph()
    {
        HoldPresentationSurfaces();
        if (_sessionGraphFreed)
            return;

        _sessionGraphFreed = true;
        try { _fadeRunner.Cancel(); } catch { }
        try { _scrubAudio.OnScrubEnded(); } catch { }
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
        try { BassOutputWatchdog.OutputAvailabilityChanged -= OnAudioOutputAvailabilityChanged; } catch { }
        try
        {
            if (_mediaEngine is IMediaErrorSource errorSource)
                errorSource.ErrorOccurred -= OnEngineErrorOccurred;
        }
        catch { }

        StopAudioDeviceMonitor();

        try { Controls.Dispose(); } catch { }
        try { _presentationCoordinator.Dispose(); } catch { }
        try { _mediaEngine.Dispose(); } catch { }
        try { (_serviceProvider as IDisposable)?.Dispose(); } catch { }
    }

    private void StartAudioDeviceMonitor()
    {
        try
        {
            _audioDeviceMonitor = ResolveAudioDeviceMonitor();
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

    private void StopAudioDeviceMonitor()
    {
        if (_audioDeviceMonitor == null)
            return;

        try { _audioDeviceMonitor.DeviceChanged -= OnAudioDeviceMonitorChanged; } catch { }
        try { _audioDeviceMonitor.Stop(); } catch { }
        try { _audioDeviceMonitor.Dispose(); } catch { }
        _audioDeviceMonitor = null;
    }

    /// <summary>Creates the engine, presentation and control objects and subscribes to them.</summary>
    [MemberNotNull(nameof(_serviceProvider), nameof(_mediaEngine), nameof(_acceptedTypes), nameof(Effects), nameof(CdgVisuals),
        nameof(_presentationCoordinator), nameof(Controls), nameof(File), nameof(_controlOperations), nameof(_positionNotifier))]
    /// <param name="operations">The control queue to keep (a mode switch), or <see langword="null"/> for a new one.</param>
    /// <param name="restoreSettings">Settings carried from the previous graph, applied before the host events are forwarded.</param>
    private void CreateSessionGraph(
        MediaPlayerConfiguration configuration,
        IReadOnlyList<string> resolvedNativeLibraryPaths,
        MediaControlOperationQueue? operations = null,
        Action? restoreSettings = null)
    {
        _configuration = configuration;
        _nativeLibraryPaths = resolvedNativeLibraryPaths;
        _serviceProvider = _platformFactory.CreateServiceProvider(configuration, resolvedNativeLibraryPaths);

        _mediaEngine = _serviceProvider.GetRequiredService<IMediaEngine>();
        IArchiveService archiveService = _serviceProvider.GetRequiredService<IArchiveService>();

        _acceptedTypes = AcceptedMediaTypes.FromCurrentRuntime();
        Effects = new MediaEffects(_mediaEngine);
        CdgVisuals = new CdgVisuals(_mediaEngine);
        _presentationCoordinator = CreatePresentationCoordinator(configuration);
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
        _controlOperations = operations ?? new MediaControlOperationQueue("MediaControl");
        _positionNotifier = CreatePositionNotifier();

        // The carried settings, then the mode and the host's options, before the events below are
        // forwarded: building a graph raises no host events.
        restoreSettings?.Invoke();
        ApplyModeToSessionGraph();

        // Subscribe internal events
        Controls.MediaEnded += OnControlsMediaEnded;
        Controls.MediaStarted += OnControlsMediaStarted;
        Controls.MediaStopped += OnControlsMediaStopped;
        SubscribeNewSessionEvents();
        SubscribeNetworkEvents();
        _presentationCoordinator.FramePresented += OnPresentationFramePresented;
        if (_mediaEngine is IMediaErrorSource reinitErrorSource)
            reinitErrorSource.ErrorOccurred += OnEngineErrorOccurred;
        DiagnosticRouter.DiagnosticOccurred += OnDiagnosticOccurred;
        // Background mode has no audio output, so the output watchdog's reports are not its own.
        if (MediaPlayerModePolicy.UsesAudioOutput(configuration.Mode))
            BassOutputWatchdog.OutputAvailabilityChanged += OnAudioOutputAvailabilityChanged;
    }

    // The video pipeline lives as long as the engine, so the setting holds across media; Reset
    // creates a new engine and applies it again.
    private void ApplyVideoFrameTiming()
    {
        if (_mediaEngine is IVideoFrameTimingControl control)
            control.FrameTiming = _videoFrameTiming;
    }

    /// <summary>
    /// Stops playback and frees the player.
    /// </summary>
    /// <remarks>
    /// Threading (ENG-024): loads in progress or queued are cancelled (they return
    /// <see langword="false"/>), then Dispose waits for the operations queued before it, at most
    /// 2 s, and tears down on the caller's thread. If they have not finished by then (a network
    /// connect attempt in progress), Dispose detaches the surfaces and returns: the player rejects
    /// every call from then on, and the teardown runs on the control queue as soon as that operation
    /// ends, so the engine may be freed shortly after Dispose returned. It throws
    /// <see cref="InvalidOperationException"/> from inside a control operation or an event handler
    /// raised in one (it would wait for itself); a UI-thread callback or task started there runs
    /// outside it once the handler returned, and may dispose. Calling it again does nothing.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        if (_controlOperations.IsExecutingOperation)
            throw new InvalidOperationException("Dispose cannot run from inside a media control operation or event handler.");

        // A network open or read blocked inside FFmpeg is aborted first, so teardown does not wait
        // for its timeout (039A).
        CancelPendingNetworkOpen();

        lock (_lifecycleGate)
        {
            if (_disposed)
                return;

            MediaControlOperationQueue operations;
            MediaControlOperationQueue.ExclusiveTurn turn;
            lock (_operationAdmissionGate)
            {
                operations = _controlOperations;
                if (operations.IsExecutingOperation)
                    throw new InvalidOperationException("Dispose cannot run from inside a media control operation or event handler.");

                turn = operations.BeginExclusiveTurn();
                Volatile.Write(ref _resetting, 1);
            }

            try { _loadLifetime.Cancel(); } catch { }
            if (!turn.TryWait(DisposeWaitTimeout))
            {
                DisposeLater(operations);
                return;
            }

            var teardownRan = false;
            try
            {
                _ = turn.Run(() =>
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

    /// <summary>
    /// Dispose gave up waiting (ENG-024): reject calls now, free the host's surfaces now (the host may
    /// destroy their windows), and tear down on the queue once the operation in progress ends.
    /// </summary>
    private void DisposeLater(MediaControlOperationQueue operations)
    {
        _disposed = true;
        GC.SuppressFinalize(this);
        using (DiagnosticRouter.EnterOwner(this))
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                "MediaControlPlayer",
                $"Dispose did not get its turn within {DisposeWaitTimeout.TotalSeconds:0.#} s; the player is closed and finishes freeing itself when the operation in progress ends.");
        }

        DetachPresentationSurfacesForDispose();
        _ = operations.Enqueue(() =>
        {
            try
            {
                DisposeCore();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaControlPlayer] Deferred dispose failed: {ex.Message}");
            }

            // From inside its own operation, the queue finishes after this returns.
            operations.Dispose();
        });
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
        try { _scrubAudio.Dispose(); } catch { }
        if (_sessionGraphFreed)
            return;

        _sessionGraphFreed = true;
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
        try { BassOutputWatchdog.OutputAvailabilityChanged -= OnAudioOutputAvailabilityChanged; } catch { }
        try
        {
            if (_mediaEngine is IMediaErrorSource errorSource)
                errorSource.ErrorOccurred -= OnEngineErrorOccurred;
        }
        catch { }

        StopAudioDeviceMonitor();

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
