using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using ResultCode = Vortice.DXGI.ResultCode;
using IDXGISwapChain = Vortice.DXGI.IDXGISwapChain;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;

/// <summary>
/// Classifies DXGI/D3D11 present/commit failures that indicate the GPU device has been
/// lost (removed, reset, hung, or a driver-internal error) versus a generic/transient
/// failure. Detection only — this does not attempt any recovery or rebuild.
/// </summary>
internal static class D3D11DeviceLostClassifier
{
    /// <summary>
    /// Returns true when <paramref name="result"/> corresponds to one of the DXGI
    /// device-lost HRESULT families (device removed, reset, hung, or driver-internal error).
    /// </summary>
    public static bool IsDeviceLost(Result result) =>
        result == ResultCode.DeviceRemoved
        || result == ResultCode.DeviceReset
        || result == ResultCode.DeviceHung
        || result == ResultCode.DriverInternalError;

    /// <summary>
    /// Best-effort check of whether the D3D11 device behind <paramref name="d3d11DevicePointer"/>
    /// reports a device-removed/reset/hung/driver-error reason. Used by the decode side, which holds
    /// only the raw FFmpeg-owned native <c>ID3D11Device</c> pointer (not a managed wrapper), to
    /// confirm a suspected hardware-decode device loss before falling back to software.
    /// Returns false for a null pointer or if the device state cannot be queried.
    /// </summary>
    public static bool IsDeviceLost(IntPtr d3d11DevicePointer)
    {
        if (d3d11DevicePointer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Balance the AddRef with the wrapper's Dispose (Release) so we do not alter the
            // FFmpeg-owned device's lifetime — same AddRef-and-wrap pattern as D3D11DeviceResources.
            _ = Marshal.AddRef(d3d11DevicePointer);
            using var device = new ID3D11Device(d3d11DevicePointer);
            return device.DeviceRemovedReason.Failure;
        }
        catch
        {
            // If the device cannot even be queried, do not force a false positive here; the
            // caller's repeated-failure gate is what actually drives recovery.
            return false;
        }
    }

    /// <summary>
    /// Builds a diagnostic-ready description of a device-lost failure. When
    /// <paramref name="result"/> is specifically <see cref="ResultCode.DeviceRemoved"/> and
    /// <paramref name="swapChain"/> is available, this best-effort enriches the message with
    /// <see cref="ID3D11Device.DeviceRemovedReason"/>.
    /// </summary>
    public static string Describe(Result result, IDXGISwapChain? swapChain)
    {
        var reasonSuffix = string.Empty;

        if (result == ResultCode.DeviceRemoved && swapChain != null)
        {
            try
            {
                using ID3D11Device? device = swapChain.GetDevice<ID3D11Device>();
                if (device != null)
                {
                    Result reason = device.DeviceRemovedReason;
                    if (reason.Failure)
                    {
                        reasonSuffix = $" (removed reason: {reason})";
                    }
                }
            }
            catch
            {
                // The device is likely already gone by the time we ask for the reason;
                // report the classified result alone rather than fail the diagnostic path.
            }
        }

        return $"{ClassificationName(result)}: {result}{reasonSuffix}";
    }

    private static string ClassificationName(Result result) =>
        result == ResultCode.DeviceRemoved
            ? "Device removed"
            : result == ResultCode.DeviceReset
                ? "Device reset"
                : result == ResultCode.DeviceHung
                    ? "Device hung"
                    : result == ResultCode.DriverInternalError ? "Driver internal error" : "Device lost";
}
