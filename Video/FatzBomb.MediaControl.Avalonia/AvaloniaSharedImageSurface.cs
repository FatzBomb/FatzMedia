using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace FatzBomb.MediaControl.Avalonia;

/// <summary>
/// The GPU shared-image path of <see cref="MediaSurface"/> (Stage 027A slice 2). Receives the
/// renderer's exported, double-buffered images as plain handles, imports them into Avalonia's
/// compositor (<see cref="ICompositionGpuInterop"/>), and shows each finished frame on a
/// <see cref="CompositionDrawingSurface"/> with the synchronization the renderer chose. No pixels
/// pass through the CPU.
/// </summary>
/// <remarks>
/// Renderer callbacks arrive on its presentation thread; they only queue work for the UI thread.
/// Every frame is submitted in order and released when the compositor's update completes, so
/// the renderer never writes an image the compositor may still read, and keyed-mutex and
/// semaphore states stay paired. Any import or update failure reports the surface unavailable
/// once, and <see cref="MediaSurface"/> switches to its frame sink.
/// </remarks>
internal sealed class AvaloniaSharedImageSurface : IMediaSharedGpuImageConsumer, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Visual _owner;
    private readonly GpuInteropCapabilities _interop;
    private readonly Action<AvaloniaSharedImageSurface, string> _unavailable;
    private readonly MediaSharedGpuImageConsumerHandle _handle;
    private readonly CompositionSurfaceVisual _visual;
    private readonly CompositionDrawingSurface _surface;
    private readonly Queue<MediaSharedGpuImageFrame> _frames = new();
    private MediaSharedGpuImageSet? _pendingSet;
    private ImportedSet? _current;
    private MediaSharedGpuImageSelection? _reportedSelection;
    private string? _unavailableReason;
    private bool _pumpQueued;
    private bool _disposed;

    public AvaloniaSharedImageSurface(
        Visual owner,
        Compositor compositor,
        GpuInteropCapabilities interop,
        MediaSharedGpuImageRequest request,
        Action<AvaloniaSharedImageSurface, string> unavailable)
    {
        _owner = owner;
        _interop = interop;
        Request = request;
        _unavailable = unavailable;
        _surface = compositor.CreateDrawingSurface();
        _visual = compositor.CreateSurfaceVisual();
        _visual.Surface = _surface;
        ElementComposition.SetElementChildVisual(owner, _visual);
        _handle = new MediaSharedGpuImageConsumerHandle(this);
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
            }
        }

        skipped?.CompleteImport(handlesConsumed: false);
        QueuePump();
    }

    public void OnFrameReady(MediaSharedGpuImageFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _frames.Enqueue(frame);
        }

        QueuePump();
    }

    public void OnUnavailable(string reason) => ReportUnavailable(reason);

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
        if (ReferenceEquals(ElementComposition.GetElementChildVisual(_owner), _visual))
            ElementComposition.SetElementChildVisual(_owner, null);

        _handle.Dispose();
        // The drawing surface goes once the compositor finished the updates that read it.
        Retire(_current, _surface.Dispose);
        _current = null;
    }

    private void QueuePump()
    {
        lock (_gate)
        {
            if (_disposed || _pumpQueued)
                return;

            _pumpQueued = true;
        }

        Dispatcher.UIThread.Post(Pump, DispatcherPriority.Render);
    }

    /// <summary>Imports a new image set if one arrived, then submits the queued frames in order (UI thread).</summary>
    private void Pump()
    {
        MediaSharedGpuImageSet? set;
        MediaSharedGpuImageFrame[] frames;
        lock (_gate)
        {
            _pumpQueued = false;
            if (_disposed)
                return;

            set = _pendingSet;
            _pendingSet = null;
            frames = [.. _frames];
            _frames.Clear();
        }

        if (set is not null)
            Import(set);

        foreach (MediaSharedGpuImageFrame frame in frames)
            Submit(frame);
    }

    private void Import(MediaSharedGpuImageSet set)
    {
        Retire(_current);
        _current = null;
        if (_interop.Interop is not { IsLost: false } interop)
        {
            set.CompleteImport(handlesConsumed: false);
            ReportUnavailable(_interop.Interop is null ? "the compositor has no GPU interop to import with" : "the compositor's GPU interop was lost");
            return;
        }

        var imported = new ImportedSet(set);
        try
        {
            var imageType = AvaloniaGpuInterop.ToAvaloniaImageHandleType(set.HandleKind);
            var semaphoreType = set.SyncKind == MediaSharedGpuImageSyncKinds.Semaphores
                ? AvaloniaGpuInterop.ToAvaloniaSemaphoreHandleType(set.SemaphoreHandleKind)
                : null;
            foreach (MediaSharedGpuImageBuffer buffer in set.Buffers)
            {
                imported.Images.Add(interop.ImportImage(
                    new PlatformHandle(buffer.ImageHandle, imageType),
                    AvaloniaGpuInterop.ToImageProperties(set, buffer)));
                if (semaphoreType is not null)
                {
                    imported.RenderComplete.Add(interop.ImportSemaphore(new PlatformHandle(buffer.RenderCompleteSemaphore, semaphoreType)));
                    imported.ConsumerRelease.Add(interop.ImportSemaphore(new PlatformHandle(buffer.ConsumerReleaseSemaphore, semaphoreType)));
                }
            }
        }
        catch (Exception exception)
        {
            set.CompleteImport(handlesConsumed: true);
            Retire(imported);
            ReportUnavailable($"importing the shared images failed: {exception.Message}");
            return;
        }

        set.CompleteImport(handlesConsumed: true);
        _current = imported;
        foreach (ICompositionGpuImportedObject importedObject in imported.Objects)
            ObserveImport(importedObject);

        if (_reportedSelection != set.Selection)
        {
            _reportedSelection = set.Selection;
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                MediaSurface.DiagnosticCategory,
                $"Shared GPU images imported: {set.Width}x{set.Height} {set.Format} x{set.Buffers.Count}, {set.Selection}");
        }
    }

    private void Submit(MediaSharedGpuImageFrame frame)
    {
        ImportedSet? current = _current;
        // Frames of a set that was replaced or failed to import: the renderer reset its ring for
        // the newer set (or this path is being abandoned), so there is nothing to give back.
        if (current is null || frame.Generation != current.Set.Generation || (uint)frame.BufferIndex >= (uint)current.Images.Count)
            return;

        MediaSharedGpuImageSet set = current.Set;
        Task update;
        try
        {
            ICompositionImportedGpuImage image = current.Images[frame.BufferIndex];
            update = set.SyncKind switch
            {
                MediaSharedGpuImageSyncKinds.KeyedMutex => _surface.UpdateWithKeyedMutexAsync(image, (uint)frame.AcquireKey, (uint)frame.ReleaseKey),
                MediaSharedGpuImageSyncKinds.Semaphores => _surface.UpdateWithSemaphoresAsync(
                    image,
                    current.RenderComplete[frame.BufferIndex],
                    current.ConsumerRelease[frame.BufferIndex]),
                _ => _surface.UpdateAsync(image),
            };
        }
        catch (Exception exception)
        {
            set.ReleaseFrame(frame, consumed: false);
            ReportUnavailable($"updating the composition surface failed: {exception.Message}");
            return;
        }

        current.Track(update);
        _ = update.ContinueWith(
            completed =>
            {
                set.ReleaseFrame(frame, completed.IsCompletedSuccessfully);
                if (!completed.IsCompletedSuccessfully)
                    ReportUnavailable($"the compositor failed to show a shared image: {completed.Exception?.GetBaseException().Message ?? "cancelled"}");
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveImport(ICompositionGpuImportedObject importedObject) =>
        _ = importedObject.ImportCompleted.ContinueWith(
            completed => ReportUnavailable($"the compositor could not import a shared image: {completed.Exception?.GetBaseException().Message ?? "cancelled"}"),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion,
            TaskScheduler.Default);

    private void ReportUnavailable(string reason)
    {
        lock (_gate)
        {
            if (_unavailableReason is not null)
                return;

            _unavailableReason = reason;
        }

        Dispatcher.UIThread.Post(() => _unavailable(this, reason));
    }

    /// <summary>Disposes a set's imports once the compositor finished every update that uses them.</summary>
    private static void Retire(ImportedSet? imported, Action? then = null)
    {
        if (imported is null)
        {
            then?.Invoke();
            return;
        }

        _ = Task.WhenAll(imported.PendingUpdates()).ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                foreach (ICompositionGpuImportedObject importedObject in imported.Objects)
                    _ = DisposeQuietlyAsync(importedObject);
                then?.Invoke();
            }),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task DisposeQuietlyAsync(IAsyncDisposable disposable)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A lost or failed import has nothing left to release.
        }
    }

    private sealed class ImportedSet(MediaSharedGpuImageSet set)
    {
        private readonly List<Task> _updates = [];

        public MediaSharedGpuImageSet Set { get; } = set;
        public List<ICompositionImportedGpuImage> Images { get; } = [];
        public List<ICompositionImportedGpuSemaphore> RenderComplete { get; } = [];
        public List<ICompositionImportedGpuSemaphore> ConsumerRelease { get; } = [];

        public IEnumerable<ICompositionGpuImportedObject> Objects =>
            Images.Cast<ICompositionGpuImportedObject>().Concat(RenderComplete).Concat(ConsumerRelease);

        public void Track(Task update)
        {
            _updates.RemoveAll(static task => task.IsCompleted);
            _updates.Add(update);
        }

        public Task[] PendingUpdates() => [.. _updates];
    }
}
