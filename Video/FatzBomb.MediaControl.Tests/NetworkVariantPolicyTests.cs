using FatzBomb.MediaControl.Core.Network;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 039A follow-up: which variant of an adaptive (HLS/DASH) network video plays.</summary>
public sealed class NetworkVariantPolicyTests
{
    private static readonly NetworkVariantCandidate P480 = new(0, 950_400, 854, 480);
    private static readonly NetworkVariantCandidate P720 = new(2, 2_270_400, 1280, 720);
    private static readonly NetworkVariantCandidate P1080 = new(4, 4_470_400, 1920, 1080);
    private static readonly NetworkVariantCandidate P2160 = new(6, 16_000_000, 3840, 2160);

    [Fact]
    public void FewerThanTwoCandidates_LeavesTheChoiceToFfmpeg()
    {
        Assert.Equal(-1, NetworkVariantPolicy.Select([]));
        Assert.Equal(-1, NetworkVariantPolicy.Select([P720]));
    }

    [Fact]
    public void PicksTheHighestBandwidthWithinTheCap_InAnyOrder()
    {
        Assert.Equal(P1080.StreamIndex, NetworkVariantPolicy.Select([P480, P720, P1080]));
        Assert.Equal(P1080.StreamIndex, NetworkVariantPolicy.Select([P1080, P480, P720]));
    }

    [Fact]
    public void SkipsVariantsAboveTheCap()
    {
        Assert.Equal(P1080.StreamIndex, NetworkVariantPolicy.Select([P480, P2160, P1080, P720]));
        Assert.Equal(P720.StreamIndex, NetworkVariantPolicy.Select([P480, P720, P1080], maxHeight: 720));
    }

    [Fact]
    public void WhenEveryVariantIsAboveTheCap_PicksTheSmallest()
    {
        var p1440 = new NetworkVariantCandidate(8, 9_000_000, 2560, 1440);
        Assert.Equal(p1440.StreamIndex, NetworkVariantPolicy.Select([P2160, p1440]));
    }

    [Fact]
    public void UnknownHeight_CountsAsWithinTheCap()
    {
        var unknown = new NetworkVariantCandidate(9, 6_000_000, 0, 0);
        Assert.Equal(unknown.StreamIndex, NetworkVariantPolicy.Select([P1080, unknown]));
    }

    [Fact]
    public void EqualBandwidth_PrefersTheLargerPicture_ThenTheFirstListed()
    {
        var small = new NetworkVariantCandidate(1, 3_000_000, 1280, 720);
        var large = new NetworkVariantCandidate(3, 3_000_000, 1920, 1080);
        var twin = new NetworkVariantCandidate(5, 3_000_000, 1920, 1080);
        Assert.Equal(large.StreamIndex, NetworkVariantPolicy.Select([small, large, twin]));
    }

    [Fact]
    public void UnknownBandwidth_FallsBackToThePictureSize()
    {
        var a = new NetworkVariantCandidate(0, 0, 1280, 720);
        var b = new NetworkVariantCandidate(1, 0, 1920, 1080);
        Assert.Equal(b.StreamIndex, NetworkVariantPolicy.Select([a, b]));
    }
}
