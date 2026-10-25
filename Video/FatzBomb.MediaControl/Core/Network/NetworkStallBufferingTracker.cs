namespace FatzBomb.MediaControl.Core.Network;

/// <summary>
/// Buffering state for sources read through FFmpeg (Stage 039A). FFmpeg exposes no download
/// buffer to sample, so buffering is detected from the reader: a network read blocked for at
/// least the stall threshold while the decoded queue is below the exit fill enters buffering;
/// a free reader with the queue back at the exit fill (or the end of the stream) leaves it.
/// Progress is reported every 10 points while buffering, as <see cref="BufferingStateTracker"/> does.
/// </summary>
internal sealed class NetworkStallBufferingTracker(TimeSpan stallThreshold, double exitFill = 0.25)
{
    /// <summary>The default stall threshold: far longer than a healthy read, short enough to report a visible stall.</summary>
    public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _stallThreshold = stallThreshold > TimeSpan.Zero ? stallThreshold : DefaultStallThreshold;
    private readonly double _exitFill = Math.Clamp(exitFill, 0, 1);
    private double _lastReportedFill = -1;

    public bool IsBuffering { get; private set; }

    /// <summary>
    /// Feeds one sample. Returns <see langword="true"/> when the buffering flag changed or the fill
    /// moved by at least 10 percentage points while buffering.
    /// </summary>
    /// <param name="readStall">How long the current network read has been blocked (zero when no read is in progress).</param>
    /// <param name="queueFill">Decoded data queued ahead of playback, <c>0..1</c> of its target.</param>
    /// <param name="endOfStream">Whether the reader reached the end of the stream.</param>
    /// <param name="fill">The fill to report.</param>
    public bool Update(TimeSpan readStall, double queueFill, bool endOfStream, out double fill)
    {
        fill = endOfStream ? 1 : double.IsFinite(queueFill) ? Math.Clamp(queueFill, 0, 1) : 0;
        var stalled = !endOfStream && readStall >= _stallThreshold;

        var wasBuffering = IsBuffering;
        if (endOfStream)
            IsBuffering = false;
        else if (!IsBuffering && stalled && fill < _exitFill)
            IsBuffering = true;
        else if (IsBuffering && !stalled && fill >= _exitFill)
            IsBuffering = false;

        var changed = wasBuffering != IsBuffering
            || (IsBuffering && Math.Abs(fill - _lastReportedFill) >= 0.1);
        if (changed)
            _lastReportedFill = fill;

        return changed;
    }

    public void Reset()
    {
        IsBuffering = false;
        _lastReportedFill = -1;
    }
}
