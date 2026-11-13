using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// Stage 046A slice 1: the Avalonia surface's share of the adapter parity work (display mode only
/// from the primary, Core auto-hide, attach refusal warning, Core's path enum).
/// </summary>
public sealed class AdapterParityTests
{
    [AvaloniaFact]
    public void ProjectorSurface_NeverForwardsItsVideoDisplayMode()
    {
        (var session, var recorder) = RecordingSession.Create();
        var primary = new MediaSurface { Player = session, VideoDisplayMode = VideoDisplayMode.Stretch };
        var main = Show(primary, 320, 180);
        Assert.Equal(VideoDisplayMode.Stretch, recorder.LastDisplayMode);

        // Opening the singer screen used to reset the session to Zoom.
        var second = new SecondaryPresentationWindow(session) { Width = 320, Height = 180 };
        second.Show();
        second.Surface.VideoDisplayMode = VideoDisplayMode.Original;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(VideoDisplayMode.Stretch, recorder.LastDisplayMode);

        // A surface that becomes primary takes over the display mode.
        second.Surface.Role = MediaPresentationSurfaceRole.Primary;
        Assert.Equal(VideoDisplayMode.Original, recorder.LastDisplayMode);
        second.Close();
        main.Close();
    }

    [AvaloniaFact]
    public void AutoHide_ShowsFramesFromTheFirstPresentedFrameUntilStop()
    {
        (var session, var recorder) = RecordingSession.Create();
        recorder.IsOpen = false;
        recorder.HasVisualContent = false;
        var surface = new MediaSurface { Player = session };
        var visibilityChanges = 0;
        surface.ContentVisibilityChanged += (_, _) => visibilityChanges++;
        var window = Show(surface, 64, 32);
        Assert.False(surface.IsContentVisible);

        // Load and first frame arrive on background threads, as from the session.
        recorder.IsOpen = true;
        recorder.HasVisualContent = true;
        Task.Run(() =>
        {
            recorder.Raise(nameof(IPlayerSession.MediaLoaded), new MediaLoadedEventArgs(Source, TimeSpan.Zero));
            recorder.Raise(nameof(IPlayerSession.PresentationFramePresented), EventArgs.Empty);
        }).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.True(surface.IsContentVisible);
        Assert.Equal(1, visibilityChanges);

        Task.Run(() => recorder.Raise(nameof(IPlayerSession.MediaStopped), EventArgs.Empty)).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.False(surface.IsContentVisible);
        Assert.Equal(2, visibilityChanges);
        window.Close();
    }

    [AvaloniaFact]
    public void OverlayContent_FillsTheSurfaceAboveTheFrames_OnBothPaths()
    {
        var overlay = new TextBlock { Text = "overlay" };
        (var session, _) = RecordingSession.Create();
        var surface = new MediaSurface
        {
            GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()),
            Player = session,
            OverlayContent = overlay,
        };
        var window = Show(surface, 320, 180);

        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        Assert.Same(overlay, surface.OverlayPresenter.Content);
        Assert.True(surface.OverlayPresenter.IsVisible);
        Assert.False(surface.OverlayPresenter.IsHitTestVisible);
        Assert.Equal(surface.Bounds.Size, surface.OverlayPresenter.Bounds.Size);
        // The shared image hangs on the frame host, which comes before the overlay.
        var children = global::Avalonia.VisualTree.VisualExtensions.GetVisualChildren(surface).ToList();
        Assert.Same(surface.GpuImageHost, children[0]);
        Assert.Same(surface.OverlayPresenter, children[^1]);

        surface.OverlayHitTestVisible = true;
        Assert.True(surface.OverlayPresenter.IsHitTestVisible);
        surface.OverlayContent = null;
        Assert.False(surface.OverlayPresenter.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void AutoHide_HidesTheSharedImageVisualWhileNothingIsShown_AndOffShowsIt()
    {
        (var session, var recorder) = RecordingSession.Create();
        recorder.IsOpen = false;
        var surface = new MediaSurface
        {
            GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()),
            Player = session
        };
        var window = Show(surface, 320, 180);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        var visual = global::Avalonia.Rendering.Composition.ElementComposition.GetElementChildVisual(surface.GpuImageHost)!;
        Assert.False(visual.Visible);

        surface.AutoHide = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(visual.Visible);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_LeavesFullScreen_AndClosesAWindowedSecondScreen()
    {
        (var session, var recorder) = RecordingSession.Create();
        var window = new KeyWindow(session) { Width = 320, Height = 180 };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.IsFullScreen = true;
        Dispatcher.UIThread.RunJobs();

        window.Press(global::Avalonia.Input.Key.Escape);
        Assert.False(window.IsFullScreen);
        Assert.False(closed);

        window.Press(global::Avalonia.Input.Key.Escape);
        Dispatcher.UIThread.RunJobs();
        Assert.True(closed);
        Assert.Contains($"detach {MediaSurface.DefaultSecondarySurfaceId}", recorder.SurfaceCalls);
    }

    sealed class KeyWindow(IPlayerSession session) : SecondaryPresentationWindow(session)
    {
        public void Press(global::Avalonia.Input.Key key) =>
            OnKeyDown(new global::Avalonia.Input.KeyEventArgs { Key = key, RoutedEvent = KeyDownEvent });
    }

    [AvaloniaFact]
    public void RefusedAttach_RaisesOneWarningOwnedByTheSession()
    {
        (var session, var recorder) = RecordingSession.Create();
        recorder.AttachResult = static _ => false;
        var warnings = new System.Collections.Concurrent.ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Level == MediaDiagnosticLevel.Warning && info.Category == MediaSurface.DiagnosticCategory && ReferenceEquals(info.Owner, session))
                warnings.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            var surface = new MediaSurface { Player = session, PreferGpuSharedSurface = false };
            var window = Show(surface, 64, 32);
            window.Width = 80;
            Dispatcher.UIThread.RunJobs();

            Assert.False(surface.IsSurfaceAttached);
            Assert.Equal(MediaSurfacePresentationPath.None, surface.PresentationPath);
            var warning = Assert.Single(warnings);
            Assert.Contains("did not accept the surface", warning.Message, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    static readonly MediaSourceInfo Source = new(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video);

    static Window Show(MediaSurface surface, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
