namespace FatzMedia.Video.Core.Network;

/// <summary>
/// Pure decisions for network audio (ENG-034 follow-up): which engine opens a network audio
/// source, and the capability row of one opened through FFmpeg.
/// </summary>
/// <remarks>
/// https audio opens through FFmpeg's network path, where <see cref="NetworkTlsPolicy"/> applies as
/// it does for network video; FFmpeg decodes it and the audio engine's output plays it. HLS
/// playlists (<c>.m3u8</c>) open through FFmpeg too. Plain http goes to an audio engine that opens
/// URLs itself, else through FFmpeg as well (the FatzAudio engine opens none).
/// </remarks>
static class NetworkAudioPolicy
{
    /// <summary>
    /// Whether network audio at <paramref name="uri"/> opens through FFmpeg instead of the audio
    /// engine's own network support: https (certificates are verified) and HLS playlists.
    /// </summary>
    public static bool OpensThroughFfmpeg(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return NetworkTlsRules.IsHttps(uri.AbsoluteUri) || NetworkVideoPolicy.IsSegmentedPlaylist(uri);
    }

    /// <summary>
    /// The capability row of network audio opened through FFmpeg: as for network video (a
    /// progressive source follows the length and range rule, a playlist is live unless FFmpeg
    /// reports a duration), and, as for 014A audio, a progressive live stream or one with ICY
    /// headers carries stream metadata.
    /// </summary>
    public static MediaSourceCapabilities Resolve(NetworkVideoProbe probe)
    {
        var row = NetworkVideoPolicy.Resolve(probe);
        return row with { HasStreamMetadata = !probe.IsSegmented && (row.IsLive || probe.HasIcyMetadata) };
    }
}
