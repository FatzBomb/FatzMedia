#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// One exact point of an audio fade envelope.
/// </summary>
public readonly record struct AudioFadePoint
{
    /// <param name="position">Normalized position across the fade, <c>0..1</c>.</param>
    /// <param name="gain">Envelope gain at that position, <c>0..1</c>.</param>
    public AudioFadePoint(double position, double gain)
    {
        if (!double.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position), position, "Fade positions must be finite and within 0..1.");
        if (!double.IsFinite(gain) || gain < 0 || gain > 1)
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "Fade gains must be finite and within 0..1.");

        // Normalize negative zero so exact-position upserts treat 0 and -0 as one point.
        Position = position + 0d;
        Gain = gain + 0d;
    }

    /// <summary>Gets the normalized position across the fade.</summary>
    public double Position { get; }

    /// <summary>Gets the envelope gain at <see cref="Position"/>.</summary>
    public double Gain { get; }
}
