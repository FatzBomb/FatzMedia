namespace FatzMedia.Video.Core.Network;

/// <summary>
/// Buffering state for sources read through FFmpeg (Stage 039A). A reader stalled for at least the
/// stall threshold while the fill is below the exit fill enters buffering; a free reader with the
/// fill back at the exit fill (or the end of the stream) leaves it. Progress is reported every 10
/// points while buffering, as <see cref="BufferingStateTracker"/> does.
/// <para>
/// Network video (slice 2) samples its read-ahead: the stall is how long the picture or sound has
/// found its packet queue empty (the buffer ran dry), and the fill is the progress towards the
/// resume threshold (<see cref="NetworkReadAheadPolicy.ResumeDuration"/>), with an exit fill of 1.
/// The Audio-mode soundtrack does the same. Without a read-ahead, the stall is the blocked read and
/// the fill the decoded queue against its target, with the default exit fill.
/// </para>
/// </summary>
sealed class NetworkStallBufferingTracker(TimeSpan stallThreshold, double exitFill = NetworkStallBufferingTracker.DefaultExitFill)
{
    /// <summary>The default stall threshold: far longer than a healthy read, short enough to report a visible stall.</summary>
    public static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromMilliseconds(500);

    /// <summary>The default exit fill: a quarter of the queue target.</summary>
    public const double DefaultExitFill = 0.25;

    readonly TimeSpan _stallThreshold = stallThreshold > TimeSpan.Zero ? stallThreshold : DefaultStallThreshold;
    readonly double _exitFill = Math.Clamp(exitFill, 0, 1);
    double _lastReportedFill = -1;

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

    /// <summary>
    /// Ends a buffering spell in progress because playback ended (ENG-034). Returns
    /// <see langword="true"/> when it was buffering, with the fill to report (1).
    /// </summary>
    public bool EndSpell(out double fill)
    {
        fill = 1;
        if (!IsBuffering)
            return false;

        IsBuffering = false;
        _lastReportedFill = fill;
        return true;
    }

    public void Reset()
    {
        IsBuffering = false;
        _lastReportedFill = -1;
    }
}
