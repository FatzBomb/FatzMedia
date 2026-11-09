namespace FatzMedia.Video.Core.Audio.Fade;

/// <summary>
/// One linear native ramp of a fade: reach <see cref="ToGain"/> over <see cref="Duration"/>.
/// </summary>
readonly record struct AudioFadeSegment(TimeSpan StartOffset, TimeSpan Duration, double FromGain, double ToGain);

/// <summary>
/// Converts an envelope into chained linear ramps and composes the final gain.
/// </summary>
static class AudioFadeSchedule
{
    /// <summary>
    /// Builds the ramps from <paramref name="startPosition"/> to the end of the envelope. Offsets
    /// are relative to the start position, so an incoming fade that starts at its audible start
    /// does not spend wall-clock time inside the muted plateau.
    /// </summary>
    public static IReadOnlyList<AudioFadeSegment> Build(AudioFadeEnvelope envelope, TimeSpan duration, double startPosition = 0)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (duration <= TimeSpan.Zero)
            return [];

        startPosition = double.IsFinite(startPosition) ? Math.Clamp(startPosition, 0, 1) : 0;
        var segments = new List<AudioFadeSegment>();
        var cursor = startPosition;
        var cursorGain = envelope.Evaluate(cursor);
        foreach (var point in envelope.Points)
        {
            if (point.Position <= cursor)
                continue;

            segments.Add(new AudioFadeSegment(
                duration * (cursor - startPosition),
                duration * (point.Position - cursor),
                cursorGain,
                point.Gain));
            cursor = point.Position;
            cursorGain = point.Gain;
        }

        if (cursor < 1)
        {
            // Hold the final gain for the remaining fade time so total duration is exact.
            segments.Add(new AudioFadeSegment(duration * (cursor - startPosition), duration * (1 - cursor), cursorGain, cursorGain));
        }

        return segments;
    }

    /// <summary>
    /// Final output gain: envelope gain x user volume x normalization gain. Each factor is an
    /// independent stage, so a fade never fights the volume or normalization model.
    /// </summary>
    public static double ComposeGain(double envelopeGain, double userVolume, double normalizationGain)
    {
        static double Clean(double value, double max) => double.IsFinite(value) ? Math.Clamp(value, 0, max) : 0;
        return Clean(envelopeGain, 1) * Clean(userVolume, 1) * Clean(normalizationGain, 8);
    }
}
