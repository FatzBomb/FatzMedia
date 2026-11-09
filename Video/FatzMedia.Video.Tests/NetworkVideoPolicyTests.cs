using FatzMedia.Video.Core.Files;
using FatzMedia.Video.Core.Network;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 039A pure decisions: classification, capability rows, looping, FFmpeg options, retry, buffering.</summary>
[Collection("File type registry")]
public sealed class NetworkVideoPolicyTests
{
    [Theory]
    [InlineData("http://h/clip.mp4", MediaNetworkContent.Auto, true)]
    [InlineData("http://h/live/index.m3u8", MediaNetworkContent.Auto, true)]
    [InlineData("http://h/LIVE.M3U8?token=1", MediaNetworkContent.Auto, true)]
    [InlineData("http://h/radio", MediaNetworkContent.Auto, false)]
    [InlineData("http://h/song.mp3", MediaNetworkContent.Auto, false)]
    [InlineData("http://h/radio", MediaNetworkContent.Video, true)]
    [InlineData("http://h/clip.mp4", MediaNetworkContent.Audio, false)]
    public void IsVideo_UsesTheHintThenTheExtension(string address, MediaNetworkContent hint, bool expected)
    {
        var added = FileTypes.Video.Add(".mp4");
        try
        {
            Assert.Equal(expected, NetworkVideoPolicy.IsVideo(new Uri(address), hint));
        }
        finally
        {
            if (added)
                _ = FileTypes.Video.Remove(".mp4");
        }
    }

    [Fact]
    public void Progressive_WithLengthAndRanges_IsASeekableNetworkFile()
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(false, 4_600_000, true, 10));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.True(row.IsDurationKnown);
        Assert.False(row.IsLive);
        Assert.True(row.IsBuffered);
        Assert.True(row.SupportsRetry);
        Assert.False(row.HasStreamMetadata);
        Assert.False(row.SupportsSidecars);
        Assert.False(row.SupportsAnalysis);
        Assert.True(NetworkVideoPolicy.CanLoop(row));
    }

    [Fact]
    public void Progressive_WithoutRanges_IsAFileThatCannotSeekOrLoop()
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(false, 4_600_000, false, 10));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.False(row.IsSeekable);
        Assert.False(NetworkVideoPolicy.CanLoop(row));
    }

    [Fact]
    public void Progressive_WithLengthButNoDuration_HasUnknownDuration()
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(false, 4_600_000, true, 0));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.False(row.IsDurationKnown);
    }

    [Fact]
    public void Progressive_WithoutLength_IsLive()
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(false, null, true, 0));

        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.False(row.IsSeekable);
        Assert.False(row.IsDurationKnown);
        Assert.True(row.IsLive);
        Assert.False(row.HasStreamMetadata);
    }

    [Fact]
    public void HlsVod_IsASeekableNetworkFile_RegardlessOfThePlaylistLength()
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(true, null, false, 10));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.True(row.IsDurationKnown);
        Assert.True(NetworkVideoPolicy.CanLoop(row));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    public void HlsLive_IsNotSeekable_AndHasNoDuration(double duration)
    {
        MediaSourceCapabilities row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(true, 253, true, duration));

        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.False(row.IsSeekable);
        Assert.False(row.IsDurationKnown);
        Assert.True(row.IsLive);
        Assert.False(NetworkVideoPolicy.CanLoop(row));
    }

    [Theory]
    [InlineData("hls", true)]
    [InlineData("dash", true)]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", false)]
    [InlineData("mpegts", false)]
    [InlineData(null, false)]
    public void IsSegmentedDemuxer_KnowsPlaylistDemuxers(string? name, bool expected) =>
        Assert.Equal(expected, NetworkVideoPolicy.IsSegmentedDemuxer(name));

    [Fact]
    public void FfmpegOptions_DefaultPolicy_DisablesReconnectAndBoundsReads()
    {
        Dictionary<string, string> options = NetworkVideoPolicy.CreateFfmpegOptions(NetworkRetryPolicy.Default).ToDictionary();

        Assert.Equal("15000000", options["rw_timeout"]);
        Assert.Equal("0", options["reconnect"]);
        Assert.Equal("0", options["reconnect_streamed"]);
        Assert.Equal("0", options["seg_max_retry"]);
        Assert.False(options.ContainsKey("reconnect_max_retries"));
    }

    [Fact]
    public void FfmpegOptions_WithRetries_MapThePolicy()
    {
        var policy = new NetworkRetryPolicy { MaxRetries = 4, MaxDelay = TimeSpan.FromSeconds(7.5), ConnectTimeout = TimeSpan.FromSeconds(5) };
        Dictionary<string, string> options = NetworkVideoPolicy.CreateFfmpegOptions(policy).ToDictionary();

        Assert.Equal("5000000", options["rw_timeout"]);
        Assert.Equal("1", options["reconnect"]);
        Assert.Equal("4", options["reconnect_max_retries"]);
        Assert.Equal("8", options["reconnect_delay_max"]);
        Assert.Equal("4", options["seg_max_retry"]);
        Assert.Equal("0", options["reconnect_streamed"]);
    }

    [Fact]
    public void Retry_StopsAtTheFirstSuccess()
    {
        var policy = new NetworkRetryPolicy { MaxRetries = 5, InitialDelay = TimeSpan.FromSeconds(1) };
        List<TimeSpan> delays = [];

        NetworkOpenOutcome outcome = NetworkOpenRetry.Run(
            policy, CancellationToken.None, number => number == 3 ? NetworkAttemptResult.Opened : NetworkAttemptResult.Failed,
            (_, delay) => delays.Add(delay), out var attempts, wait: (_, _) => false);

        Assert.Equal(NetworkOpenOutcome.Opened, outcome);
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public void Retry_DefaultPolicy_MakesOneAttempt()
    {
        NetworkOpenOutcome outcome = NetworkOpenRetry.Run(
            NetworkRetryPolicy.Default, CancellationToken.None, _ => NetworkAttemptResult.Failed, null, out var attempts, wait: (_, _) => false);

        Assert.Equal(NetworkOpenOutcome.Failed, outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Retry_UnusableContent_IsNotRetried()
    {
        NetworkOpenOutcome outcome = NetworkOpenRetry.Run(
            new NetworkRetryPolicy { MaxRetries = 3 }, CancellationToken.None, _ => NetworkAttemptResult.Unusable, null, out var attempts, wait: (_, _) => false);

        Assert.Equal(NetworkOpenOutcome.Failed, outcome);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Retry_CancelledDuringADelay_StopsWithoutAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;

        NetworkOpenOutcome outcome = NetworkOpenRetry.Run(
            new NetworkRetryPolicy { MaxRetries = 3 }, cancellation.Token,
            _ =>
            {
                calls++;
                return NetworkAttemptResult.Failed;
            },
            null, out var attempts,
            wait: (_, _) =>
            {
                cancellation.Cancel();
                return true;
            });

        Assert.Equal(NetworkOpenOutcome.Cancelled, outcome);
        Assert.Equal(1, calls);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void Retry_CancelledDuringAnAttempt_ReportsCancelled()
    {
        using var cancellation = new CancellationTokenSource();

        NetworkOpenOutcome outcome = NetworkOpenRetry.Run(
            new NetworkRetryPolicy { MaxRetries = 3 }, cancellation.Token,
            _ =>
            {
                cancellation.Cancel();
                return NetworkAttemptResult.Failed;
            },
            null, out _, wait: (_, _) => false);

        Assert.Equal(NetworkOpenOutcome.Cancelled, outcome);
    }

    [Fact]
    public void StallTracker_EntersOnAStalledReadWithAnEmptyQueue_AndLeavesWhenRefilled()
    {
        var tracker = new NetworkStallBufferingTracker(TimeSpan.FromMilliseconds(500));
        var stalled = TimeSpan.FromSeconds(1);

        Assert.False(tracker.Update(TimeSpan.FromMilliseconds(100), 0, false, out _));
        Assert.False(tracker.IsBuffering);
        Assert.True(tracker.Update(stalled, 0, false, out var fill));
        Assert.True(tracker.IsBuffering);
        Assert.Equal(0, fill);
        Assert.False(tracker.Update(stalled, 0.05, false, out _));
        Assert.True(tracker.Update(TimeSpan.Zero, 0.1, false, out _));
        Assert.True(tracker.IsBuffering);
        Assert.True(tracker.Update(TimeSpan.Zero, 0.5, false, out fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(0.5, fill);
    }

    [Fact]
    public void StallTracker_IgnoresAStallWhileTheQueueIsFull()
    {
        var tracker = new NetworkStallBufferingTracker(TimeSpan.FromMilliseconds(500));

        Assert.False(tracker.Update(TimeSpan.FromSeconds(2), 1, false, out _));
        Assert.False(tracker.IsBuffering);
    }

    [Fact]
    public void StallTracker_EndOfStreamEndsBuffering()
    {
        var tracker = new NetworkStallBufferingTracker(TimeSpan.FromMilliseconds(500));
        Assert.True(tracker.Update(TimeSpan.FromSeconds(1), 0, false, out _));

        Assert.True(tracker.Update(TimeSpan.FromSeconds(1), 0, true, out var fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(1, fill);
    }
}
