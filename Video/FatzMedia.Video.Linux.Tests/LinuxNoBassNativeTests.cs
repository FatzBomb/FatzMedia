using System.Collections.Concurrent;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Software;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check for ENG-033: a player starts and plays video with real FFmpeg and no BASS.
/// Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> to a folder of FFmpeg 9 shared libraries and
/// <c>FATZBOMB_LINUX_SAMPLE_VIDEO</c> to an H.264 clip (default:
/// <c>/opt/fatzbomb-native/media/h264_1080p.mp4</c>). Without them the tests return immediately.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxNoBassNativeTests(ITestOutputHelper output)
{
    const string DefaultSample = "/opt/fatzbomb-native/media/h264_1080p.mp4";

    [Fact]
    public void BackgroundMode_WithoutBass_PresentsFramesThroughTheSoftwareRenderer() =>
        PlaysPictureWithoutBass(MediaPlayerMode.Background);

    [Fact]
    public void FullMode_WithoutBass_PlaysPictureOnTheClockWithoutSound() =>
        PlaysPictureWithoutBass(MediaPlayerMode.Full);

    void PlaysPictureWithoutBass(MediaPlayerMode mode)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO") is { Length: > 0 } configured ? configured : DefaultSample;
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(sample) || !OperatingSystem.IsLinux())
            return;

        // An empty application folder: the only search path is the FFmpeg folder, so BASS is missing.
        var appBase = Directory.CreateTempSubdirectory("fbm-no-bass-").FullName;
        try
        {
            var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
            var factory = new LinuxPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
            var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software) { Mode = mode };
            var errors = new ConcurrentQueue<string>();
            var sink = new CountingFrameSink();

            using var player = new PlayerCore(factory, configuration, [ffmpeg]);
            player.MediaError += (_, args) => errors.Enqueue(args.Message);

            output.WriteLine(player.LastNativeLoadMessage);
            foreach (var line in player.NativeDependencies.Describe())
                output.WriteLine(line);
            Assert.False(player.NativeDependencies.IsAudioAvailable);
            Assert.True(player.NativeDependencies.RequiredLoaded, string.Join(" ", player.NativeDependencies.Describe()));
            Assert.Contains("without BASS", player.LastNativeLoadMessage, StringComparison.Ordinal);

            using var handle = new MediaPresentationFrameSinkHandle(sink);
            Assert.True(player.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
                MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                MediaPresentationSurfaceRole.Primary,
                handle.CreateSurfaceDescriptor(),
                640,
                360)));

            Assert.True(player.Load(sample), string.Join(" | ", errors));
            Assert.True(player.Play());
            var firstCount = 0;
            Assert.True(SpinWait.SpinUntil(() => (firstCount = sink.Frames) >= 5, TimeSpan.FromSeconds(15)), $"frames presented: {sink.Frames}");
            Assert.True(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"position {player.Position}");
            Assert.True(sink.Frames > firstCount);
            Assert.True(sink.LastFrameHasContent, "presented frame looks blank");
            Assert.Empty(errors);
            _ = player.Stop();
            Assert.True(player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId));
        }
        finally
        {
            Directory.Delete(appBase, recursive: true);
        }
    }

    sealed class CountingFrameSink : IMediaPresentationFrameSink
    {
        int _frames;
        volatile bool _lastFrameHasContent;

        public int Frames => Volatile.Read(ref _frames);

        public bool LastFrameHasContent => _lastFrameHasContent;

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            // Sample one row in the middle: a decoded picture has more than a handful of values.
            var row = bgraPixels.Slice(height / 2 * stride, width * 4);
            var distinct = new HashSet<byte>();
            foreach (var value in row)
            {
                if (distinct.Add(value) && distinct.Count > 8)
                    break;
            }

            _lastFrameHasContent = distinct.Count > 8;
            _ = Interlocked.Increment(ref _frames);
        }
    }
}
