using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Linux.Runtime;
using FatzBomb.MediaControl.Rendering.Software;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit.Abstractions;

namespace FatzBomb.MediaControl.Linux.Tests;

/// <summary>
/// Opt-in native check for 026B (Linux with BASS and FFmpeg): native loading reports both folders,
/// audio files play, pause, seek and stop through the system's sound server, and a video presents
/// frames on its audio clock. Set <c>FATZBOMB_LINUX_BASS_DIR</c> to a folder with <c>libbass.so</c>
/// and <c>libbass_fx.so</c> (and optionally <c>libbassflac.so</c>), <c>FATZBOMB_LINUX_FFMPEG_DIR</c>
/// to the FFmpeg 9 libraries, and have a sound server (PulseAudio or PipeWire, a null sink is enough).
/// <c>scripts/setup-linux-native.sh</c> prepares all of it and the media. Without them the tests
/// return immediately.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxBassNativeTests(ITestOutputHelper output)
{
    private const string MediaDir = "/opt/fatzbomb-native/media";

    [Fact]
    public void NativeLoad_ReportsBassAndFfmpeg()
    {
        if (!TryGetFolders(out var bass, out var ffmpeg))
            return;

        using var player = CreatePlayer(bass, ffmpeg, out _);
        output.WriteLine(player.LastNativeLoadMessage);
        foreach (var line in player.NativeDependencies.Describe())
            output.WriteLine(line);
        Assert.True(player.NativeDependencies.IsAudioAvailable, string.Join(" ", player.NativeDependencies.Describe()));
        Assert.True(player.NativeDependencies.RequiredLoaded, string.Join(" ", player.NativeDependencies.Describe()));
        Assert.Contains(bass, player.LastNativeLoadMessage, StringComparison.Ordinal);
        Assert.Contains(ffmpeg, player.LastNativeLoadMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tone.mp3")]
    [InlineData("tone.flac")]
    public void AudioFile_PlaysPausesSeeksAndStops(string name)
    {
        var file = Path.Combine(MediaDir, name);
        if (!TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(file))
            return;

        using var player = CreatePlayer(bass, ffmpeg, out var errors);
        Assert.True(player.Load(file), string.Join(" | ", errors));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(10)), $"position {player.Position}");

        // Real time on the sound server: about one second of position per second.
        var start = player.Position;
        var watch = Stopwatch.StartNew();
        Thread.Sleep(2000);
        var rate = (player.Position - start).TotalSeconds / watch.Elapsed.TotalSeconds;
        output.WriteLine($"{name}: playback rate {rate:F3}x");
        Assert.InRange(rate, 0.9, 1.1);

        Assert.True(player.Pause());
        Thread.Sleep(300);
        var paused = player.Position;
        Thread.Sleep(700);
        Assert.InRange((player.Position - paused).TotalMilliseconds, -50, 50);
        Assert.Equal(MediaPlayerState.Paused, player.State);

        Assert.True(player.Seek(TimeSpan.FromSeconds(6)));
        Assert.True(SpinWait.SpinUntil(() => Math.Abs(player.Position.TotalSeconds - 6) < 0.5, TimeSpan.FromSeconds(5)), $"after seek {player.Position}");
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => player.Position > TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(5)), $"after resume {player.Position}");

        Assert.True(player.Stop());
        Assert.True(SpinWait.SpinUntil(() => player.State == MediaPlayerState.Stopped, TimeSpan.FromSeconds(5)), $"state {player.State}");
        Assert.Empty(errors);
    }

    [Fact]
    public void Video_PresentsFramesOnTheAudioClock()
    {
        var sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO") is { Length: > 0 } configured ? configured : Path.Combine(MediaDir, "h264_1080p.mp4");
        if (!TryGetFolders(out var bass, out var ffmpeg) || !File.Exists(sample))
            return;

        using var player = CreatePlayer(bass, ffmpeg, out var errors);
        var sink = new CountingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(player.AttachPresentationSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            640,
            360)));

        Assert.True(player.Load(sample), string.Join(" | ", errors));
        Assert.True(player.Play());
        Assert.True(SpinWait.SpinUntil(() => sink.Frames >= 5 && player.Position > TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(15)), $"frames {sink.Frames}, position {player.Position}");

        // The clock follows the audio on the sound server; frames keep coming at the clip's rate.
        var startPosition = player.Position;
        var startFrames = sink.Frames;
        var watch = Stopwatch.StartNew();
        Thread.Sleep(3000);
        var rate = (player.Position - startPosition).TotalSeconds / watch.Elapsed.TotalSeconds;
        var fps = (sink.Frames - startFrames) / watch.Elapsed.TotalSeconds;
        output.WriteLine($"video: clock rate {rate:F3}x, {fps:F1} frames per second presented");
        Assert.InRange(rate, 0.9, 1.1);
        Assert.True(fps > 10, $"only {fps:F1} frames per second");
        Assert.True(sink.LastFrameHasContent, "presented frame looks blank");
        Assert.Empty(errors);
        _ = player.Stop();
        Assert.True(player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId));
    }

    private static bool TryGetFolders(out string bass, out string ffmpeg)
    {
        bass = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_BASS_DIR") ?? string.Empty;
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        return OperatingSystem.IsLinux()
            && Directory.Exists(bass) && File.Exists(Path.Combine(bass, "libbass.so"))
            && Directory.Exists(ffmpeg);
    }

    private static MediaControlPlayer CreatePlayer(string bass, string ffmpeg, out ConcurrentQueue<string> errors)
    {
        // An empty application folder: the only search paths are the two given folders.
        var appBase = Directory.CreateTempSubdirectory("fbm-bass-").FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
        var factory = new LinuxMediaControlPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
        var player = new MediaControlPlayer(factory, new MediaPlayerConfiguration(VideoDecodingMethod.Software), [bass, ffmpeg]);
        var queue = new ConcurrentQueue<string>();
        player.MediaError += (_, args) => queue.Enqueue(args.Message);
        errors = queue;
        return player;
    }

    private sealed class CountingFrameSink : IMediaPresentationFrameSink
    {
        private int _frames;
        private volatile bool _lastFrameHasContent;

        public int Frames => Volatile.Read(ref _frames);

        public bool LastFrameHasContent => _lastFrameHasContent;

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride)
        {
            ReadOnlySpan<byte> row = bgraPixels.Slice(height / 2 * stride, width * 4);
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
