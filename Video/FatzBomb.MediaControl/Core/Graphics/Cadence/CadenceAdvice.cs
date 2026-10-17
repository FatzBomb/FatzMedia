using System.Globalization;

namespace FatzBomb.MediaControl.Core.Graphics.Cadence;

/// <summary>
/// Turns a video frame rate and a display refresh rate into a one-line, host-readable cadence
/// report (Stage 017A slice 2): whether every frame stays on screen for the same number of
/// refreshes (smooth), alternates (judder, as in 3:2 pulldown), or is skipped because the
/// display is slower than the video, plus the common refresh rates that would play it evenly.
/// </summary>
internal static class CadenceAdvice
{
    // Decoders and displays report NTSC-family rates as doubles; snapping them to their exact
    // fractions lets 23.976 fps on 59.94 Hz read as the exact 3:2 match it is.
    private const double SnapToleranceHz = 0.01;

    // One minute of refreshes shows the slow drift patterns (29.97 fps on 60 Hz holds one frame
    // longer about every 16.7 s).
    private const double AnalysisSeconds = 60;

    // A hold length used by fewer than this share of frames is described as occasional.
    private const double OccasionalHoldShare = 0.1;

    private static readonly FrameRate[] FractionalRates =
    [
        new(24000, 1001), new(30000, 1001), new(48000, 1001), new(60000, 1001), new(120000, 1001)
    ];

    private static readonly FrameRate[] CommonDisplayRates =
    [
        new(24000, 1001), new(24, 1), new(25, 1), new(30000, 1001), new(30, 1), new(48000, 1001), new(48, 1), new(50, 1),
        new(60000, 1001), new(60, 1), new(72, 1), new(75, 1), new(100, 1), new(120000, 1001), new(120, 1),
        new(144, 1), new(240, 1)
    ];

    /// <summary>
    /// Converts a measured rate to an exact one: an NTSC fraction or a whole number when within
    /// 0.01 Hz, otherwise thousandths. Null when not a plausible rate.
    /// </summary>
    public static FrameRate? ToFrameRate(double hertz)
    {
        if (!double.IsFinite(hertz) || hertz < 1 || hertz > 1000)
            return null;

        foreach (FrameRate rate in FractionalRates)
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
        if (ToFrameRate(sourceFps) is not { } source || ToFrameRate(displayHz) is not { } display)
            return null;

        CadenceReport report = DisplayCadence.Analyze(source, display, (long)Math.Ceiling(display.Value * AnalysisSeconds));
        var head = $"Video {Format(source)} fps on a {Format(display)} Hz display: ";
        if (report.DroppedFrames > 0)
        {
            var shown = report.DistinctFramesShown + report.DroppedFrames;
            var skippedPercent = 100.0 * report.DroppedFrames / Math.Max(1, shown);
            return head + $"the display refreshes less often than the video changes frame, so about {skippedPercent.ToString("0", CultureInfo.InvariantCulture)}% of frames are never shown."
                + SuggestSmoothRates(source);
        }

        if (report.IsJudderFree)
        {
            return head + (report.MinimumHold == 1
                ? "each frame is shown for one refresh, so motion is smooth."
                : $"each frame is shown for {report.MinimumHold} refreshes, so motion is smooth.");
        }

        return head + DescribeUnevenHolds(report) + " Motion can look uneven (judder) on slow pans." + SuggestSmoothRates(source);
    }

    private static string DescribeUnevenHolds(CadenceReport report)
    {
        // With no dropped frames, the mean hold is exactly display rate / source rate.
        var meanHold = report.Display.Value / report.Source.Value;
        if (report.MaximumHold - report.MinimumHold == 1)
        {
            // The share of frames held for the maximum is the mean's fractional part.
            var longShare = meanHold - report.MinimumHold;
            var (usual, occasional, occasionalShare) = longShare < 0.5
                ? (report.MinimumHold, report.MaximumHold, longShare)
                : (report.MaximumHold, report.MinimumHold, 1 - longShare);
            if (occasionalShare < OccasionalHoldShare)
            {
                var everySeconds = 1 / (occasionalShare * report.Source.Value);
                return $"frames are shown for {usual} refreshes, but about every {everySeconds.ToString("0.#", CultureInfo.InvariantCulture)} s one is shown for {occasional}.";
            }

            // 3:2 pulldown alternates evenly; 23.976 fps on 60 Hz breaks a few pairs a minute.
            if (report.MinimumHold == 2 && Math.Abs(meanHold - 2.5) < 0.01)
                return "frames are shown for 3 and 2 refreshes in turn (3:2 pulldown).";
        }

        return $"frames are shown for between {report.MinimumHold} and {report.MaximumHold} refreshes.";
    }

    private static string SuggestSmoothRates(FrameRate source)
    {
        var smooth = CommonDisplayRates.Where(display => IsWholeMultiple(display, source)).Select(Format).ToArray();
        return smooth.Length == 0 ? string.Empty : $" Smooth display rates for this video: {string.Join(", ", smooth)} Hz.";
    }

    // display / source is a whole number: displayN * sourceD divisible by displayD * sourceN.
    private static bool IsWholeMultiple(FrameRate display, FrameRate source)
    {
        var numerator = (Int128)display.Numerator * source.Denominator;
        var denominator = (Int128)display.Denominator * source.Numerator;
        return denominator != 0 && numerator >= denominator && numerator % denominator == 0;
    }

    private static string Format(FrameRate rate) => rate.Denominator == 1
        ? rate.Numerator.ToString(CultureInfo.InvariantCulture)
        : rate.Value.ToString("0.###", CultureInfo.InvariantCulture);
}
