using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Files;

namespace FatzMedia.Video.Tests;

[CollectionDefinition("File type registry", DisableParallelization = true)]
public sealed class FileTypeRegistryCollection
{
}

[Collection("File type registry")]
public class MediaBackgroundResolverTests : IDisposable
{
    private readonly string _tempDir;

    public MediaBackgroundResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FatzBombMediaTests_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_ReturnsNullForNullOrWhitespaceSourcePath(string? sourcePath)
    {
        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(sourcePath!, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForNonExistentFile()
    {
        var path = Path.Combine(_tempDir, "missing.mp3");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForVideoContent()
    {
        var path = CreateFile("clip.mp4");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Video);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForImageContent()
    {
        var path = CreateFile("photo.png");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Image);

        Assert.Null(result);
    }

    [Fact]
    public void ImageMain_RequiresExplicitBackgroundSelection()
    {
        Assert.False(MediaFile.ShouldAutoDetectBackground(MediaContent.Image));
        Assert.True(MediaFile.CanUseExplicitBackground(MediaContent.Image));
        Assert.False(MediaFile.CanUseExplicitBackground(MediaContent.Video));
    }

    [Fact]
    public void Resolve_ReturnsVideoSidecarForFutureNonVisualContent()
    {
        var path = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.testvideo");

        try
        {
            _ = FileTypes.Video.Add(".testvideo");

            MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.None);

            Assert.NotNull(result);
            Assert.Equal(MediaBackgroundKind.Video, result.Kind);
            Assert.Equal(sidecarPath, result.Path);
        }
        finally
        {
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Fact]
    public void Resolve_ReturnsImageSidecarForAudioFile()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.png");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal(sidecarPath, result.Path);
    }

    [Theory]
    [InlineData("kar")]
    [InlineData("mid")]
    [InlineData("midi")]
    [InlineData("mod")]
    public void Resolve_ReturnsSidecarForAnyAudioLikeMain(string extension)
    {
        var mediaPath = CreateFile($"audio-like-{extension}.{extension}");
        var sidecarPath = CreateFile($"audio-like-{extension}.png");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(
            mediaPath,
            new FakeArchiveService(),
            MediaContent.Audio);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal(sidecarPath, result.Path);
    }

    [Fact]
    public void Resolve_ReturnsImageSidecarForCdgContent()
    {
        var cdgPath = CreateFile("track.cdg");
        var sidecarPath = CreateFile("track.jpg");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(cdgPath, new FakeArchiveService(), MediaContent.Cdg);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal(sidecarPath, result.Path);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoSidecarExists()
    {
        var audioPath = CreateFile("lonely.mp3");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_DoesNotMatchSidecarWithDifferentBaseName()
    {
        var audioPath = CreateFile("song.mp3");
        _ = CreateFile("other.png"); // different base name

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_MatchesImageSidecarCaseInsensitivelyByExtension()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.PNG");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.True(File.Exists(result.Path), $"Sidecar path should exist: {result.Path}");
        Assert.Equal(sidecarPath, result.Path, ignoreCase: true);
    }

    [Fact]
    public void Resolve_ArchivePathCallsArchiveService()
    {
        var archivePath = CreateFile("bundle.zip");
        var archiveService = new FakeArchiveService();

        // Archive path is exercised; without archive entries it falls through to sidecar
        // lookup (which finds nothing here), so result is null. The test verifies no crash.
        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(archivePath, archiveService, MediaContent.Audio);

        Assert.Null(result);
        Assert.True(archiveService.OpenCalled);
    }

    [Fact]
    public void Resolve_ArchiveUsesInnerImageBackground()
    {
        var archivePath = CreateFile("karaoke.zip");
        var archiveService = new FakeArchiveService(
            new FakeArchiveEntry("song.kar", [1]),
            new FakeArchiveEntry("background.png", [2, 3, 4]));

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(
            archivePath,
            archiveService,
            MediaContent.Audio);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal("background.png", result.DisplayName);
        Assert.Null(result.Data);
        Assert.Equal(archivePath, result.ArchivePath);
        Assert.Equal("background.png", result.ArchiveEntryKey);
    }

    [Fact]
    public void FileTree_ArchiveAudioTakesMainRoleAndVideoRemainsAvailableAsBackground()
    {
        var archivePath = CreateFile("module-video.zip");
        var archiveService = new FakeArchiveService(
            new FakeArchiveEntry("song.testmod", [1]),
            new FakeArchiveEntry("visual.testvideo", [2]));

        try
        {
            _ = FileTypes.Audio.Add(".testmod");
            _ = FileTypes.Video.Add(".testvideo");

            using FileTree tree = FileTreeBuilder.GetFileTree(archivePath, archiveService);
            MediaBackgroundCandidate? background = MediaBackgroundResolver.Resolve(
                archivePath,
                archiveService,
                MediaContent.Audio);

            Assert.Equal(FileType.AudioArchive, tree.Type);
            Assert.Equal("song.testmod", tree.AudioEntry?.Key);
            Assert.Equal("visual.testvideo", tree.VideoEntry?.Key);
            Assert.NotNull(background);
            Assert.Equal(MediaBackgroundKind.Video, background.Kind);
            Assert.Equal("visual.testvideo", background.DisplayName);
        }
        finally
        {
            _ = FileTypes.Audio.Remove(".testmod");
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Fact]
    public void FileTree_ArchiveVideoRemainsMainWhenNoAudioOrCdgExists()
    {
        var archivePath = CreateFile("video-only.zip");
        var archiveService = new FakeArchiveService(new FakeArchiveEntry("main.testvideo", [1]));

        try
        {
            _ = FileTypes.Video.Add(".testvideo");

            using FileTree tree = FileTreeBuilder.GetFileTree(archivePath, archiveService);

            Assert.Equal(FileType.VideoArchive, tree.Type);
            Assert.Equal("main.testvideo", tree.VideoEntry?.Key);
        }
        finally
        {
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Fact]
    public void FileTree_ArchiveAudioCdgPairTakesPriorityOverVideo()
    {
        var archivePath = CreateFile("karaoke-video.zip");
        var archiveService = new FakeArchiveService(
            new FakeArchiveEntry("song.testaudio", [1]),
            new FakeArchiveEntry("song.cdg", [2]),
            new FakeArchiveEntry("background.testvideo", [3]));

        try
        {
            _ = FileTypes.Audio.Add(".testaudio");
            _ = FileTypes.Video.Add(".testvideo");

            using FileTree tree = FileTreeBuilder.GetFileTree(archivePath, archiveService);

            Assert.Equal(FileType.AudioCdgArchive, tree.Type);
            Assert.Equal("song.testaudio", tree.AudioEntry?.Key);
            Assert.Equal("song.cdg", tree.CdgEntry?.Key);
            Assert.Equal("background.testvideo", tree.VideoEntry?.Key);
        }
        finally
        {
            _ = FileTypes.Audio.Remove(".testaudio");
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Fact]
    public void Resolve_ReturnsVideoSidecarWhenVideoExtensionAvailable()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.testvideo");

        try
        {
            _ = FileTypes.Video.Add(".testvideo");

            MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            Assert.Equal(MediaBackgroundKind.Video, result.Kind);
            Assert.Equal(sidecarPath, result.Path);
        }
        finally
        {
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Fact]
    public void Resolve_SelectsVideoOrImageSidecar()
    {
        var audioPath = CreateFile("song.mp3");
        var videoPath = CreateFile("song.testvideo");
        var imagePath = CreateFile("song.png");

        try
        {
            _ = FileTypes.Video.Add(".testvideo");

            MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            Assert.True(result.Kind is MediaBackgroundKind.Video or MediaBackgroundKind.Image);
            Assert.True(result.Path == videoPath || result.Path == imagePath);
        }
        finally
        {
            _ = FileTypes.Video.Remove(".testvideo");
        }
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    public void AutomaticBackgroundPolicy_RequiresEveryDiscoveryGate(
        bool ignoreSidecar,
        bool backgroundAutoDetect,
        bool sourceIsFile,
        bool resolverAvailable)
    {
        Assert.Equal(
            !ignoreSidecar && backgroundAutoDetect && sourceIsFile && resolverAvailable,
            MediaFile.ShouldResolveAutomaticBackground(
                MediaContent.Audio,
                ignoreSidecar,
                backgroundAutoDetect,
                sourceIsFile,
                resolverAvailable));
    }

    [Theory]
    [InlineData(MediaContent.Video)]
    [InlineData(MediaContent.Image)]
    [InlineData(MediaContent.Audio | MediaContent.Video)]
    public void AutomaticBackgroundPolicy_RejectsVisualMainContent(MediaContent content)
    {
        Assert.False(MediaFile.ShouldResolveAutomaticBackground(
            content,
            ignoreSidecar: false,
            backgroundAutoDetect: true,
            sourceIsFile: true,
            resolverAvailable: true));
    }

    private sealed class FakeArchiveService(params IArchiveEntryInfo[] entries) : IArchiveService
    {
        public bool OpenCalled { get; private set; }

        public IArchiveHandle Open(string path)
        {
            OpenCalled = true;
            return new FakeArchiveHandle(entries);
        }
    }

    private sealed class FakeArchiveHandle(IReadOnlyList<IArchiveEntryInfo> entries) : IArchiveHandle
    {
        public IEnumerable<IArchiveEntryInfo> Entries => entries;
        public void Dispose() { }
    }

    private sealed class FakeArchiveEntry(string key, byte[] data) : IArchiveEntryInfo
    {
        public string Key => key;
        public long? Size => data.Length;
        public void WriteTo(Stream stream) => stream.Write(data);
    }
}
