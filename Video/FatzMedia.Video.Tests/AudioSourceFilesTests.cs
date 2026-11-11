using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

public sealed class AudioSourceFilesTests
{
    [Theory]
    [InlineData("track.mod", true)]
    [InlineData("track.it", true)]
    [InlineData("track.s3m", true)]
    [InlineData("track.xm", true)]
    [InlineData("track.mo3", true)]
    [InlineData("track.mtm", true)]
    [InlineData("track.umx", true)]
    [InlineData("TRACK.MOD", true)]
    [InlineData("track.mid", false)]
    [InlineData("track.kar", false)]
    [InlineData("track.mp3", false)]
    public void IsTrackerFormat_KnowsTheTrackerExtensions(string path, bool expected) =>
        Assert.Equal(expected, AudioSourceFiles.IsTrackerFormat(path));
    // ENG-039 P5-9: an entry of known size is read into one array, not a stream buffer plus a copy.
    [Fact]
    public void ReadArchiveEntry_OfKnownSize_HoldsTheAudioOnce()
    {
        var audio = new byte[4 * 1024 * 1024];
        new Random(39).NextBytes(audio);
        var entry = new Entry(audio, audio.Length);
        _ = AudioSourceFiles.ReadArchiveEntry(new Entry([1, 2, 3], 3));

        var before = GC.GetAllocatedBytesForCurrentThread();
        (var data, var length) = AudioSourceFiles.ReadArchiveEntry(entry);
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

        (var data, var length) = AudioSourceFiles.ReadArchiveEntry(new Entry(audio, reportedSize));

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
