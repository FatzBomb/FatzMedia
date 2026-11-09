using System.Runtime.CompilerServices;

namespace FatzMedia.Video.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Upper bound for "this must eventually happen" waits. It only bounds latency on a loaded
    /// CI runner; tests that assert something does NOT happen use their own short delays.
    /// </summary>
    internal static readonly TimeSpan EventuallyTimeout = TimeSpan.FromSeconds(15);

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
