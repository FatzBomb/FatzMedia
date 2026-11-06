using System.Diagnostics;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Player.Shared;
using FatzBomb.MediaControl.Player.Shared.Presentation;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 046A slice 3: the helpers the three test players share (Player.Shared).</summary>
public sealed class PlayerPresentationHelperTests
{
    // --- renderer names and the auto rule ---

    [Theory]
    [InlineData("auto", PlayerRendererRequest.Auto)]
    [InlineData("GPU", PlayerRendererRequest.Gpu)]
    [InlineData("hardware", PlayerRendererRequest.Gpu)]
    [InlineData("warp", PlayerRendererRequest.Warp)]
    [InlineData(" Software ", PlayerRendererRequest.Software)]
    [InlineData("cpu", PlayerRendererRequest.Software)]
    [InlineData("vulkan", PlayerRendererRequest.Vulkan)]
    [InlineData("direct3d11", PlayerRendererRequest.Direct3D11)]
    [InlineData("d3d11", PlayerRendererRequest.Direct3D11)]
    public void RendererNames_Parse(string value, PlayerRendererRequest expected)
    {
        Assert.True(PlayerRendererRules.TryParse(value, out PlayerRendererRequest request));
        Assert.Equal(expected, request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("opengl")]
    public void RendererNames_RejectOthers(string? value) =>
        Assert.False(PlayerRendererRules.TryParse(value, out _));

    [Fact]
    public void RendererRequest_IsAutoByDefault_SoftwareWithTheFlag_AndAValidValueWins()
    {
        Assert.Equal(PlayerRendererRequest.Auto, PlayerStartupOptions.Parse([]).RendererRequest);
        Assert.Equal(PlayerRendererRequest.Software, PlayerStartupOptions.Parse(["--software"]).RendererRequest);
        Assert.Equal(PlayerRendererRequest.Vulkan, PlayerStartupOptions.Parse(["--software", "--renderer=vulkan"]).RendererRequest);

        PlayerStartupOptions invalid = PlayerStartupOptions.Parse(["--software", "--renderer=opengl"]);
        Assert.Equal(PlayerRendererRequest.Software, invalid.RendererRequest);
        Assert.Equal("opengl", invalid.UnknownRenderer);
        Assert.Contains(invalid.DescribeUnknownValues(), line => line.Contains("'opengl'", StringComparison.Ordinal) && line.EndsWith("using software.", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoOrder_IsGpuFirst()
    {
        Assert.Equal([PlayerRendererRequest.Gpu, PlayerRendererRequest.Warp, PlayerRendererRequest.Software], PlayerRendererRules.AutoOrder(windows: true));
        Assert.Equal([PlayerRendererRequest.Vulkan, PlayerRendererRequest.Software], PlayerRendererRules.AutoOrder(windows: false));
    }

    // ENG-039: one decoding table for every test player; the wording shows in status lines and logs.
    [Theory]
    [InlineData(PlayerRendererRequest.Direct3D11, false, VideoDecodingMethod.Auto, "hardware decoding (D3D11VA) where the GPU supports the codec")]
    [InlineData(PlayerRendererRequest.Vulkan, false, VideoDecodingMethod.Auto, "hardware decoding (Vulkan Video) where the GPU supports the codec")]
    [InlineData(PlayerRendererRequest.Software, false, VideoDecodingMethod.Auto, "hardware decoding where the GPU supports the codec")]
    [InlineData(PlayerRendererRequest.Direct3D11, true, VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)")]
    [InlineData(PlayerRendererRequest.Warp, true, VideoDecodingMethod.Software, "software decoding (the renderer runs on a software rasterizer)")]
    [InlineData(PlayerRendererRequest.Vulkan, true, VideoDecodingMethod.Software, "software decoding (the device has no Vulkan Video decoding)")]
    [InlineData(PlayerRendererRequest.Software, true, VideoDecodingMethod.Software, "software decoding (the renderer takes no hardware frames)")]
    public void DescribeDecoding_PinsTheWording(PlayerRendererRequest renderer, bool requiresSoftwareDecoding, VideoDecodingMethod decoding, string detail) =>
        Assert.Equal((decoding, detail), PlayerRendererRules.DescribeDecoding(renderer, requiresSoftwareDecoding));

    // --- shared player flags ---

    [Fact]
    public void PlayerFlags_HaveTheirDefaults()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse([]);

        Assert.Equal(1, options.Players);
        Assert.False(options.PreferFrameSinkSurface);
        Assert.False(options.SoftwareDecoding);
        Assert.Null(options.SecondScreen);
        Assert.Empty(options.DescribeUnknownValues());
    }

    [Theory]
    [InlineData("--players=3", 3)]
    [InlineData("--players=9", 4)]
    [InlineData("--players=0", 1)]
    [InlineData("--players=two", 1)]
    public void Players_AreClamped(string argument, int expected) =>
        Assert.Equal(expected, PlayerStartupOptions.Parse([argument]).Players);

    [Fact]
    public void SurfaceDecodingAndSecondScreen_Parse()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["--surface=FrameSink", "--decoding=software", "--second-screen=2"]);

        Assert.True(options.PreferFrameSinkSurface);
        Assert.True(options.SoftwareDecoding);
        Assert.Equal(2, options.SecondScreen);
        Assert.Equal((VideoDecodingMethod.Software, "software decoding (--decoding=software)"), options.ApplyDecoding(VideoDecodingMethod.Hardware));
        Assert.Equal((VideoDecodingMethod.Software, (string?)null), options.ApplyDecoding(VideoDecodingMethod.Software));
        Assert.Equal((VideoDecodingMethod.Auto, (string?)null), PlayerStartupOptions.Parse([]).ApplyDecoding(VideoDecodingMethod.Auto));
    }

    [Fact]
    public void UnknownSurfaceAndDecoding_FallBackToAuto_WithWarnings()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["--surface=gpu", "--decoding=fast", "--second-screen=0"]);

        Assert.False(options.PreferFrameSinkSurface);
        Assert.False(options.SoftwareDecoding);
        Assert.Null(options.SecondScreen);
        Assert.Equal(2, options.DescribeUnknownValues().Count);
    }

    [Fact]
    public void ForPlayer_GivesTheStartupMediaToPlayerOneOnly()
    {
        PlayerStartupOptions options = PlayerStartupOptions.Parse(["song.mp3", "--prepare-next=next.mp3", "--players=2"]);

        Assert.Equal("song.mp3", options.ForPlayer(1).MediaPath);
        Assert.Null(options.ForPlayer(2).MediaPath);
        Assert.Null(options.ForPlayer(2).PrepareNextPath);
        Assert.Equal(2, options.ForPlayer(2).Players);
    }

    // --- full-screen chrome ---

    private sealed class ChromeHost
    {
        public ChromeHost((double, double)? pointer = null)
        {
            Pointer = pointer;
            Controller = new FullScreenChromeController(Dispatcher, visible => Shown.Add(visible), () => Keep, () => Pointer);
        }

        public TestPlayerDispatcher Dispatcher { get; } = new();

        public FullScreenChromeController Controller { get; }

        public List<bool> Shown { get; } = [];

        public bool Keep { get; set; }

        public (double, double)? Pointer { get; set; }

        public TestPlayerTimer Timer => Dispatcher.Timers.Single();
    }

    [Fact]
    public void Chrome_HidesAfterTheDelay_InFullScreenOnly()
    {
        var host = new ChromeHost();

        Assert.Equal(FullScreenChromeController.HideDelay, host.Timer.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2.5), host.Timer.Interval);
        host.Controller.PointerMoved(1, 1);
        Assert.False(host.Timer.IsRunning);

        host.Controller.Enter();
        Assert.True(host.Timer.IsRunning);
        Assert.Empty(host.Shown);

        host.Timer.Tick();
        Assert.False(host.Timer.IsRunning);
        Assert.Equal([false], host.Shown);
        Assert.False(host.Controller.IsChromeVisible);
    }

    [Fact]
    public void Chrome_ARealMoveShowsIt_ASyntheticMoveDoesNot()
    {
        var host = new ChromeHost(pointer: (10, 10));
        host.Controller.Enter();
        host.Timer.Tick();
        Assert.Equal([false], host.Shown);

        // Hiding resized the picture under the still pointer: the move at the same place is ignored.
        host.Controller.PointerMoved(10, 10);
        Assert.Equal([false], host.Shown);
        Assert.False(host.Timer.IsRunning);

        host.Controller.PointerMoved(11, 10);
        Assert.Equal([false, true], host.Shown);
        Assert.True(host.Timer.IsRunning);
    }

    [Fact]
    public void Chrome_WithoutAPointerSource_UsesTheLastReportedPosition()
    {
        var host = new ChromeHost(pointer: null);
        host.Controller.Enter();
        host.Controller.PointerMoved(5, 5);
        host.Timer.Tick();

        host.Controller.PointerMoved(5, 5);
        Assert.Equal([false], host.Shown);
    }

    [Fact]
    public void Chrome_StaysAndRetries_WhileTheHostKeepsIt()
    {
        var host = new ChromeHost { Keep = true };
        host.Controller.Enter();

        host.Timer.Tick();
        Assert.True(host.Timer.IsRunning);
        Assert.Empty(host.Shown);

        host.Keep = false;
        host.Timer.Tick();
        Assert.Equal([false], host.Shown);
    }

    [Fact]
    public void Chrome_ExitShowsItAndStopsTheTimer_ThenMovesDoNothing()
    {
        var host = new ChromeHost();
        host.Controller.Enter();
        host.Timer.Tick();

        host.Controller.Exit();
        Assert.Equal([false, true], host.Shown);
        Assert.False(host.Timer.IsRunning);
        Assert.False(host.Controller.IsFullScreen);

        host.Controller.PointerMoved(50, 50);
        host.Controller.Show();
        Assert.False(host.Timer.IsRunning);
        Assert.Equal([false, true], host.Shown);
    }

    [Fact]
    public void Chrome_Dispose_DisposesTheTimer_AndALateTickDoesNothing()
    {
        var host = new ChromeHost();
        host.Controller.Enter();
        host.Controller.Dispose();

        Assert.True(host.Timer.IsDisposed);
        host.Timer.Tick();
        Assert.Empty(host.Shown);
    }

    // --- screen choice ---

    private static readonly PlayerScreen Left = new("DISPLAY1", new PlayerScreenBounds(0, 0, 1920, 1080), IsPrimary: true);
    private static readonly PlayerScreen Right = new("DISPLAY2", new PlayerScreenBounds(1920, 0, 1920, 1080), IsPrimary: false);

    [Fact]
    public void Screen_TheRequestedOneWhenItExists()
    {
        PlayerScreenChoice choice = PlayerScreenChooser.Choose([Left, Right], new PlayerScreenBounds(2000, 100, 800, 600), requested: 2);

        Assert.Equal(1, choice.ScreenIndex);
        Assert.True(choice.FullScreen);
        Assert.Equal("full screen on screen 2 of 2 (DISPLAY2)", choice.Where);
    }

    [Fact]
    public void Screen_OtherwiseTheFirstThatIsNotTheMainWindows()
    {
        // The main window is mostly on the right screen.
        PlayerScreenChoice choice = PlayerScreenChooser.Choose([Left, Right], new PlayerScreenBounds(1800, 100, 800, 600), requested: null);

        Assert.Equal(0, choice.ScreenIndex);
        Assert.True(choice.FullScreen);
    }

    [Fact]
    public void Screen_WithOneScreen_AWindowOnIt_SayingWhenTheRequestedOneIsMissing()
    {
        PlayerScreenChoice choice = PlayerScreenChooser.Choose([Left], new PlayerScreenBounds(100, 100, 800, 600), requested: 3);

        Assert.Equal(0, choice.ScreenIndex);
        Assert.False(choice.FullScreen);
        Assert.Equal("in a window (one screen; screen 3 does not exist)", choice.Where);
    }

    [Fact]
    public void Screen_None_GivesTheHostsDefaultPlacement()
    {
        PlayerScreenChoice choice = PlayerScreenChooser.Choose([], null, requested: null);

        Assert.Null(choice.ScreenIndex);
        Assert.False(choice.FullScreen);
    }

    [Fact]
    public void Screen_OwnerScreen_FallsBackToThePrimary_AndNamesAreDescribed()
    {
        PlayerScreen unnamed = new(string.Empty, new PlayerScreenBounds(-1280, 0, 1280, 1024), IsPrimary: false);

        Assert.Equal(1, PlayerScreenChooser.OwnerScreen([unnamed, Left], ownerBounds: null));
        Assert.Equal(1, PlayerScreenChooser.OwnerScreen([unnamed, Left], new PlayerScreenBounds(5000, 5000, 10, 10)));
        Assert.Equal(0, PlayerScreenChooser.OwnerScreen([unnamed, Left], new PlayerScreenBounds(-200, 0, 400, 400)));
        Assert.Equal("1280x1024 at -1280,0", PlayerScreenChooser.Describe(unnamed));
        Assert.Equal("DISPLAY1", PlayerScreenChooser.Describe(Left));
    }

    // --- stats ---

    [Fact]
    public void StatsLine_Format()
    {
        var line = PlayerStatsLine.Format(
            "P2",
            MediaPlayerState.Playing,
            59.94,
            new ProcessSample(12.4, 0.993, 101.6, 412.2),
            PlayerStatsLine.WithSecondScreen(
                PlayerStatsLine.DescribeSurface(1920, 1080, MediaSurfacePresentationPath.NativeWindow),
                PlayerStatsLine.DescribeSurface(1280, 720, MediaSurfacePresentationPath.FrameSink, presentationSuspended: true)),
            processorCount: 8);

        Assert.Equal(
            "P2 Playing | 59.9 fps | CPU 12% (0.99 of 8 cores) | managed 102 MB, working set 412 MB (whole process) | "
            + "surface 1920x1080 via native window | 2nd screen 1280x720 via frame sink (cleared: Audio mode)",
            line);
        Assert.Equal("a", PlayerStatsLine.WithSecondScreen("a", null));
    }

    [Fact]
    public void AbSwapDemoOptions_ParseTheDemoFlags_AndResolveFoldersToTheirVideos()
    {
        Assert.False(AbSwapDemoOptions.Parse(["clip.mp4"]).IsEnabled);

        AbSwapDemoOptions options = AbSwapDemoOptions.Parse(
            ["--ab-demo=\"D:\\- Testing Files\"", "--ab-demo=C:\\media\\a.mp4", "--ab-swaps=5000", "--ab-interval=1.5", "--ab-mode=Native", "--ab-collapsed", "--ab-exit"]);
        Assert.True(options.IsEnabled);
        Assert.Equal(["D:\\- Testing Files", "C:\\media\\a.mp4"], options.Sources);
        Assert.Equal(AbSwapDemoOptions.MaxSwaps, options.Swaps);
        Assert.Equal(TimeSpan.FromSeconds(1.5), options.Interval);
        Assert.True(options.Native);
        Assert.True(options.Collapsed);
        Assert.True(options.ExitWhenDone);

        AbSwapDemoOptions defaults = AbSwapDemoOptions.Parse(["--ab-demo=x", "--ab-swaps=bad", "--ab-interval=0"]);
        Assert.Equal(AbSwapDemoOptions.DefaultSwaps, defaults.Swaps);
        Assert.Equal(TimeSpan.FromSeconds(0.5), defaults.Interval);
        Assert.False(defaults.Native);
        Assert.False(defaults.Collapsed);
        Assert.False(defaults.ExitWhenDone);

        IReadOnlyList<string> clips = options.ResolveClips(
            static source => source.EndsWith("Files", StringComparison.Ordinal),
            static _ => ["D:\\- Testing Files\\b.MKV", "D:\\- Testing Files\\notes.txt", "D:\\- Testing Files\\A.mp4"]);
        Assert.Equal(["D:\\- Testing Files\\A.mp4", "D:\\- Testing Files\\b.MKV", "C:\\media\\a.mp4"], clips);
        Assert.Empty(AbSwapDemoOptions.Parse(["--ab-demo=gone"]).ResolveClips(static _ => true, static _ => null));
    }

    [Fact]
    public void AbSwapDemoSummary_DescribesCountsAndReadyTimes()
    {
        Assert.Equal("0 of 3 swaps, 3 without a first frame", AbSwapDemoSummary.Describe(3, [], 3));
        Assert.Equal(
            "4 of 5 swaps, 1 without a first frame; ready after min 40 ms, median 75 ms, max 300 ms",
            AbSwapDemoSummary.Describe(5, [300, 40, 70, 80], 1));
    }

    [Fact]
    public void UiThreadCostMeter_ReadsMillisecondsPerSecondAndPerFrame_FromRunningTotals()
    {
        long now = 0;
        var meter = new UiThreadCostMeter(() => now);
        Assert.Null(meter.Sample(TimeSpan.FromMilliseconds(5), 3));

        now += Stopwatch.Frequency * 2;
        (double MillisecondsPerSecond, double MillisecondsPerFrame)? cost = meter.Sample(TimeSpan.FromMilliseconds(5 + 240), 3 + 120);
        Assert.NotNull(cost);
        Assert.Equal(120, cost.Value.MillisecondsPerSecond, 3);
        Assert.Equal(2, cost.Value.MillisecondsPerFrame, 3);

        // Totals that went back (a new surface) start a new period.
        now += Stopwatch.Frequency;
        Assert.Null(meter.Sample(TimeSpan.FromMilliseconds(1), 1));
        now += Stopwatch.Frequency;
        Assert.Equal((0d, 0d), meter.Sample(TimeSpan.FromMilliseconds(1), 1));

        Assert.Equal("a", PlayerStatsLine.WithUiThreadCost("a", null));
        Assert.Equal("a, UI thread 12.3 ms/s (1.50 ms/frame)", PlayerStatsLine.WithUiThreadCost("a", (12.34, 1.5)));
    }

    [Fact]
    public void FrameCounter_CountsFramesPerSecond_AndRestartsEachPeriod()
    {
        long now = 0;
        var counter = new PresentedFrameCounter(() => now);
        for (var i = 0; i < 30; i++)
            counter.CountFrame();

        now = Stopwatch.Frequency / 2;
        Assert.Equal(60, counter.TakeFramesPerSecond(), precision: 6);

        now += Stopwatch.Frequency;
        Assert.Equal(0, counter.TakeFramesPerSecond());
    }

    [Fact]
    public void ProcessSampler_Reads()
    {
        using var sampler = new ProcessSampler();
        ProcessSample sample = sampler.Sample();

        Assert.True(sample.CpuPercent >= 0);
        Assert.True(sample.WorkingSetMegabytes > 0);
        Assert.True(sample.ManagedMegabytes > 0);
    }

    [Theory]
    [InlineData(PlayerRendererRequest.Vulkan, true, PlayerRendererRequest.Direct3D11)]
    [InlineData(PlayerRendererRequest.Direct3D11, true, PlayerRendererRequest.Warp)]
    [InlineData(PlayerRendererRequest.Gpu, true, PlayerRendererRequest.Warp)]
    [InlineData(PlayerRendererRequest.Auto, true, PlayerRendererRequest.Warp)]
    [InlineData(PlayerRendererRequest.Warp, true, PlayerRendererRequest.Software)]
    [InlineData(PlayerRendererRequest.Vulkan, false, PlayerRendererRequest.Software)]
    [InlineData(PlayerRendererRequest.Gpu, false, PlayerRendererRequest.Software)]
    public void FallbackAfterDeviceLoss_GoesToTheNextWorkingPath(PlayerRendererRequest current, bool windows, PlayerRendererRequest expected) =>
        Assert.Equal(expected, PlayerRendererRules.FallbackAfterDeviceLoss(current, windows));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FallbackAfterDeviceLoss_EndsAtSoftware(bool windows) =>
        Assert.Null(PlayerRendererRules.FallbackAfterDeviceLoss(PlayerRendererRequest.Software, windows));
}
