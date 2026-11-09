namespace FatzMedia.Video.Core.Network;

/// <summary>
/// Turns sampled network buffer fill into buffering transitions with hysteresis so playback
/// near the threshold does not flap.
/// </summary>
internal sealed class BufferingStateTracker(double enterThreshold = 0.05, double exitThreshold = 0.25)
{
    private readonly double _enterThreshold = Math.Clamp(enterThreshold, 0, 1);
    private readonly double _exitThreshold = Math.Clamp(Math.Max(enterThreshold, exitThreshold), 0, 1);
    private double _lastReportedFill = -1;

    public bool IsBuffering { get; private set; }

    /// <summary>
    /// Feeds one sample. Returns <see langword="true"/> when the buffering flag changed or the fill
    /// moved by at least 10 percentage points while buffering (progress worth reporting).
    /// </summary>
    /// <param name="bufferedBytes">Bytes downloaded ahead of the decode position.</param>
    /// <param name="targetBytes">Configured network buffer size in bytes.</param>
    /// <param name="downloadComplete">Whether the whole source has been downloaded.</param>
    public bool Update(long bufferedBytes, long targetBytes, bool downloadComplete, out double fill)
    {
        fill = downloadComplete
            ? 1
            : targetBytes > 0 ? Math.Clamp((double)Math.Max(0, bufferedBytes) / targetBytes, 0, 1) : 0;

        var wasBuffering = IsBuffering;
        if (downloadComplete)
            IsBuffering = false;
        else if (!IsBuffering && fill <= _enterThreshold)
            IsBuffering = true;
        else if (IsBuffering && fill >= _exitThreshold)
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
