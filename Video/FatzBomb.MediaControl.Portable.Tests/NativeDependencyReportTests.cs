using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Portable.Tests;

/// <summary>The startup report of which native libraries loaded (Stage 033A follow-up).</summary>
public sealed class NativeDependencyReportTests
{
    [Theory]
    [InlineData("avcodec-63.dll", "avcodec-62.dll")]
    [InlineData("AVCODEC-63.DLL", "avcodec-61.dll")]
    [InlineData("libavcodec.so.63", "libavcodec.so.62")]
    public void FindVersionHint_NamesTheOtherVersionFound(string expected, string present)
    {
        var hint = NativeDependencyReport.FindVersionHint(expected, ["bass.dll", present, "avformat-62.dll"]);

        Assert.Equal($"Found {present}, a different version; this control needs {expected}.", hint);
    }

    [Theory]
    [InlineData("avcodec-63.dll", "avformat-62.dll")]
    [InlineData("avcodec-63.dll", "avcodec-63.dll")]
    [InlineData("avcodec-63.dll", "libavcodec.so.62")]
    [InlineData("libavcodec.so.63", "avcodec-62.dll")]
    [InlineData("bass.dll", "bass-2.dll")]
    public void FindVersionHint_IgnoresOtherLibrariesPlatformsAndUnversionedNames(string expected, string present) =>
        Assert.Null(NativeDependencyReport.FindVersionHint(expected, [present]));

    [Fact]
    public void Report_SeparatesRequiredFromOptionalProblems()
    {
        var report = new NativeDependencyReport(
            [
                new NativeLibraryCheck("BASS", "bass.dll", true, NativeLibraryStatus.Loaded, @"C:\app\native\bass.dll"),
                new NativeLibraryCheck("BASS", "bassopus.dll", false, NativeLibraryStatus.Missing),
                new NativeLibraryCheck("FFmpeg", "avcodec-63.dll", true, NativeLibraryStatus.Missing, Hint: "Found avcodec-62.dll, a different version; this control needs avcodec-63.dll."),
                new NativeLibraryCheck("FFmpeg", "avutil-61.dll", true, NativeLibraryStatus.NotLoaded, @"C:\app\native\avutil-61.dll"),
                new NativeLibraryCheck("BASS", "basswma.dll", false, NativeLibraryStatus.FailedToLoad, @"C:\app\native\basswma.dll"),
            ],
            [@"C:\app\native"]);

        Assert.False(report.RequiredLoaded);
        Assert.Equal(["avcodec-63.dll", "avutil-61.dll"], report.RequiredProblems.Select(static check => check.FileName));
        Assert.Equal(["bassopus.dll", "basswma.dll"], report.OptionalProblems.Select(static check => check.FileName));
        Assert.Equal(
            [
                "FFmpeg avcodec-63.dll (required) is missing. Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.",
                @"FFmpeg avutil-61.dll (required) was found at C:\app\native\avutil-61.dll but not loaded because other FFmpeg files are missing.",
                "BASS bassopus.dll (optional) is missing.",
                @"BASS basswma.dll (optional) was found at C:\app\native\basswma.dll but failed to load.",
            ],
            report.Describe());
    }

    [Fact]
    public void Report_AllRequiredLoaded_HasNoRequiredProblems()
    {
        var report = new NativeDependencyReport(
            [
                new NativeLibraryCheck("BASS", "bass.dll", true, NativeLibraryStatus.Loaded),
                new NativeLibraryCheck("BASS", "bassflac.dll", false, NativeLibraryStatus.Missing),
            ],
            []);

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.Single(report.Describe());
    }

    [Fact]
    public void Report_WithoutBass_IsNotAFailureButSaysAudioIsUnavailable()
    {
        var report = new NativeDependencyReport(
            [
                new NativeLibraryCheck("BASS", "bass.dll", false, NativeLibraryStatus.Missing),
                new NativeLibraryCheck("FFmpeg", "avcodec-63.dll", true, NativeLibraryStatus.Loaded),
            ],
            [])
        { IsAudioAvailable = false };

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.Equal([NativeDependencyReport.AudioUnavailableLine, "BASS bass.dll (optional) is missing."], report.Describe());
    }

    [Fact]
    public void EmptyReport_IsWhatPlatformsWithoutChecksReturn()
    {
        Assert.True(NativeDependencyReport.Empty.IsAudioAvailable);
        Assert.True(NativeDependencyReport.Empty.RequiredLoaded);
        Assert.Empty(NativeDependencyReport.Empty.Describe());
        IMediaControlPlatformFactory factory = new MinimalFactory();
        Assert.Same(NativeDependencyReport.Empty, factory.NativeDependencies);
    }

    private sealed class MinimalFactory : IMediaControlPlatformFactory
    {
        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            throw new NotSupportedException();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            throw new NotSupportedException();
    }
}
