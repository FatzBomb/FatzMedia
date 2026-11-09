using System.Runtime.InteropServices;

namespace FatzMedia.Video.Vulkan.Tests;

/// <summary>
/// ENG-039: the Vulkan renderer's Windows export reads <c>ID3D11Device::GetDeviceRemovedReason</c>
/// through a raw vtable slot. Windows only (WARP); elsewhere the test returns at once.
/// </summary>
public sealed partial class D3D11InteropDeviceHealthTests
{
    private const int DriverTypeWarp = 5;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;

    [Fact]
    public unsafe void GetDeviceRemovedReason_HealthyDevice_IsSuccess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // The creation flag makes the neighbouring slots distinguishable: GetCreationFlags (38)
        // would return 0x20 and GetFeatureLevel (37) a feature level, not S_OK.
        void* device = null;
        Assert.Equal(0, D3D11CreateDevice(null, DriverTypeWarp, 0, CreateDeviceBgraSupport, null, 0, D3D11SdkVersion, &device, null, null));
        try
        {
            Assert.Equal(39, VulkanD3D11Interop.GetDeviceRemovedReasonSlot);
            Assert.Equal(0, VulkanD3D11Interop.GetDeviceRemovedReason((nint)device));
        }
        finally
        {
            VulkanD3D11Interop.Release((nint)device);
        }
    }

    [LibraryImport("d3d11.dll")]
    private static unsafe partial int D3D11CreateDevice(
        void* adapter,
        int driverType,
        nint software,
        uint flags,
        int* featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        void** device,
        int* featureLevel,
        void** immediateContext);
}
