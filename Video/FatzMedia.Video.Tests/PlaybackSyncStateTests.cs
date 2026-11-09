using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

public class PlaybackSyncStateTests
{
    [Fact]
    public void AdjustOffsets_MaintainsIndependentValues()
    {
        var sync = new PlaybackSyncState();

        Assert.Equal(0.1, sync.AdjustAudioVideoOffset(0.1), 6);
        Assert.Equal(-0.2, sync.AdjustCdgOffset(-0.2), 6);
        Assert.Equal(0.3, sync.AdjustSubtitleOffset(0.3), 6);

        Assert.Equal(0.1, sync.AudioVideoOffsetSeconds, 6);
        Assert.Equal(-0.2, sync.CdgOffsetSeconds, 6);
        Assert.Equal(0.3, sync.SubtitleOffsetSeconds, 6);
    }

    [Fact]
    public void SetAndReset_ClampsAndClearsAllOffsets()
    {
        var sync = new PlaybackSyncState();

        Assert.Equal(30, sync.SetAudioVideoOffset(100));
        Assert.Equal(-30, sync.SetCdgOffset(-100));
        _ = sync.SetSubtitleOffset(5);

        sync.Reset();

        Assert.Equal(0, sync.AudioVideoOffsetSeconds);
        Assert.Equal(0, sync.CdgOffsetSeconds);
        Assert.Equal(0, sync.SubtitleOffsetSeconds);
    }
}
