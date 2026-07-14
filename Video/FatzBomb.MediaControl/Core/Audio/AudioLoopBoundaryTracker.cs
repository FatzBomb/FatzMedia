namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Pure bookkeeping for gapless audio looping. The audio engine plays a BASS push stream
/// whose channel position never resets across a loop wrap (the FFmpeg decoder is seeked
/// back to 0 while the same channel keeps playing), so the played position is a single,
/// ever-increasing "continuous" timeline spanning every lap.
///
/// Each time the decoder wraps, the continuous-timeline value at which that lap's audio
/// finishes being pushed is recorded as a pending boundary. Once the actually-played
/// continuous position reaches a boundary (not when the decoder wraps - laps are still
/// buffered several seconds ahead of what has been played), that boundary becomes the new
/// cumulative offset subtracted from the continuous position to report a value that wraps
/// naturally back into [0, lap duration).
/// </summary>
internal static class AudioLoopBoundaryTracker
{
    /// <summary>
    /// Advances loop-completion bookkeeping given the current continuous (never-wrapping)
    /// played position. Dequeues every pending boundary at or before that position, updating
    /// <paramref name="completedOffsetSeconds"/> to the most recently crossed boundary.
    /// </summary>
    /// <returns>The number of boundaries crossed by this call (0 if none).</returns>
    public static int Advance(double continuousPositionSeconds, Queue<double> pendingBoundaries, ref double completedOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(pendingBoundaries);

        var crossed = 0;
        while (pendingBoundaries.Count > 0 && continuousPositionSeconds >= pendingBoundaries.Peek())
        {
            completedOffsetSeconds = pendingBoundaries.Dequeue();
            crossed++;
        }

        return crossed;
    }
}
