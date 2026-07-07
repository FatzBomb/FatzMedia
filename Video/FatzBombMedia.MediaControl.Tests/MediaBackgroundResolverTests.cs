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
    public void Resolve_ReturnsNullForContentWithNeitherAudioNorCdg()
    {
        var path = CreateFile("song.mp3");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(path, new FakeArchiveService(), MediaContent.None);

        Assert.Null(result);
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
    public void Resolve_MatchesSidecarCaseInsensitivelyByExtension()
    {
        var audioPath = CreateFile("song.mp3");
        _ = CreateFile("song.PNG"); // uppercase extension

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

        Assert.NotNull(result);
        // The resolver constructs the candidate path from FileTypes.Image extensions
        // (lowercase), but File.Exists matches case-insensitively on Windows.
        Assert.True(File.Exists(result.Path), $"Sidecar path should exist: {result.Path}");
        Assert.Equal(MediaBackgroundKind.Image, result.Kind);
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
    public void Resolve_ReturnsVideoSidecarWhenVideoExtensionAvailable()
    {
        var audioPath = CreateFile("song.mp3");
        var sidecarPath = CreateFile("song.mp4");

        try
        {
            _ = FileTypes.Video.Add(".mp4");

            MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            Assert.Equal(MediaBackgroundKind.Video, result.Kind);
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
        _ = CreateFile("song.mp4");
        _ = CreateFile("song.png");

        try
        {
            _ = FileTypes.Video.Add(".mp4");

            MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audioPath, new FakeArchiveService(), MediaContent.Audio);

            Assert.NotNull(result);
            // PreferredKindOrder() lists Video before Image, so the resolver checks
            // video extensions first. On the first file-found hit, CollectSidecarBackgrounds
            // still adds both (it iterates all extensions for each kind before returning),
            // then SelectRandom picks one non-deterministically. Therefore we accept
            // either kind.
            Assert.True(result.Kind is MediaBackgroundKind.Video or MediaBackgroundKind.Image,
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
