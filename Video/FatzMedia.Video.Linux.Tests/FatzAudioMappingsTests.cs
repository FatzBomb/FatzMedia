using FatzMedia.Video.Audio;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>037A slice 2: how our audio settings map onto FatzAudio's (pure; no native calls).</summary>
public sealed class FatzAudioMappingsTests
{
    [Theory]
    [InlineData(1.0, 1.4142)]
    [InlineData(2.0, 0.6667)]
    [InlineData(0.5, 2.8710)]
    public void Bandwidth_BecomesTheCookbookQ(double octaves, double q) =>
        Assert.Equal(q, FatzAudioMappings.BandwidthToQ(octaves), 3);

    [Fact]
    public void Equalizer_MapsEachBandToAPeak_AndOffOrFlatToNoBands()
    {
        var bands = FatzAudioMappings.ToEqualizerBands(AudioEqualizerSettings.CreateGraphic10Band(6, 0, -3));

        Assert.Equal(10, bands.Length);
        Assert.All(bands, static band => Assert.Equal(Fa.EqualizerBandType.Peak, band.Type));
        Assert.Equal(31.5f, bands[0].Frequency);
        Assert.Equal(6f, bands[0].GainDb);
        Assert.Equal(-3f, bands[2].GainDb);
        Assert.Equal(1.414f, bands[0].Q, 3);
        Assert.Empty(FatzAudioMappings.ToEqualizerBands(AudioEqualizerSettings.Disabled));
        Assert.Empty(FatzAudioMappings.ToEqualizerBands(AudioEqualizerSettings.CreateGraphic10Band()));
        Assert.Empty(FatzAudioMappings.ToEqualizerBands(AudioEqualizerSettings.CreateGraphic10Band(6) with { Enabled = false }));
    }

    [Theory]
    [InlineData(0, 1.0f)]
    [InlineData(25, 1.25f)]
    [InlineData(-50, 0.5f)]
    [InlineData(-200, 0.05f)]
    public void Tempo_PercentBecomesAMultiplier(int percent, float multiplier) =>
        Assert.Equal(multiplier, FatzAudioMappings.ToTempo(percent), 4);

    [Fact]
    public void SilenceOptions_KeepOurThresholdAndMinimum_WithRmsAnd200msSounds()
    {
        var options = FatzAudioMappings.ToSilenceOptions(new SilenceTrimOptions
        {
            Enabled = true,
            ThresholdDecibels = -42,
            MinimumSilenceDuration = TimeSpan.FromSeconds(0.75),
        });

        Assert.Equal(Fa.SilenceMode.Rms, options.Mode);
        Assert.Equal(-42f, options.ThresholdDb);
        Assert.Equal(TimeSpan.FromSeconds(0.75), options.MinLength);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.MinSound);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.Window);
    }

    [Fact]
    public void EdgeRegions_RunToTheFirstAndFromTheLastAudibleFrame()
    {
        Assert.Equal(
            [new SilenceRegion(0, 2), new SilenceRegion(5, 7)],
            FatzAudioMappings.ToEdgeRegions(firstAudible: 88200, lastAudible: 220500, lengthFrames: 308700, sampleRate: 44100, durationSeconds: 7));
        Assert.Empty(FatzAudioMappings.ToEdgeRegions(0, 308700, 308700, 44100, 7));
        Assert.Equal([new SilenceRegion(0, 7)], FatzAudioMappings.ToEdgeRegions(null, null, 308700, 44100, 7));
    }

    [Theory]
    [InlineData(Fa.MidiTextFlags.None, "word")]
    [InlineData(Fa.MidiTextFlags.NewLine, "/word")]
    [InlineData(Fa.MidiTextFlags.NewParagraph, "\\word")]
    [InlineData(Fa.MidiTextFlags.NewParagraph | Fa.MidiTextFlags.NewLine, "\\word")]
    public void Lyrics_GetTheirSoftKaraokeMarksBack(Fa.MidiTextFlags flags, string expected) =>
        Assert.Equal(expected, FatzAudioMidiEventReader.WithLayoutMarks(new Fa.MidiText(0, Fa.MidiTextType.Lyric, "word", flags, TimeSpan.Zero, 0, 0)));

    [Fact]
    public void CacheDirectory_DefaultsPerUser_EmptyTurnsItOff_AnythingElseIsUsed()
    {
        static string? Env(string _) => null;

        Assert.Equal(Path.Combine("C:/Users/u/AppData/Local", "FatzMedia.Video", "AudioCache"),
            FatzAudioMappings.ResolveCacheDirectory(null, isWindows: true, Env, "C:/Users/u/AppData/Local", "C:/Users/u"));
        Assert.Equal(Path.Combine("/home/u", ".cache", "fatzmedia-video", "audio"),
            FatzAudioMappings.ResolveCacheDirectory(null, isWindows: false, Env, "/home/u/.local/share", "/home/u"));
        Assert.Equal(Path.Combine("/xdg", "fatzmedia-video", "audio"),
            FatzAudioMappings.ResolveCacheDirectory(null, isWindows: false, static _ => "/xdg", "", "/home/u"));
        // A relative XDG_CACHE_HOME is ignored, as the XDG spec says.
        Assert.Equal(Path.Combine("/home/u", ".cache", "fatzmedia-video", "audio"),
            FatzAudioMappings.ResolveCacheDirectory(null, isWindows: false, static _ => "relative", "", "/home/u"));
        Assert.Null(FatzAudioMappings.ResolveCacheDirectory("", isWindows: true, Env, "C:/x", "C:/u"));
        Assert.Null(FatzAudioMappings.ResolveCacheDirectory("  ", isWindows: false, Env, "", "/home/u"));
        Assert.Equal(Path.GetFullPath("/my/cache"), FatzAudioMappings.ResolveCacheDirectory("/my/cache", isWindows: false, Env, "", "/home/u"));
        Assert.Equal(Path.GetFullPath("cache"), FatzAudioMappings.ResolveCacheDirectory("cache", isWindows: false, Env, "", "/home/u"));
    }

    [Theory]
    [InlineData(-20, true, -14, 6)]
    [InlineData(-8, true, -14, -6)]
    [InlineData(-50, true, -14, 12)]
    [InlineData(-20, false, -14, 0)]
    [InlineData(double.NegativeInfinity, true, -14, 0)]
    public void TrackGain_IsTargetMinusLoudness_WithinFatzAudiosLimits(double lufs, bool isFinal, double target, double gainDb) =>
        Assert.Equal(gainDb, FatzAudioMappings.TrackGainDecibels(lufs, isFinal, target), 6);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6.0206, 2)]
    [InlineData(-6.0206, 0.5)]
    [InlineData(double.NaN, 1)]
    public void NormalizationGain_IsTheDecibelGainAsAMultiplier(double gainDb, double gain) =>
        Assert.Equal(gain, FatzAudioMappings.GainFromDecibels(gainDb), 4);
}
