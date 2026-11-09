using System.Buffers;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// Reference-counted pooled pixel buffer for the retained composition background (Stage 010A).
/// The coordinator holds one reference while the buffer is current; each in-flight composition
/// holds another. The array returns to its pool when the last reference is released, so a
/// composition can present the retained pixels directly instead of copying them.
/// </summary>
sealed class RetainedFrameBuffer
{
    readonly ArrayPool<byte> _pool;
    byte[]? _array;
    int _references = 1;

    RetainedFrameBuffer(ArrayPool<byte> pool, byte[] array, int length)
    {
        _pool = pool;
        _array = array;
        Length = length;
    }

    public int Length { get; }

    /// <summary>Gets whether only the creator holds a reference (safe to overwrite in place).</summary>
    public bool IsExclusive => Volatile.Read(ref _references) == 1;

    /// <summary>Gets whether the array was returned to the pool.</summary>
    public bool IsReleased => Volatile.Read(ref _array) is null;

    public Memory<byte> Memory => _array is { } array
        ? new Memory<byte>(array, 0, Length)
        : throw new ObjectDisposedException(nameof(RetainedFrameBuffer));

    public static RetainedFrameBuffer Rent(ArrayPool<byte> pool, int length)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new RetainedFrameBuffer(pool, pool.Rent(length), length);
    }

    /// <summary>Takes another reference. Callers serialize with the owner's writes.</summary>
    public RetainedFrameBuffer AddReference()
    {
        while (true)
        {
            var current = Volatile.Read(ref _references);
            if (current <= 0)
                throw new ObjectDisposedException(nameof(RetainedFrameBuffer));
            if (Interlocked.CompareExchange(ref _references, current + 1, current) == current)
                return this;
        }
    }

    /// <summary>Drops one reference; the last one returns the array to the pool.</summary>
    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _references);
        if (remaining > 0)
            return;

        if (remaining < 0)
            throw new InvalidOperationException("RetainedFrameBuffer released more times than referenced.");

        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null)
            _pool.Return(array);
    }
}
