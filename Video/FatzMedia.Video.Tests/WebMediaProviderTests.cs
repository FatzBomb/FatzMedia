using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 060A: YouTube address detection, the web source kind and the unsupported-feature rules.</summary>
public sealed class WebMediaProviderTests
{
    const string Id = "dQw4w9WgXcQ";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ&t=30s")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=share")]
    [InlineData("http://WWW.YouTube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("  https://youtu.be/dQw4w9WgXcQ  ")]
    public void YouTubeVideoAddresses_AreRecognised(string address)
    {
        var result = WebMediaProviders.Parse(address);

        Assert.Equal(WebMediaParseOutcome.Recognized, result.Outcome);
        Assert.Equal(WebMediaProviders.YouTubeId, result.Reference!.ProviderId);
        Assert.Equal(Id, result.Reference.ItemId);
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.youtube.com/watch?list=PL123")]
    [InlineData("https://www.youtube.com/embed/videoseries?list=PL123")]
    [InlineData("https://www.youtube.com/results?search_query=abba")]
    [InlineData("https://www.youtube.com/channel/UC123")]
    [InlineData("https://www.youtube.com/@fatzbomb")]
    [InlineData("https://www.youtube.com/user/someone")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/watch")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://youtu.be/")]
    [InlineData("https://youtu.be/not-a-video-id")]
    public void YouTubeAddressesThatNameNoSingleVideo_AreUnsupportedWithAReason(string address)
    {
        var result = WebMediaProviders.Parse(address);

        Assert.Equal(WebMediaParseOutcome.Unsupported, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Null(result.Reference);
    }

    [Theory]
    [InlineData("https://example.com/song.mp3")]
    [InlineData("http://radio.example.com/live")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.example/?u=https://youtu.be/dQw4w9WgXcQ")]
    [InlineData(@"C:\music\song.mp3")]
    [InlineData("/home/user/song.mp3")]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherAddresses_AreNotWebMedia(string? address)
    {
        Assert.Equal(WebMediaParseOutcome.NotWebMedia, WebMediaProviders.Parse(address).Outcome);
    }

    [Fact]
    public void Parse_MakesAWebSourceForYouTube_ANetworkSourceForOtherUrls_AndAFileForPaths()
    {
        var web = MediaInputSource.Parse("https://youtu.be/dQw4w9WgXcQ");
        Assert.Equal(MediaInputSourceKind.WebMedia, web.Kind);
        Assert.True(web.IsWebMedia);
        Assert.False(web.IsNetwork);
        Assert.Equal(Id, web.WebMedia!.ItemId);
        Assert.Equal(MediaSourceClass.WebStream, MediaSourceCapabilityMatrix.For(web).SourceClass);

        var network = MediaInputSource.Parse("https://example.com/clip.mp4");
        Assert.Equal(MediaInputSourceKind.NetworkUri, network.Kind);

        var file = MediaInputSource.Parse(@"C:\music\song.mp3");
        Assert.Equal(MediaInputSourceKind.FilePath, file.Kind);
    }

    [Fact]
    public void Parse_ThrowsWithTheReasonForAnUnsupportedYouTubeAddress_InsteadOfSendingItToNetworkPlayback()
    {
        var ex = Assert.Throws<NotSupportedException>(() => MediaInputSource.Parse("https://www.youtube.com/playlist?list=PL1"));

        Assert.Contains("playlist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MediaPlayerMode.Full, true)]
    [InlineData(MediaPlayerMode.Background, false)]
    [InlineData(MediaPlayerMode.Audio, false)]
    public void WebMedia_TakesTheMediaSurface_SoOnlyFullModeAcceptsIt(MediaPlayerMode mode, bool accepted)
    {
        Assert.Equal(accepted, MediaPlayerModePolicy.AcceptsSource(mode, MediaInputSourceKind.WebMedia));
    }

    [Theory]
    [InlineData(WebMediaFeature.Transport, true)]
    [InlineData(WebMediaFeature.Volume, true)]
    [InlineData(WebMediaFeature.Rate, true)]
    [InlineData(WebMediaFeature.AudioRouting, false)]
    [InlineData(WebMediaFeature.KeyChange, false)]
    [InlineData(WebMediaFeature.Tempo, false)]
    [InlineData(WebMediaFeature.Mixing, false)]
    [InlineData(WebMediaFeature.CdgOverlay, false)]
    [InlineData(WebMediaFeature.Extraction, false)]
    [InlineData(WebMediaFeature.BackgroundPlayback, false)]
    public void WebMediaFeatures_ReportNotSupportedForWhatTheServiceOwns(WebMediaFeature feature, bool supported)
    {
        Assert.Equal(supported, WebMediaSupport.IsSupported(feature));
        if (supported)
            WebMediaSupport.Require(feature);
        else
            Assert.Throws<NotSupportedException>(() => WebMediaSupport.Require(feature));
    }

    [Theory]
    [InlineData(2, WebMediaErrorKind.InvalidItem)]
    [InlineData(5, WebMediaErrorKind.PlayerFailure)]
    [InlineData(100, WebMediaErrorKind.NotFound)]
    [InlineData(101, WebMediaErrorKind.EmbeddingNotAllowed)]
    [InlineData(150, WebMediaErrorKind.EmbeddingNotAllowed)]
    [InlineData(153, WebMediaErrorKind.OriginRejected)]
    [InlineData(999, WebMediaErrorKind.Unknown)]
    public void YouTubeErrorCodes_MapToAKindAndAMessage(int code, WebMediaErrorKind kind)
    {
        var (actual, message) = WebMediaErrors.FromYouTubeCode(code);

        Assert.Equal(kind, actual);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}
