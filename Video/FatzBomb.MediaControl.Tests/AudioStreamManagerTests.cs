using FatzBomb.MediaControl.Core.Audio;
using ManagedBass;

namespace FatzBomb.MediaControl.Tests;

public sealed class AudioStreamManagerTests
{
    [Theory]
    [InlineData("track.mod")]
    [InlineData("track.it")]
    [InlineData("track.s3m")]
    [InlineData("track.xm")]
    [InlineData("track.mo3")]
    [InlineData("track.mtm")]
    [InlineData("track.umx")]
    [InlineData("TRACK.MOD")]
    public void TrackerFormats_UsePrescannedDecodeSource(string path)
    {
        BassFlags flags = AudioStreamManager.GetPlaybackSourceFlags(path);

        Assert.True(AudioStreamManager.IsTrackerFormat(path));
        Assert.True(flags.HasFlag(BassFlags.Decode));
        Assert.True(flags.HasFlag(BassFlags.Float));
        Assert.True(flags.HasFlag(BassFlags.Prescan));
        Assert.True(flags.HasFlag(BassFlags.MusicPositionReset));
        Assert.True(flags.HasFlag(BassFlags.MusicStopBack));
        Assert.False(flags.HasFlag(BassFlags.Loop));
    }

    [Theory]
    [InlineData("track.mid")]
    [InlineData("track.midi")]
    [InlineData("track.kar")]
    [InlineData("track.mp3")]
    public void NonTrackerFormats_KeepExistingPlaybackFlags(string path)
    {
        BassFlags flags = AudioStreamManager.GetPlaybackSourceFlags(path);

        Assert.False(AudioStreamManager.IsTrackerFormat(path));
        Assert.True(flags.HasFlag(BassFlags.Decode));
        Assert.True(flags.HasFlag(BassFlags.Float));
        Assert.False(flags.HasFlag(BassFlags.Prescan));
        Assert.False(flags.HasFlag(BassFlags.MusicStopBack));
        Assert.False(flags.HasFlag(BassFlags.Loop));
    }
}
