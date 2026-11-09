using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// Stage 027A slice 5: one session shown on two screens (the operator view and the singer screen),
/// and the Audio-mode clear (Stage 043A slice 2 open item).
/// </summary>
public sealed class SecondScreenTests
{
    [AvaloniaFact]
    public void TwoPlayers_EachSurfaceDiagnosticBelongsToItsOwnSession()
    {
        // ENG-036: with several players in one process, a surface's diagnostics reach only the
        // player it is attached to (an unowned one reaches every player's log).
        (IMediaControlSession first, RecordingSession firstRecorder) = RecordingSession.Create();
        (IMediaControlSession second, _) = RecordingSession.Create();
        var raised = new System.Collections.Concurrent.ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == MediaSurface.DiagnosticCategory && info.Message.Contains("'eng036-", StringComparison.Ordinal))
                raised.Enqueue(info);
        }

        var windows = new List<Window>();
        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            windows.Add(Show(new MediaSurface { Player = first, SurfaceId = "eng036-main-1", Role = MediaPresentationSurfaceRole.Projector }, 320, 180));
            windows.Add(Show(new MediaSurface { Player = second, SurfaceId = "eng036-main-2", Role = MediaPresentationSurfaceRole.Projector }, 320, 180));
            var firstScreen = new SecondaryPresentationWindow(first, "eng036-screen-1") { Width = 640, Height = 360 };
            var secondScreen = new SecondaryPresentationWindow(second, "eng036-screen-2") { Width = 640, Height = 360 };
            windows.Add(firstScreen);
            windows.Add(secondScreen);
            firstScreen.Show();
            secondScreen.Show();
            Dispatcher.UIThread.RunJobs();
            firstRecorder.SwitchMode(MediaPlayerMode.Audio);
            Dispatcher.UIThread.RunJobs();

            MediaDiagnosticInfo[] all = [.. raised];
            Assert.Equal(6, all.Length);
            Assert.All(all, static info => Assert.NotNull(info.Owner));
            foreach (MediaDiagnosticInfo info in all)
                Assert.Same(info.Message.Contains("-1'", StringComparison.Ordinal) ? first : second, info.Owner);
            Assert.Equal(2, all.Count(static info => info.Message.Contains("Audio mode", StringComparison.Ordinal)));
        }
        finally
        {
            // Close even when an assertion failed, so no window outlives the test.
            foreach (Window window in windows)
                window.Close();
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    [AvaloniaFact]
    public void TwoSurfacesOnOneSession_AttachWithTheirOwnIdsRolesAndSizes()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var primary = new MediaSurface { Player = session };
        Window main = Show(primary, 320, 180);
        var second = new SecondaryPresentationWindow(session) { Width = 640, Height = 360 };
        second.Show();
        Dispatcher.UIThread.RunJobs();

        MediaPresentationSurfaceDescriptor[] attached = [.. recorder.Attached];
        Assert.Equal(2, attached.Length);
        Assert.Equal((MediaPresentationSurfaceDescriptor.PrimarySurfaceId, MediaPresentationSurfaceRole.Primary, 320, 180), Describe(attached[0]));
        Assert.Equal((MediaSurface.DefaultSecondarySurfaceId, MediaPresentationSurfaceRole.Projector, 640, 360), Describe(attached[1]));
        Assert.True(primary.IsPrimarySurfaceAttached);
        Assert.True(second.Surface.IsSurfaceAttached);
        Assert.False(second.Surface.IsPrimarySurfaceAttached);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, second.Surface.PresentationPath);

        // Each surface resizes and detaches only itself; closing the second screen keeps the primary.
        second.Width = 800;
        Dispatcher.UIThread.RunJobs();
        second.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            ["attach primary 320x180", "attach secondary 640x360", "resize secondary 800x360", "detach secondary"],
            recorder.SurfaceCalls);
        Assert.True(primary.IsPrimarySurfaceAttached);
        Assert.False(second.Surface.IsSurfaceAttached);
        main.Close();
    }

    [AvaloniaFact]
    public void TwoSurfaces_ShowTheFramesOfTheirOwnSinks()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var primary = new MediaSurface { Player = session };
        var singer = new MediaSurface { Player = session, Role = MediaPresentationSurfaceRole.Projector, SurfaceId = "singer" };
        Window main = Show(primary, 320, 180);
        Window second = Show(singer, 200, 100);
        MediaPresentationSurfaceDescriptor[] attached = [.. recorder.Attached];
        IMediaPresentationFrameSink primarySink = ResolveSink(attached.Single(static d => d.Role == MediaPresentationSurfaceRole.Primary));
        IMediaPresentationFrameSink singerSink = ResolveSink(attached.Single(static d => d.Id == "singer"));
        Assert.NotSame(primarySink, singerSink);

        // The renderer presents each target at its own size, on its presentation thread.
        Task.Run(() =>
        {
            primarySink.PresentFrame(new byte[320 * 180 * 4], 320, 180, 320 * 4);
            singerSink.PresentFrame(new byte[200 * 100 * 4], 200, 100, 200 * 4);
        }).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new PixelSize(320, 180), primary.FrameSink!.Bitmap!.PixelSize);
        Assert.Equal(new PixelSize(200, 100), singer.FrameSink!.Bitmap!.PixelSize);
        second.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void ChangingRoleOrSurfaceId_ReattachesUnderTheNewId()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 100, 50);

        surface.Role = MediaPresentationSurfaceRole.Projector;
        surface.SurfaceId = "stage";

        Assert.Equal(
            ["attach primary 100x50", "detach primary", "attach secondary 100x50", "detach secondary", "attach stage 100x50"],
            recorder.SurfaceCalls);
        Assert.Equal("stage", surface.EffectiveSurfaceId);
        window.Close();
    }

    [AvaloniaFact]
    public void SecondaryRejectedForSharedImages_FallsBackAloneWithItsReason()
    {
        // A renderer that exports shared images for the primary only (or for neither): each
        // surface decides its path, so only the secondary falls back.
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        recorder.AttachResult = descriptor =>
        {
            if (descriptor.Role == MediaPresentationSurfaceRole.Primary || descriptor.NativeSurface.SurfaceTypeId != MediaNativeSurfaceTypeIds.SharedGpuImage)
                return true;

            _ = MediaSharedGpuImageConsumerHandle.TryResolve(descriptor, out IMediaSharedGpuImageConsumer? consumer);
            consumer!.OnUnavailable("creating the shared textures failed: out of memory");
            return false;
        };
        var primary = new MediaSurface { GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()) };
        var singer = new MediaSurface
        {
            Role = MediaPresentationSurfaceRole.Projector,
            GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()),
        };
        primary.Player = session;
        singer.Player = session;
        Window main = Show(primary, 320, 180);
        Window second = Show(singer, 640, 360);

        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, primary.PresentationPath);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, singer.PresentationPath);
        Assert.Equal("creating the shared textures failed: out of memory", singer.PresentationPathReason);
        MediaPresentationSurfaceDescriptor last = recorder.LastAttached!;
        Assert.Equal((MediaSurface.DefaultSecondarySurfaceId, MediaNativeSurfaceTypeIds.ManagedBgraFrameSink), (last.Id, last.NativeSurface.SurfaceTypeId));
        second.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void AudioMode_ClearsTheLastFrameUntilTheSessionLeavesIt()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 64, 32);
        Assert.Equal(1, recorder.ModeChangedSubscribers);
        IMediaPresentationFrameSink sink = ResolveSink(recorder.LastAttached!);
        sink.PresentFrame(new byte[64 * 32 * 4], 64, 32, 64 * 4);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(surface.FrameSink!.Bitmap);

        // The switch is raised on the session's control queue; no renderer presents afterwards.
        Task.Run(() => recorder.SwitchMode(MediaPlayerMode.Audio)).Wait();
        Dispatcher.UIThread.RunJobs();

        Assert.True(surface.IsPresentationSuspended);
        Assert.Null(surface.FrameSink!.Bitmap);
        Assert.True(surface.IsPrimarySurfaceAttached);

        // Back in Full mode the new renderer's frames show again.
        Task.Run(() => recorder.SwitchMode(MediaPlayerMode.Full)).Wait();
        Dispatcher.UIThread.RunJobs();
        sink.PresentFrame(new byte[64 * 32 * 4], 64, 32, 64 * 4);
        Dispatcher.UIThread.RunJobs();

        Assert.False(surface.IsPresentationSuspended);
        Assert.NotNull(surface.FrameSink!.Bitmap);

        // A quick Audio -> Full switch: the late Audio notice finds the session in Full mode.
        recorder.Mode = MediaPlayerMode.Full;
        Task.Run(() => recorder.SwitchMode(MediaPlayerMode.Audio)).Wait();
        recorder.Mode = MediaPlayerMode.Full;
        Dispatcher.UIThread.RunJobs();
        Assert.False(surface.IsPresentationSuspended);

        window.Close();
        Assert.Equal(0, recorder.ModeChangedSubscribers);
    }

    [AvaloniaFact]
    public void AudioMode_HidesTheSharedImageVisual()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var surface = new MediaSurface { GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface, 320, 180);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        CompositionVisual visual = ElementComposition.GetElementChildVisual(surface)!;
        Assert.True(visual.Visible);

        recorder.SwitchMode(MediaPlayerMode.Audio);
        Dispatcher.UIThread.RunJobs();
        Assert.False(visual.Visible);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);

        recorder.SwitchMode(MediaPlayerMode.Full);
        Dispatcher.UIThread.RunJobs();
        Assert.True(visual.Visible);
        window.Close();
    }

    [AvaloniaFact]
    public void ASessionAlreadyInAudioMode_ShowsOnlyTheBackground()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        recorder.Mode = MediaPlayerMode.Audio;
        var surface = new MediaSurface { Player = session };
        Window window = Show(surface, 64, 32);

        Assert.True(surface.IsPresentationSuspended);
        Assert.True(surface.IsPrimarySurfaceAttached);

        surface.Player = null;
        Assert.False(surface.IsPresentationSuspended);
        window.Close();
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 1.0, 960, 540, 480, 270)]
    [InlineData(1920, 0, 2560, 1440, 1.5, 960, 540, 2480, 315)]
    [InlineData(-1280, 0, 1280, 720, 2.0, 960, 540, -1280, 0)]
    public void CenteredPosition_CentresTheScaledWindowInTheWorkingArea(
        int x, int y, int width, int height, double scaling, double windowWidth, double windowHeight, int expectedX, int expectedY) =>
        Assert.Equal(
            new PixelPoint(expectedX, expectedY),
            SecondaryPresentationWindow.CenteredPosition(new PixelRect(x, y, width, height), scaling, new Size(windowWidth, windowHeight)));

    private static (string, MediaPresentationSurfaceRole, int, int) Describe(MediaPresentationSurfaceDescriptor descriptor) =>
        (descriptor.Id, descriptor.Role, descriptor.PixelWidth, descriptor.PixelHeight);

    private static IMediaPresentationFrameSink ResolveSink(MediaPresentationSurfaceDescriptor descriptor)
    {
        Assert.True(descriptor.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint token));
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(token, out IMediaPresentationFrameSink? sink));
        return sink!;
    }

    private static Window Show(MediaSurface surface, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
