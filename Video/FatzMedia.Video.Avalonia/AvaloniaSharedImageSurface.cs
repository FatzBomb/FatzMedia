using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// The GPU shared-image path of <see cref="MediaSurface"/>, presented on Avalonia's render thread.
/// Receives the renderer's exported, double-buffered images as plain handles, imports them into
/// the compositor's graphics context there, and draws each finished frame from a composition custom
/// visual. No pixels pass through the CPU and no step waits for the UI thread.
/// </summary>
/// <remarks>
/// Renderer callbacks arrive on its presentation thread; they only queue work. The custom visual's
/// handler runs on the render thread: on each draw it imports a new image set if one arrived, takes
/// the queued frames in order (a snapshot with the synchronization the renderer chose, then the frame
/// is handed back to the renderer), and draws the newest one. Any import or draw failure reports the
/// surface unavailable once, and <see cref="MediaSurface"/> switches to its frame sink. A lost graphics
/// context (a GPU reset, ENG-039) is also reported to the renderer, which checks its own device.
/// </remarks>
sealed class AvaloniaSharedImageSurface : IMediaSharedGpuImageConsumer, IDisposable
{
    readonly Lock _gate = new();
    readonly Visual _owner;
    readonly IPlayerSession _session;
    readonly GpuInteropCapabilities _interop;
    readonly Action<AvaloniaSharedImageSurface, string, bool> _unavailable;
    readonly MediaSharedGpuImageConsumerHandle _handle;
    readonly CompositionCustomVisual _visual;
    readonly Queue<MediaSharedGpuImageFrame> _frames = new();
    MediaSharedGpuImageSet? _pendingSet;
    // The newest set the renderer sent (guarded by _gate): where a context loss is reported.
    MediaSharedGpuImageSet? _latestSet;
    string? _unavailableReason;
    // 1 while the render thread has stopped polling because nothing arrived for a while.
    int _asleep;
    // Avalonia does not draw a hidden visual, and frames are only snapshotted while drawing: see DiscardFrames.
    int _isVisible = 1;
    bool _disposed;

    public AvaloniaSharedImageSurface(
        Visual owner,
        IPlayerSession session,
        Compositor compositor,
        GpuInteropCapabilities interop,
        MediaSharedGpuImageRequest request,
        Action<AvaloniaSharedImageSurface, string, bool> unavailable)
    {
        _owner = owner;
        _session = session;
        _interop = interop;
        Request = request;
        _unavailable = unavailable;
        _visual = compositor.CreateCustomVisual(new PresentationHandler(this, interop.Importer));
        ElementComposition.SetElementChildVisual(owner, _visual);
        _handle = new MediaSharedGpuImageConsumerHandle(this);
        // The first message starts the polling; it goes back to sleep by itself when nothing arrives.
        Wake();
    }

    public MediaSharedGpuImageRequest Request { get; }

    /// <summary>Gets the first reason the path became unavailable, or null.</summary>
    public string? UnavailableReason
    {
        get
        {
            lock (_gate)
                return _unavailableReason;
        }
    }

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    /// <summary>Sizes the composition visual to the control (UI thread).</summary>
    public void SetSize(Size size) => _visual.Size = new Vector(size.Width, size.Height);

    /// <summary>
    /// Shows or hides the composition visual (UI thread). Hidden, the control's background shows
    /// instead of the last frame, which the presenter keeps.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _visual.Visible = visible;
        Volatile.Write(ref _isVisible, visible ? 1 : 0);
        if (visible)
            WakeIfAsleep();
    }

    public void OnImagesCreated(MediaSharedGpuImageSet images)
    {
        MediaSharedGpuImageSet? skipped;
        lock (_gate)
        {
            if (_disposed)
            {
                skipped = images;
            }
            else
            {
                skipped = _pendingSet;
                _pendingSet = images;
                _latestSet = images;
            }
        }

        skipped?.CompleteImport(handlesConsumed: false);
        WakeIfAsleep();
    }

    public void OnFrameReady(MediaSharedGpuImageFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _frames.Enqueue(frame);
        }

        WakeIfAsleep();
    }

    public void OnUnavailable(string reason) => ReportUnavailable(reason, contextLost: false);

    /// <summary>
    /// The compositor's graphics context was lost (ENG-039: a GPU reset): tells the renderer, which
    /// checks its own device, and reports the path unavailable for now, not for the session.
    /// </summary>
    internal void ReportContextLost(string reason)
    {
        MediaSharedGpuImageSet? set;
        lock (_gate)
            set = _latestSet;

        set?.ReportDeviceLost(reason);
        ReportUnavailable(reason, contextLost: true);
    }

    static bool IsContextLost(Exception? exception) => exception?.GetBaseException() is PlatformGraphicsContextLostException;

    /// <summary>Reports a failure: a lost context as <see cref="ReportContextLost"/>, anything else as unavailable for the session.</summary>
    void ReportFailure(string reason, Exception? exception)
    {
        if (IsContextLost(exception))
            ReportContextLost(reason);
        else
            ReportUnavailable(reason, contextLost: false);
    }

    /// <summary>Removes the visual and releases the imports (UI thread, after the session detached).</summary>
    public void Dispose()
    {
        MediaSharedGpuImageSet? pending;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            pending = _pendingSet;
            _pendingSet = null;
            _frames.Clear();
        }

        pending?.CompleteImport(handlesConsumed: false);
        _handle.Dispose();
        // The handler releases what it imported on the render thread, then the visual goes.
        _visual.SendHandlerMessage(PresentationHandler.ReleaseMessage);
        if (ReferenceEquals(ElementComposition.GetElementChildVisual(_owner), _visual))
            ElementComposition.SetElementChildVisual(_owner, null);
    }

    void WakeIfAsleep()
    {
        if (Volatile.Read(ref _asleep) == 1 && Interlocked.Exchange(ref _asleep, 0) == 1)
            Dispatcher.UIThread.Post(Wake, DispatcherPriority.Render);
    }

    void Wake() => _visual.SendHandlerMessage(PresentationHandler.WakeMessage);

    void ReportUnavailable(string reason, bool contextLost)
    {
        lock (_gate)
        {
            if (_unavailableReason is not null)
                return;

            _unavailableReason = reason;
        }

        Dispatcher.UIThread.Post(() => _unavailable(this, reason, contextLost));
    }

    bool TakeWork(out MediaSharedGpuImageSet? set, out MediaSharedGpuImageFrame[] frames)
    {
        lock (_gate)
        {
            set = _pendingSet;
            _pendingSet = null;
            frames = _frames.Count == 0 ? [] : [.. _frames];
            _frames.Clear();
            return !_disposed && (set is not null || frames.Length > 0);
        }
    }

    bool IsVisible => Volatile.Read(ref _isVisible) != 0;

    // Hidden surfaces are not drawn, so there is nothing to do for them (frames are discarded, see DiscardFrames).
    bool HasWork
    {
        get
        {
            if (!IsVisible)
                return false;

            lock (_gate)
                return _pendingSet is not null || _frames.Count > 0;
        }
    }

    /// <summary>
    /// Hands the queued frames back unconsumed (render thread): while the visual is hidden (auto-hide before the first
    /// frame, Audio mode) Avalonia does not draw it, so nothing would snapshot them and the renderer's ring would run dry.
    /// The image set stays queued; it is imported when the surface shows.
    /// </summary>
    void DiscardFrames()
    {
        MediaSharedGpuImageSet? set;
        MediaSharedGpuImageFrame[] frames;
        lock (_gate)
        {
            if (_frames.Count == 0)
                return;

            frames = [.. _frames];
            _frames.Clear();
            set = _latestSet;
        }

        foreach (var frame in frames)
        {
            if (set is not null && frame.Generation == set.Generation)
                set.ReleaseFrame(frame, consumed: false);
        }
    }

    /// <summary>Runs on the render thread: imports sets, snapshots frames, draws the newest.</summary>
    sealed class PresentationHandler(AvaloniaSharedImageSurface surface, IGpuImageImporter? importer)
        : CompositionCustomVisualHandler, IDisposable
    {
        public static readonly object WakeMessage = new();
        public static readonly object ReleaseMessage = new();

        // About half a second at 60 Hz without work: stop polling until the next frame or set.
        const int IdleTicksBeforeSleep = 30;

        ImportedSet? _imported;
        Bitmap? _current;
        MediaSharedGpuImageSelection? _reportedSelection;
        int _idleTicks;
        bool _failed;

        /// <summary>Releases the imports and the shown frame (render thread).</summary>
        public void Dispose() => Release();

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, ReleaseMessage))
            {
                Release();
                _failed = true;
                return;
            }

            _idleTicks = 0;
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            if (_failed)
                return;

            if (!surface.IsVisible)
                surface.DiscardFrames();

            if (surface.HasWork)
            {
                _idleTicks = 0;
                Invalidate();
            }
            else
            {
                _idleTicks++;
            }

            if (_idleTicks < IdleTicksBeforeSleep)
            {
                RegisterForNextAnimationFrameUpdate();
                return;
            }

            // Asleep: work that arrived after the last look must not wait for a wake-up it will not get.
            Volatile.Write(ref surface._asleep, 1);
            if (surface.HasWork && Interlocked.Exchange(ref surface._asleep, 0) == 1)
            {
                _idleTicks = 0;
                Invalidate();
                RegisterForNextAnimationFrameUpdate();
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (!_failed)
                Pump();

            if (_current is not null)
                drawingContext.DrawBitmap(_current, new Rect(_current.Size), new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y));
        }

        void Pump()
        {
            // Drawn although hidden (a pass that was already under way): hand the frames back instead of showing them.
            if (!surface.IsVisible)
            {
                surface.DiscardFrames();
                return;
            }

            if (!surface.TakeWork(out var set, out var frames))
                return;

            if (importer is null || surface._interop.Interop is { IsLost: true })
            {
                set?.CompleteImport(handlesConsumed: false);
                Fail(importer is null
                    ? "the compositor offers no render-thread image import"
                    : "the compositor's GPU interop was lost", contextLost: importer is not null);
                return;
            }

            if (set is not null)
                Import(set);

            foreach (var frame in frames)
                Show(frame);
        }

        void Import(MediaSharedGpuImageSet set)
        {
            Release(keepBitmap: true);
            IImportedGpuImages images;
            try
            {
                images = importer!.Import(set);
            }
            catch (Exception exception)
            {
                // Handles never given to the importer stay with the producer, which closes them.
                set.CompleteImport(exception is GpuImportException import ? import.ConsumedBuffers : 0);
                surface.ReportFailure($"importing the shared images failed: {exception.Message}", exception);
                _failed = true;
                return;
            }

            // The import ran here, on the render thread: the renderer's handles are open in the compositor's context.
            set.CompleteImport(handlesConsumed: true);
            _imported = new ImportedSet(set, images);
            if (_reportedSelection != set.Selection)
            {
                _reportedSelection = set.Selection;
                DiagnosticRouter.Raise(
                    surface._session,
                    MediaDiagnosticLevel.Info,
                    MediaSurface.DiagnosticCategory,
                    $"Shared GPU images imported (render thread): {set.Width}x{set.Height} {set.Format} x{set.Buffers.Count}, {set.Selection}");
            }
        }

        void Show(MediaSharedGpuImageFrame frame)
        {
            var imported = _imported;
            // Frames of a set that was replaced or failed to import: the renderer reset its ring for the newer set.
            if (imported is null || frame.Generation != imported.Set.Generation || (uint)frame.BufferIndex >= (uint)imported.Set.Buffers.Count)
                return;

            Bitmap snapshot;
            try
            {
                snapshot = imported.Images.Snapshot(frame);
            }
            catch (Exception exception)
            {
                imported.Set.ReleaseFrame(frame, consumed: false);
                surface.ReportFailure($"showing a shared image failed: {exception.Message}", exception);
                _failed = true;
                return;
            }

            // The snapshot holds the frame's content: the renderer may reuse the image now.
            imported.Set.ReleaseFrame(frame, consumed: true);
            _current?.Dispose();
            _current = snapshot;
        }

        void Fail(string reason, bool contextLost)
        {
            if (contextLost)
                surface.ReportContextLost(reason);
            else
                surface.ReportUnavailable(reason, contextLost: false);
            _failed = true;
        }

        void Release(bool keepBitmap = false)
        {
            _imported?.Dispose();
            _imported = null;
            if (!keepBitmap)
            {
                _current?.Dispose();
                _current = null;
            }
        }
    }

    sealed class ImportedSet(MediaSharedGpuImageSet set, IImportedGpuImages images) : IDisposable
    {
        public MediaSharedGpuImageSet Set { get; } = set;
        public IImportedGpuImages Images { get; } = images;

        public void Dispose() => Images.Dispose();
    }
}
