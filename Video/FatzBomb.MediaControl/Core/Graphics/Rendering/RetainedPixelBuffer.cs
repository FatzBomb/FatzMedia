namespace FatzBomb.MediaControl.Core.Graphics.Rendering;

/// <summary>
/// Sizing policy for a CPU pixel buffer a renderer keeps across target re-creations (ENG-026: a
/// managed frame sink reallocated its readback buffer on every step of a live resize). The buffer
/// grows to the largest size needed and is reused for smaller ones, but is replaced when the
/// required size drops well below it, so a window shrunk for good does not pin a 4K buffer.
/// </summary>
public static class RetainedPixelBuffer
{
    /// <summary>A buffer is replaced when the required length is below capacity / this factor.</summary>
    public const int ShrinkFactor = 4;

    /// <summary>Whether a buffer of <paramref name="capacity"/> bytes can hold <paramref name="requiredLength"/> without being replaced.</summary>
    public static bool CanReuse(int capacity, int requiredLength) =>
        requiredLength >= 0
        && capacity >= requiredLength
        && requiredLength >= capacity / ShrinkFactor;

    /// <summary>
    /// Returns <paramref name="buffer"/> when <see cref="CanReuse"/> allows it, otherwise a new
    /// array of exactly <paramref name="requiredLength"/> bytes. Contents are not preserved.
    /// </summary>
    public static byte[] Ensure(byte[]? buffer, int requiredLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredLength);
        return buffer is not null && CanReuse(buffer.Length, requiredLength)
            ? buffer
            : new byte[requiredLength];
    }
}
