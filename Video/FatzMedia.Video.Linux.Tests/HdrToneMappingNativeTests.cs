using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Software;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native check for Stages 038A and 042A: real HEVC 10-bit HDR clips (HLG and HDR10/PQ)
/// decode in software and reach the software renderer tone mapped to SDR. Set
/// <c>FATZBOMB_LINUX_FFMPEG_DIR</c> to a folder of FFmpeg 9 shared libraries; the clips are
/// read from <c>FATZBOMB_LINUX_HDR_MEDIA_DIR</c> (default <c>/opt/fatzbomb-native/media</c>).
/// Without them the tests return immediately.
/// </summary>
[Collection("File type registry")]
public sealed class HdrToneMappingNativeTests(ITestOutputHelper output)
{
    private const string DefaultMediaDirectory = "/opt/fatzbomb-native/media";

    [Theory]
    [InlineData("hevc_hlg.mp4", "HLG HDR, tone mapped to SDR", "HLG video is tone mapped to SDR on the CPU")]
    [InlineData("hevc_hdr10.mp4", "HDR10/PQ, tone mapped to SDR", "HDR10 video is tone mapped to SDR on the CPU")]
    public void HdrClip_IsToneMappedOnTheSoftwarePath(string fileName, string decodingNote, string toneMapLine)
    {
        Frame? toneMapped = CaptureFrame(fileName, default, out IReadOnlyList<string> diagnostics);
        if (toneMapped is not { } frame)
            return;

        Assert.Contains(diagnostics, line => line.StartsWith("Decoding ", StringComparison.Ordinal) && line.Contains(decodingNote, StringComparison.Ordinal));
        Assert.Contains(diagnostics, line => line.StartsWith(toneMapLine, StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, line => line.Contains("without tone mapping", StringComparison.Ordinal));

        Stats stats = Measure(frame);
        output.WriteLine($"{fileName}: mean luma {stats.MeanLuma:F1}, white-clipped {stats.WhiteFraction:P2}, black {stats.BlackFraction:P2}");

        // No clipping to white: the clip is color bars with no white area, so only a small share
        // (text and the moving markers) may reach white on all three channels.
        Assert.True(stats.WhiteFraction < 0.02, $"{stats.WhiteFraction:P2} of the picture is clipped to white");

        // A plausible SDR picture: neither crushed toward black nor blown out toward white.
        Assert.InRange(stats.MeanLuma, 60, 200);
        Assert.True(stats.BlackFraction < 0.05, $"{stats.BlackFraction:P2} of the picture is black");

        // Hue is kept through the BT.2020 to BT.709 conversion: the first bar (left edge, a
        // third of the way down) stays red.
        (byte red, byte green, byte blue) = frame.Pixel(frame.Width / 28, frame.Height / 3);
        output.WriteLine($"{fileName}: first bar {red},{green},{blue}");
        Assert.True(red > 150 && green < 60 && blue < 60, $"the red bar shows as {red},{green},{blue}");

        // The tone mapper changed the picture: with tone mapping off the same frame differs.
        Frame? plain = CaptureFrame(fileName, new HdrToneMappingOptions(Disabled: true), out IReadOnlyList<string> plainDiagnostics);
        Assert.NotNull(plain);
        Assert.Contains(plainDiagnostics, line => line.Contains("without tone mapping", StringComparison.Ordinal));
        Stats plainStats = Measure(plain.Value);
        output.WriteLine($"{fileName} without tone mapping: mean luma {plainStats.MeanLuma:F1}, white-clipped {plainStats.WhiteFraction:P2}");
        Assert.True(Math.Abs(plainStats.MeanLuma - stats.MeanLuma) > 2, "tone mapping did not change the picture");
    }

    private Frame? CaptureFrame(string fileName, HdrToneMappingOptions hdr, out IReadOnlyList<string> diagnostics)
    {
        diagnostics = [];
        var ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR");
        var mediaDirectory = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_HDR_MEDIA_DIR") is { Length: > 0 } configured ? configured : DefaultMediaDirectory;
        var path = Path.Combine(mediaDirectory, fileName);
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(path) || !OperatingSystem.IsLinux())
            return null;

        var appBase = Directory.CreateTempSubdirectory("fbm-hdr-").FullName;
        try
        {
            var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
            var factory = new LinuxMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
            var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software)
            {
                Mode = MediaPlayerMode.Background,
                HdrToneMapping = hdr,
            };
            var messages = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            var sink = new CapturingFrameSink(framesToSkip: 15);

            using var player = new MediaControlPlayer(factory, configuration, [ffmpeg]);
            player.MediaDiagnostic += (_, args) => messages.Enqueue(args.Message);
            player.MediaError += (_, args) => errors.Enqueue(args.Message);
            Assert.True(player.NativeDependencies.RequiredLoaded, string.Join(" ", player.NativeDependencies.Describe()));

            using var handle = new MediaPresentationFrameSinkHandle(sink);
            Assert.True(player.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
                MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
                MediaPresentationSurfaceRole.Primary,
                handle.CreateSurfaceDescriptor(),
                960,
                540)));

            Assert.True(player.Load(path), string.Join(" | ", errors));
            Assert.True(player.Play());
            Assert.True(SpinWait.SpinUntil(() => sink.Captured != null, TimeSpan.FromSeconds(20)), $"frames presented: {sink.Frames}");
            _ = player.Stop();
            Assert.True(player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId));
            Assert.Empty(errors);
            diagnostics = [.. messages];
            return sink.Captured;
        }
        finally
        {
            Directory.Delete(appBase, recursive: true);
        }
    }

    private static Stats Measure(Frame frame)
    {
        double lumaSum = 0;
        long white = 0, black = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                (byte r, byte g, byte b) = frame.Pixel(x, y);
                lumaSum += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                if (r >= 250 && g >= 250 && b >= 250)
                    white++;
                if (r <= 5 && g <= 5 && b <= 5)
                    black++;
            }
        }

        double pixels = frame.Width * (double)frame.Height;
        return new Stats(lumaSum / pixels, white / pixels, black / pixels);
    }

    private readonly record struct Stats(double MeanLuma, double WhiteFraction, double BlackFraction);

    private readonly record struct Frame(byte[] Bgra, int Width, int Height, int Stride)
    {
        public (byte R, byte G, byte B) Pixel(int x, int y)
        {
            var offset = (y * Stride) + (x * 4);
            return (Bgra[offset + 2], Bgra[offset + 1], Bgra[offset]);
        }
    }

    /// <summary>Keeps a copy of one presented frame, after skipping the first few.</summary>
    private sealed class CapturingFrameSink(int framesToSkip) : IMediaPresentationFrameSink
    {
        private int _frames;
        private volatile StrongBox<Frame>? _captured;

        public int Frames => Volatile.Read(ref _frames);

        public Frame? Captured => _captured?.Value;

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            if (Interlocked.Increment(ref _frames) <= framesToSkip || _captured != null)
                return;

            _captured = new StrongBox<Frame>(new Frame(bgraPixels[..(stride * height)].ToArray(), width, height, stride));
        }
    }
}
