using System.Diagnostics;
using System.Globalization;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Player.Shared.Presentation;

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

/// <summary>
/// Counts the frames one player presents (Core's <see cref="IMediaControlSession.PresentationFramePresented"/>,
/// raised on the render thread) and turns them into frames per second.
/// </summary>
public sealed class PresentedFrameCounter
{
    private readonly Func<long> _timestamp;
    private IMediaControlSession? _session;
    private long _frames;
    private long _lastTimestamp;

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
    public void Track(IMediaControlSession? session)
    {
        if (ReferenceEquals(_session, session))
            return;

        if (_session is { } previous)
            previous.PresentationFramePresented -= OnFramePresented;
        _session = session;
        _ = Interlocked.Exchange(ref _frames, 0);
        _lastTimestamp = _timestamp();
        if (session is not null)
            session.PresentationFramePresented += OnFramePresented;
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

    private void OnFramePresented(object? sender, EventArgs e) => CountFrame();
}

/// <summary>The stats line every test player shows under each player (Stage 046A slice 3).</summary>
public static class PlayerStatsLine
{
    /// <summary>
    /// Gets <c>{label} {state} | {fps} fps | CPU {n}% ({cores} of {count} cores) | managed {n} MB,
    /// working set {n} MB (whole process) | surface {surface}</c>, invariant culture.
    /// </summary>
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

    /// <summary>Gets <paramref name="primary"/>, followed by <c> | 2nd screen {secondScreen}</c> when a second screen is open.</summary>
    public static string WithSecondScreen(string primary, string? secondScreen) =>
        secondScreen is null ? primary : $"{primary} | 2nd screen {secondScreen}";
}
