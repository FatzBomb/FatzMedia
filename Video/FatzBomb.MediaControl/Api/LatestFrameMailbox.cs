#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// The latest-frame hand-off of an adapter's <see cref="IMediaPresentationFrameSink"/> (ENG-039,
/// shared by the WPF, Avalonia and WinForms frame sinks): the renderer's presentation thread
/// copies each frame in with <see cref="Post"/>, the adapter's UI thread takes the newest one with
/// <see cref="TryTake"/>, copies it to its bitmap and hands the array back with
/// <see cref="Return"/>. Frames that arrive before the UI thread takes one replace it (latest
/// wins). Two arrays alternate between the threads, so steady playback does not allocate per
/// frame; a spare more than twice the needed size (left over from a maximized window) is dropped
/// instead of kept.
/// </summary>
/// <remarks>
/// Threading: every member may be called from any thread; state changes under one lock, pixels
/// are copied outside the UI thread's bitmap work. The mailbox never marshals: <see cref="Post"/>
/// returns <see langword="true"/> when no publish is queued, and the adapter then schedules one
/// on its UI thread (where it calls <see cref="TryTake"/>, which re-arms the request).
/// </remarks>
public sealed class LatestFrameMailbox
{
    private readonly Lock _gate = new();
    private byte[]? _pending;
    // Idle arrays: two slots, because the UI thread can hand one back while another waits idle
    // (it took a frame, the renderer posted none since), and dropping either would allocate again.
    private byte[]? _spare;
    private byte[]? _secondSpare;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _pendingStride;
    private bool _publishQueued;
    private bool _closed;
    private int _bufferAllocations;

    /// <summary>Gets how many frame arrays were allocated (diagnostics and tests).</summary>
    public int BufferAllocations => Volatile.Read(ref _bufferAllocations);

    /// <summary>
    /// Copies a premultiplied BGRA frame in as the pending frame (presentation thread). An invalid
    /// size (non-positive, a stride shorter than a row, more bytes than an array holds, or a span
    /// shorter than <paramref name="stride"/> x <paramref name="height"/>) and a closed mailbox
    /// drop the frame.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the caller must schedule one publish on its UI thread (no
    /// publish was queued); <see langword="false"/> when the frame was dropped or a queued
    /// publish will take it.
    /// </returns>
    public bool Post(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
    {
        if (!TryGetLength(width, height, stride, out var length) || bgraPixels.Length < length)
            return false;

        lock (_gate)
        {
            if (_closed)
                return false;

            if (_pending is null)
            {
                // Drop a spare left over from a much larger window (after a maximize).
                byte[]? spare = _spare ?? _secondSpare;
                if (ReferenceEquals(spare, _spare))
                    _spare = null;
                else
                    _secondSpare = null;

                if (spare is not null && spare.Length <= 2L * length)
                    _pending = spare;
            }

            if (_pending is null || _pending.Length < length)
            {
                _pending = new byte[length];
                _ = Interlocked.Increment(ref _bufferAllocations);
            }

            bgraPixels[..length].CopyTo(_pending);
            (_pendingWidth, _pendingHeight, _pendingStride) = (width, height, stride);
            if (_publishQueued)
                return false;

            _publishQueued = true;
            return true;
        }
    }

    /// <summary>
    /// Takes the pending frame (UI thread) and re-arms the publish request, so the next
    /// <see cref="Post"/> asks for a publish again. The caller owns <paramref name="pixels"/>
    /// (rows of <paramref name="stride"/> bytes; the array may be longer than the frame) until it
    /// passes it to <see cref="Return"/>.
    /// </summary>
    /// <returns><see langword="false"/> when no frame is pending or the mailbox is closed.</returns>
    public bool TryTake(out byte[] pixels, out int width, out int height, out int stride)
    {
        lock (_gate)
        {
            _publishQueued = false;
            if (_closed || _pending is not { } pending)
            {
                (pixels, width, height, stride) = ([], 0, 0, 0);
                return false;
            }

            (pixels, width, height, stride) = (pending, _pendingWidth, _pendingHeight, _pendingStride);
            _pending = null;
            return true;
        }
    }

    /// <summary>Hands an array taken with <see cref="TryTake"/> back for the next frame.</summary>
    public void Return(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        lock (_gate)
        {
            if (!_closed && pixels.Length > 0)
                KeepSpare(pixels);
        }
    }

    /// <summary>
    /// Re-arms the publish request after the adapter could not schedule it (for example no window
    /// handle yet): the next <see cref="Post"/> asks again. The pending frame is kept.
    /// </summary>
    public void CancelPublishRequest()
    {
        lock (_gate)
            _publishQueued = false;
    }

    /// <summary>
    /// Drops the pending frame, keeping its array for reuse, so a frame presented before a clear
    /// does not bring the old picture back after it. A queued publish then finds nothing.
    /// </summary>
    public void DropPending()
    {
        lock (_gate)
        {
            if (_pending is { } pending && !_closed)
                KeepSpare(pending);
            _pending = null;
        }
    }

    /// <summary>
    /// Stops taking frames and releases both arrays. Returns <see langword="false"/> when the
    /// mailbox was already closed (repeat calls do nothing).
    /// </summary>
    public bool Close()
    {
        lock (_gate)
        {
            if (_closed)
                return false;

            _closed = true;
            _pending = null;
            _spare = null;
            _secondSpare = null;
            _publishQueued = false;
            return true;
        }
    }

    // Under _gate. Two arrays circulate in steady playback; a third (a frame posted while both
    // were out) is dropped.
    private void KeepSpare(byte[] pixels)
    {
        if (_spare is null)
            _spare = pixels;
        else
            _secondSpare ??= pixels;
    }

    /// <summary>
    /// Gets the byte length of a frame (<paramref name="stride"/> x <paramref name="height"/>), or
    /// <see langword="false"/> when the size is invalid or does not fit in an array.
    /// </summary>
    internal static bool TryGetLength(int width, int height, int stride, out int length)
    {
        length = 0;
        if (width <= 0 || height <= 0 || stride < 4L * width)
            return false;

        var bytes = (long)stride * height;
        if (bytes > Array.MaxLength)
            return false;

        length = (int)bytes;
        return true;
    }
}
