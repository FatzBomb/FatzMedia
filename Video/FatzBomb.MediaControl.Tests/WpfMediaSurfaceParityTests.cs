using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Wpf;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 046A slice 1: what the WPF surface and second-screen window share with the Avalonia and
/// WinForms adapters (role and surface id, display mode from the primary only, Core auto-hide and
/// the Audio-mode clear, full screen).
/// </summary>
public sealed class WpfMediaSurfaceParityTests
{
    [Fact]
    public void SurfaceDependencyProperties_DefaultToAPrimaryAutoHidingSurface()
    {
        Assert.True((bool)MediaSurface.AutoHideProperty.DefaultMetadata.DefaultValue);
        Assert.True((bool)BackgroundSurface.AutoHideProperty.DefaultMetadata.DefaultValue);
        Assert.True((bool)KaraokeSurface.AutoHideProperty.DefaultMetadata.DefaultValue);
        Assert.True(MediaSurface.IsContentVisibleProperty.ReadOnly);
        Assert.True(BackgroundSurface.IsContentVisibleProperty.ReadOnly);
        Assert.True(KaraokeSurface.IsContentVisibleProperty.ReadOnly);
        Assert.Equal(MediaPresentationSurfaceRole.Primary, MediaSurface.RoleProperty.DefaultMetadata.DefaultValue);
        Assert.Null(MediaSurface.SurfaceIdProperty.DefaultMetadata.DefaultValue);
    }

    [Fact]
    public void EffectiveSurfaceId_FollowsRoleAndSurfaceId() => RunSta(() =>
    {
        var surface = new MediaSurface();
        Assert.Equal(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, surface.EffectiveSurfaceId);

        surface.Role = MediaPresentationSurfaceRole.Projector;
        Assert.Equal(MediaSurface.DefaultSecondarySurfaceId, surface.EffectiveSurfaceId);

        surface.SurfaceId = " stage ";
        Assert.Equal("stage", surface.EffectiveSurfaceId);
        Assert.False(surface.IsSurfaceAttached);
        Assert.Equal(MediaSurfacePresentationPath.None, surface.PresentationPath);
    });

    [Fact]
    public void ProjectorSurface_DoesNotForwardItsDisplayMode() => RunSta(() =>
    {
        (IMediaControlSession session, WinFormsMediaSurfaceTests.SurfaceRecorder recorder) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        var surface = new MediaSurface { Role = MediaPresentationSurfaceRole.Projector, VideoDisplayMode = VideoDisplayMode.Stretch };

        surface.Player = session;
        surface.VideoDisplayMode = VideoDisplayMode.Original;
        Assert.Equal(0, recorder.VideoDisplayModeSets);

        surface.Role = MediaPresentationSurfaceRole.Primary;
        Assert.Equal(1, recorder.VideoDisplayModeSets);
        surface.Player = null;
    });

    [Fact]
    public void AudioMode_SuspendsTheSurfaceUntilThePlayerLeavesIt() => RunSta(RunAudioModeScenario);

    [Fact]
    public void UnloadedSurface_KeepsFollowingItsPlayer() => RunSta(RunUnloadedScenario);

    [Fact]
    public void SecondaryWindow_HostsAProjectorSurfaceInTheRequestedMode() => RunSta(() =>
    {
        (IMediaControlSession session, WinFormsMediaSurfaceTests.SurfaceRecorder recorder) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        var window = new SecondaryPresentationWindow(session, " singer ", presentationMode: WpfPresentationMode.WpfComposited);

        Assert.Equal(MediaPresentationSurfaceRole.Projector, window.Surface.Role);
        Assert.Equal("singer", window.SurfaceId);
        Assert.Equal(WpfPresentationMode.WpfComposited, window.Surface.PresentationMode);
        // The surface takes the session once the window has its handle (shown).
        Assert.Null(window.Surface.Player);
        Assert.Equal(0, recorder.VideoDisplayModeSets);

        window.IsFullScreen = true;
        Assert.Equal(WindowStyle.None, window.WindowStyle);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.IsFullScreen = false;
        Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
        Assert.Equal(WindowState.Normal, window.WindowState);
        window.Close();
        Assert.Null(window.Surface.Player);
    });

    [Theory]
    [InlineData(0, 0, 1920, 1040, 1440, 810, 240, 115)]
    [InlineData(1920, 0, 2560, 1400, 960, 540, 2720, 430)]
    [InlineData(-1280, 0, 1280, 680, 1500, 900, -1280, 0)]
    public void CenteredPosition_CentresTheWindowInTheWorkingArea(int x, int y, int width, int height, int windowWidth, int windowHeight, int expectedX, int expectedY) =>
        Assert.Equal(
            (expectedX, expectedY),
            PresentationScreen.CenteredPosition(new Int32Rect(x, y, width, height), windowWidth, windowHeight));

    private static void RunAudioModeScenario()
    {
        var factory = new PlayerModeSwitchTests.SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var surface = new MediaSurface { AutoHide = false, Player = player };
        DrainDispatcher();
        Assert.False(surface.IsPresentationSuspended);

        Assert.True(player.SetModeAsync(MediaPlayerMode.Audio).GetAwaiter().GetResult());
        player.FlushControlOperationsAsync().GetAwaiter().GetResult();
        DrainDispatcher();
        Assert.True(surface.IsPresentationSuspended);

        Assert.True(player.SetModeAsync(MediaPlayerMode.Full).GetAwaiter().GetResult());
        player.FlushControlOperationsAsync().GetAwaiter().GetResult();
        DrainDispatcher();
        Assert.False(surface.IsPresentationSuspended);
        surface.Player = null;
    }

    private static void RunUnloadedScenario()
    {
        // The content state follows the Player property, not the load state (029C: a paused
        // video moved to another tab or parent stays visible).
        var factory = new PlayerModeSwitchTests.SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var surface = new MediaSurface { AutoHide = false, Player = player };
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        DrainDispatcher();

        Assert.True(player.SetModeAsync(MediaPlayerMode.Audio).GetAwaiter().GetResult());
        player.FlushControlOperationsAsync().GetAwaiter().GetResult();
        DrainDispatcher();
        Assert.True(surface.IsPresentationSuspended);
        surface.Player = null;
        Assert.False(surface.IsPresentationSuspended);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WPF test thread did not complete.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
