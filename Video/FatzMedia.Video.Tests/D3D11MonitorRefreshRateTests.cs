using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 017A slice 3: the per-monitor refresh rate cache behind the Direct3D11 backend's
/// GetSurfaceRefreshRateHz, and the Win32 display-config struct layouts it marshals.
/// </summary>
public sealed class D3D11MonitorRefreshRateTests
{
    static readonly nint Window = 0x1234;

    [Fact]
    public void Get_BeforePrime_ReturnsNullWithoutQuerying()
    {
        var queries = 0;
        var cache = new MonitorRefreshRateCache(_ => { queries++; return 60; });

        Assert.Null(cache.Get(Window));
        Assert.Equal(0, queries);
    }

    [Fact]
    public void Prime_QueriesOnce_AndGetReturnsTheCachedRateWhileFresh()
    {
        var queries = 0;
        long now = 0;
        var cache = new MonitorRefreshRateCache(_ => { queries++; return 59.94; }, () => now);

        cache.PrimeIfMissing(Window);
        Assert.Equal(59.94, cache.Get(Window));
        now += System.Diagnostics.Stopwatch.Frequency / 2;
        Assert.Equal(59.94, cache.Get(Window));
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Get_WhenStale_ReturnsTheOldRateAndRefreshesInTheBackground()
    {
        var rate = 60.0;
        long now = 0;
        using var refreshed = new SemaphoreSlim(0);
        var cache = new MonitorRefreshRateCache(_ =>
        {
            var current = rate;
            if (current != 60.0)
                _ = refreshed.Release();
            return current;
        }, () => now);
        cache.PrimeIfMissing(Window);

        rate = 144; // the window moved to another monitor
        now += 2 * System.Diagnostics.Stopwatch.Frequency;
        Assert.Equal(60.0, cache.Get(Window));

        Assert.True(await refreshed.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntilAsync(() => cache.Get(Window) == 144);
    }

    [Fact]
    public void QueryFailure_IsCachedAsUnknown()
    {
        var cache = new MonitorRefreshRateCache(_ => throw new InvalidOperationException("no display"));

        cache.PrimeIfMissing(Window);
        Assert.Null(cache.Get(Window));
    }

    [Fact]
    public void PrimeIfMissing_DoesNotQueryAKnownWindowAgain()
    {
        var queries = 0;
        var cache = new MonitorRefreshRateCache(_ => { queries++; return 60; });

        cache.PrimeIfMissing(Window);
        cache.PrimeIfMissing(Window);

        Assert.Equal(1, queries);
    }

    [Fact]
    public void Remove_ForgetsTheWindow()
    {
        var cache = new MonitorRefreshRateCache(_ => 75);
        cache.PrimeIfMissing(Window);

        cache.Remove(Window);

        Assert.Null(cache.Get(Window));
    }

    [Fact]
    public void DisplayConfigStructs_MatchTheWin32Layouts()
    {
        Assert.Equal(72, Marshal.SizeOf<Win32MonitorRefreshRate.DisplayConfigPathInfo>());
        Assert.Equal(64, Marshal.SizeOf<Win32MonitorRefreshRate.DisplayConfigModeInfo>());
        Assert.Equal(20, Marshal.SizeOf<Win32MonitorRefreshRate.DisplayConfigDeviceInfoHeader>());
        Assert.Equal(84, Marshal.SizeOf<Win32MonitorRefreshRate.DisplayConfigSourceDeviceName>());
        Assert.Equal(104, Marshal.SizeOf<Win32MonitorRefreshRate.MonitorInfoEx>());
    }

    [Fact]
    public void TryGet_InvalidWindow_ReturnsNull() => Assert.Null(Win32MonitorRefreshRate.TryGet(0));

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the background refresh.");
            await Task.Delay(10);
        }
    }
}
