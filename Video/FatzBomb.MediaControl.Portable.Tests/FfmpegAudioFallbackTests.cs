using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Files;
using System.Buffers.Binary;
using System.Text;

namespace FatzBomb.MediaControl.Portable.Tests;

/// <summary>
/// Stage 033A slice 2: audio formats no BASS plugin claims open through an FFmpeg conversion.
/// The extension sets are process-wide, so each test registers its own made-up extension.
/// </summary>
public sealed class FfmpegAudioFallbackTests
{
    [Fact]
    public void FloatWavHeader_DescribesThe32BitFloatData()
    {
        var header = new byte[FloatWav.HeaderBytes];
        FloatWav.WriteHeader(header, sampleRate: 48000, channels: 2, dataBytes: 48000 * 2 * 4);

        Assert.Equal("RIFF", Ascii(header, 0));
        Assert.Equal((uint)(FloatWav.HeaderBytes - 8 + (48000 * 8)), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal("WAVE", Ascii(header, 8));
        Assert.Equal("fmt ", Ascii(header, 12));
        Assert.Equal(18u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22)));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)));
        Assert.Equal(48000u * 8, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(36)));
        Assert.Equal("fact", Ascii(header, 38));
        Assert.Equal(48000u, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(46)));
        Assert.Equal("data", Ascii(header, 50));
        Assert.Equal(48000u * 8, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(54)));
    }

    [Fact]
    public void FloatWavHeader_RejectsDataBeyondTheRiffLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FloatWav.WriteHeader(new byte[FloatWav.HeaderBytes], 44100, 2, uint.MaxValue));

    [Fact]
    public void ExtensionOnlyFfmpegDecodes_RequiresConversionAndIsPlayable()
    {
        const string extension = ".fbfallbacka";
        _ = FileTypes.FfmpegAudio.Add(extension);
        try
        {
            Assert.True(FfmpegAudioFallback.RequiresConversion($"song{extension}"));
            Assert.True(FfmpegAudioFallback.CanRetryWithFfmpeg($"song{extension}"));
            Assert.True(FileTypes.IsPlayableAudio($"song{extension}"));
            Assert.False(FileTypes.IsAudio($"song{extension}"));
            Assert.Contains(extension, AcceptedMediaTypes.FromCurrentRuntime().Audio);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(extension);
        }
    }

    [Fact]
    public void ExtensionABassPluginClaims_StaysOnBassButCanRetryWithFfmpeg()
    {
        const string extension = ".fbfallbackb";
        _ = FileTypes.Audio.Add(extension);
        _ = FileTypes.FfmpegAudio.Add(extension);
        try
        {
            Assert.False(FfmpegAudioFallback.RequiresConversion($"song{extension}"));
            Assert.True(FfmpegAudioFallback.CanRetryWithFfmpeg($"song{extension}"));
        }
        finally
        {
            _ = FileTypes.Audio.Remove(extension);
            _ = FileTypes.FfmpegAudio.Remove(extension);
        }
    }

    [Fact]
    public void UnknownExtension_IsNotPlayableAudio()
    {
        Assert.False(FfmpegAudioFallback.RequiresConversion("notes.fbunknown"));
        Assert.False(FfmpegAudioFallback.CanRetryWithFfmpeg("notes.fbunknown"));
        Assert.False(FileTypes.IsPlayableAudio("notes.fbunknown"));
    }

    [Fact]
    public void FileTree_RoutesFallbackAudioAndPairsItWithCdg()
    {
        const string extension = ".fbfallbackc";
        _ = FileTypes.FfmpegAudio.Add(extension);
        var directory = Directory.CreateTempSubdirectory("fatzbomb-fallback-");
        try
        {
            var audio = Path.Combine(directory.FullName, $"song{extension}");
            var cdg = Path.Combine(directory.FullName, "song.cdg");
            File.WriteAllBytes(audio, [1]);
            File.WriteAllBytes(cdg, [1]);

            using FileTree audioTree = FileTreeBuilder.GetFileTree(audio, new NoArchiveService(), searchCompanionFile: false);
            Assert.Equal(FileType.Audio, audioTree.Type);
            Assert.Equal(audio, audioTree.AudioPath);

            using FileTree cdgTree = FileTreeBuilder.GetFileTree(cdg, new NoArchiveService());
            Assert.Equal(FileType.AudioCdg, cdgTree.Type);
            Assert.Equal(audio, cdgTree.AudioPath);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(extension);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RegisterFfmpegAudio_AddsCommonFormatsBassMayLack()
    {
        Assert.Contains(".ape", FileTypes.FfmpegAudioExtensions);
        Assert.Contains(".wv", FileTypes.FfmpegAudioExtensions);
        Assert.Contains(".m4a", FileTypes.FfmpegAudioExtensions);
        Assert.Contains(".opus", FileTypes.FfmpegAudioExtensions);
        Assert.DoesNotContain(".mp4", FileTypes.FfmpegAudioExtensions);
        Assert.DoesNotContain(".mid", FileTypes.FfmpegAudioExtensions);
        Assert.DoesNotContain(".mod", FileTypes.FfmpegAudioExtensions);
    }

    [Fact]
    public void DeleteOlderThan_RemovesOnlyStaleScratchFilesOfThatType()
    {
        var stale = TempMediaFile.NewPath(".wav");
        var fresh = TempMediaFile.NewPath(".wav");
        var otherType = TempMediaFile.NewPath(".fbkeep");
        try
        {
            foreach (var path in new[] { stale, fresh, otherType })
                File.WriteAllBytes(path, [1]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(otherType, DateTime.UtcNow.AddDays(-2));

            TempMediaFile.DeleteOlderThan(".wav", TimeSpan.FromDays(1));

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(otherType));
        }
        finally
        {
            TempMediaFile.TryDelete(stale);
            TempMediaFile.TryDelete(fresh);
            TempMediaFile.TryDelete(otherType);
        }
    }

    private static string Ascii(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

    private sealed class NoArchiveService : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new InvalidOperationException("No archive expected.");
    }
}
