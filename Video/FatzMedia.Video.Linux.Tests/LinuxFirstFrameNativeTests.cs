using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Linux.Runtime;
using FatzMedia.Video.Software;
using Xunit.Abstractions;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Opt-in native checks for Stage 051A with real FFmpeg and the software renderer (no BASS: picture
/// only). Set <c>FATZBOMB_LINUX_FFMPEG_DIR</c> and optionally <c>FATZBOMB_LINUX_SAMPLE_VIDEO</c>
/// (default <c>/opt/fatzbomb-native/media/h264_1080p.mp4</c>); without them the tests return at once.
/// </summary>
[Collection("File type registry")]
public sealed class LinuxFirstFrameNativeTests(ITestOutputHelper output)
{
    const string DefaultSample = "/opt/fatzbomb-native/media/h264_1080p.mp4";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void ALoadBeforeTheSurfaceAttaches_StillReachesTheSurface_AndArmsTheFirstFrame()
    {
        // Why the test players' 500 ms startup delay could go: nothing is attached during the load,
        // nothing plays, and the surface that attaches afterwards still gets the loaded frame.
        if (!TryGetSample(out var ffmpeg, out var sample))
            return;

        using var player = CreatePlayer(ffmpeg);
        player.PlayOnLoad = false;
        using var tracker = new MediaSurfaceContentTracker { Session = player };
        Assert.True(player.Load(sample));
        Thread.Sleep(500);
        Assert.Equal(0, tracker.FirstFrameLoad);

        var sink = new FrameCounter();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(player.AttachPresentationSurface(Descriptor(handle)));

        Assert.True(SpinWait.SpinUntil(() => sink.Frames > 0, Timeout), "no frame reached the surface attached after the load");
        Assert.True(SpinWait.SpinUntil(() => tracker.FirstFrameLoad != 0, Timeout), "the tracker saw no first frame");
        output.WriteLine($"frames {sink.Frames}, first-frame load {tracker.FirstFrameLoad}, state {player.State}");
        Assert.True(player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId));
    }

    [Theory]
    [InlineData("bass")]
    [InlineData("fatz")]
    public void FullMode_ALoadBeforeTheSurfaceAttaches_StillReachesIt(string engine)
    {
        // The test players' startup media: Full mode, video with sound, BASS or FatzAudio. Needs
        // FATZBOMB_LINUX_BASS_DIR and a sound server as well (LinuxNativeTestPlayer).
        if (!LinuxNativeTestPlayer.TryGetFolders(out var bass, out var ffmpeg) || !TryGetSample(out _, out var sample))
            return;

        var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software)
        {
            AudioEngineFactory = engine == "fatz" ? new FatzAudioEngineFactory(nullOutput: true) : null,
        };
        using var session = LinuxNativeTestPlayer.Create([bass, ffmpeg], configuration, attachSurface: false);
        var player = session.Player;
        player.PlayOnLoad = false;
        using var tracker = new MediaSurfaceContentTracker { Session = player };
        Assert.True(player.Load(sample), string.Join(" | ", session.Errors));
        Thread.Sleep(500);
        Assert.Equal(0, tracker.FirstFrameLoad);

        var sink = new FrameCounter();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(player.AttachPresentationSurface(Descriptor(handle)));
        Assert.True(SpinWait.SpinUntil(() => sink.Frames > 0, Timeout), "no frame reached the surface attached after the load");
        Assert.True(SpinWait.SpinUntil(() => tracker.FirstFrameLoad != 0, Timeout), "the tracker saw no first frame");

        // And it plays from there, with sound, on the surface that attached late.
        var framesAtAttach = sink.Frames;
        Assert.True(session.PlayUntil(1, Timeout), $"position {player.Position}");
        Assert.True(sink.Frames > framesAtAttach);
        Assert.True(player.NativeDependencies.IsAudioAvailable);
        Assert.Empty(session.Errors);
        output.WriteLine($"{engine}: frames {sink.Frames}, first-frame load {tracker.FirstFrameLoad}");
        Assert.True(player.DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId));
    }

    [Fact]
    public void AbSwap_EachLoadOfTheHiddenPlayer_ReportsItsOwnFirstFrame()
    {
        // The A/B pattern on Core alone: two players, loads alternate between them, and each load's
        // first frame is reported once, after a frame of it reached that player's surface.
        if (!TryGetSample(out var ffmpeg, out var sample))
            return;

        using var a = CreatePlayer(ffmpeg);
        using var b = CreatePlayer(ffmpeg);
        PlayerCore[] players = [a, b];
        var sinks = new[] { new FrameCounter(), new FrameCounter() };
        var handles = sinks.Select(static sink => new MediaPresentationFrameSinkHandle(sink)).ToArray();
        var trackers = players.Select(static player => new MediaSurfaceContentTracker { Session = player }).ToArray();
        try
        {
            for (var i = 0; i < 2; i++)
                Assert.True(players[i].AttachPresentationSurface(Descriptor(handles[i])));

            var lastLoad = new int[2];
            for (var swap = 0; swap < 6; swap++)
            {
                var next = swap % 2;
                var framesBefore = sinks[next].Frames;
                var started = DateTime.UtcNow;
                Assert.True(players[next].Load(sample), $"swap {swap}: load failed");
                Assert.True(
                    SpinWait.SpinUntil(() => trackers[next].FirstFrameLoad > lastLoad[next], Timeout),
                    $"swap {swap}: no first frame");
                Assert.True(sinks[next].Frames > framesBefore, $"swap {swap}: first frame reported before one reached the surface");
                lastLoad[next] = trackers[next].FirstFrameLoad;
                output.WriteLine($"swap {swap}: player {(next == 0 ? 'A' : 'B')} ready after {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
                _ = players[1 - next].Stop();
            }
        }
        finally
        {
            foreach (var tracker in trackers)
                tracker.Dispose();
            for (var i = 0; i < 2; i++)
            {
                _ = players[i].DetachPresentationSurface(MediaPresentationSurfaceDescriptor.PrimarySurfaceId);
                handles[i].Dispose();
            }
        }
    }

    static bool TryGetSample(out string ffmpeg, out string sample)
    {
        ffmpeg = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_FFMPEG_DIR") ?? string.Empty;
        sample = Environment.GetEnvironmentVariable("FATZBOMB_LINUX_SAMPLE_VIDEO") is { Length: > 0 } configured ? configured : DefaultSample;
        return OperatingSystem.IsLinux() && Directory.Exists(ffmpeg) && File.Exists(sample);
    }

    // Background mode: picture only, so no BASS is needed; the application folder is empty.
    static PlayerCore CreatePlayer(string ffmpeg)
    {
        var appBase = Directory.CreateTempSubdirectory("fbm-first-frame-").FullName;
        var bootstrapper = new LinuxNativeLibraryBootstrapper(new LinuxNativeOperations(), appBase, LinuxNativeLibraryLocator.CurrentRuntimeIdentifier);
        var factory = new LinuxPlatformFactory(new SoftwarePresentationCoordinatorFactory(), LinuxPlatformOptions.Default, bootstrapper);
        var configuration = new MediaPlayerConfiguration(VideoDecodingMethod.Software) { Mode = MediaPlayerMode.Background };
        return new PlayerCore(factory, configuration, [ffmpeg]);
    }

    static MediaPresentationSurfaceDescriptor Descriptor(MediaPresentationFrameSinkHandle handle) => new(
        MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
        MediaPresentationSurfaceRole.Primary,
        handle.CreateSurfaceDescriptor(),
        640,
        360);

    sealed class FrameCounter : IMediaPresentationFrameSink
    {
        int _frames;

        public int Frames => Volatile.Read(ref _frames);

        public void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) => _ = Interlocked.Increment(ref _frames);
    }
}
