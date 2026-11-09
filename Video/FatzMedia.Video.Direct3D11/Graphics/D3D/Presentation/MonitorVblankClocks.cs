using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics.Composition;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Vertical-blank timing per monitor for <see cref="IMediaRendererBackend.TryGetSurfaceVsyncTiming"/>
/// (Stage 017A slice 4). Each monitor a surface is on gets a background thread that waits for that
/// monitor's blanks (<c>IDXGIOutput.WaitForVBlank</c>) and keeps a <see cref="VblankGrid"/>. It is
/// the monitor's own timing whatever the swap chain (DirectComposition or HWND), and works the same
/// for primary and secondary windows. The thread runs only while timing is asked for
/// (display-locked video): it stops after a few seconds without a query.
/// </summary>
sealed partial class MonitorVblankClocks : IDisposable
{
    const uint MonitorDefaultToNearest = 2;

    readonly Lock _gate = new();
    readonly Dictionary<nint, Clock> _clocks = [];
    bool _disposed;

    /// <summary>
    /// The timing of the monitor showing <paramref name="hwnd"/>. The first query for a monitor
    /// starts its clock, so timing is available a few refreshes later. Cheap: a lookup and a lock.
    /// </summary>
    public bool TryGet(nint hwnd, double? nominalRefreshHz, out PresentationVsyncTiming timing)
    {
        timing = default;
        if (hwnd == 0)
            return false;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
            return false;

        Clock? clock;
        lock (_gate)
        {
            if (_disposed)
                return false;

            if (!_clocks.TryGetValue(monitor, out clock))
            {
                clock = new Clock(monitor);
                _clocks.Add(monitor, clock);
            }
        }

        return clock.TryGet(nominalRefreshHz, out timing);
    }

    public void Dispose()
    {
        Clock[] clocks;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            clocks = [.. _clocks.Values];
            _clocks.Clear();
        }

        foreach (var clock in clocks)
            clock.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    sealed class Clock(nint monitor) : IDisposable
    {
        static readonly long IdleTicks = Stopwatch.Frequency * 3;
        static readonly long RetryAfterFailureTicks = Stopwatch.Frequency * 5;
        const int MaxConsecutiveWaitFailures = 10;
        const int MaxReportedRestarts = 3;

        readonly Lock _gate = new();
        readonly VblankGrid _grid = new(Stopwatch.Frequency);
        Thread? _thread;
        PresentationVsyncTiming _timing;
        double? _nominalRefreshHz;
        long _lastQuery;
        long _failedAt;
        bool _failureReported;
        int _reportedRestarts;
        bool _disposed;

        public bool TryGet(double? nominalRefreshHz, out PresentationVsyncTiming timing)
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                timing = default;
                if (_disposed)
                    return false;

                _lastQuery = now;
                _nominalRefreshHz = nominalRefreshHz;
                // A mode change shows up here within a second (QueryDisplayConfig cache).
                _grid.SeedRefreshRate(nominalRefreshHz);
                if (_thread is null && (_failedAt == 0 || now - _failedAt > RetryAfterFailureTicks))
                    Start();

                timing = _timing;
            }

            return timing.IsValid;
        }

        public void Dispose()
        {
            // The thread notices within one refresh and releases its output; it is a background
            // thread, so a monitor that stopped sending blanks never holds up shutdown.
            lock (_gate)
            {
                _disposed = true;
                _timing = default;
            }
        }

        void Start()
        {
            _timing = default;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "FatzBomb vblank clock",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }

        void Run()
        {
            IDXGIOutput? output = null;
            try
            {
                output = FindOutput(monitor);
                if (output is null)
                {
                    Fail("no DXGI output belongs to the monitor");
                    return;
                }

                lock (_gate)
                {
                    _grid.Reset();
                    _grid.SeedRefreshRate(_nominalRefreshHz);
                }

                var failures = 0;
                while (true)
                {
                    lock (_gate)
                    {
                        if (_disposed || Stopwatch.GetTimestamp() - _lastQuery > IdleTicks)
                        {
                            _thread = null;
                            _timing = default;
                            return;
                        }
                    }

                    try
                    {
                        output.WaitForVBlank();
                    }
                    catch (SharpGenException ex)
                    {
                        if (++failures >= MaxConsecutiveWaitFailures)
                        {
                            Fail($"WaitForVBlank failed: {ex.Message}");
                            return;
                        }

                        Thread.Sleep(16);
                        continue;
                    }

                    var timestamp = Stopwatch.GetTimestamp();

                    failures = 0;
                    string? restartReason = null;
                    lock (_gate)
                    {
                        var restartsBefore = _grid.Restarts;
                        _grid.Observe(timestamp);
                        if (_grid.Restarts != restartsBefore && _reportedRestarts++ < MaxReportedRestarts)
                            restartReason = _grid.LastRestartReason;

                        // While the grid measures the rhythm again, the last good timing stays
                        // published: predictions from it hold for seconds, and dropping it would
                        // switch the video to the other timing path for a few refreshes.
                        if (_grid.IsValid)
                            _timing = new PresentationVsyncTiming(monitor, _grid.LastVsyncTimestamp, _grid.PeriodTicks, _grid.Restarts);
                        else if (_timing.IsValid)
                            _timing = _timing with { Resyncs = _grid.Restarts };
                    }

                    if (restartReason is not null)
                    {
                        DiagnosticRouter.Raise(
                            MediaDiagnosticLevel.Info,
                            "D3D11.Vblank",
                            $"A monitor's vertical-blank rhythm was measured again: {restartReason}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                output?.Dispose();
            }
        }

        void Fail(string reason)
        {
            bool report;
            lock (_gate)
            {
                _thread = null;
                _timing = default;
                _failedAt = Stopwatch.GetTimestamp();
                report = !_failureReported && !_disposed;
                _failureReported = true;
            }

            if (report)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Info,
                    "D3D11.Vblank",
                    $"Vertical-blank timing is unavailable for a monitor ({reason}); display-locked video timing uses source cadence there.");
            }
        }

        static IDXGIOutput? FindOutput(nint monitor)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter!.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        if (output!.Description.Monitor == monitor)
                            return output;

                        output.Dispose();
                    }
                }
            }

            return null;
        }
    }
}
