using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>
/// Shared GPU images on Windows (Stage 045A slice 3), in the two forms Avalonia's compositors import:
/// <list type="bullet">
/// <item><see cref="MediaSharedGpuImageHandleKind.D3D11TextureNtHandle"/> with a keyed mutex, for
/// the default ANGLE/Direct3D11 compositor: Direct3D11 creates the shared texture on the Vulkan
/// device's adapter (<see cref="VulkanD3D11Interop"/>), Vulkan imports it
/// (<c>VK_KHR_external_memory_win32</c>, D3D11 texture handle type) and takes the keyed mutex in
/// each submission (<c>VK_KHR_win32_keyed_mutex</c>); the image is handed to the external queue
/// family after each frame.</item>
/// <item><see cref="MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle"/>, optionally with exported
/// semaphores (<c>VK_KHR_external_semaphore_win32</c>), for Avalonia's Vulkan backend: the Linux
/// opaque-descriptor export with NT handles.</item>
/// </list>
/// NT handles stay with the renderer (importers open their own references) and are closed when
/// both sides are done with the set.
/// </summary>
internal sealed unsafe partial class VulkanOffscreenCompositor
{
    private const string ExternalMemoryWin32Extension = "VK_KHR_external_memory_win32";
    private const string ExternalSemaphoreWin32Extension = "VK_KHR_external_semaphore_win32";
    private const string Win32KeyedMutexExtension = "VK_KHR_win32_keyed_mutex";

    /// <summary>
    /// How long a submission waits for an image's keyed mutex before it fails (ms). The ring hands
    /// out only textures the compositor released, so the mutex is normally free at once; a long
    /// wait held the queue lock FFmpeg's decoding also takes (ENG-038, ENG-039).
    /// </summary>
    internal const uint KeyedMutexTimeoutMilliseconds = 50;

    private delegate* unmanaged<Device, MemoryGetWin32HandleInfoKHR*, nint*, Result> _getMemoryWin32Handle;
    private delegate* unmanaged<Device, SemaphoreGetWin32HandleInfoKHR*, nint*, Result> _getSemaphoreWin32Handle;
    private delegate* unmanaged<Device, ExternalMemoryHandleTypeFlags, nint, MemoryWin32HandlePropertiesKHR*, Result> _getMemoryWin32HandleProperties;
    private bool _exportWin32MemoryCandidate;
    private bool _exportWin32SemaphoreCandidate;
    private bool _importD3D11Candidate;
    private nint _d3d11Device;

    /// <summary>Gets whether images can be exported as opaque Win32 NT handles.</summary>
    public bool CanExportMemoryWin32 { get; private set; }

    /// <summary>Gets whether binary semaphores can be exported as opaque Win32 NT handles.</summary>
    public bool CanExportSemaphoreWin32 { get; private set; }

    /// <summary>Gets whether keyed-mutex Direct3D11 shared textures can be imported and drawn into.</summary>
    public bool CanShareD3D11Textures { get; private set; }

    /// <summary>
    /// Checks the selected device for the Windows export paths and returns the device extensions
    /// to enable. Each path is optional; the reason covers the case where neither is available.
    /// </summary>
    private string[] ProbeWin32ExportSupport()
    {
        if (!_vk.IsDeviceExtensionPresent(_physicalDevice, ExternalMemoryWin32Extension))
        {
            ExportUnavailableReason = $"the Vulkan device lacks {ExternalMemoryWin32Extension}";
            return [];
        }

        FormatProperties formatProperties;
        _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, ExportFormat, &formatProperties);
        if ((formatProperties.OptimalTilingFeatures & FormatFeatureFlags.BlitDstBit) == 0)
        {
            ExportUnavailableReason = $"the Vulkan device cannot blit into {ExportFormat}";
            return [];
        }

        List<string> extensions = [ExternalMemoryWin32Extension];
        ExternalMemoryFeatureFlags opaque = QueryExternalImageFeatures(ExternalMemoryHandleTypeFlags.OpaqueWin32Bit, ExportUsage);
        _exportWin32MemoryCandidate = (opaque & ExternalMemoryFeatureFlags.ExportableBit) != 0 && (opaque & ExternalMemoryFeatureFlags.ImportableBit) != 0;
        if (_exportWin32MemoryCandidate && _vk.IsDeviceExtensionPresent(_physicalDevice, ExternalSemaphoreWin32Extension))
        {
            var semaphoreInfo = new PhysicalDeviceExternalSemaphoreInfo
            {
                SType = StructureType.PhysicalDeviceExternalSemaphoreInfo,
                HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
            };
            var semaphoreProperties = new ExternalSemaphoreProperties { SType = StructureType.ExternalSemaphoreProperties };
            _vk.GetPhysicalDeviceExternalSemaphoreProperties(_physicalDevice, &semaphoreInfo, &semaphoreProperties);
            if ((semaphoreProperties.ExternalSemaphoreFeatures & ExternalSemaphoreFeatureFlags.ExportableBit) != 0)
            {
                _exportWin32SemaphoreCandidate = true;
                extensions.Add(ExternalSemaphoreWin32Extension);
            }
        }

        // Keyed-mutex textures from Direct3D11 are only written by the blit.
        ExternalMemoryFeatureFlags d3d11 = QueryExternalImageFeatures(ExternalMemoryHandleTypeFlags.D3D11TextureBit, D3D11ImageUsage);
        _importD3D11Candidate = (d3d11 & ExternalMemoryFeatureFlags.ImportableBit) != 0
            && _vk.IsDeviceExtensionPresent(_physicalDevice, Win32KeyedMutexExtension)
            && DeviceLuid.Length == 8;
        if (_importD3D11Candidate)
            extensions.Add(Win32KeyedMutexExtension);

        if (!_exportWin32MemoryCandidate && !_importD3D11Candidate)
        {
            ExportUnavailableReason = $"the Vulkan device can neither export {ExportFormat} images as NT handles nor import keyed-mutex Direct3D11 textures ({opaque}; {d3d11})";
            return [];
        }

        ExportUnavailableReason = null;
        return [.. extensions];
    }

    private const ImageUsageFlags D3D11ImageUsage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.ColorAttachmentBit;

    private ExternalMemoryFeatureFlags QueryExternalImageFeatures(ExternalMemoryHandleTypeFlags handleType, ImageUsageFlags usage)
    {
        var externalInfo = new PhysicalDeviceExternalImageFormatInfo
        {
            SType = StructureType.PhysicalDeviceExternalImageFormatInfo,
            HandleType = handleType,
        };
        var formatInfo = new PhysicalDeviceImageFormatInfo2
        {
            SType = StructureType.PhysicalDeviceImageFormatInfo2,
            PNext = &externalInfo,
            Format = ExportFormat,
            Type = ImageType.Type2D,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
        };
        var externalProperties = new ExternalImageFormatProperties { SType = StructureType.ExternalImageFormatProperties };
        var imageProperties = new ImageFormatProperties2 { SType = StructureType.ImageFormatProperties2, PNext = &externalProperties };
        return _vk.GetPhysicalDeviceImageFormatProperties2(_physicalDevice, &formatInfo, &imageProperties) == Result.Success
            ? externalProperties.ExternalMemoryProperties.ExternalMemoryFeatures
            : ExternalMemoryFeatureFlags.None;
    }

    /// <summary>Resolves the Win32 export entry points of the enabled extensions.</summary>
    private void LoadWin32ExportFunctions()
    {
        if (_exportWin32MemoryCandidate || _importD3D11Candidate)
        {
            _getMemoryWin32Handle = (delegate* unmanaged<Device, MemoryGetWin32HandleInfoKHR*, nint*, Result>)LoadDeviceFunction("vkGetMemoryWin32HandleKHR");
            _getMemoryWin32HandleProperties = (delegate* unmanaged<Device, ExternalMemoryHandleTypeFlags, nint, MemoryWin32HandlePropertiesKHR*, Result>)LoadDeviceFunction("vkGetMemoryWin32HandlePropertiesKHR");
        }

        CanExportMemoryWin32 = _exportWin32MemoryCandidate && _getMemoryWin32Handle != null;
        CanShareD3D11Textures = _importD3D11Candidate && _getMemoryWin32HandleProperties != null;
        if (_exportWin32SemaphoreCandidate && CanExportMemoryWin32)
        {
            _getSemaphoreWin32Handle = (delegate* unmanaged<Device, SemaphoreGetWin32HandleInfoKHR*, nint*, Result>)LoadDeviceFunction("vkGetSemaphoreWin32HandleKHR");
            CanExportSemaphoreWin32 = _getSemaphoreWin32Handle != null;
        }

        if (!CanExportMemoryWin32 && !CanShareD3D11Textures && ExportUnavailableReason is null && OperatingSystem.IsWindows())
            ExportUnavailableReason = "vkGetMemoryWin32HandleKHR is unavailable";
    }

    /// <summary>An exportable image whose memory leaves as an opaque NT handle, with optional exported semaphores.</summary>
    private VulkanExportedImage CreateWin32ExportedImage(uint width, uint height, bool withSemaphores)
    {
        VkImage image = CreateExternalImage(width, height, ExternalMemoryHandleTypeFlags.OpaqueWin32Bit, ExportUsage);
        DeviceMemory memory = default;
        nint memoryHandle = 0;
        VkSemaphore renderComplete = default;
        VkSemaphore consumerRelease = default;
        nint renderCompleteHandle = 0;
        nint consumerReleaseHandle = 0;
        try
        {
            MemoryRequirements requirements;
            _vk.GetImageMemoryRequirements(_device, image, &requirements);
            var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image };
            var exportInfo = new ExportMemoryAllocateInfo
            {
                SType = StructureType.ExportMemoryAllocateInfo,
                PNext = &dedicated,
                HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
            };
            memory = AllocateMemory(requirements.MemoryTypeBits, requirements.Size, ImageMemoryPreferences, &exportInfo, "vkAllocateMemory (Win32 export)", out _);
            VulkanResult.Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory (Win32 export)");

            var getHandle = new MemoryGetWin32HandleInfoKHR
            {
                SType = StructureType.MemoryGetWin32HandleInfoKhr,
                Memory = memory,
                HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
            };
            VulkanResult.Check(_getMemoryWin32Handle(_device, &getHandle, &memoryHandle), "vkGetMemoryWin32HandleKHR");

            if (withSemaphores)
            {
                (renderComplete, renderCompleteHandle) = CreateWin32ExportedSemaphore();
                (consumerRelease, consumerReleaseHandle) = CreateWin32ExportedSemaphore();
            }

            return new VulkanExportedImage(image, memory, requirements.Size, -1, renderComplete, -1, consumerRelease, -1)
            {
                MemoryNtHandle = memoryHandle,
                RenderCompleteNtHandle = renderCompleteHandle,
                ConsumerReleaseNtHandle = consumerReleaseHandle,
            };
        }
        catch
        {
            DestroyExportedImage(
                new VulkanExportedImage(image, memory, 0, -1, renderComplete, -1, consumerRelease, -1)
                {
                    MemoryNtHandle = memoryHandle,
                    RenderCompleteNtHandle = renderCompleteHandle,
                    ConsumerReleaseNtHandle = consumerReleaseHandle,
                },
                closeDescriptors: true);
            throw;
        }
    }

    private (VkSemaphore Semaphore, nint Handle) CreateWin32ExportedSemaphore()
    {
        var exportInfo = new ExportSemaphoreCreateInfo
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &exportInfo };
        VkSemaphore semaphore;
        VulkanResult.Check(_vk.CreateSemaphore(_device, &info, null, &semaphore), "vkCreateSemaphore (Win32 export)");
        var getHandle = new SemaphoreGetWin32HandleInfoKHR
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        };
        nint handle = 0;
        Result result = _getSemaphoreWin32Handle(_device, &getHandle, &handle);
        if (result != Result.Success)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
            throw new VulkanException("vkGetSemaphoreWin32HandleKHR", result);
        }

        return (semaphore, handle);
    }

    /// <summary>
    /// A keyed-mutex Direct3D11 shared texture on the Vulkan device's adapter, imported as this
    /// image's memory. The NT handle goes to the compositor, which opens the same texture.
    /// </summary>
    private VulkanExportedImage CreateD3D11SharedImage(uint width, uint height)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Direct3D11 shared textures need Windows.");

        if (_d3d11Device == 0)
        {
            _d3d11Device = VulkanD3D11Interop.CreateDevice(DeviceLuid, out var failure);
            if (_d3d11Device == 0)
                throw new InvalidOperationException(failure);
        }

        (nint texture, nint handle) = VulkanD3D11Interop.CreateSharedTexture(_d3d11Device, (int)width, (int)height);
        VkImage image = default;
        DeviceMemory memory = default;
        try
        {
            image = CreateExternalImage(width, height, ExternalMemoryHandleTypeFlags.D3D11TextureBit, D3D11ImageUsage);
            MemoryRequirements requirements;
            _vk.GetImageMemoryRequirements(_device, image, &requirements);
            var handleProperties = new MemoryWin32HandlePropertiesKHR { SType = StructureType.MemoryWin32HandlePropertiesKhr };
            VulkanResult.Check(_getMemoryWin32HandleProperties(_device, ExternalMemoryHandleTypeFlags.D3D11TextureBit, handle, &handleProperties), "vkGetMemoryWin32HandlePropertiesKHR");
            // A Direct3D11 texture is always imported as a dedicated allocation; the import does
            // not take the handle.
            var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image };
            var import = new ImportMemoryWin32HandleInfoKHR
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                PNext = &dedicated,
                HandleType = ExternalMemoryHandleTypeFlags.D3D11TextureBit,
                Handle = handle,
            };
            memory = AllocateMemory(requirements.MemoryTypeBits & handleProperties.MemoryTypeBits, requirements.Size, ImageMemoryPreferences, &import, "vkAllocateMemory (Direct3D11 texture import)", out _);
            VulkanResult.Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory (Direct3D11 texture import)");
            return new VulkanExportedImage(image, memory, requirements.Size, -1, default, -1, default, -1)
            {
                MemoryNtHandle = handle,
                D3D11Texture = texture,
            };
        }
        catch
        {
            if (image.Handle != 0)
                _vk.DestroyImage(_device, image, null);
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
            VulkanD3D11Interop.Release(texture);
            VulkanD3D11Interop.CloseNtHandle(handle);
            throw;
        }
    }

    /// <summary>Closes an NT handle no importer needs any more.</summary>
    internal static void CloseNtHandle(nint handle)
    {
        if (handle != 0 && OperatingSystem.IsWindows())
            VulkanD3D11Interop.CloseNtHandle(handle);
    }

    /// <summary>Releases what the Windows export holds outside Vulkan (the Direct3D11 device). Disposal only (after the probe gate closed).</summary>
    private void DestroyWin32ExportResources()
    {
        if (_d3d11Device != 0 && OperatingSystem.IsWindows())
            VulkanD3D11Interop.Release(_d3d11Device);
        _d3d11Device = 0;
    }
}
