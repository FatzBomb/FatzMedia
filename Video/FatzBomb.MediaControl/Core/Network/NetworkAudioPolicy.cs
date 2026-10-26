namespace FatzBomb.MediaControl.Core.Network;

/// <summary>
/// Pure decisions for network audio (ENG-034 follow-up): which engine opens a network audio
/// source, and the capability row of one opened through FFmpeg.
/// </summary>
/// <remarks>
/// BASS (Stage 014A) does not verify https certificates and has no setting for it, so https
/// audio opens through FFmpeg's network path, where <see cref="NetworkTlsPolicy"/> applies as it
/// does for network video; FFmpeg decodes it and the audio engine's output plays it. Plain http
/// stays on BASS. HLS playlists (<c>.m3u8</c>) open through FFmpeg too: BASS has no HLS support.
/// </remarks>
internal static class NetworkAudioPolicy
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
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(probe);
        return row with { HasStreamMetadata = !probe.IsSegmented && (row.IsLive || probe.HasIcyMetadata) };
    }
}
