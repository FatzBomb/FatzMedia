using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Vertical-blank timing per monitor for <see cref="IMediaRendererBackend.TryGetSurfaceVsyncTiming"/>
/// (Stage 017A slice 4). Each monitor a surface is on gets a background thread that waits for that
/// monitor's blanks (<c>IDXGIOutput.WaitForVBlank</c>) and keeps a <see cref="VblankGrid"/>. It is
/// the monitor's own timing whatever the swap chain (DirectComposition or HWND), and works the same
/// for primary and secondary windows. The thread runs only while timing is asked for
/// (display-locked video): it stops after a few seconds without a query.
/// </summary>
internal sealed partial class MonitorVblankClocks : IDisposable
{
    private const uint MonitorDefaultToNearest = 2;

    private readonly Lock _gate = new();
    private readonly Dictionary<nint, Clock> _clocks = [];
    private bool _disposed;

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

        foreach (Clock clock in clocks)
            clock.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    private sealed class Clock(nint monitor) : IDisposable
    {
        private static readonly long IdleTicks = Stopwatch.Frequency * 3;
        private static readonly long RetryAfterFailureTicks = Stopwatch.Frequency * 5;
        private const int MaxConsecutiveWaitFailures = 10;

        private readonly Lock _gate = new();
        private readonly VblankGrid _grid = new(Stopwatch.Frequency);
        private Thread? _thread;
        private PresentationVsyncTiming _timing;
        private double? _nominalRefreshHz;
        private long _lastQuery;
        private long _failedAt;
        private bool _failureReported;
        private bool _disposed;

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

        private void Start()
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

        private void Run()
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
                    lock (_gate)
                    {
                        _grid.Observe(timestamp);
                        _timing = _grid.IsValid
                            ? new PresentationVsyncTiming(monitor, _grid.LastVsyncTimestamp, _grid.PeriodTicks)
                            : default;
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

        private void Fail(string reason)
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

        private static IDXGIOutput? FindOutput(nint monitor)
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter!.EnumOutputs(outputIndex, out IDXGIOutput? output).Success; outputIndex++)
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
