using FatzMedia.Video.Core.Network;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-034 follow-ups, the pure parts: which engine opens network audio and its capability row,
/// which open failures are not retried, the clock hold of network video without sound, and the end
/// of a buffering spell when playback ends.
/// </summary>
public sealed class NetworkFollowUpPolicyTests
{
    [Theory]
    [InlineData("http://radio.example/stream", false)]
    [InlineData("http://radio.example/stream.mp3", false)]
    [InlineData("HTTP://radio.example/stream", false)]
    [InlineData("https://radio.example/stream", true)]
    [InlineData("HTTPS://radio.example/stream.mp3", true)]
    [InlineData("http://radio.example/live/index.m3u8", true)]
    [InlineData("https://radio.example/live/index.M3U8", true)]
    public void NetworkAudio_HttpsAndHlsOpenThroughFfmpeg_PlainHttpStaysOnTheAudioEngine(string address, bool expected) =>
        Assert.Equal(expected, NetworkAudioPolicy.OpensThroughFfmpeg(new Uri(address)));

    [Fact]
    public void NetworkAudioRow_LiveStream_IsLiveWithStreamMetadata()
    {
        var row = NetworkAudioPolicy.Resolve(new NetworkVideoProbe(false, null, false, 0));

        Assert.Equal(MediaSourceClass.NetworkLive, row.SourceClass);
        Assert.True(row.IsLive);
        Assert.False(row.IsSeekable);
        Assert.False(row.IsDurationKnown);
        Assert.True(row.IsBuffered);
        Assert.True(row.SupportsRetry);
        Assert.True(row.HasStreamMetadata);
        // The same rule as 014A's BASS row.
        Assert.Equal(MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(null, false, false)), row);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NetworkAudioRow_File_CarriesStreamMetadataOnlyWithIcyHeaders(bool icy, bool expected)
    {
        var row = NetworkAudioPolicy.Resolve(new NetworkVideoProbe(false, 320_000, true, 20, icy));

        Assert.Equal(MediaSourceClass.NetworkFile, row.SourceClass);
        Assert.True(row.IsSeekable);
        Assert.True(row.IsDurationKnown);
        Assert.Equal(expected, row.HasStreamMetadata);
    }

    [Fact]
    public void NetworkAudioRow_HlsPlaylist_FollowsTheVideoRuleWithoutStreamMetadata()
    {
        var live = NetworkAudioPolicy.Resolve(new NetworkVideoProbe(true, null, false, 0, HasIcyMetadata: true));
        var vod = NetworkAudioPolicy.Resolve(new NetworkVideoProbe(true, null, false, 30));

        Assert.Equal(MediaSourceClass.NetworkLive, live.SourceClass);
        Assert.False(live.HasStreamMetadata);
        Assert.Equal(MediaSourceClass.NetworkFile, vod.SourceClass);
        Assert.True(vod.IsSeekable);
    }

    [Fact]
    public void NetworkVideoRow_IgnoresIcyHeaders()
    {
        var row = NetworkVideoPolicy.Resolve(new NetworkVideoProbe(false, null, false, 0, HasIcyMetadata: true));

        Assert.False(row.HasStreamMetadata);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Connection refused", false)]
    [InlineData("certificate verification failed for media.example: certificate verify failed (Input/output error)", true)]
    [InlineData(NetworkVideoPolicy.UnstreamableMp4Error, true)]
    public void PermanentOpenErrors_AreCertificateRejectionsAndUnstreamableMp4s(string? error, bool expected) =>
        Assert.Equal(expected, NetworkVideoPolicy.IsPermanentOpenError(error));

    [Fact]
    public void UnstreamableMp4Error_NamesBothRemedies()
    {
        Assert.Contains("byte ranges", NetworkVideoPolicy.UnstreamableMp4Error, StringComparison.Ordinal);
        Assert.Contains("-movflags +faststart", NetworkVideoPolicy.UnstreamableMp4Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ClockHold_StartsOnceThePictureHasNoFrameAndHasStarved()
    {
        var starved = NetworkClockHold.StarvedFor;

        Assert.False(NetworkClockHold.ShouldHold(held: false, frameReady: true, starved * 4, resumeFill: 0, atEnd: false));
        Assert.False(NetworkClockHold.ShouldHold(held: false, frameReady: false, starved / 2, resumeFill: 0, atEnd: false));
        Assert.True(NetworkClockHold.ShouldHold(held: false, frameReady: false, starved, resumeFill: 0, atEnd: false));
        Assert.False(NetworkClockHold.ShouldHold(held: false, frameReady: false, starved * 4, resumeFill: 0, atEnd: true));
    }

    [Fact]
    public void ClockHold_EndsWhenAFrameIsReadyAndTheBufferRefilled_OrAtTheEnd()
    {
        Assert.True(NetworkClockHold.ShouldHold(held: true, frameReady: true, TimeSpan.Zero, resumeFill: 0.6, atEnd: false));
        Assert.True(NetworkClockHold.ShouldHold(held: true, frameReady: false, TimeSpan.Zero, resumeFill: 1, atEnd: false));
        Assert.False(NetworkClockHold.ShouldHold(held: true, frameReady: true, TimeSpan.Zero, resumeFill: 1, atEnd: false));
        Assert.False(NetworkClockHold.ShouldHold(held: true, frameReady: false, TimeSpan.Zero, resumeFill: 0, atEnd: true));
    }

    [Fact]
    public void HoldableClock_HoldsAndReleasesWhereItStopped()
    {
        var inner = new PlaybackClock();
        var clock = new HoldableClock(inner);
        clock.Start(5);

        Assert.True(clock.Hold());
        Assert.True(clock.IsHeld);
        Assert.False(inner.IsRunning);
        var heldAt = clock.PositionSeconds;
        Thread.Sleep(30);
        Assert.Equal(heldAt, clock.PositionSeconds);
        Assert.False(clock.Hold());

        Assert.True(clock.Release());
        Assert.False(clock.IsHeld);
        Assert.True(inner.IsRunning);
        Assert.InRange(clock.PositionSeconds, heldAt, heldAt + 1);
        Assert.False(clock.Release());
    }

    [Fact]
    public void HoldableClock_DoesNotHoldAStoppedClock()
    {
        var clock = new HoldableClock(new PlaybackClock());

        Assert.False(clock.Hold());
        Assert.False(clock.IsHeld);
    }

    [Fact]
    public void HoldableClock_TransportChangesEndAHold()
    {
        var inner = new PlaybackClock();
        var clock = new HoldableClock(inner);

        clock.Start(1);
        Assert.True(clock.Hold());
        clock.Pause();
        Assert.False(clock.IsHeld);
        // A later release must not restart a clock the transport paused.
        Assert.False(clock.Release());
        Assert.False(inner.IsRunning);

        clock.Start(2);
        Assert.True(clock.Hold());
        clock.Seek(3);
        Assert.False(clock.IsHeld);
        Assert.Equal(3, clock.PositionSeconds);

        clock.Start();
        Assert.True(clock.Hold());
        clock.Stop();
        Assert.False(clock.IsHeld);
        Assert.Equal(0, clock.PositionSeconds);
    }

    [Fact]
    public void StallTracker_EndSpell_EndsBufferingOnce()
    {
        var tracker = new NetworkStallBufferingTracker(TimeSpan.FromMilliseconds(500));
        Assert.False(tracker.EndSpell(out _));
        Assert.True(tracker.Update(TimeSpan.FromSeconds(1), 0, false, out _));

        Assert.True(tracker.EndSpell(out var fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(1, fill);
        Assert.False(tracker.EndSpell(out _));
    }

    [Fact]
    public void BufferingMonitor_PlaybackEndingDuringBuffering_RaisesBufferingFalse()
    {
        var gate = new Lock();
        var sample = new NetworkBufferingSample(true, TimeSpan.FromSeconds(2), 0, false);
        void SetSample(NetworkBufferingSample next)
        {
            lock (gate)
                sample = next;
        }

        NetworkBufferingSample GetSample()
        {
            lock (gate)
                return sample;
        }

        var changes = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        using var monitor = new NetworkBufferingMonitor(GetSample, args => changes.Enqueue(args.IsBuffering), NetworkBufferingMonitor.ReadAheadExitFill);

        Assert.True(SpinWait.SpinUntil(() => changes.Contains(true), TimeSpan.FromSeconds(5)), "no buffering start");
        // Paused: the state is left as it is.
        SetSample(new NetworkBufferingSample(false, TimeSpan.FromSeconds(2), 0, false));
        Thread.Sleep(600);
        Assert.Equal([true], changes);

        SetSample(new NetworkBufferingSample(false, TimeSpan.FromSeconds(2), 0, false, PlaybackEnded: true));
        Assert.True(SpinWait.SpinUntil(() => changes.Count == 2, TimeSpan.FromSeconds(5)), "no buffering end");
        Thread.Sleep(600);
        Assert.Equal([true, false], changes);
    }

    [Fact]
    public void BufferingMonitor_RunsItsPollOnEveryTick()
    {
        var ticks = 0;
        using var monitor = new NetworkBufferingMonitor(() => default, _ => { }, onTick: () => Interlocked.Increment(ref ticks));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 2, TimeSpan.FromSeconds(5)), $"ticks {ticks}");
    }
}
