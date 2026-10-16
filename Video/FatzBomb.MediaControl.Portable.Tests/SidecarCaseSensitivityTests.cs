using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Tests;

[Collection("File type registry")]
public sealed class SidecarCaseSensitivityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fbm-sidecar-" + Guid.NewGuid().ToString("N"));

    public SidecarCaseSensitivityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void UppercaseExtension_MatchesAndReturnsTheRealPath()
    {
        var audio = CreateFile("song.mp3");
        var sidecar = CreateFile("song.PNG");

        MediaBackgroundCandidate? result = MediaBackgroundResolver.Resolve(audio, new NoArchives(), MediaContent.Audio);

        Assert.Equal(sidecar, result!.Path);
    }

    [Fact]
    public void BaseNameCase_FollowsTheFileSystemOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var audio = CreateFile("song.mp3");
        _ = CreateFile("Song.png");

        Assert.Null(MediaBackgroundResolver.Resolve(audio, new NoArchives(), MediaContent.Audio));
    }

    [Fact]
    public void WildcardCharactersInBaseName_AreMatchedLiterally()
    {
        if (OperatingSystem.IsWindows())
            return; // '*' and '?' are not valid in Windows file names

        var audio = CreateFile("what?.mp3");
        _ = CreateFile("whatX.png");
        var sidecar = CreateFile("what?.png");

        Assert.Equal(sidecar, MediaBackgroundResolver.Resolve(audio, new NoArchives(), MediaContent.Audio)!.Path);
    }

    [Fact]
    public void SourceFileItself_IsNotASidecar()
    {
        var audio = CreateFile("song.mp3");
        _ = CreateFile("song.tar.gz");

        Assert.Null(MediaBackgroundResolver.Resolve(audio, new NoArchives(), MediaContent.Audio));
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private sealed class NoArchives : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new NotSupportedException();
    }
}
