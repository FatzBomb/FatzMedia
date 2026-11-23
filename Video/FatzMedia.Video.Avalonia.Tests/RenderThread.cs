using Avalonia.Headless;
using Avalonia.Threading;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// The frame sink takes frames on the compositor's animation-frame tick, not on the UI thread: a
/// test drives the (manual) headless render timer until the condition holds.
/// </summary>
static class RenderThread
{
    /// <summary>Ticks the render timer and runs UI jobs until <paramref name="done"/> holds; fails after five seconds.</summary>
    public static void Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "the compositor did not take the frame in time");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
