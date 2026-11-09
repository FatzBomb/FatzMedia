using System.Globalization;

namespace FatzMedia.Video.Core.Graphics.Cadence;

/// <summary>Frame-rate expressed exactly as a rational (e.g. 24000/1001 for 23.976).</summary>
internal readonly record struct FrameRate(long Numerator, long Denominator)
{
    public static FrameRate Fps23_976 { get; } = new(24000, 1001);
    public static FrameRate Fps29_97 { get; } = new(30000, 1001);
    public static FrameRate Hz59_94 { get; } = new(60000, 1001);

    public double Value => (double)Numerator / Denominator;

    public static FrameRate FromInteger(int framesPerSecond) => new(framesPerSecond, 1);

    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : Value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>What one display refresh shows.</summary>
internal readonly record struct CadenceSlot(long VsyncIndex, long SourceFrame, bool IsRepeat, int SkippedFrames);

/// <summary>Summary of a simulated cadence window.</summary>
internal sealed record CadenceReport(
    FrameRate Source,
    FrameRate Display,
    long Vsyncs,
    long DistinctFramesShown,
    long Repeats,
    long DroppedFrames,
    IReadOnlyList<int> HoldPattern,
    int MinimumHold,
    int MaximumHold)
{
    /// <summary>Gets whether every source frame is held for the same number of refreshes.</summary>
    public bool IsJudderFree => MinimumHold == MaximumHold && DroppedFrames == 0;
}

/// <summary>
/// Deterministic display-cadence model (Stage 017A). At each display refresh the shown source
/// frame is the latest frame whose presentation time has been reached on the media clock:
/// <c>frame = floor((vsync * displayPeriod + phase) * sourceRate)</c>. Source frames whose
/// whole display window falls between two refreshes are dropped; frames shown on more than
/// one refresh are repeats. Exact rational arithmetic keeps long runs (drift between 23.976
/// and 24 Hz, 29.97 and 30 Hz) free of floating-point error. Each presentation target gets
/// its own cadence; the media clock stays authoritative.
/// </summary>
internal static class DisplayCadence
{
    /// <summary>
    /// Simulates <paramref name="vsyncs"/> display refreshes. <paramref name="phaseNumerator"/>
    /// over <paramref name="phaseDenominator"/> seconds offsets the media clock against vsync.
    /// </summary>
    public static IEnumerable<CadenceSlot> Simulate(FrameRate source, FrameRate display, long vsyncs, long phaseNumerator = 0, long phaseDenominator = 1)
    {
        Validate(source, display);
        ArgumentOutOfRangeException.ThrowIfNegative(vsyncs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(phaseDenominator);

        long previous = -1;
        for (long vsync = 0; vsync < vsyncs; vsync++)
        {
            var frame = FrameAt(source, display, vsync, phaseNumerator, phaseDenominator);
            var skipped = previous < 0 ? 0 : (int)Math.Max(0, frame - previous - 1);
            yield return new CadenceSlot(vsync, frame, frame == previous, skipped);
            previous = frame;
        }
    }

    /// <summary>Simulates and summarizes; the hold pattern lists refreshes per distinct frame.</summary>
    public static CadenceReport Analyze(FrameRate source, FrameRate display, long vsyncs, long phaseNumerator = 0, long phaseDenominator = 1)
    {
        var holds = new List<int>();
        long repeats = 0;
        long dropped = 0;
        long distinct = 0;
        var currentHold = 0;
        foreach (CadenceSlot slot in Simulate(source, display, vsyncs, phaseNumerator, phaseDenominator))
        {
            dropped += slot.SkippedFrames;
            if (slot.IsRepeat)
            {
                repeats++;
                currentHold++;
                continue;
            }

            if (currentHold > 0)
                holds.Add(currentHold);
            distinct++;
            currentHold = 1;
        }

        // The last frame's hold is truncated by the window; it is not part of the pattern.
        var inner = holds.Count > 1 ? holds.Skip(1).ToArray() : holds.ToArray();
        return new CadenceReport(
            source,
            display,
            vsyncs,
            distinct,
            repeats,
            dropped,
            inner,
            inner.Length == 0 ? 0 : inner.Min(),
            inner.Length == 0 ? 0 : inner.Max());
    }

    /// <summary>Source frame visible at a refresh, using exact integer arithmetic.</summary>
    public static long FrameAt(FrameRate source, FrameRate display, long vsync, long phaseNumerator = 0, long phaseDenominator = 1)
    {
        // t = vsync * dD/dN + pN/pD ; frame = floor(t * sN/sD)
        Int128 numerator = ((Int128)vsync * display.Denominator * phaseDenominator + (Int128)phaseNumerator * display.Numerator) * source.Numerator;
        Int128 denominator = (Int128)display.Numerator * phaseDenominator * source.Denominator;
        Int128 quotient = Int128.DivRem(numerator, denominator).Quotient;
        if (numerator < 0 && quotient * denominator != numerator)
            quotient--;
        return (long)quotient;
    }

    private static void Validate(FrameRate source, FrameRate display)
    {
        if (source.Numerator <= 0 || source.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(source));
        if (display.Numerator <= 0 || display.Denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(display));
    }
}
