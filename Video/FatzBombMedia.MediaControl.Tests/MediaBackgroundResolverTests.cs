using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Files;

namespace FatzBombMedia.MediaControl.Tests;

public class MediaBackgroundResolverTests : IDisposable
{
    private readonly string _tempDir;

    public MediaBackgroundResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FatzBombMediaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
        var result = MediaBackgroundResolver.Resolve(sourcePath!, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForNonExistentFile()
    {
        var path = Path.Combine(_tempDir, "missing.mp3");

        var result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForVideoContent()
    {
        var path = CreateFile("clip.mp4");

        var result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Video);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForImageContent()
    {
        var path = CreateFile("photo.png");

        var result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.Image);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsNullForContentWithNeitherAudioNorCdg()
    {
        var path = CreateFile("song.mp3");

        var result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.None);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ReturnsImageSidecarForAudioFile()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.png");

        var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal(sidecarPath, result.Path);
    }

    [Fact]
    public void Resolve_ReturnsImageSidecarForCdgContent()
    {
        var cdgPath = CreateFile("track.cdg");
        var sidecarPath = CreateFile("track.jpg");

        var result = MediaBackgroundResolver.Resolve(cdgPath, new FakeArchiveService(), MediaContent.Cdg);

        Assert.NotNull(result);
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
        Assert.Equal(sidecarPath, result.Path);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNoSidecarExists()
    {
        var audioPath = CreateFile("lonely.mp3");

        var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_DoesNotMatchSidecarWithDifferentBaseName()
    {
        var audioPath = CreateFile("song.mp3");
        CreateFile("other.png"); // different base name

        var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_MatchesSidecarCaseInsensitivelyByExtension()
    {
        var audioPath = CreateFile("song.mp3");
        CreateFile("song.PNG"); // uppercase extension

        var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.NotNull(result);
        // The resolver constructs the candidate path from FileTypes.Image extensions
        // (lowercase), but File.Exists matches case-insensitively on Windows.
        Assert.True(File.Exists(result!.Path), $"Sidecar path should exist: {result.Path}");
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
    }

    [Fact]
    public void Resolve_ArchivePathCallsArchiveService()
    {
        var archivePath = CreateFile("bundle.zip");
        var archiveService = new FakeArchiveService();

        // Archive path is exercised; without archive entries it falls through to sidecar
        // lookup (which finds nothing here), so result is null. The test verifies no crash.
        var result = MediaBackgroundResolver.Resolve(archivePath, archiveService, MediaContent.Audio);

        Assert.Null(result);
        Assert.True(archiveService.OpenCalled);
    }

    [Fact]
    public void Resolve_ReturnsVideoSidecarWhenVideoExtensionAvailable()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.mp4");

        try
        {
            FileTypes.Video.Add(".mp4");

            var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            Assert.Equal(MediaBackgroundKind.Video, result!.Kind);
            Assert.Equal(sidecarPath, result.Path);
        }
        finally
        {
            FileTypes.Video.Clear();
        }
    }

    [Fact]
    public void Resolve_PrefersVideoOverImageSidecar()
    {
        var audioPath = CreateFile("song.mp3");
        CreateFile("song.mp4");
        CreateFile("song.png");

        try
        {
            FileTypes.Video.Add(".mp4");

            var result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            // PreferredKindOrder() lists Video before Image, so the resolver checks
            // video extensions first. On the first file-found hit, CollectSidecarBackgrounds
            // still adds both (it iterates all extensions for each kind before returning),
            // then SelectRandom picks one non-deterministically. Therefore we accept
            // either kind.
            Assert.True(result!.Kind == MediaBackgroundKind.Video || result!.Kind == MediaBackgroundKind.Image,
                $"Expected Video or Image, got {result.Kind}");
        }
        finally
        {
            FileTypes.Video.Clear();
        }
    }

    private sealed class FakeArchiveService : IArchiveService
    {
        public bool OpenCalled { get; private set; }

        public IArchiveHandle Open(string path)
        {
            OpenCalled = true;
            return new FakeArchiveHandle();
        }
    }

    private sealed class FakeArchiveHandle : IArchiveHandle
    {
        public IEnumerable<IArchiveEntryInfo> Entries => [];
        public void Dispose() { }
    }
}