namespace FatzBomb.MediaControl.Core.Network;

/// <summary>One video rendition FFmpeg exposes for an adaptive (HLS/DASH) source.</summary>
/// <param name="StreamIndex">The FFmpeg stream index.</param>
/// <param name="Bandwidth">The variant's advertised bandwidth in bits/s (HLS <c>BANDWIDTH</c>, DASH <c>bandwidth</c>), or 0 when unknown.</param>
/// <param name="Width">Coded width in pixels, or 0 when unknown.</param>
/// <param name="Height">Coded height in pixels, or 0 when unknown.</param>
internal readonly record struct NetworkVariantCandidate(int StreamIndex, long Bandwidth, int Width, int Height);

/// <summary>
/// Picks the one video variant to play from an adaptive network source. FFmpeg's HLS and DASH
/// demuxers download segments for every variant whose streams are not discarded, so playing
/// "all of them" fetches every quality at once (the owner's 039A smoke: slow seeks and stutter).
/// The player has no adaptive switching; it plays one fixed variant:
/// <list type="bullet">
/// <item>the highest bandwidth among the variants at or below <see cref="DefaultMaxHeight"/>
/// (ties: the larger picture, then the lower stream index);</item>
/// <item>when every variant is taller than the cap, the smallest one (the closest to it);</item>
/// <item>a variant with an unknown height counts as within the cap.</item>
/// </list>
/// </summary>
internal static class NetworkVariantPolicy
{
    /// <summary>The height cap: 1080p. The engine does not know the display size when it opens.</summary>
    public const int DefaultMaxHeight = 1080;

    /// <summary>
    /// Returns the stream index of the chosen variant, or -1 when there are fewer than two
    /// candidates (nothing to choose: the caller keeps FFmpeg's own best stream).
    /// </summary>
    public static int Select(IReadOnlyList<NetworkVariantCandidate> candidates, int maxHeight = DefaultMaxHeight)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count < 2)
            return -1;

        NetworkVariantCandidate? best = null;
        foreach (NetworkVariantCandidate candidate in candidates)
        {
            if (candidate.Height <= maxHeight && (best is null || IsBetterWithinCap(candidate, best.Value)))
                best = candidate;
        }

        if (best is not null)
            return best.Value.StreamIndex;

        // Every variant is above the cap: take the smallest picture, then the lower bandwidth.
        NetworkVariantCandidate smallest = candidates[0];
        foreach (NetworkVariantCandidate candidate in candidates)
        {
            if (candidate.Height < smallest.Height
                || (candidate.Height == smallest.Height && candidate.Bandwidth < smallest.Bandwidth))
            {
                smallest = candidate;
            }
        }

        return smallest.StreamIndex;
    }

    private static bool IsBetterWithinCap(NetworkVariantCandidate candidate, NetworkVariantCandidate best) =>
        candidate.Bandwidth != best.Bandwidth
            ? candidate.Bandwidth > best.Bandwidth
            : (long)candidate.Width * candidate.Height > (long)best.Width * best.Height;
}
