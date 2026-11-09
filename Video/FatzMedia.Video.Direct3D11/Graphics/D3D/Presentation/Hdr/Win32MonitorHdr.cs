using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Video.Hdr;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;

/// <summary>
/// Reads the HDR state of the monitor showing a window (Stage 042A slice 2): the DXGI output
/// whose monitor is the window's (<c>IDXGIOutput6.GetDesc1</c>: HDR is on when its color space
/// is <c>RGB_FULL_G2084_NONE_P2020</c>; its peak luminance), and Windows' SDR white level for
/// that monitor (<c>DisplayConfigGetDeviceInfo</c>, <c>DISPLAYCONFIG_SDR_WHITE_LEVEL</c>). Runs
/// off the presentation thread (<see cref="MonitorQueryCache{T}"/>). DXGI caches outputs per
/// factory, so the factory is created again when it is no longer current (HDR toggled, a
/// monitor added or removed).
/// </summary>
static unsafe class Win32MonitorHdr
{
    const uint DisplayConfigDeviceInfoGetSdrWhiteLevel = 11;
    static readonly Lock FactoryGate = new();
    static IDXGIFactory1? s_factory;

    /// <summary>The HDR state of the monitor showing <paramref name="hwnd"/>, or null when Windows cannot tell.</summary>
    public static HdrDisplayInfo? TryGet(nint hwnd)
    {
        if (hwnd == 0)
            return null;

        var monitor = Win32MonitorRefreshRate.MonitorFromWindow(hwnd, Win32MonitorRefreshRate.MonitorDefaultToNearest);
        if (monitor == 0 || !TryGetOutputDescription(monitor, out var description))
            return null;

        var sdrWhiteNits = TryGetSdrWhiteNits(hwnd) ?? 0;
        return new HdrDisplayInfo(
            description.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
            description.MaxLuminance,
            sdrWhiteNits);
    }

    static bool TryGetOutputDescription(nint monitor, out OutputDescription1 description)
    {
        description = default;
        lock (FactoryGate)
        {
            if (s_factory == null || !s_factory.IsCurrent)
            {
                s_factory?.Dispose();
                s_factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            }

            for (uint adapterIndex = 0; s_factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter!.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            using var output6 = output!.QueryInterfaceOrNull<IDXGIOutput6>();
                            if (output6 == null)
                                continue;

                            var candidate = output6.Description1;
                            if (candidate.Monitor != monitor)
                                continue;

                            description = candidate;
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Nits of Windows' SDR white level for the monitor showing <paramref name="hwnd"/> (Settings
    /// &gt; Display &gt; HDR &gt; SDR content brightness), or null when Windows cannot tell.
    /// </summary>
    static double? TryGetSdrWhiteNits(nint hwnd)
    {
        if (!Win32MonitorRefreshRate.TryGetMonitorDeviceName(hwnd, out var deviceName)
            || !Win32MonitorRefreshRate.TryQueryActivePaths(out var paths))
        {
            return null;
        }

        for (var index = 0; index < paths.Length; index++)
        {
            ref var path = ref paths[index];
            if (!Win32MonitorRefreshRate.TryGetSourceDeviceName(path.SourceInfo.AdapterId, path.SourceInfo.Id, out var sourceName)
                || !string.Equals(sourceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var request = new DisplayConfigSdrWhiteLevel
            {
                Header = new Win32MonitorRefreshRate.DisplayConfigDeviceInfoHeader
                {
                    Type = DisplayConfigDeviceInfoGetSdrWhiteLevel,
                    Size = (uint)sizeof(DisplayConfigSdrWhiteLevel),
                    AdapterId = path.TargetInfo.AdapterId,
                    Id = path.TargetInfo.Id,
                },
            };
            if (Win32MonitorRefreshRate.DisplayConfigGetDeviceInfo(&request.Header) == Win32MonitorRefreshRate.ErrorSuccess
                && request.SdrWhiteLevel > 0)
            {
                return HdrOutputPolicy.SdrWhiteLevelToNits(request.SdrWhiteLevel);
            }
        }

        return null;
    }

    // DISPLAYCONFIG_SDR_WHITE_LEVEL: 24 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigSdrWhiteLevel
    {
        public Win32MonitorRefreshRate.DisplayConfigDeviceInfoHeader Header;
        public uint SdrWhiteLevel;
    }
}
