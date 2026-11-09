using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Tests;

[Collection("File type registry")]
public sealed class SidecarCaseSensitivityTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "fbm-sidecar-" + Guid.NewGuid().ToString("N"));

    public SidecarCaseSensitivityTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void UppercaseExtension_MatchesTheSidecar()
    {
        var audio = CreateFile("song.mp3");
        var sidecar = CreateFile("song.PNG");

        var result = MediaBackgroundResolver.Resolve(audio, new NotSupportedArchiveService(), MediaContent.Audio);

        // Linux lists the directory and returns the real name. Case-insensitive file systems
        // (Windows, macOS) keep the original exact-name probe, whose path differs only in case
        // and opens the same file.
        Assert.Equal(sidecar, result!.Path, ignoreCase: !OperatingSystem.IsLinux());
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public void BaseNameCase_FollowsTheFileSystemOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var audio = CreateFile("song.mp3");
        _ = CreateFile("Song.png");

        Assert.Null(MediaBackgroundResolver.Resolve(audio, new NotSupportedArchiveService(), MediaContent.Audio));
    }

    [Fact]
    public void WildcardCharactersInBaseName_AreMatchedLiterally()
    {
        if (OperatingSystem.IsWindows())
            return; // '*' and '?' are not valid in Windows file names

        var audio = CreateFile("what?.mp3");
        _ = CreateFile("whatX.png");
        var sidecar = CreateFile("what?.png");

        Assert.Equal(sidecar, MediaBackgroundResolver.Resolve(audio, new NotSupportedArchiveService(), MediaContent.Audio)!.Path);
    }

    [Fact]
    public void SourceFileItself_IsNotASidecar()
    {
        var audio = CreateFile("song.mp3");
        _ = CreateFile("song.tar.gz");

        Assert.Null(MediaBackgroundResolver.Resolve(audio, new NotSupportedArchiveService(), MediaContent.Audio));
    }

    string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, []);
        return path;
    }
}
