using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Wpf;

namespace FatzMedia.Video.Tests;

public sealed class MediaSurfacePresentationModeTests
{
    // Last step the STA thread reached, named in the timeout message.
    static volatile string _step = "not started";

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"STA WPF test thread did not complete (last step: {_step}).");
        if (threadException != null)
            ExceptionDispatchInfo.Capture(threadException).Throw();
    }

    static void RunDeferredPresentationModeScenario()
    {
        _step = "create player";
        var engine = new TestMediaEngine();
        using var player = new PlayerCore(
            new TestPlatformFactory(engine),
            MediaPlayerConfiguration.Auto,
            []);
        _step = "create surface";
        var surface = new MediaSurface { Player = player };

        _step = "load first";
        Assert.True(player.Load("first.mp3"));
        surface.PresentationMode = WpfPresentationMode.WpfComposited;
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        _step = "load second";
        Assert.True(player.Load("second.mp3"));
        _step = "drain after load";
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.NativeHwndOverlay, surface.PresentationMode);

        _step = "close";
        player.Close();
        _step = "drain after close";
        DrainDispatcher();
        Assert.Equal(WpfPresentationMode.WpfComposited, surface.PresentationMode);

        _step = "detach surface";
        surface.Player = null;
        _step = "dispose player";
    }

    static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    sealed class TestPlatformFactory(TestMediaEngine engine) : IPlatformFactory, IDisposable
    {
        readonly NullPresentationCoordinator _coordinator = new();

        public void Dispose() => _coordinator.Dispose();

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
