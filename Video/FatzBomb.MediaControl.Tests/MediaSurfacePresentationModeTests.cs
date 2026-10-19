using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Wpf;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace FatzBomb.MediaControl.Tests;

public sealed class MediaSurfacePresentationModeTests
{
    [Fact]
    public void PresentationMode_StaysDeferredAcrossReplacementAndAppliesAfterClose()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                RunDeferredPresentationModeScenario();
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA WPF test thread did not complete.");
        if (threadException != null)
            ExceptionDispatchInfo.Capture(threadException).Throw();
    }

    private static void RunDeferredPresentationModeScenario()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(
            new TestPlatformFactory(engine),
            MediaPlayerConfiguration.Auto,
            []);
        var surface = new MediaSurface { Player = player };

        Assert.True(player.Load("first.mp3"));
        surface.PresentationMode = WpfPresentationMode.WpfComposited;
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        Assert.True(player.Load("second.mp3"));
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        player.Close();
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.WpfComposited, surface.PresentationMode);

        surface.Player = null;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
    {
        private readonly NullPresentationCoordinator _coordinator = new();

        public string LastNativeLoadMessage => string.Empty;
        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(
            IMediaEngine mediaEngine,
            MediaPlayerConfiguration configuration) => _coordinator;

        public IServiceProvider CreateServiceProvider(
            MediaPlayerConfiguration configuration,
            IReadOnlyList<string> nativeLibraryPaths) => new TestServiceProvider(engine);
    }
}
