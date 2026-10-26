using System.Drawing;
using System.Windows.Forms;
using FatzBomb.MediaControl.WinForms;
using WinFormsMediaSurface = FatzBomb.MediaControl.WinForms.MediaSurface;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 046A: the WinForms adapter's second-screen window (Avalonia shape).</summary>
public sealed class WinFormsSecondaryPresentationWindowTests
{
    [Fact]
    public void Window_AttachesAProjectorSurfaceAndDetachesItWhenClosed() => RunSta(() =>
    {
        (IMediaControlSession session, WinFormsMediaSurfaceTests.SurfaceRecorder recorder) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        var window = new SecondaryPresentationWindow(session, " singer ") { ShowInTaskbar = false };
        Assert.Equal(MediaPresentationSurfaceRole.Projector, window.Surface.Role);
        Assert.Equal("singer", window.Surface.SurfaceId);

        window.ShowOnScreen(null, fullScreen: false);
        Application.DoEvents();
        Assert.True(window.Surface.IsSurfaceAttached);
        Assert.Equal(MediaPresentationSurfaceRole.Projector, recorder.LastAttached!.Role);
        Assert.Equal("singer", recorder.LastAttached.Id);
        Assert.Equal(0, recorder.VideoDisplayModeSets);

        window.Close();
        Assert.Equal("singer", recorder.DetachedIds.Last());
        Assert.Null(window.Surface.Player);
    });

    [Fact]
    public void F11AndEscape_ToggleAndLeaveFullScreen() => RunSta(() =>
    {
        (IMediaControlSession session, _) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        using var window = new KeyWindow(session);
        FormBorderStyle border = window.FormBorderStyle;

        window.Press(Keys.F11);
        Assert.True(window.IsFullScreen);
        Assert.Equal(FormBorderStyle.None, window.FormBorderStyle);
        Assert.Equal(FormWindowState.Maximized, window.WindowState);

        window.Press(Keys.Escape);
        Assert.False(window.IsFullScreen);
        Assert.Equal(border, window.FormBorderStyle);
        Assert.Equal(FormWindowState.Normal, window.WindowState);

        // Escape in a window closes it (the keyboard way out of a windowed second screen).
        window.ShowOnScreen(null, fullScreen: false);
        window.Press(Keys.Escape);
        Assert.True(window.IsDisposed);
    });

    [Theory]
    [InlineData(0, 0, 1920, 1040, 976, 579, 472, 230)]
    [InlineData(1920, 0, 2560, 1400, 976, 579, 2712, 410)]
    [InlineData(-1280, 0, 1280, 680, 1500, 900, -1280, 0)]
    public void CenteredLocation_CentresTheWindowInTheWorkingArea(int x, int y, int width, int height, int windowWidth, int windowHeight, int expectedX, int expectedY) =>
        Assert.Equal(
            new Point(expectedX, expectedY),
            SecondaryPresentationWindow.CenteredLocation(new Rectangle(x, y, width, height), new Size(windowWidth, windowHeight)));

    [Fact]
    public void ProjectorSurfaceInAnyForm_DoesNotTouchTheDisplayMode() => RunSta(() =>
    {
        (IMediaControlSession session, WinFormsMediaSurfaceTests.SurfaceRecorder recorder) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Role = MediaPresentationSurfaceRole.Projector, Player = session };

        surface.VideoDisplayMode = VideoDisplayMode.Stretch;

        Assert.Equal(0, recorder.VideoDisplayModeSets);
    });

    private sealed class KeyWindow(IMediaControlSession session) : SecondaryPresentationWindow(session)
    {
        public void Press(Keys key) => OnKeyDown(new KeyEventArgs(key));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WinForms test thread did not complete.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
