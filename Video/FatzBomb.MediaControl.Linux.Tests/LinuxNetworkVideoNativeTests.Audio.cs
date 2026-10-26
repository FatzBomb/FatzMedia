using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Network;
using FFmpeg.AutoGen;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// ENG-034 follow-ups: https network audio through FFmpeg (certificate checks, ICY and Ogg titles
/// timed to playback), checked at the demuxer level because playing audio needs BASS, which the
/// sandbox does not have; the player's behaviour without BASS; and MP4s on servers without byte
/// ranges. Media: <c>audio/tone.mp3</c>, <c>audio/tone.ogg</c> and <c>faststart.mp4</c> (made by
/// <c>scripts/setup-linux-native.sh</c>); the tests that need them return without them.
/// </summary>
public sealed partial class LinuxNetworkVideoNativeTests
{
    private const string ToneMp3 = "audio/tone.mp3";
    private const string ToneOgg = "audio/tone.ogg";
    private const string FaststartMp4 = "faststart.mp4";

    [Fact]
    public void HttpsAudio_SelfSignedServer_IsRejectedByDefault_AndAcceptedWithItsCaFile()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, ToneMp3)))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        var url = server.UriFor(ToneMp3).AbsoluteUri;

        using (var rejected = new SharedDemuxer())
        {
            Assert.False(rejected.Open(url, FfmpegNetworkOptions.Default));
            output.WriteLine($"https audio, default policy: {rejected.LastNetworkError}");
            Assert.True(NetworkTlsRules.IsCertificateFailureError(rejected.LastNetworkError), rejected.LastNetworkError);
            Assert.True(NetworkVideoPolicy.IsPermanentOpenError(rejected.LastNetworkError));
            Assert.Empty(server.Requests);
        }

        using var accepted = new SharedDemuxer();
        var options = FfmpegNetworkOptions.Default with { Tls = new NetworkTlsPolicy { CertificateAuthorityFile = certificate.PemPath } };
        Assert.True(accepted.Open(url, options), accepted.LastNetworkError);
        MediaSourceCapabilities row = NetworkAudioPolicy.Resolve(accepted.GetNetworkProbe());
        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.False(row.HasStreamMetadata);
        Assert.True(accepted.SelectAudioStream() >= 0);
        Assert.True(accepted.SelectVideoStream() < 0);
    }

    [Fact]
    public void HttpsAudio_WithoutBass_IsRejectedAsAudioUnavailable_BeforeConnecting()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, ToneMp3)))
            return;

        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var session = PlayerSession.Create(ffmpegDirectory, MediaPlayerMode.Full);

        Assert.False(session.Player.Load(MediaInputSource.FromUri(server.UriFor(ToneMp3))));

        Assert.True(SpinWait.SpinUntil(() => session.ErrorList.Any(line => line.Contains("Audio engine unavailable", StringComparison.Ordinal)), TimeSpan.FromSeconds(5)),
            $"errors: {session.Errors}; diagnostics: {string.Join(" | ", session.DiagnosticList)}");
        Assert.Equal(0, server.Handshakes);
    }

    /// <summary>
    /// An Icecast-style live MP3 over https with a title change every 5 s of audio, served four
    /// times faster than it plays so the read-ahead runs ahead: each title is taken when the played
    /// position reaches it (within FFmpeg's 32 KB I/O buffer, 2 s at 128 kbit/s), not when it was
    /// downloaded (the reader is 8 s ahead).
    /// </summary>
    [Fact]
    public unsafe void HttpsIcyStream_TitlesFollowThePlayedPosition()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, ToneMp3)))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate)
        {
            // 128 kbit/s: 16000 bytes is one second of audio.
            IcyMetaInterval = 16000,
            IcyTitle = block => $"Test Artist - Song {block / 5}",
            Plan = _ => new HttpResponsePlan(BytesPerSecond: 64_000),
        };

        using var demuxer = new SharedDemuxer();
        var options = FfmpegNetworkOptions.Default with { Tls = new NetworkTlsPolicy { CertificateAuthorityFile = certificate.PemPath } };
        Assert.True(demuxer.Open(server.UriFor(ToneMp3).AbsoluteUri, options), demuxer.LastNetworkError);
        NetworkVideoProbe probe = demuxer.GetNetworkProbe();
        MediaSourceCapabilities row = NetworkAudioPolicy.Resolve(probe);
        Assert.True(probe.HasIcyMetadata);
        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.True(row.HasStreamMetadata);

        var audio = demuxer.SelectAudioStream();
        demuxer.RegisterAudioStream(audio);
        demuxer.DiscardUnregisteredNetworkStreams();
        var timeBase = ffmpeg.av_q2d(demuxer.GetStream(audio)->time_base);

        // Nothing played yet: the first title (read with the header) is due at once.
        Assert.True(demuxer.TryTakeStreamMetadata(0, out StreamMetadata first), "no title at the start");
        Assert.Equal("Song 0", first.Title);
        Assert.Equal("Test Artist", first.Artist);

        // "Play" to 13 s by taking packets; titles are asked for at the played position. ICY puts
        // block k after (k + 1) s of audio, so "Song 1" starts at 6 s and "Song 2" at 11 s.
        List<(double At, string Title)> taken = [];
        AVPacket* packet = ffmpeg.av_packet_alloc();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // The first read starts the reader; let it get 8 s ahead before "playing" on.
            Assert.True(demuxer.ReadAudioPacket(packet) >= 0);
            ffmpeg.av_packet_unref(packet);
            Assert.True(SpinWait.SpinUntil(() => demuxer.ReadAhead!.BufferedSeconds >= 8, TimeSpan.FromSeconds(10)), $"read-ahead {demuxer.ReadAhead!.BufferedSeconds:F1} s");
            var played = 0.0;
            while (played < 13 && stopwatch.Elapsed < TimeSpan.FromSeconds(20))
            {
                Assert.True(demuxer.ReadAudioPacket(packet) >= 0, "the stream ended early");
                played = (packet->pts + packet->duration) * timeBase;
                ffmpeg.av_packet_unref(packet);
                if (demuxer.TryTakeStreamMetadata(played, out StreamMetadata metadata))
                    taken.Add((played, metadata.Title!));
            }
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }

        output.WriteLine($"ICY titles taken at: {string.Join(", ", taken.Select(entry => $"{entry.Title} at {entry.At:F2} s"))}; read-ahead at the end {demuxer.ReadAhead?.BufferedSeconds:F1} s");
        Assert.Equal(["Song 1", "Song 2"], taken.Select(entry => entry.Title));
        Assert.InRange(taken[0].At, 6 - 2.5, 6 + 0.5);
        Assert.InRange(taken[1].At, 11 - 2.5, 11 + 0.5);
    }

    [Fact]
    public void HttpsOgg_ReportsItsTitleAndArtist()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, ToneOgg)))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var certificate = SelfSignedCertificate.Create();
        using var server = new LocalHttpMediaServer(media, certificate: certificate.Certificate);
        using var demuxer = new SharedDemuxer();
        var options = FfmpegNetworkOptions.Default with { Tls = new NetworkTlsPolicy { CertificateAuthorityFile = certificate.PemPath } };
        Assert.True(demuxer.Open(server.UriFor(ToneOgg).AbsoluteUri, options), demuxer.LastNetworkError);

        Assert.True(demuxer.TryTakeStreamMetadata(0, out StreamMetadata metadata), "no Ogg tags");
        Assert.Equal("Ogg Title", metadata.Title);
        Assert.Equal("Ogg Artist", metadata.Artist);
        Assert.False(demuxer.TryTakeStreamMetadata(0, out _));
    }

    /// <summary>
    /// ENG-034 owner smoke: an MP4 with its index at the end, from a server without byte ranges
    /// (like <c>python -m http.server</c>), fails the open clearly and is not retried; the same
    /// file with its index first plays.
    /// </summary>
    [Fact]
    public void Mp4WithTheIndexAtTheEnd_OnAServerWithoutRanges_FailsClearly_AndAFaststartCopyPlays()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, FaststartMp4)))
            return;

        using var server = new LocalHttpMediaServer(media) { SupportsRanges = false };
        using var session = PlayerSession.Create(ffmpegDirectory, MediaPlayerMode.Full, new NetworkRetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero });

        Assert.False(session.Player.Load(MediaInputSource.FromUri(server.UriFor(Mp4))));
        Assert.True(SpinWait.SpinUntil(() => session.ErrorList.Count > 0, TimeSpan.FromSeconds(5)), "no MediaError");
        output.WriteLine($"moov at the end, no ranges: {session.Errors}");
        Assert.Contains(session.ErrorList, line => line.Contains("-movflags +faststart", StringComparison.Ordinal) && line.Contains("byte ranges", StringComparison.Ordinal));
        Assert.Contains(session.DiagnosticList, line => line.Contains("after 1 attempt(s)", StringComparison.Ordinal));

        var errorsBefore = session.ErrorList.Count;
        Assert.True(session.Player.Load(MediaInputSource.FromUri(server.UriFor(FaststartMp4))), session.Errors);
        MediaSourceCapabilities? row = session.Player.CurrentSourceCapabilities;
        Assert.NotNull(row);
        Assert.False(row.IsSeekable);
        Assert.True(session.Player.Play());
        Assert.True(SpinWait.SpinUntil(() => session.Sink.Frames >= 5, TimeSpan.FromSeconds(15)), $"frames {session.Sink.Frames}");
        Assert.True(session.Sink.LastFrameHasContent, "presented frame looks blank");
        Assert.Equal(errorsBefore, session.ErrorList.Count);
        _ = session.Player.Stop();
    }

    /// <summary>
    /// ENG-034 read-ahead review: a consumer that closes (its engine failed after re-enabling its
    /// stream) has its stream discarded again, by the reader, so HLS and DASH stop fetching it.
    /// </summary>
    [Fact]
    public unsafe void UnregisteredAudio_IsDiscardedAgainByTheReader()
    {
        if (!TryGetEnvironment(out var ffmpegDirectory, out var media) || !File.Exists(Path.Combine(media, HlsPlaylist)))
            return;

        FfmpegAudioReaderNativeTests.ActivateFfmpeg(ffmpegDirectory);
        using var server = new LocalHttpMediaServer(media);
        using var demuxer = new SharedDemuxer();
        Assert.True(demuxer.Open(server.UriFor(HlsPlaylist).AbsoluteUri, FfmpegNetworkOptions.Default), demuxer.LastNetworkError);
        var video = demuxer.SelectVideoStream();
        var audio = demuxer.SelectAudioStream();
        demuxer.RegisterVideoStream(video);
        demuxer.RegisterAudioStream(audio);
        demuxer.DiscardUnregisteredNetworkStreams();
        Assert.Equal(AVDiscard.AVDISCARD_DEFAULT, demuxer.GetStream(audio)->discard);

        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            // The reader is running now.
            Assert.True(demuxer.ReadVideoPacket(packet) >= 0);
            ffmpeg.av_packet_unref(packet);
            demuxer.UnregisterAudioStream();
            var deadline = Stopwatch.StartNew();
            var discarded = false;
            while (!discarded && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Keep the reader busy: it applies the discard between its reads.
                if (demuxer.ReadVideoPacket(packet) >= 0)
                    ffmpeg.av_packet_unref(packet);
                discarded = demuxer.GetStream(audio)->discard == AVDiscard.AVDISCARD_ALL;
            }

            Assert.True(discarded, "the audio stream was not discarded again");
            Assert.Equal(AVDiscard.AVDISCARD_DEFAULT, demuxer.GetStream(video)->discard);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
        }
    }
}
