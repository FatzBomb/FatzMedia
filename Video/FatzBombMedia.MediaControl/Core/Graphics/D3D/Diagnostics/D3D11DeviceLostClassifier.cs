using SharpGen.Runtime;
using Vortice.Direct3D11;
using ResultCode = Vortice.DXGI.ResultCode;
using IDXGISwapChain = Vortice.DXGI.IDXGISwapChain;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;

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

    private static string ClassificationName(Result result)
    {
        if (result == ResultCode.DeviceRemoved) return "Device removed";
        if (result == ResultCode.DeviceReset) return "Device reset";
        if (result == ResultCode.DeviceHung) return "Device hung";
        if (result == ResultCode.DriverInternalError) return "Driver internal error";
        return "Device lost";
    }
}
