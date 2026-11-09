using System.Collections.Concurrent;
using System.Diagnostics;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// Reports a Vulkan call that blocked the presentation thread (ENG-039: after a GPU reset a
/// presentation loop stalled for 14.5 s and the log did not say where). A call slower than
/// <see cref="Threshold"/> raises one warning naming it, at most once a second per call site.
/// Process-wide: the warning goes to whichever player made the call.
/// </summary>
static class VulkanStallMeter
{
    /// <summary>A call slower than this is reported.</summary>
    internal static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(250);

    static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);
    static readonly ConcurrentDictionary<string, long> LastReports = new(StringComparer.Ordinal);

    /// <summary>Reports <paramref name="operation"/> when it took longer than <see cref="Threshold"/> since <paramref name="startTimestamp"/>.</summary>
    public static void Report(long startTimestamp, string operation)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp, now);
        if (elapsed < Threshold)
            return;

        if (LastReports.TryGetValue(operation, out var last) && Stopwatch.GetElapsedTime(last, now) < ReportInterval)
            return;

        LastReports[operation] = now;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Vulkan", $"{operation} took {elapsed.TotalMilliseconds:F0} ms.");
    }
}
