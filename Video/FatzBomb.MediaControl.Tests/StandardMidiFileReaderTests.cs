using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Subtitle;

namespace FatzBomb.MediaControl.Tests;

/// <summary>037A slice 1: MIDI lyrics and metadata read from the file itself, with no audio engine handle.</summary>
public sealed class StandardMidiFileReaderTests
{
    /// <summary>
    /// A two-tempo karaoke file: 120 BPM until tick 1920 (2 s), then 240 BPM; lyrics on track 1,
    /// notes on track 2 ending at tick 3840 (3 s).
    /// </summary>
    internal static byte[] CreateKaraokeFile() => new MidiTestFileWriter(480)
        .Track(static t => t
            .Name(0, "Conductor")
            .Copyright(0, "(c) 2026 Test")
            .Tempo(0, 500_000)
            .TimeSignature(0, 3, 2)
            .KeySignature(0, -3, 1)
            .Tempo(1920, 250_000))
        .Track(static t => t
            .Text(0, "@KMIDI KARAOKE FILE")
            .Text(0, "@TMy Song")
            .Text(0, "@TThe Artist")
            .Lyric(960, "Hel")
            .Lyric(1200, "lo ")
            .Lyric(1440, "world")
            .Lyric(2400, "/Sec")
            .Lyric(2640, "ond"))
        .Track(static t => t
            .SysEx(0)
            .Note(0, 3840))
        .ToArray();

    [Fact]
    public void Reads_MarksInOrder_WithBassTextForSignatures()
    {
        StandardMidiFileReader reader = Assert.IsType<StandardMidiFileReader>(StandardMidiFileReader.TryParse(CreateKaraokeFile()));

        Assert.Equal(["Hel", "lo ", "world", "/Sec", "ond"], reader.GetMarks(MidiMarkKind.Lyric).Select(static m => m.Text));
        Assert.All(reader.GetMarks(MidiMarkKind.Lyric), static m => Assert.Equal(1, m.Track));
        Assert.Equal(["@KMIDI KARAOKE FILE", "@TMy Song", "@TThe Artist"], reader.GetMarks(MidiMarkKind.Text).Select(static m => m.Text));
        Assert.Equal("Conductor", Assert.Single(reader.GetMarks(MidiMarkKind.TrackName)).Text);
        Assert.Equal("(c) 2026 Test", Assert.Single(reader.GetMarks(MidiMarkKind.Copyright)).Text);
        Assert.Equal("3/4 24 8", Assert.Single(reader.GetMarks(MidiMarkKind.TimeSignature)).Text);
        Assert.Equal("-3 1", Assert.Single(reader.GetMarks(MidiMarkKind.KeySignature)).Text);
        Assert.Equal([new MidiTempoEvent(0, 500_000), new MidiTempoEvent(1920, 250_000)], reader.GetTempoEvents());
    }

    [Fact]
    public void Times_FollowTheTempoMap()
    {
        StandardMidiFileReader reader = StandardMidiFileReader.TryParse(CreateKaraokeFile())!;

        Assert.Equal(1.0, reader.PositionToSeconds(960), 9);
        Assert.Equal(2.0, reader.PositionToSeconds(1920), 9);
        Assert.Equal(2.25, reader.PositionToSeconds(2400), 9);
        Assert.Equal(3.0, reader.GetDurationSeconds(), 9);
    }

    [Fact]
    public void Lyrics_AndMetadata_BuildFromTheFile()
    {
        IReadOnlyList<SubtitleCue>? cues = MidiLyricCueBuilder.ExtractLyrics(
            StandardMidiFileReader.TryParse(CreateKaraokeFile()),
            "song.kar",
            TimeSpan.FromSeconds(1));

        Assert.NotNull(cues);
        Assert.Contains(cues, static cue => cue.Text.Contains("Hello world", StringComparison.Ordinal));
        KaraokeMetadata metadata = cues[0].KaraokeTiming!.Metadata!;
        Assert.Equal("My Song", metadata.Title);
        Assert.Equal("The Artist", metadata.Artist);
        Assert.Equal("(c) 2026 Test", metadata.Copyright);
        Assert.Equal("Cm", metadata.KeySignature);
        Assert.Equal("120 BPM", metadata.Tempo);
        Assert.Equal("3/4", metadata.TimeSignature);
        Assert.Equal(TimeSpan.FromSeconds(3), metadata.Duration);
        Assert.False(metadata.IsFallback);
    }

    [Fact]
    public void RmidWrapper_IsUnwrapped()
    {
        byte[] rmi = MidiTestFileWriter.WrapRmid(CreateKaraokeFile());

        Assert.Equal(5, StandardMidiFileReader.TryParse(rmi)!.GetMarks(MidiMarkKind.Lyric).Count);
    }

    [Fact]
    public void LoadsFromPathAndArchiveEntry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fatzbomb-smf-{Guid.NewGuid():N}.kar");
        File.WriteAllBytes(path, CreateKaraokeFile());
        try
        {
            Assert.Equal(5, StandardMidiFileReader.TryLoad(path)!.GetMarks(MidiMarkKind.Lyric).Count);
            Assert.Equal(5, StandardMidiFileReader.TryLoad(new BytesArchiveEntry("x/song.kar", CreateKaraokeFile()))!.GetMarks(MidiMarkKind.Lyric).Count);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Null(StandardMidiFileReader.TryLoad(path));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 0x04, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 1, 0, 1, 0, 0 })]
    public void NotMidi_ReadsAsNull(byte[] data) => Assert.Null(StandardMidiFileReader.TryParse(data));

    [Fact]
    public void TruncatedTrack_KeepsTheEventsBeforeTheCut()
    {
        byte[] data = CreateKaraokeFile();
        StandardMidiFileReader reader = StandardMidiFileReader.TryParse(data.AsSpan(0, data.Length - 40))!;

        Assert.NotEmpty(reader.GetMarks(MidiMarkKind.Lyric));
    }

    [Fact]
    public void NonUtf8Text_DecodesAsLatin1()
    {
        byte[] data = new MidiTestFileWriter()
            .Track(static t => t.Meta(0, 0x05, [0x43, 0x61, 0x66, 0xE9]))
            .ToArray();

        Assert.Equal("Café", Assert.Single(StandardMidiFileReader.TryParse(data)!.GetMarks(MidiMarkKind.Lyric)).Text);
    }

    internal sealed class BytesArchiveEntry(string key, byte[] data) : FatzBomb.MediaControl.Core.Abstractions.IArchiveEntryInfo
    {
        public string Key => key;
        public long? Size => data.Length;
        public void WriteTo(Stream stream) => stream.Write(data);
    }
}
