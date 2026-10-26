using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Avalonia.Tests;

/// <summary>
/// Stage 046A slice 1: the Avalonia surface's share of the adapter parity work (display mode only
/// from the primary, Core auto-hide, attach refusal warning, Core's path enum).
/// </summary>
public sealed class AdapterParityTests
{
    [AvaloniaFact]
    public void ProjectorSurface_NeverForwardsItsVideoDisplayMode()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        var primary = new MediaSurface { Player = session, VideoDisplayMode = VideoDisplayMode.Stretch };
        Window main = Show(primary, 320, 180);
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
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        recorder.IsOpen = false;
        recorder.HasVisualContent = false;
        var surface = new MediaSurface { Player = session };
        var visibilityChanges = 0;
        surface.ContentVisibilityChanged += (_, _) => visibilityChanges++;
        Window window = Show(surface, 64, 32);
        Assert.False(surface.IsContentVisible);

        // Load and first frame arrive on background threads, as from the session.
        recorder.IsOpen = true;
        recorder.HasVisualContent = true;
        Task.Run(() =>
        {
            recorder.Raise(nameof(IMediaControlSession.MediaLoaded), new MediaLoadedEventArgs(Source, TimeSpan.Zero));
            recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
        }).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.True(surface.IsContentVisible);
        Assert.Equal(1, visibilityChanges);

        Task.Run(() => recorder.Raise(nameof(IMediaControlSession.MediaStopped), EventArgs.Empty)).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.False(surface.IsContentVisible);
        Assert.Equal(2, visibilityChanges);
        window.Close();
    }

    [AvaloniaFact]
    public void AutoHide_HidesTheSharedImageVisualWhileNothingIsShown_AndOffShowsIt()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
        recorder.IsOpen = false;
        var surface = new MediaSurface { GpuInteropProvider = SharedGpuImagePathTests.FakeInterop.Provider(SharedGpuImagePathTests.FakeInterop.Direct3D()) };
        surface.Player = session;
        Window window = Show(surface, 320, 180);
        Assert.Equal(MediaSurfacePresentationPath.SharedGpuImage, surface.PresentationPath);
        global::Avalonia.Rendering.Composition.CompositionVisual visual = global::Avalonia.Rendering.Composition.ElementComposition.GetElementChildVisual(surface)!;
        Assert.False(visual.Visible);

        surface.AutoHide = false;
        Dispatcher.UIThread.RunJobs();
        Assert.True(visual.Visible);
        window.Close();
    }

    [AvaloniaFact]
    public void RefusedAttach_RaisesOneWarningOwnedByTheSession()
    {
        (IMediaControlSession session, RecordingSession recorder) = RecordingSession.Create();
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
            Window window = Show(surface, 64, 32);
            window.Width = 80;
            Dispatcher.UIThread.RunJobs();

            Assert.False(surface.IsSurfaceAttached);
            Assert.Equal(MediaSurfacePresentationPath.None, surface.PresentationPath);
            MediaDiagnosticInfo warning = Assert.Single(warnings);
            Assert.Contains("did not accept the surface", warning.Message, StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    private static readonly MediaSourceInfo Source = new(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video);

    private static Window Show(MediaSurface surface, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
