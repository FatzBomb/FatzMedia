using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Network;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 039A slice 2, the pure parts of the network read-ahead: buffer accounting, the
/// seek-in-range decision, end markers and loop wraps, starvation, the policy's clamps, and the
/// buffering hysteresis that the read-ahead feeds.
/// </summary>
public sealed class ReadAheadBufferTests
{
    private const int Video = 0;
    private const int Audio = 1;
    private const double FrameSeconds = 1 / 30.0;
    private const double AudioPacketSeconds = 1024 / 48000.0;

    private readonly List<int> _released = [];

    private ReadAheadBuffer<int> CreateBuffer(bool withAudio = true)
    {
        var buffer = new ReadAheadBuffer<int>(_released.Add);
        buffer.Register(Video, lead: true);
        if (withAudio)
            buffer.Register(Audio, lead: false);
        return buffer;
    }

    /// <summary>Adds <paramref name="seconds"/> of interleaved 30 fps video (a keyframe every 2 s) and 48 kHz AAC.</summary>
    private static void AddMedia(ReadAheadBuffer<int> buffer, double from, double seconds, bool withAudio = true, long videoBytes = 1000, long audioBytes = 100)
    {
        var audioTime = from;
        for (var frame = 0; frame < (int)Math.Round(seconds / FrameSeconds); frame++)
        {
            var time = from + (frame * FrameSeconds);
            var isKey = Math.Abs((time / 2.0) - Math.Round(time / 2.0)) < 1e-6;
            _ = buffer.Add(VideoId(time), new ReadAheadPacketInfo(Video, time, time, FrameSeconds, videoBytes, isKey));
            while (withAudio && audioTime < time + FrameSeconds - 1e-9)
            {
                _ = buffer.Add(AudioId(audioTime), new ReadAheadPacketInfo(Audio, audioTime, audioTime, AudioPacketSeconds, audioBytes, true));
                audioTime += AudioPacketSeconds;
            }
        }
    }

    // Packet ids encode the stream and the time in milliseconds, so tests can tell what is next.
    private static int VideoId(double time) => (int)Math.Round(time * 1000);

    private static int AudioId(double time) => 1_000_000 + (int)Math.Round(time * 1000);

    private static double TakeTime(ReadAheadBuffer<int> buffer, int stream)
    {
        Assert.Equal(ReadAheadTake.Packet, buffer.TryTake(stream, Stopwatch.GetTimestamp(), out var id));
        return (id % 1_000_000) / 1000.0;
    }

    [Fact]
    public void Accounting_MeasuresTheLeadStreamsTimeAndAllBytes()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 4);

        Assert.InRange(buffer.BufferedSeconds, 3.99, 4.01);
        var audioPackets = (int)Math.Ceiling(4 / AudioPacketSeconds);
        Assert.Equal((120 * 1000) + (audioPackets * 100), buffer.Bytes);

        // Taking packets lowers both; audio taken alone does not change the lead stream's time.
        for (var i = 0; i < 30; i++)
            _ = TakeTime(buffer, Video);
        Assert.InRange(buffer.BufferedSeconds, 2.99, 3.01);
        _ = TakeTime(buffer, Audio);
        Assert.InRange(buffer.BufferedSeconds, 2.99, 3.01);
        Assert.Equal((90 * 1000) + ((audioPackets - 1) * 100), buffer.Bytes);
    }

    [Fact]
    public void Accounting_WithoutAPictureMeasuresTheSound()
    {
        ReadAheadBuffer<int> buffer = new(_released.Add);
        buffer.Register(Audio, lead: false);
        for (var time = 0.0; time < 3; time += AudioPacketSeconds)
            _ = buffer.Add(AudioId(time), new ReadAheadPacketInfo(Audio, time, time, AudioPacketSeconds, 100, true));

        Assert.Equal(Audio, buffer.LeadStream);
        Assert.InRange(buffer.BufferedSeconds, 2.99, 3.03);
    }

    [Fact]
    public void Caps_TheFirstOneReachedMakesTheBufferFull()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 4);

        Assert.False(buffer.IsFull(maxSeconds: 20, maxBytes: 64L << 20));
        Assert.True(buffer.IsFull(maxSeconds: 4, maxBytes: 64L << 20));
        Assert.True(buffer.IsFull(maxSeconds: 20, maxBytes: buffer.Bytes));
    }

    [Fact]
    public void ResumeFill_ReachesOneAtTheResumeTimeAQuarterOfTheBytesOrACap()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer(withAudio: false);
        AddMedia(buffer, 0, 1, withAudio: false);

        Assert.InRange(buffer.ResumeFill(resumeSeconds: 2, maxSeconds: 20, maxBytes: 64L << 20), 0.49, 0.51);
        Assert.Equal(1, buffer.ResumeFill(resumeSeconds: 1, maxSeconds: 20, maxBytes: 64L << 20));
        Assert.Equal(1, buffer.ResumeFill(resumeSeconds: 2, maxSeconds: 20, maxBytes: buffer.Bytes * 4));
        Assert.Equal(1, buffer.ResumeFill(resumeSeconds: 2, maxSeconds: 1, maxBytes: 64L << 20));
        buffer.Flush();
        Assert.Equal(0, buffer.ResumeFill(resumeSeconds: 2, maxSeconds: 20, maxBytes: 64L << 20));
    }

    [Fact]
    public void UnregisteredStreams_AreReleasedNotQueued()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer(withAudio: false);

        Assert.False(buffer.Add(42, new ReadAheadPacketInfo(Audio, 0, 0, AudioPacketSeconds, 100, true)));
        Assert.Equal([42], _released);
        Assert.Equal(0, buffer.Bytes);
    }

    [Fact]
    public void EndMarker_EndsEachStreamAfterItsPackets_UntilCleared()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 0.1);
        buffer.AddEndOfStream();

        while (buffer.TryTake(Video, 1, out _) == ReadAheadTake.Packet)
        {
        }

        Assert.True(buffer.IsEnded(Video));
        Assert.Equal(ReadAheadTake.EndOfStream, buffer.TryTake(Video, 1, out _));
        // The sound still has its packets before its own marker.
        Assert.Equal(ReadAheadTake.Packet, buffer.TryTake(Audio, 1, out _));
        Assert.False(buffer.IsEnded(Audio));

        buffer.ClearEnd(Video);
        Assert.Equal(ReadAheadTake.Empty, buffer.TryTake(Video, 1, out _));
    }

    [Fact]
    public void LoopWrap_KeepsTheSoundsTailAndEndBeforeTheNextLap()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 9.5, 0.5);
        buffer.AddEndOfStream();
        while (buffer.TryTake(Video, 1, out _) == ReadAheadTake.Packet)
        {
        }

        // The picture's side of the wrap: its queue is emptied, the reader seeks to 0.
        buffer.FlushStream(Video);
        Assert.False(buffer.IsEnded(Video));
        AddMedia(buffer, 0, 0.5);

        Assert.Equal(0, TakeTime(buffer, Video));
        // The sound plays its tail, finds the end (and wraps), then the next lap.
        Assert.Equal(9.5, TakeTime(buffer, Audio), 3);
        var tail = 1;
        while (buffer.TryTake(Audio, 1, out _) == ReadAheadTake.Packet)
            tail++;
        Assert.True(buffer.IsEnded(Audio));
        Assert.InRange(tail, 20, 26);
        buffer.ClearEnd(Audio);
        Assert.Equal(0, TakeTime(buffer, Audio), 3);
    }

    [Fact]
    public void ClearEnd_AfterTheEnd_AwaitsTheNextLapUntilSomethingIsTaken()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        buffer.MarkEnded(Audio);
        buffer.ClearEnd(Audio);
        Assert.True(buffer.IsAwaitingNextLap(Audio));
        // Re-arming a stream that had not ended (a seek) awaits nothing.
        buffer.ClearEnd(Video);
        Assert.False(buffer.IsAwaitingNextLap(Video));

        AddMedia(buffer, 0, 0.1);
        _ = TakeTime(buffer, Audio);
        Assert.False(buffer.IsAwaitingNextLap(Audio));

        buffer.MarkEnded(Audio);
        buffer.ClearEnd(Audio);
        buffer.Flush();
        Assert.False(buffer.IsAwaitingNextLap(Audio));
    }

    [Fact]
    public void Starvation_IsTimedFromTheFirstEmptyTake_AndEndsWithAPacketOrTheEnd()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        var start = Stopwatch.GetTimestamp();

        Assert.Equal(ReadAheadTake.Empty, buffer.TryTake(Video, start, out _));
        Assert.True(buffer.HasStarvedStream);
        Assert.Equal(ReadAheadTake.Empty, buffer.TryTake(Video, start + Stopwatch.Frequency, out _));
        var later = start + (2 * Stopwatch.Frequency);
        Assert.InRange(buffer.LongestStarvation(later).TotalSeconds, 1.99, 2.01);

        AddMedia(buffer, 0, 0.1);
        _ = TakeTime(buffer, Video);
        Assert.False(buffer.HasStarvedStream);
        Assert.Equal(TimeSpan.Zero, buffer.LongestStarvation(later));

        while (buffer.TryTake(Video, later, out _) == ReadAheadTake.Packet)
        {
        }

        Assert.True(buffer.HasStarvedStream);
        buffer.AddEndOfStream();
        Assert.False(buffer.HasStarvedStream);
    }

    [Fact]
    public void SeekWithin_AForwardBufferedTarget_DropsToTheKeyframeAtOrBeforeIt()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 20);
        _ = TakeTime(buffer, Video);
        _ = TakeTime(buffer, Audio);
        var bytesBefore = buffer.Bytes;

        Assert.True(buffer.TrySeekWithin(Video, 7.3));

        // The picture restarts at the keyframe at 6 s; the sound at the first packet not ending before it.
        Assert.Equal(6, TakeTime(buffer, Video), 3);
        var audioStart = TakeTime(buffer, Audio);
        Assert.InRange(audioStart, 6 - AudioPacketSeconds, 6);
        Assert.True(buffer.Bytes < bytesBefore);
        Assert.Contains(VideoId(FrameSeconds), _released);
        Assert.InRange(buffer.BufferedSeconds, 13.9, 14.1);
    }

    [Fact]
    public void SeekWithin_ATargetOnAKeyframeStartsThere()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 10);

        Assert.True(buffer.TrySeekWithin(Video, 4));
        Assert.Equal(4, TakeTime(buffer, Video), 3);
    }

    [Theory]
    [InlineData(25.0)] // past the buffered packets
    [InlineData(3.0)] // behind the queue: its keyframe at 2 s was taken already
    [InlineData(double.NaN)]
    public void SeekWithin_ATargetOutsideTheBuffer_ChangesNothing(double target)
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 20);
        for (var i = 0; i < 70; i++) // the picture is at 2.33 s
            _ = TakeTime(buffer, Video);
        for (var i = 0; i < 100; i++)
            _ = TakeTime(buffer, Audio);
        var bytes = buffer.Bytes;

        Assert.False(buffer.TrySeekWithin(Video, target));
        Assert.Equal(bytes, buffer.Bytes);
        Assert.Empty(_released);
        Assert.Equal(70 * FrameSeconds, TakeTime(buffer, Video), 3);
    }

    [Fact]
    public void SeekWithin_IsRefusedWhenTheSoundAlreadyTookTheTargetsPackets()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 20);
        // The sound runs about 4 s ahead of the picture (its output buffer).
        while (TakeTime(buffer, Audio) < 4.5)
        {
        }

        Assert.False(buffer.TrySeekWithin(Video, 4.2));
        Assert.True(buffer.TrySeekWithin(Video, 6.5));
        Assert.Equal(6, TakeTime(buffer, Video), 3);
        // The sound was behind the keyframe: it skips to it too and decodes on to the target.
        Assert.InRange(TakeTime(buffer, Audio), 6 - AudioPacketSeconds, 6);
    }

    [Fact]
    public void SeekWithin_IsRefusedAfterAStreamEnded_AndWithoutTimes()
    {
        ReadAheadBuffer<int> ended = CreateBuffer(withAudio: false);
        AddMedia(ended, 0, 2, withAudio: false);
        ended.AddEndOfStream();
        while (ended.TryTake(Video, 1, out _) == ReadAheadTake.Packet)
        {
        }

        Assert.False(ended.TrySeekWithin(Video, 1));

        ReadAheadBuffer<int> untimed = CreateBuffer(withAudio: false);
        for (var i = 0; i < 60; i++)
            _ = untimed.Add(i, new ReadAheadPacketInfo(Video, null, null, 0, 1000, i % 30 == 0));
        Assert.False(untimed.TrySeekWithin(Video, 1.5));
        Assert.Equal(0, untimed.BufferedSeconds);
    }

    [Fact]
    public void Flush_ReleasesEverythingAndRearmsTheStreams()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 1);
        buffer.AddEndOfStream();
        var queued = 30 + (int)Math.Ceiling(1 / AudioPacketSeconds);

        buffer.Flush();

        Assert.Equal(queued, _released.Count);
        Assert.Equal(0, buffer.Bytes);
        Assert.Equal(0, buffer.BufferedSeconds);
        Assert.Equal(ReadAheadTake.Empty, buffer.TryTake(Video, 1, out _));
    }

    [Fact]
    public void Unregister_DropsTheStreamsPackets()
    {
        ReadAheadBuffer<int> buffer = CreateBuffer();
        AddMedia(buffer, 0, 1);
        var videoBytes = 30 * 1000;

        buffer.Unregister(Audio);

        Assert.False(buffer.IsRegistered(Audio));
        Assert.Equal(videoBytes, buffer.Bytes);
        Assert.Equal(ReadAheadTake.Empty, buffer.TryTake(Audio, 1, out _));
    }

    [Fact]
    public void Policy_DefaultsAndClamps()
    {
        NetworkReadAheadPolicy policy = NetworkReadAheadPolicy.Default;
        Assert.Equal(TimeSpan.FromSeconds(20), policy.MaxDuration);
        Assert.Equal(64L << 20, policy.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.ResumeDuration);

        var clamped = new NetworkReadAheadPolicy { MaxDuration = TimeSpan.Zero, MaxBytes = 1, ResumeDuration = TimeSpan.FromMinutes(5) };
        Assert.Equal(TimeSpan.FromSeconds(1), clamped.MaxDuration);
        Assert.Equal(1L << 20, clamped.MaxBytes);
        // The resume threshold never exceeds what the buffer can hold.
        Assert.Equal(TimeSpan.FromSeconds(1), clamped.ResumeDuration);
        Assert.Equal(TimeSpan.FromMinutes(10), (policy with { MaxDuration = TimeSpan.FromHours(1) }).MaxDuration);
        Assert.Equal(1L << 30, (policy with { MaxBytes = long.MaxValue }).MaxBytes);

        Assert.Equal(TimeSpan.FromSeconds(30), policy.ReconnectFor);
        Assert.Equal(TimeSpan.Zero, (policy with { ReconnectFor = TimeSpan.FromSeconds(-1) }).ReconnectFor);
        Assert.Equal(TimeSpan.FromMinutes(5), (policy with { ReconnectFor = TimeSpan.FromHours(1) }).ReconnectFor);
    }

    [Fact]
    public void Hysteresis_StartsWhenTheBufferRunsDry_AndEndsAtTheResumeThreshold()
    {
        var tracker = new NetworkStallBufferingTracker(NetworkStallBufferingTracker.DefaultStallThreshold, NetworkBufferingMonitor.ReadAheadExitFill);
        var dry = TimeSpan.FromSeconds(0.6);

        // A short gap in the packets is not a dry buffer.
        Assert.False(tracker.Update(TimeSpan.FromMilliseconds(200), 0, false, out _));
        Assert.True(tracker.Update(dry, 0, false, out var fill));
        Assert.True(tracker.IsBuffering);
        Assert.Equal(0, fill);

        // Packets flow again but less than the resume threshold is buffered: still buffering,
        // with progress reported every 10 points.
        Assert.True(tracker.Update(TimeSpan.Zero, 0.3, false, out fill));
        Assert.True(tracker.IsBuffering);
        Assert.Equal(0.3, fill);
        Assert.False(tracker.Update(TimeSpan.Zero, 0.35, false, out _));
        Assert.True(tracker.Update(TimeSpan.Zero, 0.95, false, out _));
        Assert.True(tracker.IsBuffering);

        Assert.True(tracker.Update(TimeSpan.Zero, 1, false, out fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(1, fill);

        // A long buffer never enters, whatever the consumer's last wait.
        Assert.False(tracker.Update(dry, 1, false, out _));
        Assert.False(tracker.IsBuffering);
    }

    [Fact]
    public void Hysteresis_EndOfTheSourceEndsBuffering()
    {
        var tracker = new NetworkStallBufferingTracker(NetworkStallBufferingTracker.DefaultStallThreshold, NetworkBufferingMonitor.ReadAheadExitFill);
        Assert.True(tracker.Update(TimeSpan.FromSeconds(1), 0, false, out _));

        Assert.True(tracker.Update(TimeSpan.Zero, 0.2, true, out var fill));
        Assert.False(tracker.IsBuffering);
        Assert.Equal(1, fill);
    }
}
