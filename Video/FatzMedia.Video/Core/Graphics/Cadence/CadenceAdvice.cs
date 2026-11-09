using System.Globalization;

namespace FatzMedia.Video.Core.Graphics.Cadence;

/// <summary>
/// Turns a video frame rate and a display refresh rate into a one-line, host-readable cadence
/// report (Stage 017A slice 2): whether every frame stays on screen for the same number of
/// refreshes (smooth), alternates (judder, as in 3:2 pulldown), or is skipped because the
/// display is slower than the video, plus the common refresh rates that would play it evenly.
/// </summary>
/// <remarks>
/// Works from the exact ratio display / source, which is what <see cref="DisplayCadence"/>
/// produces over an unbounded run: holds are the ratio's whole part, and its fractional part
/// is the share of frames held one refresh longer. This also covers near-matches whose drift
/// takes minutes (60 fps on a 60.015 Hz display).
/// </remarks>
static class CadenceAdvice
{
    // Decoders and displays report NTSC-family rates as doubles; snapping them to their exact
    // fractions lets 23.976 fps on 59.94 Hz read as the exact 3:2 match it is.
    const double SnapToleranceHz = 0.01;

    // Above these, a "frame rate" is more likely a container timebase than a real cadence.
    const double MaximumSourceFps = 300;
    const double MaximumDisplayHz = 500;

    // A hold length used by fewer than this share of frames is described as occasional, as
    // are skipped frames below this share.
    const double OccasionalShare = 0.1;

    static readonly FrameRate[] FractionalRates =
    [
        new(24000, 1001), new(30000, 1001), new(48000, 1001), new(60000, 1001), new(120000, 1001)
    ];

    static readonly FrameRate[] CommonDisplayRates =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001), new(30, 1), new(48000, 1001), new(48, 1), new(50, 1),
        new(60000, 1001), new(60, 1), new(72, 1), new(75, 1), new(100, 1), new(120000, 1001), new(120, 1),
        new(144, 1), new(240, 1)
    ];

    /// <summary>
    /// Converts a measured rate to an exact one: an NTSC fraction or a whole number when within
    /// 0.01 Hz, otherwise thousandths. Null when not a plausible rate.
    /// </summary>
    public static FrameRate? ToFrameRate(double hertz, double maximumHertz = MaximumDisplayHz)
    {
        if (!double.IsFinite(hertz) || hertz < 1 || hertz > maximumHertz)
            return null;

        foreach (var rate in FractionalRates)
        {
            if (Math.Abs(rate.Value - hertz) <= SnapToleranceHz)
                return rate;
        }

        var whole = Math.Round(hertz);
        return Math.Abs(whole - hertz) <= SnapToleranceHz
            ? FrameRate.FromInteger((int)whole)
            : new FrameRate((long)Math.Round(hertz * 1000), 1000);
    }

    /// <summary>Describes how <paramref name="sourceFps"/> plays on a <paramref name="displayHz"/> display, or null when either is unknown.</summary>
    public static string? Describe(double sourceFps, double displayHz)
    {
        if (ToFrameRate(sourceFps, MaximumSourceFps) is not { } source || ToFrameRate(displayHz) is not { } display)
            return null;

        var head = $"Video {source} fps on a {display} Hz display: ";

        // display / source as an exact fraction: whole holds plus a remainder.
        var numerator = (Int128)display.Numerator * source.Denominator;
        var denominator = (Int128)display.Denominator * source.Numerator;
        var hold = (int)(numerator / denominator);
        var longShare = (double)(numerator % denominator) / (double)denominator;
        if (hold == 0)
            return head + DescribeSkippedFrames(source, display) + SuggestSmoothRates(source);

        if (longShare == 0)
            return head + $"each frame is shown for {Refreshes(hold)}, so motion is smooth.";

        return head + DescribeUnevenHolds(source, hold, longShare) + " Motion can look uneven (judder) on slow pans." + SuggestSmoothRates(source);
    }

    static string DescribeSkippedFrames(FrameRate source, FrameRate display)
    {
        var skippedShare = 1 - (display.Value / source.Value);
        if (skippedShare < OccasionalShare)
        {
            var everySeconds = 1 / (source.Value - display.Value);
            return $"the display refreshes slightly less often than the video changes frame, so about every {Seconds(everySeconds)} one frame is never shown.";
        }

        return $"the display refreshes less often than the video changes frame, so about {(100 * skippedShare).ToString("0", CultureInfo.InvariantCulture)}% of frames are never shown.";
    }

    static string DescribeUnevenHolds(FrameRate source, int hold, double longShare)
    {
        var (usual, occasional, occasionalShare) = longShare < 0.5
            ? (hold, hold + 1, longShare)
            : (hold + 1, hold, 1 - longShare);
        if (occasionalShare < OccasionalShare)
        {
            var everySeconds = 1 / (occasionalShare * source.Value);
            return $"frames are shown for {Refreshes(usual)}, but about every {Seconds(everySeconds)} one is shown for {occasional}.";
        }

        // 3:2 pulldown alternates evenly; 23.976 fps on 60 Hz breaks a few pairs a minute.
        return hold == 2 && Math.Abs(longShare - 0.5) < 0.01
            ? "frames are shown for 3 and 2 refreshes in turn (3:2 pulldown)."
            : $"frames are shown for {hold} or {hold + 1} refreshes.";
    }

    static string SuggestSmoothRates(FrameRate source)
    {
        var smooth = CommonDisplayRates.Where(display => IsWholeMultiple(display, source)).Select(rate => rate.ToString()).ToArray();
        return smooth.Length == 0 ? string.Empty : $" Smooth display rates for this video: {string.Join(", ", smooth)} Hz.";
    }

    // display / source is a whole number: displayN * sourceD divisible by displayD * sourceN.
    static bool IsWholeMultiple(FrameRate display, FrameRate source)
    {
        var numerator = (Int128)display.Numerator * source.Denominator;
        var denominator = (Int128)display.Denominator * source.Numerator;
        return numerator >= denominator && numerator % denominator == 0;
    }

    static string Refreshes(int count) => count == 1 ? "one refresh" : $"{count} refreshes";

    static string Seconds(double seconds) => seconds >= 120
        ? $"{(seconds / 60).ToString("0", CultureInfo.InvariantCulture)} min"
        : $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)} s";
}
