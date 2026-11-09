using SharpGen.Runtime;
using Vortice.Direct3D11;
using IDXGISwapChain = Vortice.DXGI.IDXGISwapChain;
using ResultCode = Vortice.DXGI.ResultCode;

namespace FatzMedia.Video.Core.Graphics.D3D.Diagnostics;

/// <summary>
/// Classifies DXGI/D3D11 present/commit failures that indicate the GPU device has been
/// lost (removed, reset, hung, or a driver-internal error) versus a generic/transient
/// failure. Detection only — this does not attempt any recovery or rebuild.
/// </summary>
static class D3D11DeviceLostClassifier
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
                using var device = swapChain.GetDevice<ID3D11Device>();
                if (device != null)
                {
                    var reason = device.DeviceRemovedReason;
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

    /// <summary>
    /// A failure message naming the step that threw (ENG-039), with the removal reason when
    /// <paramref name="device"/> is gone: after a GPU reset a call can fail with an unrelated code
    /// (the reset smoke saw <c>E_INVALIDARG</c> from the video processor), so the device is asked.
    /// </summary>
    public static string DescribeStepFailure(string step, Exception exception, ID3D11Device? device)
    {
        var message = $"{step} failed: {exception.GetType().Name}: {exception.Message}";
        Result removed = default;
        try
        {
            removed = device?.DeviceRemovedReason ?? default;
        }
        catch (SharpGenException)
        {
            // The device may already be released; report the step alone.
        }

        return removed.Failure ? $"{message} ({Describe(removed, swapChain: null)})" : message;
    }

    static string ClassificationName(Result result) =>
        result == ResultCode.DeviceRemoved
            ? "Device removed"
            : result == ResultCode.DeviceReset
                ? "Device reset"
                : result == ResultCode.DeviceHung
                    ? "Device hung"
                    : result == ResultCode.DriverInternalError ? "Driver internal error" : "Device lost";
}
