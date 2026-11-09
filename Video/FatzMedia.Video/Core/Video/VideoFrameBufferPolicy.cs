namespace FatzMedia.Video.Core.Video;

/// <summary>
/// Decoded-frame queue sizing shared by the video pipeline and the decoder. The pipeline buffers
/// <see cref="GetTargetBufferedFrames"/> frames; the decoder caps a native hardware queue at
/// <see cref="GetHardwareQueueCapacity"/> and asks FFmpeg for
/// <see cref="GetHardwareSurfaceReserve"/> extra pool surfaces, because every queued hardware
/// frame pins a surface of FFmpeg's fixed d3d11va pool (ENG-027).
/// </summary>
internal static class VideoFrameBufferPolicy
{
    public const int MaxBufferedFrames = 12;
    public const int MinBufferedFrames = 4;
    public const long MaxBufferedBytes = 96L * 1024 * 1024;

    /// <summary>Hardware queue floor: room for seek decoding and frame bursts at 4K.</summary>
    public const int MinHardwareQueueFrames = 8;

    /// <summary>
    /// Hardware frames held outside the queue: the frame on screen, and the previous one until the
    /// presentation thread hands over (the decode thread can refill the queue meanwhile).
    /// </summary>
    public const int PresentationHeldHardwareFrames = 2;

    /// <summary>
    /// The pipeline's buffer target: about a quarter second of video, at most
    /// <see cref="MaxBufferedBytes"/> of BGRA frames, within [<see cref="MinBufferedFrames"/>,
    /// <see cref="MaxBufferedFrames"/>]; 10 frames when the rate is unknown.
    /// </summary>
    public static int GetTargetBufferedFrames(int width, int height, double framesPerSecond)
    {
        var frameBytes = Math.Max(1L, (long)width * height * 4);
        var memoryBoundFrames = (int)Math.Clamp(MaxBufferedBytes / frameBytes, MinBufferedFrames, MaxBufferedFrames);
        var timingBoundFrames = double.IsFinite(framesPerSecond) && framesPerSecond > 0
            ? Math.Clamp((int)Math.Ceiling(framesPerSecond * 0.25), MinBufferedFrames, MaxBufferedFrames)
            : 10;

        return Math.Min(memoryBoundFrames, timingBoundFrames);
    }

    /// <summary>The native hardware queue cap: the buffer target, at least <see cref="MinHardwareQueueFrames"/>.</summary>
    public static int GetHardwareQueueCapacity(int targetBufferedFrames, int maxQueueFrames) =>
        Math.Clamp(Math.Max(MinHardwareQueueFrames, targetBufferedFrames), 1, Math.Max(1, maxQueueFrames));

    /// <summary>Extra pool surfaces for a full hardware queue plus the presentation hold.</summary>
    public static int GetHardwareSurfaceReserve(int queueCapacity) =>
        Math.Max(0, queueCapacity) + PresentationHeldHardwareFrames;
}
