using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// The Direct3D11 side of the Vulkan renderer's shared images on Windows (Stage 045A slice 3):
/// Avalonia's default compositor (ANGLE on Direct3D11) imports only DXGI shared textures, and
/// Vulkan cannot export memory that Direct3D11 opens. So the renderer creates keyed-mutex shared
/// textures with Direct3D11, on the adapter of its Vulkan device (matched by LUID), and imports
/// them into Vulkan (<c>VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT</c>,
/// <c>VK_KHR_win32_keyed_mutex</c>). Plain COM through vtables: the renderer stays free of
/// Windows-only packages.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe partial class VulkanD3D11Interop
{
    const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    const uint D3D11SdkVersion = 7;
    const uint DxgiFormatR8G8B8A8Unorm = 28;
    const uint BindShaderResource = 0x8;
    const uint BindRenderTarget = 0x20;
    const uint MiscSharedKeyedMutex = 0x100;
    const uint MiscSharedNtHandle = 0x800;
    const uint SharedResourceRead = 0x80000000;
    const uint SharedResourceWrite = 0x1;

    // The adapter LUID in DXGI_ADAPTER_DESC1: after the 128 WCHARs of the description (256 bytes),
    // four UINTs (16) and three SIZE_Ts (24).
    const int AdapterDescLuidOffset = 296;
    const int AdapterDescSize = 312;

    // Vtable slots: IUnknown, IDXGIFactory1::EnumAdapters1, IDXGIAdapter1::GetDesc1,
    // ID3D11Device::CreateTexture2D, IDXGIResource1::CreateSharedHandle.
    const int QueryInterfaceSlot = 0;
    const int ReleaseSlot = 2;
    const int EnumAdapters1Slot = 12;
    const int GetDesc1Slot = 10;
    const int CreateTexture2DSlot = 5;
    const int CreateSharedHandleSlot = 13;

    /// <summary>
    /// <c>ID3D11Device::GetDeviceRemovedReason</c>: after IUnknown (0-2), CreateBuffer (3) through
    /// GetCreationFlags (38) in <c>d3d11.h</c> order (pinned by <c>D3D11InteropDeviceHealthTests</c>).
    /// </summary>
    internal const int GetDeviceRemovedReasonSlot = 39;

    static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid Resource1Id = new("30961379-4609-4a41-998e-54fe567ee0c1");

    /// <summary>
    /// Creates a Direct3D11 device on the adapter whose LUID is <paramref name="luid"/> (8 bytes,
    /// as Vulkan reports it), or returns 0 with the reason.
    /// </summary>
    public static nint CreateDevice(ReadOnlySpan<byte> luid, out string? failure)
    {
        if (luid.Length != 8)
        {
            failure = "the Vulkan device reports no LUID";
            return 0;
        }

        void* factory = null;
        var factoryId = FactoryId;
        var result = CreateDXGIFactory1(&factoryId, &factory);
        if (result < 0)
        {
            failure = $"CreateDXGIFactory1 failed (0x{result:X8})";
            return 0;
        }

        try
        {
            var desc = stackalloc byte[AdapterDescSize];
            for (uint index = 0; ; index++)
            {
                void* adapter = null;
                result = ((delegate* unmanaged<void*, uint, void**, int>)Slot(factory, EnumAdapters1Slot))(factory, index, &adapter);
                if (result == DxgiErrorNotFound)
                    break;
                if (result < 0)
                    continue;

                try
                {
                    new Span<byte>(desc, AdapterDescSize).Clear();
                    if (((delegate* unmanaged<void*, byte*, int>)Slot(adapter, GetDesc1Slot))(adapter, desc) < 0
                        || !new ReadOnlySpan<byte>(desc + AdapterDescLuidOffset, 8).SequenceEqual(luid))
                    {
                        continue;
                    }

                    void* device = null;
                    // D3D_DRIVER_TYPE_UNKNOWN with an explicit adapter; default feature levels.
                    result = D3D11CreateDevice(adapter, 0, 0, 0, null, 0, D3D11SdkVersion, &device, null, null);
                    if (result < 0)
                    {
                        failure = $"D3D11CreateDevice failed on the Vulkan device's adapter (0x{result:X8})";
                        return 0;
                    }

                    failure = null;
                    return (nint)device;
                }
                finally
                {
                    Release((nint)adapter);
                }
            }
        }
        finally
        {
            Release((nint)factory);
        }

        failure = "no DXGI adapter has the Vulkan device's LUID";
        return 0;
    }

    /// <summary>
    /// Creates an RGBA8 texture shared through an NT handle with a keyed mutex (key 0 free, as
    /// <see cref="MediaSharedGpuImageRing.ProducerKey"/> expects). The caller releases the
    /// texture and closes the handle.
    /// </summary>
    public static (nint Texture, nint NtHandle) CreateSharedTexture(nint device, int width, int height)
    {
        var desc = stackalloc uint[11];
        desc[0] = (uint)width;
        desc[1] = (uint)height;
        desc[2] = 1; // MipLevels
        desc[3] = 1; // ArraySize
        desc[4] = DxgiFormatR8G8B8A8Unorm;
        desc[5] = 1; // SampleDesc.Count
        desc[6] = 0; // SampleDesc.Quality
        desc[7] = 0; // D3D11_USAGE_DEFAULT
        desc[8] = BindShaderResource | BindRenderTarget;
        desc[9] = 0; // CPUAccessFlags
        desc[10] = MiscSharedKeyedMutex | MiscSharedNtHandle;
        void* texture = null;
        var result = ((delegate* unmanaged<void*, uint*, void*, void**, int>)Slot((void*)device, CreateTexture2DSlot))((void*)device, desc, null, &texture);
        if (result < 0)
            throw new InvalidOperationException($"ID3D11Device::CreateTexture2D (shared, keyed mutex) failed (0x{result:X8})");

        void* resource = null;
        try
        {
            var resourceId = Resource1Id;
            result = ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(texture, QueryInterfaceSlot))(texture, &resourceId, &resource);
            if (result < 0)
                throw new InvalidOperationException($"IDXGIResource1 is unavailable (0x{result:X8})");

            nint handle = 0;
            result = ((delegate* unmanaged<void*, void*, uint, char*, nint*, int>)Slot(resource, CreateSharedHandleSlot))(
                resource, null, SharedResourceRead | SharedResourceWrite, null, &handle);
            if (result < 0)
                throw new InvalidOperationException($"IDXGIResource1::CreateSharedHandle failed (0x{result:X8})");

            return ((nint)texture, handle);
        }
        catch
        {
            Release((nint)texture);
            throw;
        }
        finally
        {
            Release((nint)resource);
        }
    }

    /// <summary>
    /// <c>ID3D11Device::GetDeviceRemovedReason</c>: S_OK while the device works, a failure code
    /// (<c>DXGI_ERROR_DEVICE_REMOVED</c>, <c>_RESET</c>, ...) once a GPU reset or removal took it.
    /// </summary>
    public static int GetDeviceRemovedReason(nint device) =>
        ((delegate* unmanaged<void*, int>)Slot((void*)device, GetDeviceRemovedReasonSlot))((void*)device);

    /// <summary>Releases a COM reference (no-op for 0).</summary>
    public static void Release(nint unknown)
    {
        if (unknown != 0)
            _ = ((delegate* unmanaged<void*, uint>)Slot((void*)unknown, ReleaseSlot))((void*)unknown);
    }

    /// <summary>Closes an NT handle (no-op for 0).</summary>
    public static void CloseNtHandle(nint handle)
    {
        if (handle != 0)
            _ = CloseHandle(handle);
    }

    static nint Slot(void* unknown, int index) => (*(nint**)unknown)[index];

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, void** factory);

    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
