using System.Diagnostics;

namespace FatzBomb.MediaControl.Player.Avalonia;

/// <summary>One reading of the whole process (all players share it).</summary>
internal readonly record struct ProcessSample(double CpuPercent, double CoresUsed, double ManagedMegabytes, double WorkingSetMegabytes);

/// <summary>Process CPU (share of all cores since the previous sample) and memory, for measuring.</summary>
internal sealed class ProcessSampler : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private long _lastTimestamp;

    public ProcessSampler()
    {
        _lastCpu = _process.TotalProcessorTime;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    public ProcessSample Sample()
    {
        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds, 0.001);
        var cores = (cpu - _lastCpu).TotalSeconds / elapsed;
        _lastCpu = cpu;
        _lastTimestamp = now;

        return new ProcessSample(
            cores / Environment.ProcessorCount * 100,
            cores,
            GC.GetTotalMemory(false) / 1048576.0,
            _process.WorkingSet64 / 1048576.0);
    }

    public void Dispose() => _process.Dispose();
}
