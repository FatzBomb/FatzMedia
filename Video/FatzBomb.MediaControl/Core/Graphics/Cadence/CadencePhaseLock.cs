namespace FatzBomb.MediaControl.Core.Graphics.Cadence;

/// <summary>
/// Hysteresis for display-locked selection (Stage 017A slice 4): keeps frame boundaries away from
/// refreshes so the cadence breaks once per drift crossing, not several times.
/// </summary>
/// <remarks>
/// <para>
/// When the frame rate and refresh rate are close to a simple ratio (2.5 refreshes per frame for
/// 24 fps on 60 Hz, 2 for 30 on 60, 5 for 24 on 120), frames start at only a few distinct places
/// between refreshes: <c>q</c> phase families, <c>P/q</c> apart for refresh period <c>P</c>. The
/// drift between the audio clock and the display (0.1 % for 23.976 fps on 60 Hz) moves those
/// boundaries slowly across a refresh, and while one sits within the filtered clock's remaining
/// noise, frames still move back and forth between two refreshes.
/// </para>
/// <para>
/// Each time a frame starts, its margin (how long it had been due at that refresh) is checked
/// against its family's spacing. A boundary closer than a quarter of the spacing moves the
/// selection phase by that quarter, away from the refresh, which changes no hold. The offset thus
/// follows the drift in small steps; when it passes half a spacing it wraps by one spacing, which
/// is the one cadence break the drift requires. The offset stays within half a spacing (a quarter
/// of a refresh for 2.5), so A/V timing moves by at most that much.
/// </para>
/// <para>
/// Uneven ratios (25 fps on 60 Hz) have many families, all of them close to some refresh; there is
/// no boundary to protect, so the lock stays out of the way. Single-threaded.
/// </para>
/// </remarks>
internal sealed class CadencePhaseLock
{
    private const int MaxFamilies = 4;
    private const double RatioTolerance = 0.02;

    private double _spacingSeconds;
    private double _periodSeconds;
    private double _framesPerSecond;

    /// <summary>Gets the offset added to the selection time, in seconds.</summary>
    public double Offset { get; private set; }

    /// <summary>Gets how many phase families the current ratio has, or 0 when the lock is off.</summary>
    public int Families { get; private set; }

    /// <summary>Gets how many times the offset wrapped: the cadence breaks the drift made.</summary>
    public long Wraps { get; private set; }

    /// <summary>Clears the offset (after a seek or a clock restart).</summary>
    public void Reset() => Offset = 0;

    /// <summary>
    /// Sets the refresh period and frame rate; a real change clears the offset. Rates whose ratio
    /// is not near a fraction with a denominator up to 4 turn the lock off.
    /// </summary>
    /// <remarks>
    /// The measured refresh period moves by a few parts per million on every blank; only a change
    /// above 0.1 % (another refresh rate, another video) counts, or the offset would be cleared on
    /// every refresh and the hysteresis would never act.
    /// </remarks>
    public void Configure(double periodSeconds, double framesPerSecond)
    {
        if (IsClose(periodSeconds, _periodSeconds) && IsClose(framesPerSecond, _framesPerSecond))
            return;

        _periodSeconds = periodSeconds;
        _framesPerSecond = framesPerSecond;
        Offset = 0;
        Families = 0;
        _spacingSeconds = 0;
        if (!(periodSeconds > 0) || !(framesPerSecond > 0))
            return;

        var refreshesPerFrame = 1.0 / (periodSeconds * framesPerSecond);
        for (var families = 1; families <= MaxFamilies; families++)
        {
            var scaled = refreshesPerFrame * families;
            if (Math.Round(scaled) >= 1 && Math.Abs(scaled - Math.Round(scaled)) < RatioTolerance)
            {
                Families = families;
                _spacingSeconds = periodSeconds / families;
                return;
            }
        }
    }

    private static bool IsClose(double value, double reference) =>
        reference > 0 && Math.Abs(value - reference) <= reference * 0.001;

    /// <summary>
    /// A new frame started showing at a refresh whose selection time (offset included) was
    /// <paramref name="selectionTime"/>; <paramref name="framePresentationTime"/> is its timestamp.
    /// </summary>
    public void OnFrameStarted(double selectionTime, double framePresentationTime)
    {
        if (Families == 0)
            return;

        var margin = selectionTime - framePresentationTime;
        if (!(margin >= 0) || margin >= _periodSeconds)
            return; // a late or skipped frame: no information about the phase

        var guard = _spacingSeconds / 4;
        var withinFamily = margin % _spacingSeconds;
        if (withinFamily < guard)
            Offset += guard;   // the boundary is just behind this refresh: frames come due earlier
        else if (withinFamily > _spacingSeconds - guard)
            Offset -= guard;   // just ahead of it: later
        else
            return;

        var half = _spacingSeconds / 2;
        if (Offset > half)
        {
            Offset -= _spacingSeconds;
            Wraps++;
        }
        else if (Offset <= -half)
        {
            Offset += _spacingSeconds;
            Wraps++;
        }
    }
}
