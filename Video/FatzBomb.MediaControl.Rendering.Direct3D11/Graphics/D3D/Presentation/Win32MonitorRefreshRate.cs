using System.Runtime.InteropServices;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Reads the exact refresh rate of the monitor showing a window (Stage 017A slice 3). Finds
/// the window's monitor and its GDI device name, then matches it against the active display
/// paths from <c>QueryDisplayConfig</c>, whose target refresh rate is an exact fraction
/// (60000/1001 for 59.94 Hz). DWM's composition rate, the previous source, is one rate for the
/// whole desktop and can belong to a different monitor.
/// </summary>
internal static unsafe partial class Win32MonitorRefreshRate
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint QdcOnlyActivePaths = 2;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxQueryAttempts = 3;

    /// <summary>Refresh rate in Hz of the monitor showing <paramref name="hwnd"/>, or null when Windows cannot tell.</summary>
    public static double? TryGet(nint hwnd)
    {
        if (hwnd == 0 || !TryGetMonitorDeviceName(hwnd, out var deviceName))
            return null;

        for (var attempt = 0; attempt < MaxQueryAttempts; attempt++)
        {
            uint pathCount;
            uint modeCount;
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, &pathCount, &modeCount) != ErrorSuccess || pathCount == 0)
                return null;

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            int result;
            fixed (DisplayConfigPathInfo* pathsPointer = paths)
            fixed (DisplayConfigModeInfo* modesPointer = modes)
                result = QueryDisplayConfig(QdcOnlyActivePaths, &pathCount, pathsPointer, &modeCount, modesPointer, 0);

            // The topology can change between the two calls; ask again with the new sizes.
            if (result == ErrorInsufficientBuffer)
                continue;
            if (result != ErrorSuccess)
                return null;

            // QDC_ONLY_ACTIVE_PATHS returns active paths only. A duplicated (cloned) desktop has
            // several paths for one source; when their rates differ, which one the window is
            // paced by is unknowable, so report nothing and let the platform rate apply.
            double? found = null;
            for (var index = 0; index < pathCount; index++)
            {
                ref DisplayConfigPathInfo path = ref paths[index];
                if (!TryGetSourceDeviceName(path.SourceInfo.AdapterId, path.SourceInfo.Id, out var sourceName)
                    || !string.Equals(sourceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DisplayConfigRational rate = path.TargetInfo.RefreshRate;
                if (rate.Numerator == 0 || rate.Denominator == 0)
                    return null;

                var hertz = (double)rate.Numerator / rate.Denominator;
                if (!double.IsFinite(hertz) || hertz < 1 || hertz > 1000 || (found is { } previous && Math.Abs(previous - hertz) > 0.001))
                    return null;

                found = hertz;
            }

            return found;
        }

        return null;
    }

    private static bool TryGetMonitorDeviceName(nint hwnd, out string deviceName)
    {
        deviceName = string.Empty;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == 0)
            return false;

        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (GetMonitorInfoW(monitor, &info) == 0)
            return false;

        deviceName = new string(info.DeviceName);
        return deviceName.Length > 0;
    }

    private static bool TryGetSourceDeviceName(Luid adapterId, uint sourceId, out string name)
    {
        var request = new DisplayConfigSourceDeviceName
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = DisplayConfigDeviceInfoGetSourceName,
                Size = (uint)sizeof(DisplayConfigSourceDeviceName),
                AdapterId = adapterId,
                Id = sourceId,
            },
        };
        if (DisplayConfigGetDeviceInfo(&request.Header) != ErrorSuccess)
        {
            name = string.Empty;
            return false;
        }

        name = new string(request.ViewGdiDeviceName);
        return true;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, uint* pathCount, uint* modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, uint* pathCount, DisplayConfigPathInfo* paths, uint* modeCount, DisplayConfigModeInfo* modes, nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DisplayConfigDeviceInfoHeader* request);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    // DISPLAYCONFIG_PATH_INFO: 72 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    // DISPLAYCONFIG_MODE_INFO: 64 bytes; the mode union is not read, only sized.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct DisplayConfigModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    // DISPLAYCONFIG_SOURCE_DEVICE_NAME: 84 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public fixed char ViewGdiDeviceName[32];
    }

    // MONITORINFOEXW: 104 bytes.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfoEx
    {
        public uint Size;
        public fixed int Monitor[4];
        public fixed int Work[4];
        public uint Flags;
        public fixed char DeviceName[32];
    }
}
