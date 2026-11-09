using FatzMedia.Audio;
using Xunit;

namespace FatzMedia.Audio.Tests;

/// <summary>Writing loudness tags (<see cref="AudioEngine.WriteLoudnessTags(IReadOnlyList{string}, bool)"/>), on copies of the
/// files of tests/data/tags in a temp folder.</summary>
public class LoudnessTagsTests
{
    private static string FindTestFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(dir.FullName, relative);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(relative);
    }

    private static string CopyToTemp(string dir, string file)
    {
        string to = Path.Combine(dir, file);
        File.Copy(FindTestFile("tests/data/tags/" + file), to);
        return to;
    }

    private static string? Item(string path, string key) =>
        Tags.Read(path, TagReadOptions.NoInfo).Items.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    [Fact]
    public void Album_WritesTrackAndAlbumGain_AndKeepsOtherTags()
    {
        string dir = Directory.CreateTempSubdirectory("fa-lt-").FullName;
        try
        {
            string flac = CopyToTemp(dir, "tags.flac");
            string mp3 = CopyToTemp(dir, "id3v24.mp3");
            using var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true });
            var results = engine.WriteLoudnessTags([flac, mp3], album: true);
            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Written, r.Error.ToString()));
            Assert.Equal(TagFormat.Vorbis, results[0].TagFormat);
            Assert.Equal(TagFormat.Id3v2, results[1].TagFormat);
            Assert.Equal(results[0].AlbumLufs, results[1].AlbumLufs);
            Assert.Equal(-18.0 - results[0].IntegratedLufs, results[0].TrackGainDb, 9);
            Assert.Equal(FormattableString.Invariant($"{results[1].TrackGainDb:+0.00;-0.00} dB"), Item(mp3, "REPLAYGAIN_TRACK_GAIN"));
            Assert.NotNull(Item(flac, "REPLAYGAIN_ALBUM_GAIN"));
            Assert.Equal("Song Title", Tags.Read(flac).Title);
            Assert.Equal("The Artist", Tags.Read(mp3).Artist);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UnsupportedFormat_IsLeftAlone_AndTheSingleFileCallThrows()
    {
        string dir = Directory.CreateTempSubdirectory("fa-lt-").FullName;
        try
        {
            string aiff = CopyToTemp(dir, "tags.aiff");
            byte[] before = File.ReadAllBytes(aiff);
            using var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true });
            var r = engine.WriteLoudnessTags([aiff])[0];
            Assert.False(r.Written);
            Assert.Equal(ErrorCode.UnsupportedFormat, r.Error);
            Assert.Null(r.TagFormat);
            Assert.Equal(before, File.ReadAllBytes(aiff));
            var ex = Assert.Throws<AudioException>(() => engine.WriteLoudnessTags(aiff));
            Assert.Equal(ErrorCode.UnsupportedFormat, ex.ErrorCode);
            Assert.Throws<ArgumentException>(() => engine.WriteLoudnessTags(Array.Empty<string>()));
        }
        finally { Directory.Delete(dir, true); }
    }
}
