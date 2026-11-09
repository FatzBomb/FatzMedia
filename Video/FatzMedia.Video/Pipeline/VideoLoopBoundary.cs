namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Pure loop-boundary and end-of-media rules extracted from <see cref="VideoPipeline"/>
/// (Stage 041A, the first 015A seam): end tolerance, loop boundary, loop overflow, natural end,
/// restart and drained-at-end decisions. Values and rules are unchanged from the original inline
/// code.
/// </summary>
internal static class VideoLoopBoundary
{
    /// <summary>How close to the end counts as the end: one frame, kept between 20 and 120 ms (50 ms without a frame rate).</summary>
    public static double EndToleranceSeconds(double framesPerSecond) => framesPerSecond > 0
        ? Math.Clamp(1.0 / framesPerSecond, 0.02, 0.12)
        : 0.05;

    /// <summary>
    /// Where a lap ends: the shorter of the media and the video stream durations when the video's
    /// is known, else the media duration.
    /// </summary>
    public static double LoopBoundarySeconds(double videoDurationSeconds, double mediaDurationSeconds) =>
        double.IsFinite(videoDurationSeconds) && videoDurationSeconds > 0
            ? mediaDurationSeconds > 0 ? Math.Min(mediaDurationSeconds, videoDurationSeconds) : videoDurationSeconds
            : mediaDurationSeconds;

    /// <summary>
    /// The position in the next lap for a clock that ran past the boundary: the overflow,
    /// wrapped when it exceeds a whole lap; 0 for invalid input or no overflow.
    /// </summary>
    public static double LoopOverflowPosition(double currentPosition, double loopBoundary)
    {
        if (!double.IsFinite(currentPosition) || !double.IsFinite(loopBoundary) || loopBoundary <= 0)
            return 0;

        var overflow = currentPosition - loopBoundary;
        return overflow <= 0 ? 0 : overflow >= loopBoundary ? overflow % loopBoundary : overflow;
    }

    /// <summary>Whether playing media has reached its duration, within the end tolerance.</summary>
    public static bool HasReachedNaturalEnd(bool isPlaying, double durationSeconds, double position, double tolerance) =>
        isPlaying && durationSeconds > 0 && position >= durationSeconds - tolerance;

    /// <summary>
    /// Whether a looping video should restart: the decoder is drained at the boundary, or the
    /// clock is a tolerance past it with nothing queued (EOF and duration metadata can be
    /// imperfect; restarting beats freezing on the retained frame).
    /// </summary>
    public static bool ShouldRestartLoop(bool endOfStream, int queuedFrames, double position, double loopBoundary, double tolerance) =>
        (endOfStream && queuedFrames == 0 && position >= loopBoundary - tolerance)
        || (queuedFrames == 0 && position >= loopBoundary + tolerance);

    /// <summary>
    /// Whether a drained decoder means playback has ended (short of the natural end): EOF with
    /// nothing queued, and with audio only once the clock has reached the loop boundary.
    /// </summary>
    public static bool HasDrainedAtEnd(bool endOfStream, int queuedFrames, bool hasAudio, double position, double loopBoundary, double tolerance) =>
        endOfStream && queuedFrames <= 0 && (!hasAudio || position >= loopBoundary - tolerance);
}
