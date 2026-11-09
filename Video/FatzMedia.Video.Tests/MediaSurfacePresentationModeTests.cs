using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Wpf;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace FatzMedia.Video.Tests;

public sealed class MediaSurfacePresentationModeTests
{
    // Last step the STA thread reached, named in the timeout message.
    private static volatile string s_step = "not started";

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

        // Cold WPF startup at the start of a parallel test run can be slow; same budget as the WinForms STA test.
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"STA WPF test thread did not complete (last step: {s_step}).");
        if (threadException != null)
            ExceptionDispatchInfo.Capture(threadException).Throw();
    }

    private static void RunDeferredPresentationModeScenario()
    {
        s_step = "create player";
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(
            new TestPlatformFactory(engine),
            MediaPlayerConfiguration.Auto,
            []);
        s_step = "create surface";
        var surface = new MediaSurface { Player = player };

        s_step = "load first";
        Assert.True(player.Load("first.mp3"));
        surface.PresentationMode = WpfPresentationMode.WpfComposited;
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        s_step = "load second";
        Assert.True(player.Load("second.mp3"));
        s_step = "drain after load";
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        s_step = "close";
        player.Close();
        s_step = "drain after close";
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.WpfComposited, surface.PresentationMode);

        s_step = "detach surface";
        surface.Player = null;
        s_step = "dispose player";
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed class TestPlatformFactory(TestMediaEngine engine) : IPlatformFactory
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
