using System.Runtime.CompilerServices;

namespace FatzBomb.MediaControl.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Several tests block a worker thread while waiting on queued control operations. Under
    /// parallel xUnit collections on small CI runners the default thread-pool ramp-up can delay
    /// a <see cref="Task.Run(Action)"/> continuation past the tests' wall-clock timeouts, so the
    /// minimum worker count is raised once per test process. Production code is unaffected.
    /// </summary>
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        _ = ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(completionPorts, 64));
    }
}
