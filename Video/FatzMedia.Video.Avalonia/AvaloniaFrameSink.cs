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
/// render thread. Core's <see cref="LatestFrameMailbox"/> hands the latest frame over (latest
/// wins, two arrays alternating, so steady playback does not allocate per frame); this sink owns
/// the visual and its lifecycle.
/// </summary>
/// <remarks>
/// The picture never waits for the UI thread (a drop-down opening, a window loading): the render
/// thread takes each frame from the mailbox on its own animation-frame tick, so a busy UI thread
/// does not freeze the video. Only the first frame after an idle period, and the control's size,
/// visibility and clearing, go through the UI thread. The shared GPU image path cannot do this:
/// Avalonia accepts its image updates only from the UI thread.
/// </remarks>
sealed class AvaloniaFrameSink : IMediaPresentationFrameSink, IDisposable
{
    readonly LatestFrameMailbox _mailbox = new();
    readonly Action _firstFrameShown;
    readonly MediaPresentationFrameSinkHandle _handle;
    Visual? _owner;
    CompositionCustomVisual? _visual;
    PresentationHandler? _presenter;
    // 1 while the render thread has stopped polling because no frame arrived for a while.
    int _asleep;
    long _publishedFrames;
    long _drawnFrames;
    int _hasFrame;
    // Counts clears: a frame the render thread took before a clear never counts as shown after it.
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

    /// <summary>Gets how many frames the render thread took from the mailbox (diagnostics and tests).</summary>
    public long PublishedFrames => Interlocked.Read(ref _publishedFrames);

    /// <summary>Gets how many frames were drawn since the last attach (diagnostics and tests).</summary>
    public long DrawnFrames => Interlocked.Read(ref _drawnFrames);

    /// <summary>Gets how many frame buffers were allocated (diagnostics and tests).</summary>
    internal int BufferAllocations => _mailbox.BufferAllocations;

    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor() => _handle.CreateSurfaceDescriptor();

    /// <summary>Hangs the frame visual on <paramref name="owner"/> (UI thread); a visual already attached is replaced.</summary>
    public void AttachVisual(Visual owner, Compositor compositor, Size size, bool visible)
    {
        DetachVisual();
        _owner = owner;
        _presenter = new PresentationHandler(this);
        _visual = compositor.CreateCustomVisual(_presenter);
        _visual.Size = new Vector(size.Width, size.Height);
        _visual.Visible = visible;
        ElementComposition.SetElementChildVisual(owner, _visual);
        Volatile.Write(ref _asleep, 1);
        Wake();
    }

    /// <summary>Takes the visual off its owner and drops the displayed frame (UI thread).</summary>
    public void DetachVisual()
    {
        Clear();
        if (_owner is { } owner && _visual is not null && ReferenceEquals(ElementComposition.GetElementChildVisual(owner), _visual))
            ElementComposition.SetElementChildVisual(owner, null);

        _visual = null;
        _presenter = null;
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

    public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        _ = _mailbox.Post(bgraPixels, width, height, stride);
        // The render thread polls while frames flow; after an idle stretch it sleeps and needs one wake-up.
        if (Volatile.Read(ref _asleep) == 1 && Interlocked.Exchange(ref _asleep, 0) == 1)
            Dispatcher.UIThread.Post(Wake, DispatcherPriority.Render);
    }

    /// <summary>Drops the displayed and any waiting frame (UI thread).</summary>
    public void Clear()
    {
        var generation = Interlocked.Increment(ref _generation);
        _mailbox.DropPending();
        Volatile.Write(ref _hasFrame, 0);
        Interlocked.Exchange(ref _frameSize, 0);
        _visual?.SendHandlerMessage(new PresentationHandler.ClearRequest(generation));
    }

    public void Dispose()
    {
        if (!_mailbox.Close())
            return;

        _handle.Dispose();
        DetachVisual();
    }

    void Wake() => _visual?.SendHandlerMessage(PresentationHandler.WakeMessage);

    void OnFrameTaken() => Interlocked.Increment(ref _publishedFrames);

    // Render thread, after it drew a frame that belongs to clear generation `generation`.
    void OnFrameDrawn(int generation, int width, int height)
    {
        // A clear came after this frame was taken: the frame is gone, not shown.
        if (generation != Volatile.Read(ref _generation))
            return;

        _ = Interlocked.Increment(ref _drawnFrames);
        _ = Interlocked.Exchange(ref _frameSize, ((long)width << 32) | (uint)height);
        if (Interlocked.Exchange(ref _hasFrame, 1) == 0)
            Dispatcher.UIThread.Post(_firstFrameShown, DispatcherPriority.Render);
    }

    /// <summary>
    /// Runs on the render thread: takes the newest frame from the mailbox on every animation-frame
    /// tick while frames flow, copies it into its own bitmap and draws that. The bitmap never leaves
    /// this thread, so no lock is needed around it.
    /// </summary>
    sealed class PresentationHandler(AvaloniaFrameSink sink) : CompositionCustomVisualHandler, IDisposable
    {
        public static readonly object WakeMessage = new();

        /// <summary>Drops the bitmap, unless a frame newer than clear generation <paramref name="Generation"/> was taken meanwhile.</summary>
        public sealed record ClearRequest(int Generation);

        // About half a second at 60 Hz without a new frame: stop polling until the next one.
        const int IdleTicksBeforeSleep = 30;

        WriteableBitmap? _bitmap;
        // The clear generation current when the bitmap's frame was taken.
        int _frameGeneration;
        int _idleTicks;

        public override void OnMessage(object message)
        {
            if (message is ClearRequest clear)
            {
                // A frame of the next load may already be here: only an older one is dropped.
                if (_frameGeneration < clear.Generation)
                    Dispose();
                Invalidate();
                return;
            }

            _idleTicks = 0;
            RegisterForNextAnimationFrameUpdate();
        }

        /// <summary>Releases the bitmap (render thread); the next frame makes a new one.</summary>
        public void Dispose()
        {
            _bitmap?.Dispose();
            _bitmap = null;
        }

        public override void OnAnimationFrameUpdate()
        {
            if (TakePending())
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
            if (TakePending())
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

        unsafe bool TakePending()
        {
            if (!sink._mailbox.TryTake(out var pixels, out var width, out var height, out var stride))
                return false;

            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }

            using (var framebuffer = _bitmap.Lock())
            {
                var rowBytes = width * 4;
                var destination = new Span<byte>((void*)framebuffer.Address, framebuffer.RowBytes * height);
                for (var y = 0; y < height; y++)
                    pixels.AsSpan(y * stride, rowBytes).CopyTo(destination.Slice(y * framebuffer.RowBytes, rowBytes));
            }

            // Hand the copied-out buffer back for the next frame.
            sink._mailbox.Return(pixels);
            _frameGeneration = Volatile.Read(ref sink._generation);
            sink.OnFrameTaken();
            return true;
        }
    }
}
