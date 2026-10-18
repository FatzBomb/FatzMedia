using FatzBomb.MediaControl.Wpf;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 022A: the WPF composited sink reuses its frame array instead of allocating one
/// surface-sized array per presented frame.
/// </summary>
public sealed class WpfCompositedSurfaceHostTests
{
    [Fact]
    public void PresentFrame_ReusesTheArrayOncePublished_AndGrowsOnlyForLargerFrames() => RunOnSta(() =>
    {
        using var host = new WpfCompositedSurfaceHost();
        var small = new byte[4 * 2 * 4];
        var large = new byte[8 * 4 * 4];

        for (var frame = 0; frame < 5; frame++)
        {
            host.PresentFrame(small, 4, 2, 16);
            DrainDispatcher();
        }

        Assert.Equal(1, host.BufferAllocations);

        host.PresentFrame(large, 8, 4, 32);
        DrainDispatcher();
        host.PresentFrame(new byte[6 * 4 * 4], 6, 4, 24); // fits the larger array, within 2x
        DrainDispatcher();

        Assert.Equal(2, host.BufferAllocations);

        // A spare more than twice the needed size (after a maximize) is dropped, not kept.
        var tiny = new byte[1 * 1 * 4];
        host.PresentFrame(tiny, 1, 1, 4);
        DrainDispatcher();
        Assert.Equal(3, host.BufferAllocations);
    });

    private static void RunOnSta(Action scenario)
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                scenario();
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

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
