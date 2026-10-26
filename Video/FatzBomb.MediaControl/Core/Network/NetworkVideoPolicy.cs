using FatzBomb.MediaControl.Core.Files;
using System.Globalization;

namespace FatzBomb.MediaControl.Core.Network;

/// <summary>
/// What FFmpeg reported after connecting to a network video source (Stage 039A).
/// </summary>
/// <param name="IsSegmented">The source is a segmented playlist (HLS) rather than one progressive file.</param>
/// <param name="ContentLength">Total length in bytes of a progressive file, or <see langword="null"/> when the server did not report one.</param>
/// <param name="SupportsByteSeeking">Whether the progressive file accepts byte-range requests.</param>
/// <param name="DurationSeconds">The container duration, or 0 when unknown (a live playlist or an unindexed stream).</param>
/// <param name="HasIcyMetadata">Whether the server sent ICY (Shoutcast/Icecast) headers.</param>
internal readonly record struct NetworkVideoProbe(bool IsSegmented, long? ContentLength, bool SupportsByteSeeking, double DurationSeconds, bool HasIcyMetadata = false);

/// <summary>
/// Pure decisions for network video (Stage 039A): classification, the capability row, looping,
/// and the FFmpeg options derived from the host's <see cref="NetworkRetryPolicy"/>.
/// </summary>
internal static class NetworkVideoPolicy
{
    /// <summary>Path extensions of segmented playlists played through FFmpeg's HLS demuxer.</summary>
    public static IReadOnlySet<string> SegmentedPlaylistExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".m3u8" };

    /// <summary>FFmpeg demuxer names that read a playlist of segments (no byte seeking).</summary>
    private static readonly HashSet<string> SegmentedDemuxers = new(StringComparer.Ordinal) { "hls", "dash" };

    /// <summary>
    /// Whether a network source is video: the host hint, else the URI path extension through the
    /// local-file video set, plus HLS playlists (<c>.m3u8</c>). Anything else falls back to audio (014A).
    /// </summary>
    public static bool IsVideo(Uri uri, MediaNetworkContent hint)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return hint switch
        {
            MediaNetworkContent.Video => true,
            MediaNetworkContent.Audio => false,
            _ => FileTypes.IsVideo(uri.AbsolutePath) || IsSegmentedPlaylist(uri),
        };
    }

    /// <summary>Whether the URI path names a segmented playlist.</summary>
    public static bool IsSegmentedPlaylist(Uri uri) =>
        SegmentedPlaylistExtensions.Contains(Path.GetExtension(uri.AbsolutePath));

    /// <summary>Whether an FFmpeg demuxer name reads a segmented playlist.</summary>
    public static bool IsSegmentedDemuxer(string? demuxerName) =>
        demuxerName is not null && SegmentedDemuxers.Contains(demuxerName);

    /// <summary>
    /// The capability row of a connected network video. A progressive file follows the 014A rule
    /// (finite with a known length, seekable only with range support); a playlist is finite and
    /// seekable when it is complete (VOD: FFmpeg reports its duration) and live otherwise. Duration
    /// counts as known only when FFmpeg reported one. Video carries no ICY/Ogg stream metadata.
    /// </summary>
    public static MediaSourceCapabilities Resolve(NetworkVideoProbe probe)
    {
        var durationKnown = double.IsFinite(probe.DurationSeconds) && probe.DurationSeconds > 0;
        if (probe.IsSegmented)
        {
            return new MediaSourceCapabilities
            {
                SourceClass = durationKnown ? MediaSourceClass.NetworkFile : MediaSourceClass.NetworkLive,
                IsSeekable = durationKnown,
                IsDurationKnown = durationKnown,
                IsLive = !durationKnown,
                IsBuffered = true,
                SupportsRetry = true,
                HasStreamMetadata = false,
                SupportsSidecars = false,
                SupportsAnalysis = false,
            };
        }

        MediaSourceCapabilities row = MediaSourceCapabilityMatrix.ForNetwork(
            new NetworkStreamProbe(probe.ContentLength, probe.SupportsByteSeeking, HasIcyMetadata: false));
        return row with
        {
            IsDurationKnown = row.IsDurationKnown && durationKnown,
            HasStreamMetadata = false,
        };
    }

    /// <summary>
    /// The open error for an MP4 whose index (<c>moov</c>) follows its media data on a server
    /// without byte ranges (ENG-034): FFmpeg reads to the index and cannot go back.
    /// </summary>
    public const string UnstreamableMp4Error =
        "this MP4 keeps its index (moov) after the media data and the server does not support byte ranges, so it cannot be streamed; " +
        "serve it from a server with range support, or remux it with -movflags +faststart";

    /// <summary>
    /// Whether a failed open is not worth retrying: a rejected certificate is rejected again, and an
    /// MP4 that cannot be streamed from this server stays so.
    /// </summary>
    public static bool IsPermanentOpenError(string? error) =>
        error is not null
        && (NetworkTlsRules.IsCertificateFailureError(error) || error.StartsWith(UnstreamableMp4Error, StringComparison.Ordinal));

    /// <summary>Whether a network video can loop: only a finite, seekable source can restart.</summary>
    public static bool CanLoop(MediaSourceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.IsSeekable && !capabilities.IsLive;
    }

    /// <summary>
    /// FFmpeg open options for a network source. Reads time out after the policy's connect timeout
    /// (<c>rw_timeout</c>). Mid-stream recovery uses FFmpeg's own HTTP reconnect, enabled only when
    /// the host opted into retries: at most <see cref="NetworkRetryPolicy.MaxRetries"/> reconnects,
    /// none waiting longer than <see cref="NetworkRetryPolicy.MaxDelay"/>, and the same number of
    /// retries for an HLS segment. Streamed (no-range) sources are never reconnected, because FFmpeg
    /// would restart them from the first byte.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> CreateFfmpegOptions(NetworkRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var readTimeoutMicroseconds = (long)policy.ConnectTimeout.TotalMicroseconds;
        List<KeyValuePair<string, string>> options =
        [
            new("rw_timeout", readTimeoutMicroseconds.ToString(CultureInfo.InvariantCulture)),
            new("reconnect", policy.MaxRetries > 0 ? "1" : "0"),
            new("reconnect_streamed", "0"),
            new("seg_max_retry", policy.MaxRetries.ToString(CultureInfo.InvariantCulture)),
        ];

        if (policy.MaxRetries > 0)
        {
            options.Add(new("reconnect_max_retries", policy.MaxRetries.ToString(CultureInfo.InvariantCulture)));
            options.Add(new("reconnect_delay_max", ((int)Math.Ceiling(policy.MaxDelay.TotalSeconds)).ToString(CultureInfo.InvariantCulture)));
        }

        return options;
    }
}
