using FatzMedia.Video.Core.Network;

namespace FatzMedia.Video.Tests;

public sealed class NetworkSourceTests
{
    [Theory]
    [InlineData("http://radio.example/stream")]
    [InlineData("https://cdn.example/media/song.mp3")]
    public void FromUri_AcceptsHttpAndHttps(string address)
    {
        var source = MediaInputSource.FromUri(new Uri(address));

        Assert.Equal(MediaInputSourceKind.NetworkUri, source.Kind);
        Assert.True(source.IsNetwork);
        Assert.False(source.IsFile);
        Assert.Null(source.FilePath);
        Assert.Equal(MediaNetworkContent.Auto, source.NetworkContent);
    }

    [Theory]
    [InlineData("ftp://host/file.mp3")]
    [InlineData("file:///c:/music/song.mp3")]
    [InlineData("rtsp://camera/feed")]
    public void FromUri_RejectsOtherSchemes(string address) =>
        Assert.Throws<ArgumentException>(() => MediaInputSource.FromUri(new Uri(address)));

    [Fact]
    public void FromUri_RejectsRelativeUri() =>
        Assert.Throws<ArgumentException>(() => MediaInputSource.FromUri(new Uri("song.mp3", UriKind.Relative)));

    [Fact]
    public void FromUri_RejectsUndefinedContentHint() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MediaInputSource.FromUri(new Uri("http://h/a"), (MediaNetworkContent)99));

    [Theory]
    [InlineData("https://cdn.example/media/My%20Song.mp3", "My Song.mp3")]
    [InlineData("http://radio.example/", "radio.example")]
    [InlineData("http://radio.example", "radio.example")]
    public void FromUri_DisplayNameUsesLastSegmentOrHost(string address, string expected) =>
        Assert.Equal(expected, MediaInputSource.FromUri(new Uri(address)).DisplayName);

    [Fact]
    public void Matrix_LocalFileRow()
    {
        var row = MediaSourceCapabilityMatrix.For(MediaInputSource.FromFile("song.mp3"));

        Assert.Same(MediaSourceCapabilityMatrix.LocalFile, row);
        Assert.True(row.IsSeekable);
        Assert.True(row.IsDurationKnown);
        Assert.False(row.IsBuffered);
        Assert.True(row.SupportsSidecars);
        Assert.True(row.SupportsAnalysis);
    }

    [Fact]
    public void Matrix_ArchiveRow_IsLocalWithoutAnalysis()
    {
        var row = MediaSourceCapabilityMatrix.Archive;

        Assert.Equal(MediaSourceClass.Archive, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.False(row.SupportsAnalysis);
        Assert.False(row.IsBuffered);
    }

    [Fact]
    public void Matrix_ImageRow() =>
        Assert.Same(MediaSourceCapabilityMatrix.InMemoryImage, MediaSourceCapabilityMatrix.For(MediaInputSource.FromBgra32Image(new byte[4], 1, 1, 4)));

    [Fact]
    public void Matrix_UnprobedNetwork_IsConservativeLive()
    {
        var row = MediaSourceCapabilityMatrix.For(MediaInputSource.FromUri(new Uri("https://h/song.mp3")));

        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.False(row.IsSeekable);
        Assert.True(row.IsBuffered);
        Assert.False(row.SupportsSidecars);
    }

    [Fact]
    public void Matrix_NetworkFileWithRanges_IsSeekableFinite()
    {
        var row = MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(1_000_000, true, false));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.True(row.IsDurationKnown);
        Assert.False(row.IsLive);
        Assert.True(row.IsBuffered);
        Assert.True(row.SupportsRetry);
        Assert.False(row.HasStreamMetadata);
        Assert.False(row.SupportsAnalysis);
    }

    [Fact]
    public void Matrix_NetworkFileWithoutRanges_IsNotSeekable() =>
        Assert.False(MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(1_000_000, false, false)).IsSeekable);

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Matrix_UnknownLength_IsLive(long? length)
    {
        var row = MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(length, true, false));

        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.False(row.IsSeekable);
        Assert.False(row.IsDurationKnown);
        Assert.True(row.IsLive);
        Assert.True(row.HasStreamMetadata);
    }

    [Fact]
    public void Matrix_FiniteIcyStream_ReportsMetadata() =>
        Assert.True(MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(5000, false, true)).HasStreamMetadata);

    [Fact]
    public void SourceInfo_CarriesUriAndCapabilities()
    {
        var source = MediaInputSource.FromUri(new Uri("https://h/a.mp3"));
        var probed = MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(10, true, false));

        var info = new MediaSourceInfo(source, Core.Abstractions.MediaContent.Audio, probed);

        Assert.Equal(source.Uri, info.Uri);
        Assert.Same(probed, info.Capabilities);
    }
}

public sealed class NetworkRetryPolicyTests
{
    [Fact]
    public void Default_IsSingleAttemptNoRetry()
    {
        var policy = NetworkRetryPolicy.Default;

        Assert.Equal(0, policy.MaxRetries);
        Assert.Equal(1, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.ConnectTimeout);
    }

    [Fact]
    public void Retries_AreBounded()
    {
        Assert.Equal(NetworkRetryPolicy.MaximumRetries, new NetworkRetryPolicy { MaxRetries = 1000 }.MaxRetries);
        Assert.Equal(0, new NetworkRetryPolicy { MaxRetries = -3 }.MaxRetries);
    }

    [Fact]
    public void Delays_BackOffExponentiallyAndCap()
    {
        var policy = new NetworkRetryPolicy
        {
            MaxRetries = 5,
            InitialDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2,
            MaxDelay = TimeSpan.FromSeconds(5),
        };

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)],
            Enumerable.Range(1, 5).Select(policy.GetDelayBeforeRetry));
    }

    [Fact]
    public void Values_AreClamped()
    {
        var policy = new NetworkRetryPolicy
        {
            InitialDelay = TimeSpan.FromMinutes(5),
            BackoffMultiplier = double.NaN,
            MaxDelay = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.Zero,
        };

        Assert.Equal(TimeSpan.FromSeconds(30), policy.InitialDelay);
        Assert.Equal(2, policy.BackoffMultiplier);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.ConnectTimeout);
    }

    [Fact]
    public void GetDelay_RejectsZeroRetryNumber() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkRetryPolicy.Default.GetDelayBeforeRetry(0));
}

public sealed class IcyMetadataParserTests
{
    [Fact]
    public void ParseIcy_SplitsArtistAndTitle()
    {
        var metadata = IcyMetadataParser.ParseIcy("StreamTitle='Daft Punk - One More Time';StreamUrl='';");

        Assert.Equal("Daft Punk", metadata.Artist);
        Assert.Equal("One More Time", metadata.Title);
        Assert.Equal("Daft Punk - One More Time", metadata.RawTitle);
    }

    [Fact]
    public void ParseIcy_KeepsApostrophesInsideTitle()
    {
        var metadata = IcyMetadataParser.ParseIcy("StreamTitle='Guns N' Roses - Sweet Child O' Mine';");

        Assert.Equal("Guns N' Roses", metadata.Artist);
        Assert.Equal("Sweet Child O' Mine", metadata.Title);
    }

    [Fact]
    public void ParseIcy_TitleWithoutSeparator()
    {
        var metadata = IcyMetadataParser.ParseIcy("StreamTitle='Station ID';");

        Assert.Null(metadata.Artist);
        Assert.Equal("Station ID", metadata.Title);
    }

    [Fact]
    public void ParseIcy_WithoutTrailingSemicolon()
    {
        var metadata = IcyMetadataParser.ParseIcy("StreamTitle='A - B'");

        Assert.Equal("A", metadata.Artist);
        Assert.Equal("B", metadata.Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("StreamUrl='x';")]
    [InlineData("StreamTitle='';")]
    public void ParseIcy_MissingOrEmpty_IsDefault(string? input) =>
        Assert.Equal(default, IcyMetadataParser.ParseIcy(input));

    [Fact]
    public void ParseOgg_ReadsTitleAndArtistCaseInsensitively()
    {
        var metadata = IcyMetadataParser.ParseOggComments(["artist=Air", "TITLE=La Femme d'Argent", "ALBUM=Moon Safari"]);

        Assert.Equal("Air", metadata.Artist);
        Assert.Equal("La Femme d'Argent", metadata.Title);
        Assert.Equal("Air - La Femme d'Argent", metadata.RawTitle);
    }

    [Fact]
    public void ParseOgg_ArtistOnly_RawTitleIsArtistWithoutSeparator()
    {
        var metadata = IcyMetadataParser.ParseOggComments(["ARTIST=Foo"]);

        Assert.Equal("Foo", metadata.Artist);
        Assert.Null(metadata.Title);
        Assert.Equal("Foo", metadata.RawTitle);
    }

    [Fact]
    public void ParseOgg_NoKnownFields_IsDefault() =>
        Assert.Equal(default, IcyMetadataParser.ParseOggComments(["ALBUM=x", "junk"]));
}

public sealed class BufferingStateTrackerTests
{
    [Fact]
    public void EntersBelowEnterThresholdAndExitsAboveExitThreshold()
    {
        var tracker = new BufferingStateTracker(0.05, 0.25);

        Assert.False(tracker.Update(500, 1000, false, out _));
        Assert.True(tracker.Update(40, 1000, false, out var fill));
        Assert.True(tracker.IsBuffering);
        Assert.Equal(0.04, fill, precision: 6);

        Assert.False(tracker.Update(100, 1000, false, out _)); // between thresholds: no flap, <10 pt move
        Assert.True(tracker.IsBuffering);

        Assert.True(tracker.Update(300, 1000, false, out _));
        Assert.False(tracker.IsBuffering);
    }

    [Fact]
    public void ReportsProgressEveryTenPointsWhileBuffering()
    {
        var tracker = new BufferingStateTracker(0.05, 0.5);
        _ = tracker.Update(0, 1000, false, out _);

        Assert.False(tracker.Update(50, 1000, false, out _));
        Assert.True(tracker.Update(120, 1000, false, out _));
        Assert.False(tracker.Update(150, 1000, false, out _));
        Assert.True(tracker.Update(230, 1000, false, out _));
    }

    [Fact]
    public void DownloadComplete_EndsBufferingAtFullFill()
    {
        var tracker = new BufferingStateTracker();
        _ = tracker.Update(0, 1000, false, out _);

        Assert.True(tracker.Update(0, 1000, true, out var fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(1, fill);
    }

    [Fact]
    public void ZeroTarget_TreatedAsEmpty()
    {
        var tracker = new BufferingStateTracker();

        Assert.True(tracker.Update(100, 0, false, out var fill));
        Assert.Equal(0, fill);
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var tracker = new BufferingStateTracker();
        _ = tracker.Update(0, 1000, false, out _);

        tracker.Reset();

        Assert.False(tracker.IsBuffering);
    }
}
