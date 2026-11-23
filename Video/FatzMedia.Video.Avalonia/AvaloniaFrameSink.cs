using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// Receives final premultiplied BGRA frames from the selected renderer on its presentation
/// thread and shows the latest one through a composition custom visual, drawn on Avalonia's
/// render thread. This sink owns the visual and its lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// The picture never waits for the UI thread (a drop-down opening, a window loading): the render
/// thread takes each frame on its own animation-frame tick, so a busy UI thread does not freeze the
/// video. Only the first frame after an idle period, and the control's size, visibility and clearing,
/// go through the UI thread.
/// </para>
/// <para>
/// The renderer's thread copies each frame straight into one of three bitmaps (the one being drawn,
/// the newest finished one, and one to write), so the render thread only draws: a full-screen
/// frame is copied once, off the render thread, and the render thread's budget goes to drawing it.
/// Frames that arrive before the render thread takes one replace it (latest wins). Steady playback
/// allocates no bitmaps.
/// </para>
/// </remarks>
sealed class AvaloniaFrameSink : IMediaPresentationFrameSink, IDisposable
{
    // Being drawn (render thread), newest finished, being written: never the same one twice.
    const int SlotCount = 3;

    readonly Lock _gate = new();
    readonly WriteableBitmap?[] _slots = new WriteableBitmap?[SlotCount];
    readonly Action _firstFrameShown;
    readonly MediaPresentationFrameSinkHandle _handle;
    // Guarded by _gate: the slot being drawn, the newest finished one and the one being written (-1: none).
    int _drawing = -1;
    int _ready = -1;
    int _writing = -1;
    // The generation current when the frame in _ready was finished.
    int _readyGeneration;
    bool _closed;
    Visual? _owner;
    CompositionCustomVisual? _visual;
    // 1 while the render thread has stopped polling because no frame arrived for a while.
    int _asleep;
    long _publishedFrames;
    long _drawnFrames;
    int _hasFrame;
    int _bitmapAllocations;
    // Counts clears: a frame finished before a clear never counts as shown after it.
    int _generation;
    // The newest frame's size as (width << 32 | height), 0 when none (diagnostics and tests).
    long _frameSize;

    /// <param name="firstFrameShown">Called on the UI thread once per clear, when the first frame after it was drawn.</param>
    public AvaloniaFrameSink(Action firstFrameShown)
    {
        _firstFrameShown = firstFrameShown ?? throw new ArgumentNullException(nameof(firstFrameShown));
        _handle = new MediaPresentationFrameSinkHandle(this);
    }

    /// <summary>Gets whether a frame was drawn since the last clear or attach.</summary>
    public bool HasFrame => Volatile.Read(ref _hasFrame) != 0;

    /// <summary>Gets the pixel size of the frame on screen, or null when there is none (diagnostics and tests).</summary>
    internal PixelSize? FrameSize => Interlocked.Read(ref _frameSize) is var packed and not 0
        ? new PixelSize((int)(packed >> 32), (int)(packed & 0xFFFFFFFF))
        : null;

    /// <summary>Gets how many frames the render thread took for display (diagnostics and tests).</summary>
    public long PublishedFrames => Interlocked.Read(ref _publishedFrames);

    /// <summary>Gets how many frames were drawn since the last attach (diagnostics and tests).</summary>
    public long DrawnFrames => Interlocked.Read(ref _drawnFrames);

    /// <summary>Gets how many bitmaps were allocated (diagnostics and tests).</summary>
    internal int BitmapAllocations => Volatile.Read(ref _bitmapAllocations);

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    /// <summary>Hangs the frame visual on <paramref name="owner"/> (UI thread); a visual already attached is replaced.</summary>
    public void AttachVisual(Visual owner, Compositor compositor, Size size, bool visible)
    {
        DetachVisual();
        _owner = owner;
        _visual = compositor.CreateCustomVisual(new PresentationHandler(this));
        _visual.Size = new Vector(size.Width, size.Height);
        _visual.Visible = visible;
        ElementComposition.SetElementChildVisual(owner, _visual);
        // The first message starts the polling; it goes back to sleep by itself when no frame arrives.
        Wake();
    }

    /// <summary>Takes the visual off its owner and drops the displayed frame (UI thread).</summary>
    public void DetachVisual()
    {
        Clear();
        if (_owner is { } owner && _visual is not null && ReferenceEquals(ElementComposition.GetElementChildVisual(owner), _visual))
            ElementComposition.SetElementChildVisual(owner, null);

        _visual = null;
        _owner = null;
    }

    /// <summary>Sizes the visual to the control (UI thread).</summary>
    public void SetSize(Size size)
    {
        if (_visual is { } visual)
            visual.Size = new Vector(size.Width, size.Height);
    }

    /// <summary>Shows or hides the visual (UI thread); hidden, the control's background shows.</summary>
    public void SetVisible(bool visible)
    {
        if (_visual is { } visual)
            visual.Visible = visible;
    }

    /// <summary>Copies the frame into a free bitmap and makes it the newest one (the renderer's presentation thread).</summary>
    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < 4L * width || (long)stride * (height - 1) + 4L * width > bgraPixels.Length)
            return;

        int slot;
        WriteableBitmap? bitmap;
        int generation;
        lock (_gate)
        {
            if (_closed)
                return;

            // At most two slots are taken (drawing, ready): one of the three is always free.
            slot = FreeSlot();
            _writing = slot;
            bitmap = _slots[slot];
            generation = Volatile.Read(ref _generation);
        }

        try
        {
            if (bitmap is null || bitmap.PixelSize.Width != width || bitmap.PixelSize.Height != height)
            {
                // The slot is free, so no thread uses the old bitmap.
                bitmap?.Dispose();
                bitmap = null;
                lock (_gate)
                    _slots[slot] = null;

                bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                _ = Interlocked.Increment(ref _bitmapAllocations);
            }

            Copy(bgraPixels, width, height, stride, bitmap);
        }
        catch
        {
            // A failed allocation or lock must not leave the slot marked as being written.
            lock (_gate)
            {
                _writing = -1;
                if (bitmap is not null && !ReferenceEquals(_slots[slot], bitmap))
                    _slots[slot] = bitmap;
            }

            throw;
        }

        lock (_gate)
        {
            _writing = -1;
            if (_closed)
            {
                bitmap.Dispose();
                _slots[slot] = null;
                return;
            }

            _slots[slot] = bitmap;
            // The frame it replaces (if the render thread did not take it) is free again.
            _ready = slot;
            _readyGeneration = generation;
        }

        // The render thread polls while frames flow; after an idle stretch it sleeps and needs one wake-up.
        if (Volatile.Read(ref _asleep) == 1 && Interlocked.Exchange(ref _asleep, 0) == 1)
            Dispatcher.UIThread.Post(Wake, DispatcherPriority.Render);
    }

    /// <summary>Drops the displayed and any waiting frame (UI thread).</summary>
    public void Clear()
    {
        var generation = Interlocked.Increment(ref _generation);
        lock (_gate)
            _ready = -1;

        Volatile.Write(ref _hasFrame, 0);
        Interlocked.Exchange(ref _frameSize, 0);
        _visual?.SendHandlerMessage(new PresentationHandler.ClearRequest(generation));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;

            _closed = true;
        }

        _handle.Dispose();
        // The render thread drops the bitmap it was drawing (it is nobody's once the sink is closed).
        _visual?.SendHandlerMessage(PresentationHandler.ReleaseMessage);
        DetachVisual();
        lock (_gate)
        {
            // The slot the render thread may still draw (and one a presentation thread is writing) go with
            // their last user; the others are free now.
            for (var i = 0; i < SlotCount; i++)
            {
                if (i != _drawing && i != _writing)
                {
                    _slots[i]?.Dispose();
                    _slots[i] = null;
                }
            }
        }
    }

    // Under _gate: a slot neither drawn nor holding the newest frame.
    int FreeSlot()
    {
        for (var i = 0; i < SlotCount; i++)
        {
            if (i != _drawing && i != _ready)
                return i;
        }

        return 0;
    }

    static unsafe void Copy(ReadOnlySpan<byte> pixels, int width, int height, int stride, WriteableBitmap bitmap)
    {
        using var framebuffer = bitmap.Lock();
        var rowBytes = width * 4;
        var destination = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
        if (framebuffer.RowBytes == stride && pixels.Length >= stride * height)
        {
            pixels[..(stride * height)].CopyTo(destination);
            return;
        }

        for (var y = 0; y < height; y++)
            pixels.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * framebuffer.RowBytes, rowBytes));
    }

    void Wake() => _visual?.SendHandlerMessage(PresentationHandler.WakeMessage);

    // Render thread: makes the newest finished frame the one to draw. False when none is waiting.
    bool TryTakeReady(out WriteableBitmap? bitmap, out int generation)
    {
        lock (_gate)
        {
            // A frame finished across a clear belongs to the old load: dropped, not shown.
            if (_ready >= 0 && _readyGeneration != Volatile.Read(ref _generation))
                _ready = -1;

            if (_ready < 0)
            {
                (bitmap, generation) = (null, 0);
                return false;
            }

            _drawing = _ready;
            _ready = -1;
            bitmap = _slots[_drawing];
            generation = _readyGeneration;
        }

        _ = Interlocked.Increment(ref _publishedFrames);
        return true;
    }

    // Render thread: nothing is drawn any more (a clear came, or the visual is going away).
    void StopDrawing()
    {
        lock (_gate)
            _drawing = -1;
    }

    void ReleaseDrawn(WriteableBitmap? bitmap)
    {
        // The sink was disposed while this was the slot in use: it is nobody's now.
        if (bitmap is null)
            return;

        lock (_gate)
        {
            if (!_closed)
                return;

            for (var i = 0; i < SlotCount; i++)
            {
                if (ReferenceEquals(_slots[i], bitmap))
                    _slots[i] = null;
            }
        }

        bitmap.Dispose();
    }

    // Render thread, after it drew a frame that belongs to clear generation `generation`.
    void OnFrameDrawn(int generation, int width, int height)
    {
        // A clear came after this frame was finished: the frame is gone, not shown.
        if (generation != Volatile.Read(ref _generation))
            return;

        _ = Interlocked.Increment(ref _drawnFrames);
        _ = Interlocked.Exchange(ref _frameSize, ((long)width << 32) | (uint)height);
        if (Interlocked.Exchange(ref _hasFrame, 1) == 0)
            Dispatcher.UIThread.Post(_firstFrameShown, DispatcherPriority.Render);
    }

    /// <summary>
    /// Runs on the render thread: takes the newest finished frame on every animation-frame tick while
    /// frames flow and draws its bitmap. It never writes pixels.
    /// </summary>
    sealed class PresentationHandler(AvaloniaFrameSink sink) : CompositionCustomVisualHandler
    {
        public static readonly object WakeMessage = new();
        public static readonly object ReleaseMessage = new();

        // About half a second at 60 Hz without a new frame: stop polling until the next one.
        const int IdleTicksBeforeSleep = 30;

        WriteableBitmap? _bitmap;
        // The clear generation current when the bitmap's frame was finished.
        int _frameGeneration;
        int _idleTicks;

        /// <summary>Stops drawing, unless a frame newer than clear generation <paramref name="Generation"/> was taken meanwhile.</summary>
        public sealed record ClearRequest(int Generation);

        public override void OnMessage(object message)
        {
            if (ReferenceEquals(message, ReleaseMessage))
            {
                sink.StopDrawing();
                sink.ReleaseDrawn(_bitmap);
                _bitmap = null;
                return;
            }

            if (message is ClearRequest clear)
            {
                // A frame of the next load may already be here: only an older one is dropped.
                if (_frameGeneration < clear.Generation)
                {
                    sink.StopDrawing();
                    sink.ReleaseDrawn(_bitmap);
                    _bitmap = null;
                }

                Invalidate();
                return;
            }

            _idleTicks = 0;
            RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            if (TakeReady())
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

            // Asleep: a frame that arrived after the last look must not wait for a wake-up it will not get.
            Volatile.Write(ref sink._asleep, 1);
            if (TakeReady())
            {
                Invalidate();
                if (Interlocked.Exchange(ref sink._asleep, 0) == 1)
                {
                    _idleTicks = 0;
                    RegisterForNextAnimationFrameUpdate();
                }
            }
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (_bitmap is { } bitmap)
            {
                // The renderer already composed for this pixel size (letterboxing included), so the
                // bitmap fills the surface.
                drawingContext.DrawBitmap(bitmap, new Rect(bitmap.Size), new Rect(0, 0, EffectiveSize.X, EffectiveSize.Y));
                sink.OnFrameDrawn(_frameGeneration, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            }
        }

        bool TakeReady()
        {
            if (!sink.TryTakeReady(out var bitmap, out var generation))
                return false;

            _bitmap = bitmap;
            _frameGeneration = generation;
            return true;
        }
    }
}
