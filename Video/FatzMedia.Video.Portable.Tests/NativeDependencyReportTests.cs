using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Portable.Tests;

/// <summary>The startup report of which native libraries loaded (Stage 033A follow-up).</summary>
public sealed class NativeDependencyReportTests
{
    [Theory]
    [InlineData("avcodec-63.dll", "avcodec-62.dll")]
    [InlineData("AVCODEC-63.DLL", "avcodec-61.dll")]
    [InlineData("libavcodec.so.63", "libavcodec.so.62")]
    public void FindVersionHint_NamesTheOtherVersionFound(string expected, string present)
    {
        var hint = NativeDependencyReport.FindVersionHint(expected, ["fatzaudio.dll", present, "avformat-62.dll"]);

        Assert.Equal($"Found {present}, a different version; this control needs {expected}.", hint);
    }

    [Theory]
    [InlineData("avcodec-63.dll", "avformat-62.dll")]
    [InlineData("avcodec-63.dll", "avcodec-63.dll")]
    [InlineData("avcodec-63.dll", "libavcodec.so.62")]
    [InlineData("libavcodec.so.63", "avcodec-62.dll")]
    [InlineData("fatzaudio.dll", "fatzaudio-2.dll")]
    public void FindVersionHint_IgnoresOtherLibrariesPlatformsAndUnversionedNames(string expected, string present) =>
        Assert.Null(NativeDependencyReport.FindVersionHint(expected, [present]));

    [Fact]
    public void Report_SeparatesRequiredFromOptionalProblems()
    {
        var report = new NativeDependencyReport(
            [
                new NativeLibraryCheck("FFmpeg", "avformat-63.dll", true, NativeLibraryStatus.Loaded, @"C:\app\native\avformat-63.dll"),
                new NativeLibraryCheck("FFmpeg", "avdevice-63.dll", false, NativeLibraryStatus.Missing),
                new NativeLibraryCheck("FFmpeg", "avcodec-63.dll", true, NativeLibraryStatus.Missing, Hint: "Found avcodec-62.dll, a different version; this control needs avcodec-63.dll."),
                new NativeLibraryCheck("FFmpeg", "avutil-61.dll", true, NativeLibraryStatus.NotLoaded, @"C:\app\native\avutil-61.dll"),
                new NativeLibraryCheck("FFmpeg", "avfilter-12.dll", false, NativeLibraryStatus.FailedToLoad, @"C:\app\native\avfilter-12.dll"),
            ],
            [@"C:\app\native"]);

        Assert.False(report.RequiredLoaded);
        Assert.Equal(["avcodec-63.dll", "avutil-61.dll"], report.RequiredProblems.Select(static check => check.FileName));
        Assert.Equal(["avdevice-63.dll", "avfilter-12.dll"], report.OptionalProblems.Select(static check => check.FileName));
        Assert.Equal(
            [
                "FFmpeg avcodec-63.dll (required) is missing. Found avcodec-62.dll, a different version; this control needs avcodec-63.dll.",
                @"FFmpeg avutil-61.dll (required) was found at C:\app\native\avutil-61.dll but not loaded because other FFmpeg files are missing.",
                "FFmpeg avdevice-63.dll (optional) is missing.",
                @"FFmpeg avfilter-12.dll (optional) was found at C:\app\native\avfilter-12.dll but failed to load.",
            ],
            report.Describe());
    }

    [Fact]
    public void Report_AllRequiredLoaded_HasNoRequiredProblems()
    {
        var report = new NativeDependencyReport(
            [
                new NativeLibraryCheck("FFmpeg", "avformat-63.dll", true, NativeLibraryStatus.Loaded),
                new NativeLibraryCheck("FFmpeg", "avdevice-63.dll", false, NativeLibraryStatus.Missing),
            ],
            []);

        Assert.True(report.RequiredLoaded);
        Assert.Empty(report.RequiredProblems);
        Assert.Single(report.Describe());
    }

    [Fact]
    public void EmptyReport_IsWhatPlatformsWithoutChecksReturn()
    {
        Assert.True(NativeDependencyReport.Empty.RequiredLoaded);
        Assert.Empty(NativeDependencyReport.Empty.Describe());
        IPlatformFactory factory = new MinimalFactory();
        Assert.Same(NativeDependencyReport.Empty, factory.NativeDependencies);
    }

    sealed class MinimalFactory : IPlatformFactory
    {
        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            throw new NotSupportedException();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            throw new NotSupportedException();
    }
}
