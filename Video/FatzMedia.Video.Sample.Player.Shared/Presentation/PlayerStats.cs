using System.Diagnostics;
using System.Globalization;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Sample.Player.Shared.Presentation;

/// <summary>One reading of the whole process (all players of a window share it).</summary>
/// <param name="CpuPercent">The share of all cores used since the previous reading, 0-100.</param>
/// <param name="CoresUsed">The cores used since the previous reading (CPU seconds per second).</param>
/// <param name="ManagedMegabytes">The managed heap, in MiB.</param>
/// <param name="WorkingSetMegabytes">The working set, in MiB.</param>
public readonly record struct ProcessSample(double CpuPercent, double CoresUsed, double ManagedMegabytes, double WorkingSetMegabytes);

/// <summary>
/// Process CPU (share of all cores since the previous sample) and memory, for the stats line
/// (moved from the Avalonia player, Stage 046A slice 3). Call <see cref="Sample"/> about once a
/// second from one thread.
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    readonly Process _process = Process.GetCurrentProcess();
    TimeSpan _lastCpu;
    long _lastTimestamp;

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

/// <summary>
/// Counts the frames one player presents (Core's <see cref="IPlayerSession.PresentationFramePresented"/>,
/// raised on the render thread) and turns them into frames per second.
/// </summary>
public sealed class PresentedFrameCounter
{
    readonly Func<long> _timestamp;
    IPlayerSession? _session;
    long _frames;
    long _lastTimestamp;

    public PresentedFrameCounter()
        : this(Stopwatch.GetTimestamp)
    {
    }

    /// <param name="timestamp">A <see cref="Stopwatch"/>-frequency clock (tests pass a fake one).</param>
    public PresentedFrameCounter(Func<long> timestamp)
    {
        _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
        _lastTimestamp = timestamp();
    }

    /// <summary>Counts <paramref name="session"/>'s frames from now on (null stops counting). UI thread.</summary>
    public void Track(IPlayerSession? session)
    {
        if (ReferenceEquals(_session, session))
            return;

        if (_session is { } previous)
            previous.PresentationFramePresented -= OnFramePresented;
        _session = session;
        _ = Interlocked.Exchange(ref _frames, 0);
        _lastTimestamp = _timestamp();
        session?.PresentationFramePresented += OnFramePresented;
    }

    /// <summary>Gets the frames per second since the previous call (or since tracking started) and starts a new period.</summary>
    public double TakeFramesPerSecond()
    {
        var now = _timestamp();
        var elapsed = Math.Max((now - _lastTimestamp) / (double)Stopwatch.Frequency, 0.001);
        _lastTimestamp = now;
        return Interlocked.Exchange(ref _frames, 0) / elapsed;
    }

    /// <summary>Counts one presented frame (what the session event does; from any thread).</summary>
    public void CountFrame() => Interlocked.Increment(ref _frames);

    void OnFramePresented(object? sender, EventArgs e) => CountFrame();
}

/// <summary>
/// Turns a surface's running totals of UI-thread work (the WPF composited bitmap writes, Stage 051A)
/// into a reading per period: milliseconds per second and per frame. Call <see cref="Sample"/> about
/// once a second from one thread.
/// </summary>
public sealed class UiThreadCostMeter
{
    readonly Func<long> _timestamp;
    long _lastTimestamp;
    TimeSpan _lastTime;
    long _lastFrames;
    bool _started;

    public UiThreadCostMeter()
        : this(Stopwatch.GetTimestamp)
    {
    }

    /// <param name="timestamp">A <see cref="Stopwatch"/>-frequency clock (tests pass a fake one).</param>
    public UiThreadCostMeter(Func<long> timestamp) => _timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));

    /// <summary>
    /// Gets the cost since the previous call from the surface's totals, or null on the first call
    /// (or when the totals went back, a new surface): it starts a new period either way.
    /// </summary>
    public (double MillisecondsPerSecond, double MillisecondsPerFrame)? Sample(TimeSpan totalTime, long totalFrames)
    {
        var now = _timestamp();
        var started = _started && totalFrames >= _lastFrames && totalTime >= _lastTime;
        var elapsed = Math.Max((now - _lastTimestamp) / (double)Stopwatch.Frequency, 0.001);
        var spent = (totalTime - _lastTime).TotalMilliseconds;
        var frames = totalFrames - _lastFrames;
        _started = true;
        _lastTimestamp = now;
        _lastTime = totalTime;
        _lastFrames = totalFrames;
        if (!started)
            return null;

        return (spent / elapsed, frames > 0 ? spent / frames : 0);
    }
}

/// <summary>The stats line every test player shows under each player (Stage 046A slice 3).</summary>
public static class PlayerStatsLine
{
    /// <summary>
    /// Gets <c>{label} {state} | {fps} fps | CPU {n}% ({cores} of {count} cores) | managed {n} MB,
    /// working set {n} MB (whole process) | surface {surface}</c>, invariant culture.
    /// </summary>
    /// <param name="label">The player's name, shown first.</param>
    /// <param name="state">The player's transport state.</param>
    /// <param name="framesPerSecond">The video frames presented per second.</param>
    /// <param name="process">The latest CPU and memory reading of this process.</param>
    /// <param name="surface">What the host's surface does, usually <see cref="DescribeSurface"/>.</param>
    /// <param name="processorCount">The machine's logical processors; <see cref="Environment.ProcessorCount"/> when null.</param>
    public static string Format(string label, MediaPlayerState state, double framesPerSecond, ProcessSample process, string surface, int? processorCount = null) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{label} {state} | {framesPerSecond:F1} fps | CPU {process.CpuPercent:F0}% ({process.CoresUsed:F2} of {processorCount ?? Environment.ProcessorCount} cores) | "
            + $"managed {process.ManagedMegabytes:F0} MB, working set {process.WorkingSetMegabytes:F0} MB (whole process) | surface {surface}");

    /// <summary>
    /// Gets <c>{width}x{height} via {path}</c>, with <c>(cleared: Audio mode)</c> when the surface
    /// is suspended, using Core's path names (<see cref="MediaSurfacePresentationPathText"/>).
    /// </summary>
    public static string DescribeSurface(int pixelWidth, int pixelHeight, MediaSurfacePresentationPath path, bool presentationSuspended = false) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{pixelWidth}x{pixelHeight} via {MediaSurfacePresentationPathText.Describe(path)}{(presentationSuspended ? " (cleared: Audio mode)" : string.Empty)}");

    /// <summary>
    /// Gets <c>{surface}, UI thread {n} ms/s ({n} ms/frame)</c> for a surface whose frames cost UI-thread
    /// time (Stage 051A), or <paramref name="surface"/> unchanged without a reading.
    /// </summary>
    public static string WithUiThreadCost(string surface, (double MillisecondsPerSecond, double MillisecondsPerFrame)? cost) =>
        cost is not { } reading
            ? surface
            : string.Create(CultureInfo.InvariantCulture, $"{surface}, UI thread {reading.MillisecondsPerSecond:F1} ms/s ({reading.MillisecondsPerFrame:F2} ms/frame)");

    /// <summary>Gets <paramref name="primary"/>, followed by <c> | 2nd screen {secondScreen}</c> when a second screen is open.</summary>
    public static string WithSecondScreen(string primary, string? secondScreen) =>
        secondScreen is null ? primary : $"{primary} | 2nd screen {secondScreen}";
}
