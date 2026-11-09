#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Immutable, sorted fade envelope of exact position/gain points with linear interpolation.
/// </summary>
/// <remarks>
/// Points are authoritative: a point at an existing position replaces it (last write wins),
/// including the implicit default end points, and no epsilon merging is applied. Every change
/// produces a new snapshot; an envelope in use is never mutated.
/// </remarks>
public sealed class AudioFadeEnvelope
{
    readonly AudioFadePoint[] _points;

    AudioFadeEnvelope(AudioFadePoint[] sortedPoints)
    {
        _points = sortedPoints;
        Points = Array.AsReadOnly(sortedPoints);
        AudibleStart = FindAudibleStart(sortedPoints);
    }

    /// <summary>Gets the default outgoing envelope: full gain at 0, muted at 1.</summary>
    public static AudioFadeEnvelope Outgoing { get; } = new([new(0, 1), new(1, 0)]);

    /// <summary>Gets the default incoming envelope: muted at 0, full gain at 1.</summary>
    public static AudioFadeEnvelope Incoming { get; } = new([new(0, 0), new(1, 1)]);

    /// <summary>Gets the sorted envelope points.</summary>
    public IReadOnlyList<AudioFadePoint> Points { get; }

    /// <summary>
    /// Gets the position where the envelope leaves its leading zero plateau. Incoming playback
    /// starts here so muted lead time does not consume source audio. <c>0</c> when the envelope
    /// starts audible; <c>1</c> when it never becomes audible.
    /// </summary>
    public double AudibleStart { get; }

    /// <summary>Creates an outgoing envelope by upserting points over the outgoing defaults.</summary>
    public static AudioFadeEnvelope CreateOutgoing(IEnumerable<AudioFadePoint> points) => Outgoing.WithPoints(points);

    /// <summary>Creates an incoming envelope by upserting points over the incoming defaults.</summary>
    public static AudioFadeEnvelope CreateIncoming(IEnumerable<AudioFadePoint> points) => Incoming.WithPoints(points);

    /// <summary>Returns a new envelope with <paramref name="point"/> upserted.</summary>
    public AudioFadeEnvelope WithPoint(AudioFadePoint point) => WithPoints([point]);

    /// <summary>Returns a new envelope with every point upserted in order (last write wins).</summary>
    public AudioFadeEnvelope WithPoints(IEnumerable<AudioFadePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var byPosition = new SortedDictionary<double, AudioFadePoint>();
        foreach (var existing in _points)
            byPosition[existing.Position] = existing;
        foreach (var point in points)
            byPosition[point.Position] = point;

        return new AudioFadeEnvelope([.. byPosition.Values]);
    }

    /// <summary>
    /// Evaluates the envelope at a normalized position (clamped to <c>0..1</c>). Positions before
    /// the first point or after the last point hold that point's gain.
    /// </summary>
    public double Evaluate(double position)
    {
        if (!double.IsFinite(position))
            position = 0;
        position = Math.Clamp(position, 0, 1);

        if (position <= _points[0].Position)
            return _points[0].Gain;

        for (var i = 1; i < _points.Length; i++)
        {
            var right = _points[i];
            if (position > right.Position)
                continue;

            var left = _points[i - 1];
            var span = right.Position - left.Position;
            var t = (position - left.Position) / span;
            return left.Gain + ((right.Gain - left.Gain) * t);
        }

        return _points[^1].Gain;
    }

    static double FindAudibleStart(AudioFadePoint[] points)
    {
        if (points[0].Gain > 0)
            return 0;

        // Gain before the first point holds the first point's gain, so a zero first point opens
        // the plateau at 0 and every following zero point extends it.
        var plateauEnd = 0d;
        foreach (var point in points)
        {
            if (point.Gain > 0)
                return plateauEnd;

            plateauEnd = point.Position;
        }

        return 1;
    }
}
