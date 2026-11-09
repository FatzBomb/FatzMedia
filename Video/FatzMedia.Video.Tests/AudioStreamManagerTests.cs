using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using ManagedBass;

namespace FatzMedia.Video.Tests;

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
        var flags = AudioStreamManager.GetPlaybackSourceFlags(path);

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
        var flags = AudioStreamManager.GetPlaybackSourceFlags(path);

        Assert.False(AudioStreamManager.IsTrackerFormat(path));
        Assert.True(flags.HasFlag(BassFlags.Decode));
        Assert.True(flags.HasFlag(BassFlags.Float));
        Assert.False(flags.HasFlag(BassFlags.Prescan));
        Assert.False(flags.HasFlag(BassFlags.MusicStopBack));
        Assert.False(flags.HasFlag(BassFlags.Loop));
    }

    // ENG-039 P5-9: an entry of known size is read into one array, not a stream buffer plus a copy.
    [Fact]
    public void ReadArchiveEntry_OfKnownSize_HoldsTheAudioOnce()
    {
        var audio = new byte[4 * 1024 * 1024];
        new Random(39).NextBytes(audio);
        var entry = new Entry(audio, audio.Length);
        _ = AudioStreamManager.ReadArchiveEntry(new Entry([1, 2, 3], 3));

        var before = GC.GetAllocatedBytesForCurrentThread();
        (var data, var length) = AudioStreamManager.ReadArchiveEntry(entry);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(audio.Length, length);
        Assert.Equal(audio, data);
        Assert.True(allocated < audio.Length + (64 * 1024), $"{allocated} bytes for a {audio.Length}-byte entry");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(10L)]
    [InlineData(100_000L)]
    public void ReadArchiveEntry_OfWrongOrUnknownSize_ReturnsExactlyTheEntry(long? reportedSize)
    {
        var audio = new byte[50_000];
        new Random(5).NextBytes(audio);

        (var data, var length) = AudioStreamManager.ReadArchiveEntry(new Entry(audio, reportedSize));

        Assert.Equal(audio.Length, length);
        Assert.Equal(audio.Length, data.Length);
        Assert.Equal(audio, data);
    }

    sealed class Entry(byte[] data, long? size) : IArchiveEntryInfo
    {
        public string Key => "entry.mp3";

        public long? Size => size;

        public void WriteTo(Stream stream) => stream.Write(data);
    }
}
