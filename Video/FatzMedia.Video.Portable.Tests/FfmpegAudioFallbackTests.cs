using System.Buffers.Binary;
using System.Text;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Files;

namespace FatzMedia.Video.Portable.Tests;

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
    public void PlanOutput_RefusesADurationBeyondTheWavLimit_BeforeDecoding()
    {
        // 7 hours of stereo 44.1 kHz float is about 8.9 GB; a WAV holds 4 GB.
        (var target, _) = FfmpegAudioFallback.PlanOutput(7 * 3600, 44100, 2, FfmpegAudioFallback.DefaultMaxInMemoryBytes);

        Assert.Equal(FfmpegAudioFallback.ConversionTarget.TooLong, target);
    }

    [Fact]
    public void PlanOutput_GoesToAFile_WhenTheEstimateExceedsTheMemoryLimit()
    {
        // An hour of stereo 48 kHz float is about 1.4 GB: it must not start as a 160 MB buffer.
        (var target, var capacity) = FfmpegAudioFallback.PlanOutput(3600, 48000, 2, FfmpegAudioFallback.DefaultMaxInMemoryBytes);

        Assert.Equal(FfmpegAudioFallback.ConversionTarget.File, target);
        Assert.Equal(0, capacity);
    }

    [Fact]
    public void PlanOutput_SizesTheBufferFromTheDuration_WithSlack()
    {
        (var target, var capacity) = FfmpegAudioFallback.PlanOutput(10, 48000, 2, FfmpegAudioFallback.DefaultMaxInMemoryBytes);

        var data = 10L * 48000 * 2 * sizeof(float);
        Assert.Equal(FfmpegAudioFallback.ConversionTarget.Memory, target);
        Assert.InRange(capacity, data + FloatWav.HeaderBytes, (data * 11 / 10) + 65536 + FloatWav.HeaderBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlanOutput_UnknownDuration_StartsInMemoryAtZero(double duration)
    {
        Assert.Equal((FfmpegAudioFallback.ConversionTarget.Memory, 0L), FfmpegAudioFallback.PlanOutput(duration, 48000, 2, FfmpegAudioFallback.DefaultMaxInMemoryBytes));
    }

    [Fact]
    public void ExtensionOnlyFfmpegDecodes_RequiresConversionAndIsPlayable()
    {
        const string Extension = ".fbfallbacka";
        _ = FileTypes.FfmpegAudio.Add(Extension);
        try
        {
            Assert.True(FfmpegAudioFallback.RequiresConversion($"song{Extension}"));
            Assert.True(FfmpegAudioFallback.CanRetryWithFfmpeg($"song{Extension}"));
            Assert.True(FileTypes.IsPlayableAudio($"song{Extension}"));
            Assert.False(FileTypes.IsAudio($"song{Extension}"));
            Assert.Contains(Extension, AcceptedMediaTypes.FromCurrentRuntime().Audio);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(Extension);
        }
    }

    [Fact]
    public void ExtensionABassPluginClaims_StaysOnBassButCanRetryWithFfmpeg()
    {
        const string Extension = ".fbfallbackb";
        _ = FileTypes.Audio.Add(Extension);
        _ = FileTypes.FfmpegAudio.Add(Extension);
        try
        {
            Assert.False(FfmpegAudioFallback.RequiresConversion($"song{Extension}"));
            Assert.True(FfmpegAudioFallback.CanRetryWithFfmpeg($"song{Extension}"));
        }
        finally
        {
            _ = FileTypes.Audio.Remove(Extension);
            _ = FileTypes.FfmpegAudio.Remove(Extension);
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
        const string Extension = ".fbfallbackc";
        _ = FileTypes.FfmpegAudio.Add(Extension);
        var directory = Directory.CreateTempSubdirectory("fatzbomb-fallback-");
        try
        {
            var audio = Path.Combine(directory.FullName, $"song{Extension}");
            var cdg = Path.Combine(directory.FullName, "song.cdg");
            File.WriteAllBytes(audio, [1]);
            File.WriteAllBytes(cdg, [1]);

            using var audioTree = FileTreeBuilder.GetFileTree(audio, new NoArchiveService(), searchCompanionFile: false);
            Assert.Equal(FileType.Audio, audioTree.Type);
            Assert.Equal(audio, audioTree.AudioPath);

            using var cdgTree = FileTreeBuilder.GetFileTree(cdg, new NoArchiveService());
            Assert.Equal(FileType.AudioCdg, cdgTree.Type);
            Assert.Equal(audio, cdgTree.AudioPath);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(Extension);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FileTree_ArchivedFallbackAudio_PairsWithItsCdg()
    {
        // A zip of song.flac + song.cdg without bassflac played as a silent CD+G.
        const string Extension = ".fbfallbackd";
        _ = FileTypes.FfmpegAudio.Add(Extension);
        var archive = Path.Combine(Path.GetTempPath(), $"fatzbomb-fallback-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(archive, [1]);
        try
        {
            using var tree = FileTreeBuilder.GetFileTree(archive, new EntriesArchiveService($"song{Extension}", "song.cdg"));

            Assert.Equal(FileType.AudioCdgArchive, tree.Type);
            Assert.Equal($"song{Extension}", tree.AudioEntry!.Key);
            Assert.Equal("song.cdg", tree.CdgEntry!.Key);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(Extension);
            File.Delete(archive);
        }
    }

    [Fact]
    public void FileTree_Archive_PrefersAudioABassPluginDecodes()
    {
        const string Fallback = ".fbfallbacke";
        const string Bass = ".fbfallbackf";
        _ = FileTypes.FfmpegAudio.Add(Fallback);
        _ = FileTypes.Audio.Add(Bass);
        var archive = Path.Combine(Path.GetTempPath(), $"fatzbomb-Fallback-{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(archive, [1]);
        try
        {
            using var paired = FileTreeBuilder.GetFileTree(archive, new EntriesArchiveService($"song{Fallback}", $"song{Bass}", "song.cdg"));
            Assert.Equal(FileType.AudioCdgArchive, paired.Type);
            Assert.Equal($"song{Bass}", paired.AudioEntry!.Key);

            using var audioOnly = FileTreeBuilder.GetFileTree(archive, new EntriesArchiveService($"a{Fallback}", $"b{Bass}"));
            Assert.Equal(FileType.AudioArchive, audioOnly.Type);
            Assert.Equal($"b{Bass}", audioOnly.AudioEntry!.Key);
        }
        finally
        {
            _ = FileTypes.FfmpegAudio.Remove(Fallback);
            _ = FileTypes.Audio.Remove(Bass);
            File.Delete(archive);
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

    static string Ascii(byte[] bytes, int offset) => Encoding.ASCII.GetString(bytes, offset, 4);

    sealed class NoArchiveService : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new InvalidOperationException("No archive expected.");
    }

    sealed class EntriesArchiveService(params string[] keys) : IArchiveService
    {
        public IArchiveHandle Open(string path) => new Handle([.. keys.Select(static key => (IArchiveEntryInfo)new Entry(key))]);

        sealed class Handle(IReadOnlyList<IArchiveEntryInfo> entries) : IArchiveHandle
        {
            public IEnumerable<IArchiveEntryInfo> Entries => entries;
            public void Dispose() { }
        }

        sealed class Entry(string key) : IArchiveEntryInfo
        {
            public string Key => key;
            public long? Size => 1;
            public void WriteTo(Stream stream) => stream.WriteByte(1);
        }
    }
}
