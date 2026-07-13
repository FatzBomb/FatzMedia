namespace FatzBombMedia.MediaControl.Core.Subtitle;

public sealed record SubtitleCue(int Sequence, TimeSpan Start, TimeSpan End, string Text)
{
    internal KaraokeCueTiming? KaraokeTiming { get; init; }
}

internal sealed record KaraokeCueTiming(
    IReadOnlyList<KaraokeLineTiming> Lines,
    int ActiveLineIndex,
    TimeSpan TransitionStart,
    TimeSpan TransitionEnd)
{
    private const int TransitionFrameRate = 30;

    public IReadOnlyList<KaraokeLineTiming> OutgoingPageLines { get; init; } = [];
    public int OutgoingActiveLineIndex { get; init; }
    public bool ClearsPage { get; init; }
    public TimeSpan IncomingStart { get; init; }
    public TimeSpan TransitionComplete { get; init; }
    public TimeSpan PageFadeOutStart { get; init; }
    public TimeSpan PageFadeOutEnd { get; init; }
    public TimeSpan PageFadeInStart { get; init; }
    public TimeSpan PageFadeInEnd { get; init; }

    public int GetProgressRevision(TimeSpan position, TimeSpan transitionDuration)
    {
        var hash = new HashCode();
        foreach (KaraokeLineTiming line in Lines)
            hash.Add(line.GetHighlightedLength(position));
        foreach (KaraokeLineTiming line in OutgoingPageLines)
            hash.Add(line.GetHighlightedLength(position));
        hash.Add(ClearsPage);

        var transitionFrame = GetTransitionFrame(position);
        hash.Add(transitionFrame);
        return hash.ToHashCode();
    }

    public float GetTransitionProgress(TimeSpan position, TimeSpan transitionDuration)
    {
        if (TransitionComplete <= TransitionStart)
            return 1;

        transitionDuration = TransitionComplete - TransitionStart;
        if (transitionDuration <= TimeSpan.Zero)
            return 1;

        var progress = Math.Clamp((position - TransitionStart).TotalMilliseconds / transitionDuration.TotalMilliseconds, 0, 1);
        return SmoothStep(progress);
    }

    public float GetPageFadeOutProgress(TimeSpan position)
    {
        TimeSpan duration = PageFadeOutEnd - PageFadeOutStart;
        if (duration <= TimeSpan.Zero)
            return 1;

        var progress = Math.Clamp((position - PageFadeOutStart).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        return SmoothStep(progress);
    }

    public float GetPageFadeInProgress(TimeSpan position, TimeSpan transitionDuration)
    {
        transitionDuration = PageFadeInEnd > PageFadeInStart
            ? PageFadeInEnd - PageFadeInStart
            : GetEffectiveIncomingTransitionDuration(transitionDuration);
        if (transitionDuration <= TimeSpan.Zero)
            return position >= PageFadeInEnd ? 1 : 0;

        var progress = Math.Clamp((position - PageFadeInStart).TotalMilliseconds / transitionDuration.TotalMilliseconds, 0, 1);
        return SmoothStep(progress);
    }

    private TimeSpan GetEffectiveIncomingTransitionDuration(TimeSpan requestedDuration)
    {
        TimeSpan availableDuration = TransitionEnd - IncomingStart;
        return availableDuration > TimeSpan.Zero && availableDuration < requestedDuration
            ? availableDuration
            : requestedDuration;
    }

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
}

internal sealed record KaraokeLineTiming(string Text, IReadOnlyList<KaraokeTextSegment> Segments)
{
    public int GetHighlightedLength(TimeSpan position)
    {
        var length = 0;
        foreach (KaraokeTextSegment segment in Segments)
        {
            if (segment.Start > position)
                break;
            length = Math.Max(length, segment.TextStart + segment.TextLength);
        }

        return Math.Min(length, Text.Length);
    }
}

internal readonly record struct KaraokeTextSegment(int TextStart, int TextLength, TimeSpan Start);
