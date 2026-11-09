namespace FatzMedia.Video.Core.Graphics.Cadence;

/// <summary>
/// The media position at each display refresh for display-locked video timing (Stage 017A
/// slice 4), filtered so the clock's read jitter cannot flip a frame between two refreshes.
/// </summary>
/// <remarks>
/// <para>
/// The raw position comes from the audio clock read a few milliseconds before the refresh and
/// projected to it, so it carries the clock's jitter (a millisecond or two). When a frame's time
/// falls close to a refresh boundary, that jitter alone moves the frame back and forth between
/// two refreshes and breaks an even cadence (3:2 becomes 3:3:1:...). An alpha-beta filter on the
/// refresh grid tracks position and rate: the jitter is averaged out, while the real drift
/// between the audio clock and the display (0.1 % for 23.976 fps on 60 Hz) is followed, so a
/// boundary is crossed once, cleanly, as <see cref="DisplayCadence"/> specifies.
/// </para>
/// <para>
/// A jump (seek, loop, pause and resume, tempo change) restarts the filter at the observed
/// position. Single-threaded: the presentation loop owns it.
/// </para>
/// </remarks>
sealed class VsyncMediaClock(long stopwatchFrequency)
{
    /// <summary>An error this large is a jump, not jitter: the filter restarts there.</summary>
    internal const double JumpThresholdSeconds = 0.040;

    /// <summary>Refreshes further apart than this (paused, stalled) restart the filter.</summary>
    internal const double MaxRefreshGapSeconds = 0.25;

    /// <summary>
    /// Refreshes closer than this (above 500 Hz) are the same refresh predicted twice: dividing
    /// clock jitter by such a short gap would wreck the rate estimate.
    /// </summary>
    internal const double MinRefreshGapSeconds = 0.002;

    const double PositionGain = 0.1;
    const double RateGain = 0.004;
    const double MinRate = 0.05;
    const double MaxRate = 8.0;

    readonly double _frequency = stopwatchFrequency > 0
        ? stopwatchFrequency
        : throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));

    bool _hasState;
    long _vsync;
    double _position;
    double _rate = 1.0;
    long _samples;

    /// <summary>Gets the filtered playback rate (media seconds per wall second).</summary>
    public double Rate => _rate;

    /// <summary>Gets how many times the filter restarted (seek, loop, pause or a large jump).</summary>
    public long Restarts { get; private set; }

    /// <summary>Forgets the state, rate included; the next update starts the filter at its observation.</summary>
    public void Reset()
    {
        _hasState = false;
        _rate = 1.0;
    }

    /// <summary>
    /// Takes the raw media position projected to the refresh at <paramref name="vsyncTimestamp"/>
    /// and returns the filtered position for that refresh. The same refresh twice returns the
    /// same answer.
    /// </summary>
    public double Update(long vsyncTimestamp, double observedPosition)
    {
        if (!double.IsFinite(observedPosition))
            return _hasState ? _position : 0;

        if (_hasState && vsyncTimestamp == _vsync)
            return _position;

        if (!_hasState)
            return Restart(vsyncTimestamp, observedPosition);

        var elapsed = (vsyncTimestamp - _vsync) / _frequency;
        if (elapsed > 0 && elapsed < MinRefreshGapSeconds)
            return _position;

        if (elapsed <= 0 || elapsed > MaxRefreshGapSeconds)
            return Restart(vsyncTimestamp, observedPosition);

        var predicted = _position + (_rate * elapsed);
        var error = observedPosition - predicted;
        if (Math.Abs(error) > JumpThresholdSeconds)
            return Restart(vsyncTimestamp, observedPosition);

        // Right after a restart the gains are those of a least-squares line through the samples
        // so far, so a new rate (after a tempo change) is found within a few refreshes; they then
        // settle to the steady gains that average the jitter out.
        _samples++;
        var k = (double)_samples;
        var positionGain = Math.Max(PositionGain, 2 * ((2 * k) - 1) / (k * (k + 1)));
        var rateGain = Math.Max(RateGain, 6 / (k * (k + 1)));

        _vsync = vsyncTimestamp;
        _position = predicted + (positionGain * error);
        _rate = Math.Clamp(_rate + (rateGain * error / elapsed), MinRate, MaxRate);
        return _position;
    }

    double Restart(long vsyncTimestamp, double observedPosition)
    {
        if (_hasState)
            Restarts++;

        // The previous rate is the best guess (seek, loop, resume); a new tempo is measured by
        // the warm-up gains. A restart straight after a restart means that guess was wrong (a
        // rate far off would otherwise restart the filter on every refresh): start from 1.
        if (_hasState && _samples <= 1)
            _rate = 1.0;

        _hasState = true;
        _samples = 1;
        _vsync = vsyncTimestamp;
        _position = observedPosition;
        return _position;
    }
}
