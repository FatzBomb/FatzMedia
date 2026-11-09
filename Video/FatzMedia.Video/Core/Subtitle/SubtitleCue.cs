using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Core.Subtitle;

public sealed record SubtitleCue(int Sequence, TimeSpan Start, TimeSpan End, string Text)
{
    public KaraokeCueTiming? KaraokeTiming { get; init; }
}

public sealed record KaraokeCueTiming(
    IReadOnlyList<KaraokeLineTiming> Lines,
    int ActiveLineIndex,
    TimeSpan TransitionStart,
    TimeSpan TransitionEnd)
{
    private const int TransitionFrameRate = 60;
    private static readonly TimeSpan IntroFadeInStart = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IntroFadeDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IntroClearLead = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SplitBandEnd = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EndCardMinimumWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan EndCardFadeDuration = TimeSpan.FromMilliseconds(750);

    public IReadOnlyList<KaraokeLineTiming> OutgoingPageLines { get; init; } = [];
    public int OutgoingActiveLineIndex { get; init; }
    public bool ClearsPage { get; init; }
    public TimeSpan IncomingStart { get; init; }
    public TimeSpan TransitionComplete { get; init; }
    public TimeSpan PageFadeOutStart { get; init; }
    public TimeSpan PageFadeOutEnd { get; init; }
    public TimeSpan PageFadeInStart { get; init; }
    public TimeSpan PageFadeInEnd { get; init; }
    public KaraokeMetadata? Metadata { get; init; }
    public TimeSpan FirstLyricStart { get; init; }
    public TimeSpan FirstPresentationStart { get; init; }
    public TimeSpan MediaDuration { get; init; }
    public TimeSpan EndLyricFadeStart { get; init; }
    public TimeSpan EndLyricFadeEnd { get; init; }

    public bool UseSplitMetadataBands => FirstPresentationStart < SplitBandEnd;

    public int GetProgressRevision(TimeSpan position, TimeSpan wipeDuration)
    {
        // Called on every presented frame: index loops, since enumerating an IReadOnlyList allocates.
        var hash = new HashCode();
        AddLineProgress(ref hash, Lines, position, wipeDuration);
        AddLineProgress(ref hash, OutgoingPageLines, position, wipeDuration);
        hash.Add(ClearsPage);

        var transitionFrame = GetTransitionFrame(position);
        hash.Add(transitionFrame);
        var introOpacity = GetIntroMetadataOpacity(position);
        var endCardOpacity = GetEndMetadataOpacity(position);
        hash.Add((int)(introOpacity * 100));
        hash.Add((int)(endCardOpacity * 100));
        hash.Add((int)(GetEndLyricOpacity(position) * 100));
        hash.Add(position >= GetIntroFadeOutEnd());
        hash.Add(MediaDuration > TimeSpan.Zero && position >= MediaDuration);
        hash.Add(EndLyricFadeEnd > EndLyricFadeStart && position >= EndLyricFadeEnd);
        return hash.ToHashCode();
    }

    private static void AddLineProgress(ref HashCode hash, IReadOnlyList<KaraokeLineTiming> lines, TimeSpan position, TimeSpan wipeDuration)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            KaraokeLineTiming line = lines[i];
            hash.Add(line.GetHighlightedLength(position));
            hash.Add((int)(line.GetSyllableWipeFraction(position, wipeDuration) * 100));
            hash.Add(line.GetCountdownRevision(position));
        }
    }

    public float GetTransitionProgress(TimeSpan position)
    {
        if (TransitionComplete <= TransitionStart)
            return 1;

        TimeSpan transitionDuration = TransitionComplete - TransitionStart;
        if (transitionDuration <= TimeSpan.Zero)
            return 1;

        var progress = Math.Clamp((position - TransitionStart).TotalMilliseconds / transitionDuration.TotalMilliseconds, 0, 1);
        return (float)progress;
    }

    public float GetPageFadeOutProgress(TimeSpan position)
    {
        TimeSpan duration = PageFadeOutEnd - PageFadeOutStart;
        if (duration <= TimeSpan.Zero)
            return 1;

        var progress = Math.Clamp((position - PageFadeOutStart).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        return EaseInCubic(progress);
    }

    public float GetPageFadeInProgress(TimeSpan position, TimeSpan transitionDuration)
    {
        transitionDuration = PageFadeInEnd > PageFadeInStart
            ? PageFadeInEnd - PageFadeInStart
            : GetEffectiveIncomingTransitionDuration(transitionDuration);
        if (transitionDuration <= TimeSpan.Zero)
            return position >= PageFadeInEnd ? 1 : 0;

        var progress = Math.Clamp((position - PageFadeInStart).TotalMilliseconds / transitionDuration.TotalMilliseconds, 0, 1);
        return EaseInOutCubic(progress);
    }

    public float GetIntroMetadataOpacity(TimeSpan position)
    {
        if (Metadata == null || position < IntroFadeInStart)
            return 0;

        TimeSpan fadeOutEnd = GetIntroFadeOutEnd();
        if (position >= fadeOutEnd)
            return 0;

        TimeSpan fadeInEnd = IntroFadeInStart + IntroFadeDuration;
        if (position < fadeInEnd)
        {
            var fadeInProgress = Math.Clamp(
                (position - IntroFadeInStart).TotalMilliseconds / IntroFadeDuration.TotalMilliseconds,
                0,
                1);
            return SmoothStep(fadeInProgress);
        }

        TimeSpan fadeOutStart = fadeOutEnd - IntroFadeDuration;
        if (position <= fadeOutStart)
            return 1;

        var fadeOutProgress = Math.Clamp(
            (position - fadeOutStart).TotalMilliseconds / IntroFadeDuration.TotalMilliseconds,
            0,
            1);
        return 1 - SmoothStep(fadeOutProgress);
    }

    public float GetEndMetadataOpacity(TimeSpan position)
    {
        if (Metadata is not { IsFallback: false }
            || MediaDuration - EndLyricFadeEnd < EndCardMinimumWindow
            || position < EndLyricFadeEnd
            || position >= MediaDuration)
        {
            return 0;
        }

        TimeSpan fadeInEnd = EndLyricFadeEnd + EndCardFadeDuration;
        if (position < fadeInEnd)
        {
            var progress = Math.Clamp(
                (position - EndLyricFadeEnd).TotalMilliseconds / EndCardFadeDuration.TotalMilliseconds,
                0,
                1);
            return SmoothStep(progress);
        }

        TimeSpan fadeOutStart = MediaDuration - EndCardFadeDuration;
        if (position <= fadeOutStart)
            return 1;

        var fadeOutProgress = Math.Clamp(
            (position - fadeOutStart).TotalMilliseconds / EndCardFadeDuration.TotalMilliseconds,
            0,
            1);
        return 1 - SmoothStep(fadeOutProgress);
    }

    public float GetEndLyricOpacity(TimeSpan position)
    {
        if (EndLyricFadeEnd <= EndLyricFadeStart)
            return 1;
        if (position < EndLyricFadeStart)
            return 1;
        if (position >= EndLyricFadeEnd)
            return 0;
        var progress = Math.Clamp((position - EndLyricFadeStart).TotalMilliseconds / (EndLyricFadeEnd - EndLyricFadeStart).TotalMilliseconds, 0, 1);
        return 1 - SmoothStep(progress);
    }

    private TimeSpan GetEffectiveIncomingTransitionDuration(TimeSpan requestedDuration)
    {
        TimeSpan availableDuration = TransitionEnd - IncomingStart;
        return availableDuration > TimeSpan.Zero && availableDuration < requestedDuration
            ? availableDuration
            : requestedDuration;
    }

    private TimeSpan GetIntroFadeOutEnd() => UseSplitMetadataBands
        ? Min(SplitBandEnd, MediaDuration)
        : Max(IntroFadeInStart + IntroFadeDuration, FirstPresentationStart - IntroClearLead);

    private int GetTransitionFrame(TimeSpan position)
    {
        if (ClearsPage)
        {
            if (position >= PageFadeOutStart && position < PageFadeOutEnd)
            {
                TimeSpan elapsed = position - PageFadeOutStart;
                return (int)(elapsed.TotalSeconds * TransitionFrameRate);
            }

            TimeSpan incomingElapsed = position - PageFadeInStart;
            return position >= PageFadeInStart && position < PageFadeInEnd
                ? 10_000 + (int)(incomingElapsed.TotalSeconds * TransitionFrameRate)
                : -1;
        }

        TimeSpan normalElapsed = position - TransitionStart;
        return TransitionComplete > TransitionStart && normalElapsed >= TimeSpan.Zero && position < TransitionComplete
            ? (int)(normalElapsed.TotalSeconds * TransitionFrameRate)
            : -1;
    }

    private static float SmoothStep(double progress) => (float)(progress * progress * (3 - (2 * progress)));
    private static float EaseInCubic(double progress) => (float)(progress * progress * progress);
    private static float EaseInOutCubic(double progress) => progress < 0.5
        ? (float)(4 * progress * progress * progress)
        : (float)(1 - (Math.Pow((-2 * progress) + 2, 3) / 2));
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
}

public sealed record KaraokeLineTiming(
    string Text,
    IReadOnlyList<KaraokeTextSegment> Segments,
    bool HasCountdown = false)
{
    internal static readonly TimeSpan CountdownDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InstantCutThreshold = TimeSpan.FromMilliseconds(150);

    public TimeSpan FirstLyricStart => Segments.Count > 0 ? Segments[0].Start : TimeSpan.Zero;

    public KaraokeCountdownState GetCountdownState(TimeSpan position)
    {
        if (!HasCountdown || Segments.Count == 0)
            return KaraokeCountdownState.None;

        TimeSpan timeRemaining = FirstLyricStart - position;
        if (timeRemaining > CountdownDuration || timeRemaining <= TimeSpan.Zero)
            return KaraokeCountdownState.None;

        if (timeRemaining <= TimeSpan.FromSeconds(1))
        {
            return new KaraokeCountdownState(
                true,
                4,
                0,
                (float)timeRemaining.TotalSeconds);
        }

        var elapsedSeconds = CountdownDuration.TotalSeconds - timeRemaining.TotalSeconds;
        var completedArrowCount = (int)Math.Floor(elapsedSeconds);
        return new KaraokeCountdownState(
            true,
            completedArrowCount,
            (float)(elapsedSeconds - completedArrowCount),
            1);
    }

    public int GetCountdownRevision(TimeSpan position)
    {
        KaraokeCountdownState state = GetCountdownState(position);
        if (!state.IsVisible)
            return -1;

        TimeSpan countdownStart = FirstLyricStart - CountdownDuration;
        return (int)((position - countdownStart).TotalSeconds * 60);
    }

    public int GetHighlightedLength(TimeSpan position)
    {
        var length = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            KaraokeTextSegment segment = Segments[i];
            if (segment.Start > position)
                break;
            length = Math.Max(length, segment.TextStart + segment.TextLength);
        }

        return Math.Min(length, Text.Length);
    }

    /// <summary>
    /// Returns the character length of syllables that are fully highlighted (not currently transitioning).
    /// This excludes the most recent syllable if it is still within its wipe transition window.
    /// </summary>
    public int GetFullyHighlightedLength(TimeSpan position, TimeSpan wipeDuration)
    {
        KaraokeTextSegment? lastSegment = null;
        var length = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            KaraokeTextSegment segment = Segments[i];
            if (segment.Start > position)
                break;
            lastSegment = segment;
            length = Math.Max(length, segment.TextStart + segment.TextLength);
        }

        if (lastSegment == null || wipeDuration <= TimeSpan.Zero)
            return Math.Min(length, Text.Length);

        // If the last segment is still within its wipe window, exclude it
        TimeSpan effectiveWipe = GetSegmentWipeDuration(lastSegment.Value, wipeDuration);
        TimeSpan elapsed = position - lastSegment.Value.Start;
        if (elapsed >= TimeSpan.Zero && elapsed < effectiveWipe)
        {
            length -= lastSegment.Value.TextLength;
            return Math.Max(0, length);
        }

        return Math.Min(length, Text.Length);
    }

    /// <summary>
    /// Returns the smooth wipe fraction for the current syllable: 0 at marker start,
    /// 1 when the wipe transition is complete (or when the next marker arrives).
    /// </summary>
    public float GetSyllableWipeFraction(TimeSpan position, TimeSpan wipeDuration)
    {
        KaraokeTextSegment? currentSegment = null;
        for (var i = 0; i < Segments.Count; i++)
        {
            KaraokeTextSegment segment = Segments[i];
            if (segment.Start > position)
                break;
            currentSegment = segment;
        }

        if (currentSegment == null)
            return 0;

        TimeSpan elapsed = position - currentSegment.Value.Start;
        if (elapsed < TimeSpan.Zero)
            return 0;

        TimeSpan effectiveDuration = GetSegmentWipeDuration(currentSegment.Value, wipeDuration);

        if (effectiveDuration <= TimeSpan.Zero)
            return 1;

        var progress = Math.Clamp((float)(elapsed.TotalMilliseconds / effectiveDuration.TotalMilliseconds), 0, 1);
        return progress;
    }

    /// <summary>
    /// Calculates the per-segment wipe duration: 80% of the gap to the next segment,
    /// capped at the configured wipe duration. If below the instant-cut threshold, snaps to 0.
    /// For the last segment with no next marker, uses the configured duration.
    /// </summary>
    private TimeSpan GetSegmentWipeDuration(KaraokeTextSegment segment, TimeSpan wipeDuration)
    {
        if (wipeDuration <= TimeSpan.Zero)
            return TimeSpan.Zero;

        // Find next segment start
        TimeSpan nextStart = TimeSpan.MaxValue;
        var found = false;
        for (var i = 0; i < Segments.Count; i++)
        {
            KaraokeTextSegment s = Segments[i];
            if (found)
            {
                nextStart = s.Start;
                break;
            }
            if (s == segment)
                found = true;
        }

        if (nextStart == TimeSpan.MaxValue)
            return wipeDuration;

        TimeSpan timeToNext = nextStart - segment.Start;
        if (timeToNext <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var calculated = TimeSpan.FromTicks((long)(timeToNext.Ticks * 0.8));
        TimeSpan clamped = calculated <= wipeDuration ? calculated : wipeDuration;

        return clamped < InstantCutThreshold ? TimeSpan.Zero : clamped;
    }
}

public readonly record struct KaraokeTextSegment(int TextStart, int TextLength, TimeSpan Start);

public readonly record struct KaraokeCountdownState(
    bool IsVisible,
    int CompletedArrowCount,
    float ActiveArrowProgress,
    float Opacity)
{
    public static readonly KaraokeCountdownState None = new(false, 0, 0, 0);
}
