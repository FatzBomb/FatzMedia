using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>One exported image: its memory (an opaque POSIX descriptor) and optional exported semaphores.</summary>
internal sealed record VulkanExportedImage(
    VkImage Image,
    DeviceMemory Memory,
    ulong MemorySize,
    int MemoryFd,
    VkSemaphore RenderComplete,
    int RenderCompleteFd,
    VkSemaphore ConsumerRelease,
    int ConsumerReleaseFd);

/// <summary>Double-buffered exported images of one size (Stage 027A slice 2). Owned by the compositor that created them.</summary>
internal sealed class VulkanExportedImageSet(int width, int height, IReadOnlyList<VulkanExportedImage> images, bool hasSemaphores)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IReadOnlyList<VulkanExportedImage> Images { get; } = images;
    public bool HasSemaphores { get; } = hasSemaphores;
    public bool IsDestroyed { get; set; }
}

/// <summary>
/// Shared GPU image export (Stage 027A slice 2): exportable images whose memory leaves as opaque
/// POSIX file descriptors (<c>VK_KHR_external_memory_fd</c>), optionally with exported binary
/// semaphores (<c>VK_KHR_external_semaphore_fd</c>). Each frame is composed into the private
/// target as before, then blitted into one exported image instead of being read back.
/// </summary>
internal sealed unsafe partial class VulkanOffscreenCompositor
{
    /// <summary>Exported images are RGBA8: the widest-supported import format (OpenGL has no BGRA storage format). The blit swizzles.</summary>
    internal const Format ExportFormat = Format.R8G8B8A8Unorm;

    /// <summary>The layout exported images are left in after each frame; importers are told it.</summary>
    internal const ImageLayout ExportLayout = ImageLayout.TransferSrcOptimal;

    // The union of what exporters and importers commonly create shared images with; opaque handles
    // require matching creation parameters on both sides.
    private const ImageUsageFlags ExportUsage =
        ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit;

    private const string ExternalMemoryFdExtension = "VK_KHR_external_memory_fd";
    private const string ExternalSemaphoreFdExtension = "VK_KHR_external_semaphore_fd";

    private readonly HashSet<VulkanExportedImageSet> _exportedSets = [];
    private delegate* unmanaged<Device, MemoryGetFdInfoKHR*, int*, Result> _getMemoryFd;
    private delegate* unmanaged<Device, SemaphoreGetFdInfoKHR*, int*, Result> _getSemaphoreFd;
    private bool _exportMemoryCandidate;
    private bool _exportSemaphoreCandidate;

    /// <summary>Gets whether images can be exported as opaque POSIX descriptors.</summary>
    public bool CanExportMemoryFd { get; private set; }

    /// <summary>Gets whether binary semaphores can be exported as opaque POSIX descriptors.</summary>
    public bool CanExportSemaphoreFd { get; private set; }

    /// <summary>Gets the device UUID (16 bytes), or empty when the driver does not report one.</summary>
    public byte[] DeviceUuid { get; private set; } = [];

    /// <summary>Gets the device LUID (8 bytes), or empty when not valid (always on Linux).</summary>
    public byte[] DeviceLuid { get; private set; } = [];

    /// <summary>Gets why export is unavailable, or null when <see cref="CanExportMemoryFd"/>.</summary>
    public string? ExportUnavailableReason { get; private set; }

    /// <summary>
    /// Creates <paramref name="count"/> exportable images of the given size, with exported
    /// render-complete and consumer-release semaphores when <paramref name="withSemaphores"/>.
    /// The descriptors in the result are new; ownership passes to whoever imports them.
    /// </summary>
    public VulkanExportedImageSet CreateExportedImages(int width, int height, int count, bool withSemaphores)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanExportMemoryFd)
                throw new InvalidOperationException(ExportUnavailableReason ?? "Vulkan memory export is unavailable.");
            if (withSemaphores && !CanExportSemaphoreFd)
                throw new InvalidOperationException("Vulkan semaphore export is unavailable.");

            List<VulkanExportedImage> images = [];
            try
            {
                for (var i = 0; i < count; i++)
                    images.Add(CreateExportedImage((uint)width, (uint)height, withSemaphores));
            }
            catch
            {
                foreach (VulkanExportedImage image in images)
                    DestroyExportedImage(image, closeDescriptors: true);
                throw;
            }

            var set = new VulkanExportedImageSet(width, height, images, withSemaphores);
            _exportedSets.Add(set);
            return set;
        }
    }

    /// <summary>
    /// Composes <paramref name="plan"/> into image <paramref name="index"/> of <paramref name="set"/>
    /// and completes the GPU work before returning. With semaphores it first waits on the image's
    /// consumer-release semaphore (when <paramref name="waitForConsumerRelease"/>) and signals its
    /// render-complete semaphore.
    /// </summary>
    public void RenderToExport(in VulkanFramePlan plan, VulkanExportedImageSet set, int index, bool waitForConsumerRelease)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, set.Images.Count);
        _ = RenderCore(plan, new ExportTarget(set, set.Images[index], waitForConsumerRelease));
    }

    /// <summary>
    /// Destroys the producer side of <paramref name="set"/>. Imported copies keep the memory alive;
    /// <paramref name="closeDescriptors"/> also closes descriptors no importer took over.
    /// </summary>
    public void DestroyExportedImages(VulkanExportedImageSet set, bool closeDescriptors)
    {
        ArgumentNullException.ThrowIfNull(set);
        lock (_sync)
        {
            if (set.IsDestroyed || !_exportedSets.Remove(set))
                return;

            set.IsDestroyed = true;
            if (_device.Handle != 0)
            {
                lock (QueueLock)
                    _ = _vk.QueueWaitIdle(_queue);
            }

            foreach (VulkanExportedImage image in set.Images)
                DestroyExportedImage(image, closeDescriptors);
        }
    }

    /// <summary>Closes a POSIX descriptor no importer took over.</summary>
    internal static void CloseDescriptor(int fd)
    {
        if (fd >= 0 && !OperatingSystem.IsWindows())
            _ = PosixClose(fd);
    }

    /// <summary>
    /// Test support: imports an exported memory descriptor (taking ownership of it) into a new
    /// image created with the export parameters, copies it out, and returns its RGBA8 rows.
    /// </summary>
    internal byte[] ImportAndReadBack(int memoryFd, ulong memorySize, int width, int height)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CanExportMemoryFd)
                throw new InvalidOperationException(ExportUnavailableReason);

            VkImage image = CreateExternalImage((uint)width, (uint)height);
            DeviceMemory memory = default;
            BufferResource? readback = null;
            try
            {
                MemoryRequirements requirements;
                _vk.GetImageMemoryRequirements(_device, image, &requirements);
                var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image };
                var import = new ImportMemoryFdInfoKHR
                {
                    SType = StructureType.ImportMemoryFDInfoKhr,
                    PNext = &dedicated,
                    HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
                    Fd = memoryFd,
                };
                var typeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
                if (typeIndex < 0)
                    typeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.None);
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    PNext = &import,
                    AllocationSize = Math.Max(memorySize, requirements.Size),
                    MemoryTypeIndex = (uint)typeIndex,
                };
                Check(_vk.AllocateMemory(_device, &allocateInfo, null, &memory), "vkAllocateMemory (import)");
                Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory (import)");

                readback = EnsureBuffer(ref _readbackBuffer, (ulong)width * (ulong)height * 4, BufferUsageFlags.TransferDstBit, ReadbackMemoryPreferences);
                Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
                var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");
                // The exporter left the contents in ExportLayout.
                Barrier(image, ExportLayout, ImageLayout.TransferSrcOptimal, 0, AccessFlags.TransferReadBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit);
                var copy = new BufferImageCopy { ImageSubresource = ColorLayers(), ImageExtent = new Extent3D((uint)width, (uint)height, 1) };
                _vk.CmdCopyImageToBuffer(_commandBuffer, image, ImageLayout.TransferSrcOptimal, readback.Value.Buffer, 1, &copy);
                Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");
                CommandBuffer commandBuffer = _commandBuffer;
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
                Fence fence = _fence;
                Submit(&submit);
                Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");

                var length = width * height * 4;
                var pixels = new byte[length];
                ReadBack(readback.Value, pixels);
                return pixels;
            }
            finally
            {
                _vk.DestroyImage(_device, image, null);
                if (memory.Handle != 0)
                    _vk.FreeMemory(_device, memory, null);
            }
        }
    }

    /// <summary>
    /// Checks the selected device for memory (and semaphore) descriptor export and returns the
    /// device extensions to enable. Export is optional: the frame-sink path never needs it.
    /// </summary>
    private string[] ProbeExportSupport()
    {
        CanExportMemoryFd = false;
        CanExportSemaphoreFd = false;
        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);
        if (properties.ApiVersion < Vk.Version11)
        {
            ExportUnavailableReason = "the Vulkan device is older than 1.1";
            return [];
        }

        var idProperties = new PhysicalDeviceIDProperties { SType = StructureType.PhysicalDeviceIDProperties };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &idProperties };
        _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
        DeviceUuid = new ReadOnlySpan<byte>(idProperties.DeviceUuid, 16).ToArray();
        DeviceLuid = idProperties.DeviceLuidvalid ? new ReadOnlySpan<byte>(idProperties.DeviceLuid, 8).ToArray() : [];

        if (OperatingSystem.IsWindows())
        {
            ExportUnavailableReason = "Vulkan image export on Windows (opaque NT handles) is not implemented yet";
            return [];
        }

        if (!_vk.IsDeviceExtensionPresent(_physicalDevice, ExternalMemoryFdExtension))
        {
            ExportUnavailableReason = $"the Vulkan device lacks {ExternalMemoryFdExtension}";
            return [];
        }

        FormatProperties formatProperties;
        _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, ExportFormat, &formatProperties);
        if ((formatProperties.OptimalTilingFeatures & FormatFeatureFlags.BlitDstBit) == 0)
        {
            ExportUnavailableReason = $"the Vulkan device cannot blit into {ExportFormat}";
            return [];
        }

        var externalInfo = new PhysicalDeviceExternalImageFormatInfo
        {
            SType = StructureType.PhysicalDeviceExternalImageFormatInfo,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
        };
        var formatInfo = new PhysicalDeviceImageFormatInfo2
        {
            SType = StructureType.PhysicalDeviceImageFormatInfo2,
            PNext = &externalInfo,
            Format = ExportFormat,
            Type = ImageType.Type2D,
            Tiling = ImageTiling.Optimal,
            Usage = ExportUsage,
        };
        var externalProperties = new ExternalImageFormatProperties { SType = StructureType.ExternalImageFormatProperties };
        var imageProperties = new ImageFormatProperties2 { SType = StructureType.ImageFormatProperties2, PNext = &externalProperties };
        Result formatResult = _vk.GetPhysicalDeviceImageFormatProperties2(_physicalDevice, &formatInfo, &imageProperties);
        ExternalMemoryFeatureFlags memoryFeatures = externalProperties.ExternalMemoryProperties.ExternalMemoryFeatures;
        if (formatResult != Result.Success
            || (memoryFeatures & ExternalMemoryFeatureFlags.ExportableBit) == 0
            || (memoryFeatures & ExternalMemoryFeatureFlags.ImportableBit) == 0)
        {
            ExportUnavailableReason = $"the Vulkan device cannot export {ExportFormat} images as opaque descriptors ({formatResult}, {memoryFeatures})";
            return [];
        }

        _exportMemoryCandidate = true;
        ExportUnavailableReason = null;
        List<string> extensions = [ExternalMemoryFdExtension];
        if (_vk.IsDeviceExtensionPresent(_physicalDevice, ExternalSemaphoreFdExtension))
        {
            var semaphoreInfo = new PhysicalDeviceExternalSemaphoreInfo
            {
                SType = StructureType.PhysicalDeviceExternalSemaphoreInfo,
                HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
            };
            var semaphoreProperties = new ExternalSemaphoreProperties { SType = StructureType.ExternalSemaphoreProperties };
            _vk.GetPhysicalDeviceExternalSemaphoreProperties(_physicalDevice, &semaphoreInfo, &semaphoreProperties);
            if ((semaphoreProperties.ExternalSemaphoreFeatures & ExternalSemaphoreFeatureFlags.ExportableBit) != 0)
            {
                _exportSemaphoreCandidate = true;
                extensions.Add(ExternalSemaphoreFdExtension);
            }
        }

        return [.. extensions];
    }

    /// <summary>Resolves the descriptor-export entry points of the enabled extensions.</summary>
    private void LoadExportFunctions()
    {
        if (_exportMemoryCandidate)
        {
            _getMemoryFd = (delegate* unmanaged<Device, MemoryGetFdInfoKHR*, int*, Result>)LoadDeviceFunction("vkGetMemoryFdKHR");
            CanExportMemoryFd = _getMemoryFd != null;
            if (!CanExportMemoryFd)
                ExportUnavailableReason = "vkGetMemoryFdKHR is unavailable";
        }

        if (_exportSemaphoreCandidate && CanExportMemoryFd)
        {
            _getSemaphoreFd = (delegate* unmanaged<Device, SemaphoreGetFdInfoKHR*, int*, Result>)LoadDeviceFunction("vkGetSemaphoreFdKHR");
            CanExportSemaphoreFd = _getSemaphoreFd != null;
        }
    }

    private nint LoadDeviceFunction(string name)
    {
        var pName = (byte*)SilkMarshal.StringToPtr(name);
        try
        {
            return (nint)_vk.GetDeviceProcAddr(_device, pName).Handle;
        }
        finally
        {
            _ = SilkMarshal.Free((nint)pName);
        }
    }

    private VulkanExportedImage CreateExportedImage(uint width, uint height, bool withSemaphores)
    {
        VkImage image = CreateExternalImage(width, height);
        DeviceMemory memory = default;
        var memoryFd = -1;
        VkSemaphore renderComplete = default;
        VkSemaphore consumerRelease = default;
        var renderCompleteFd = -1;
        var consumerReleaseFd = -1;
        try
        {
            MemoryRequirements requirements;
            _vk.GetImageMemoryRequirements(_device, image, &requirements);
            // Dedicated: importers (and some drivers) require it for opaque image memory.
            var dedicated = new MemoryDedicatedAllocateInfo { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image };
            var exportInfo = new ExportMemoryAllocateInfo
            {
                SType = StructureType.ExportMemoryAllocateInfo,
                PNext = &dedicated,
                HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            var typeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
            if (typeIndex < 0)
                typeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.None);
            if (typeIndex < 0)
                throw new VulkanException("No memory type for an exported image; vkAllocateMemory", Result.ErrorOutOfDeviceMemory);

            var allocateInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = &exportInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = (uint)typeIndex,
            };
            Check(_vk.AllocateMemory(_device, &allocateInfo, null, &memory), "vkAllocateMemory (export)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory (export)");

            var getFd = new MemoryGetFdInfoKHR
            {
                SType = StructureType.MemoryGetFDInfoKhr,
                Memory = memory,
                HandleType = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
            };
            Check(_getMemoryFd(_device, &getFd, &memoryFd), "vkGetMemoryFdKHR");

            if (withSemaphores)
            {
                (renderComplete, renderCompleteFd) = CreateExportedSemaphore();
                (consumerRelease, consumerReleaseFd) = CreateExportedSemaphore();
            }

            return new VulkanExportedImage(image, memory, requirements.Size, memoryFd, renderComplete, renderCompleteFd, consumerRelease, consumerReleaseFd);
        }
        catch
        {
            DestroyExportedImage(
                new VulkanExportedImage(image, memory, 0, memoryFd, renderComplete, renderCompleteFd, consumerRelease, consumerReleaseFd),
                closeDescriptors: true);
            throw;
        }
    }

    private (VkSemaphore Semaphore, int Fd) CreateExportedSemaphore()
    {
        var exportInfo = new ExportSemaphoreCreateInfo
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
        };
        var info = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &exportInfo };
        VkSemaphore semaphore;
        Check(_vk.CreateSemaphore(_device, &info, null, &semaphore), "vkCreateSemaphore (export)");
        var getFd = new SemaphoreGetFdInfoKHR
        {
            SType = StructureType.SemaphoreGetFDInfoKhr,
            Semaphore = semaphore,
            HandleType = ExternalSemaphoreHandleTypeFlags.OpaqueFDBit,
        };
        var fd = -1;
        Result result = _getSemaphoreFd(_device, &getFd, &fd);
        if (result != Result.Success)
        {
            _vk.DestroySemaphore(_device, semaphore, null);
            throw new VulkanException("vkGetSemaphoreFdKHR", result);
        }

        return (semaphore, fd);
    }

    private VkImage CreateExternalImage(uint width, uint height)
    {
        var external = new ExternalMemoryImageCreateInfo
        {
            SType = StructureType.ExternalMemoryImageCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueFDBit,
        };
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            PNext = &external,
            ImageType = ImageType.Type2D,
            Format = ExportFormat,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ExportUsage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VkImage image;
        Check(_vk.CreateImage(_device, &info, null, &image), "vkCreateImage (export)");
        return image;
    }

    private void DestroyExportedImage(VulkanExportedImage image, bool closeDescriptors)
    {
        if (image.RenderComplete.Handle != 0)
            _vk.DestroySemaphore(_device, image.RenderComplete, null);
        if (image.ConsumerRelease.Handle != 0)
            _vk.DestroySemaphore(_device, image.ConsumerRelease, null);
        if (image.Image.Handle != 0)
            _vk.DestroyImage(_device, image.Image, null);
        if (image.Memory.Handle != 0)
            _vk.FreeMemory(_device, image.Memory, null);
        if (closeDescriptors)
        {
            CloseDescriptor(image.MemoryFd);
            CloseDescriptor(image.RenderCompleteFd);
            CloseDescriptor(image.ConsumerReleaseFd);
        }
    }

    private void ValidateExportTarget(ExportTarget export)
    {
        if (export.Set.IsDestroyed || !_exportedSets.Contains(export.Set))
            throw new ObjectDisposedException(nameof(VulkanExportedImageSet));
    }

    /// <summary>
    /// Blits the finished target (TransferSrcOptimal) into the exported image, which converts
    /// BGRA to RGBA and scales if a resize raced the frame, then leaves it in <see cref="ExportLayout"/>.
    /// </summary>
    private void RecordExportBlit(VkImage target, uint width, uint height, ExportTarget export)
    {
        VkImage destination = export.Image.Image;
        // The previous contents are discarded; with semaphore sync the submit waits for the
        // compositor's release before this stage.
        Barrier(destination, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);
        var blit = new ImageBlit { SrcSubresource = ColorLayers(), DstSubresource = ColorLayers() };
        blit.SrcOffsets[0] = default;
        blit.SrcOffsets[1] = new Offset3D((int)width, (int)height, 1);
        blit.DstOffsets[0] = default;
        blit.DstOffsets[1] = new Offset3D(export.Set.Width, export.Set.Height, 1);
        _vk.CmdBlitImage(_commandBuffer, target, ImageLayout.TransferSrcOptimal, destination, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
        Barrier(destination, ImageLayout.TransferDstOptimal, ExportLayout, AccessFlags.TransferWriteBit, 0, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit);
    }

    private void DestroyExportedSets()
    {
        foreach (VulkanExportedImageSet set in _exportedSets)
        {
            set.IsDestroyed = true;
            foreach (VulkanExportedImage image in set.Images)
                DestroyExportedImage(image, closeDescriptors: false);
        }

        _exportedSets.Clear();
    }

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int PosixClose(int fd);

    private readonly record struct ExportTarget(VulkanExportedImageSet Set, VulkanExportedImage Image, bool WaitForConsumerRelease);
}
