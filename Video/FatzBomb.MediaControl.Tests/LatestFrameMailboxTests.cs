namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-039: Core's latest-frame hand-off, shared by the WPF, Avalonia and WinForms frame sinks.
/// </summary>
public sealed class LatestFrameMailboxTests
{
    [Fact]
    public void Post_AsksForOnePublish_UntilTheFrameIsTaken()
    {
        var mailbox = new LatestFrameMailbox();

        Assert.True(mailbox.Post(Frame(4, 2, fill: 1), 4, 2, 16));
        Assert.False(mailbox.Post(Frame(4, 2, fill: 2), 4, 2, 16));

        Assert.True(mailbox.TryTake(out var pixels, out var width, out var height, out var stride));
        Assert.Equal((4, 2, 16), (width, height, stride));
        Assert.Equal(2, pixels[0]); // latest wins
        Assert.False(mailbox.TryTake(out _, out _, out _, out _));

        // Taking re-arms the request.
        Assert.True(mailbox.Post(Frame(4, 2), 4, 2, 16));
    }

    [Fact]
    public void SteadyFrames_ReuseTwoArrays()
    {
        var mailbox = new LatestFrameMailbox();
        var frame = Frame(4, 4);
        for (var i = 0; i < 10; i++)
        {
            _ = mailbox.Post(frame, 4, 4, 16);
            Assert.True(mailbox.TryTake(out var pixels, out _, out _, out _));
            mailbox.Return(pixels);
        }

        Assert.Equal(1, mailbox.BufferAllocations);

        // A frame posted while the UI thread still holds one gets the second array.
        _ = mailbox.Post(frame, 4, 4, 16);
        Assert.True(mailbox.TryTake(out var held, out _, out _, out _));
        _ = mailbox.Post(frame, 4, 4, 16);
        mailbox.Return(held);
        Assert.True(mailbox.TryTake(out var next, out _, out _, out _));
        mailbox.Return(next);
        Assert.NotSame(held, next);
        Assert.Equal(2, mailbox.BufferAllocations);
    }

    [Fact]
    public void Spare_GrowsForLargerFrames_AndIsDroppedWhenMoreThanTwiceTheNeed()
    {
        var mailbox = new LatestFrameMailbox();
        Publish(mailbox, 4, 2);
        Publish(mailbox, 4, 2);
        Assert.Equal(1, mailbox.BufferAllocations);

        Publish(mailbox, 8, 4);
        Publish(mailbox, 6, 4); // fits the larger array, within 2x
        Assert.Equal(2, mailbox.BufferAllocations);

        Publish(mailbox, 1, 1); // the spare is 32x the need (after a maximize): dropped
        Assert.Equal(3, mailbox.BufferAllocations);
    }

    [Fact]
    public void DropPending_KeepsTheArray_AndAQueuedPublishFindsNothing()
    {
        var mailbox = new LatestFrameMailbox();
        Assert.True(mailbox.Post(Frame(4, 2), 4, 2, 16));

        mailbox.DropPending();

        Assert.False(mailbox.TryTake(out _, out _, out _, out _));
        Publish(mailbox, 4, 2);
        Assert.Equal(1, mailbox.BufferAllocations);
    }

    [Fact]
    public void CancelPublishRequest_KeepsTheFrame_AndTheNextPostAsksAgain()
    {
        var mailbox = new LatestFrameMailbox();
        Assert.True(mailbox.Post(Frame(4, 2, fill: 1), 4, 2, 16));

        mailbox.CancelPublishRequest();

        Assert.True(mailbox.Post(Frame(4, 2, fill: 2), 4, 2, 16));
        Assert.True(mailbox.TryTake(out var pixels, out _, out _, out _));
        Assert.Equal(2, pixels[0]);
    }

    [Fact]
    public void Close_DropsFrames_AndIsIdempotent()
    {
        var mailbox = new LatestFrameMailbox();
        _ = mailbox.Post(Frame(4, 2), 4, 2, 16);

        Assert.True(mailbox.Close());
        Assert.False(mailbox.Close());

        Assert.False(mailbox.TryTake(out _, out _, out _, out _));
        Assert.False(mailbox.Post(Frame(4, 2), 4, 2, 16));
        mailbox.Return(new byte[32]); // ignored after close
    }

    [Theory]
    [InlineData(0, 2, 16)]
    [InlineData(4, 0, 16)]
    [InlineData(4, 2, 15)] // stride shorter than a row
    [InlineData(4, 2, 32)] // span shorter than stride x height
    [InlineData(1, int.MaxValue, int.MaxValue)] // more bytes than an array holds: no overflow
    [InlineData(int.MaxValue, 1, int.MaxValue)] // width x 4 overflows int
    public void Post_DropsInvalidFrames(int width, int height, int stride)
    {
        var mailbox = new LatestFrameMailbox();

        Assert.False(mailbox.Post(new byte[32], width, height, stride));
        Assert.False(mailbox.TryTake(out _, out _, out _, out _));
        Assert.Equal(0, mailbox.BufferAllocations);
    }

    [Fact]
    public async Task ConcurrentPostAndTake_NeverHandsOneArrayToBothSides()
    {
        // The presentation thread posts while the "UI" thread takes, copies and returns: a frame
        // the UI side reads is never overwritten while it holds it.
        var mailbox = new LatestFrameMailbox();
        const int Frames = 20_000;
        var torn = 0;
        var producer = Task.Run(() =>
        {
            var frame = new byte[16 * 16 * 4];
            for (var i = 0; i < Frames; i++)
            {
                Array.Fill(frame, (byte)i);
                _ = mailbox.Post(frame, 16, 16, 64);
            }
        });

        var consumer = Task.Run(() =>
        {
            while (true)
            {
                var done = producer.IsCompleted;
                if (!mailbox.TryTake(out var pixels, out _, out _, out var stride))
                {
                    if (done)
                        return;
                    continue;
                }

                var first = pixels[0];
                for (var i = 0; i < 16 * stride; i += 97)
                {
                    if (pixels[i] != first)
                        _ = Interlocked.Increment(ref torn);
                }

                mailbox.Return(pixels);
            }
        });

        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, torn);
        Assert.InRange(mailbox.BufferAllocations, 1, 2);
    }

    [Fact]
    public void Post_UsesTheSecondSpare_WhenTheFirstIsTooLarge()
    {
        var mailbox = new LatestFrameMailbox();
        Assert.True(mailbox.Post(Frame(8, 8), 8, 8, 32));
        Assert.True(mailbox.TryTake(out var large, out _, out _, out _));
        _ = mailbox.Post(Frame(4, 2), 4, 2, 16);
        mailbox.Return(large);
        Assert.True(mailbox.TryTake(out var small, out _, out _, out _));
        mailbox.Return(small);
        Assert.Equal(2, mailbox.BufferAllocations);

        // The large spare is more than twice the need; the small one fits and is reused.
        Publish(mailbox, 4, 2);

        Assert.Equal(2, mailbox.BufferAllocations);
    }

    private static void Publish(LatestFrameMailbox mailbox, int width, int height)
    {
        _ = mailbox.Post(Frame(width, height), width, height, width * 4);
        Assert.True(mailbox.TryTake(out var pixels, out _, out _, out _));
        mailbox.Return(pixels);
    }

    private static byte[] Frame(int width, int height, byte fill = 0)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, fill);
        return pixels;
    }
}
