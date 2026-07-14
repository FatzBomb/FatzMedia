namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// Shared decoder-level primitive for seamless video looping. Both the background video loop
/// (PipelineMediaEngine.BackgroundVideoLoop) and the primary video pipeline
/// (VideoPipeline.TryPreSeekLoopStart) use the same strategy: when the decoder reaches
/// end-of-stream with tail frames still queued, seek the FFmpeg read cursor back to 0 while
/// preserving the queue, then decode a few packets inline so position-0 frames are ready
/// behind the tail frames before the tail is exhausted.
///
/// The invariant this enforces - "seek the decoder, never the output" - is what makes the
/// loop boundary invisible. The output path (composition layer / presentation clock) never
/// stops; only the decoder's input cursor wraps.
/// </summary>
internal static class SeamlessLoopRestart
{
    /// <summary>Default inline top-up batch size. Small: the goal is to prime the queue, not drain a decode budget on the presentation/loop thread.</summary>
    public const int DefaultTopUpBatchSize = 8;

    /// <summary>
    /// Pre-seek the decoder to the start when it has reached EOF but still has queued tail
    /// frames. preserveQueue keeps those tail frames so they present first while the newly
    /// decoded position-0 frames flow in behind them - eliminating the visual gap at the loop
    /// boundary. No-op (returns false) unless the decoder is at EOF with a non-empty queue.
    /// </summary>
    public static bool TryPreSeekToStart(VideoDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        return decoder.EndOfStream && decoder.FrameCount > 0
            && decoder.Seek(0, exact: false, preserveQueue: true);
    }

    /// <summary>
    /// Inline top-up decode: decode packets until the queue grows by at least one frame,
    /// reaches <paramref name="frameThreshold"/>, hits EOF, or the batch budget is spent -
    /// whichever comes first. Stopping the moment a frame is produced avoids stalling slow
    /// software decoders (e.g. AV1 at ~30ms/packet) by decoding a fixed batch synchronously.
    /// </summary>
    public static void TopUpDecode(VideoDecoder decoder, int batchSize, int frameThreshold)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        var framesBefore = decoder.FrameCount;
        for (var i = 0; i < batchSize; i++)
        {
            if (ShouldStopTopUp(decoder.FrameCount, framesBefore, frameThreshold, decoder.EndOfStream))
                break;

            _ = decoder.DecodeNextFrame();
        }
    }

    /// <summary>
    /// Pure stop predicate for <see cref="TopUpDecode"/>. Stop when the decoder is at EOF, the
    /// queue has reached the threshold, or the queue has already grown past where it started
    /// (a new frame emerged - the queue is primed).
    /// </summary>
    public static bool ShouldStopTopUp(int currentFrameCount, int framesBefore, int frameThreshold, bool endOfStream) =>
        endOfStream || currentFrameCount >= frameThreshold || currentFrameCount > framesBefore;

    /// <summary>
    /// The top-up target: fill to just under a ceiling (leaving 2 frames of headroom so the
    /// decoder can keep emitting), never below 2.
    /// </summary>
    public static int GetTopUpThreshold(int ceilingFrames) => Math.Max(2, ceilingFrames - 2);
}
