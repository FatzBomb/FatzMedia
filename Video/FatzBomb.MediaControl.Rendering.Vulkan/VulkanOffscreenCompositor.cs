using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>Integer pixel rectangle (left/top inclusive, right/bottom exclusive).</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>One background blit: the source rectangle scaled into the destination rectangle.</summary>
internal readonly record struct VulkanBlit(PixelRect Source, PixelRect Destination);

/// <summary>
/// Headless Vulkan device that clears an offscreen BGRA8 target, blits one uploaded BGRA
/// layer into it with nearest filtering, and reads the result back. This is the first
/// vertical slice of the Vulkan backend; swapchain presentation and shader-based overlay
/// composition are later slices.
/// </summary>
internal sealed unsafe class VulkanOffscreenCompositor : IDisposable
{
    private const Format TargetFormat = Format.B8G8R8A8Unorm;

    private readonly Vk _vk;
    private readonly Lock _sync = new();
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    private PhysicalDeviceMemoryProperties _memoryProperties;

    private ImageResource? _sourceImage;
    private ImageResource? _targetImage;
    private BufferResource? _uploadBuffer;
    private BufferResource? _readbackBuffer;
    private byte[] _output = [];
    private bool _disposed;

    private VulkanOffscreenCompositor(Vk vk) => _vk = vk;

    /// <summary>Gets the selected device's name.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Gets the selected device's type.</summary>
    public PhysicalDeviceType DeviceType { get; private set; }

    /// <summary>
    /// Creates the compositor, or returns <see langword="null"/> with a reason when no usable
    /// Vulkan device exists (no loader, no ICD, or no device that can blit BGRA8).
    /// </summary>
    public static VulkanOffscreenCompositor? TryCreate(out string? failure)
    {
        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception exception)
        {
            // Missing loader surfaces differently per OS and binding version; any failure here
            // means "no Vulkan", never a crash.
            failure = $"Vulkan loader unavailable: {exception.Message}";
            return null;
        }

        var compositor = new VulkanOffscreenCompositor(vk);
        try
        {
            if (compositor.Initialize(out failure))
                return compositor;
        }
        catch (Exception exception)
        {
            failure = $"Vulkan initialization failed: {exception.Message}";
        }

        compositor.Dispose();
        return null;
    }

    /// <summary>
    /// Clears the target to <paramref name="clearBgraPremultiplied"/>, optionally blits the source
    /// layer, and returns the target as tightly packed BGRA8 rows. The returned span is backed by a
    /// buffer the compositor reuses; it is valid until the next <see cref="Render"/> or
    /// <see cref="Dispose"/>, so callers copy it before rendering again.
    /// </summary>
    public ReadOnlySpan<byte> Render(
        int outputWidth,
        int outputHeight,
        (byte B, byte G, byte R, byte A) clearBgraPremultiplied,
        ReadOnlySpan<byte> sourceBgra,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        VulkanBlit? blit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var hasSource = blit is { Source.IsEmpty: false, Destination.IsEmpty: false } && sourceWidth > 0 && sourceHeight > 0;

            _targetImage = EnsureImage(_targetImage, (uint)outputWidth, (uint)outputHeight);
            _readbackBuffer = EnsureBuffer(_readbackBuffer, (ulong)outputWidth * (ulong)outputHeight * 4, BufferUsageFlags.TransferDstBit);
            if (hasSource)
            {
                _sourceImage = EnsureImage(_sourceImage, (uint)sourceWidth, (uint)sourceHeight);
                _uploadBuffer = EnsureBuffer(_uploadBuffer, (ulong)sourceWidth * (ulong)sourceHeight * 4, BufferUsageFlags.TransferSrcBit);
                Upload(sourceBgra, sourceWidth, sourceHeight, sourceStride);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");

            VkImage target = _targetImage.Value.Image;
            Transition(target, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
            var clear = new ClearColorValue(
                clearBgraPremultiplied.R / 255f,
                clearBgraPremultiplied.G / 255f,
                clearBgraPremultiplied.B / 255f,
                clearBgraPremultiplied.A / 255f);
            var range = ColorRange();
            _vk.CmdClearColorImage(_commandBuffer, target, ImageLayout.TransferDstOptimal, &clear, 1, &range);

            if (hasSource)
            {
                VkImage source = _sourceImage!.Value.Image;
                Transition(source, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
                var copy = new BufferImageCopy
                {
                    BufferOffset = 0,
                    BufferRowLength = 0,
                    BufferImageHeight = 0,
                    ImageSubresource = ColorLayers(),
                    ImageOffset = default,
                    ImageExtent = new Extent3D((uint)sourceWidth, (uint)sourceHeight, 1),
                };
                _vk.CmdCopyBufferToImage(_commandBuffer, _uploadBuffer!.Value.Buffer, source, ImageLayout.TransferDstOptimal, 1, &copy);
                Transition(source, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
                // Order the blit's writes after the clear's writes on the same target.
                Transition(target, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);

                VulkanBlit region = blit!.Value;
                var imageBlit = new ImageBlit
                {
                    SrcSubresource = ColorLayers(),
                    DstSubresource = ColorLayers(),
                };
                imageBlit.SrcOffsets[0] = new Offset3D(region.Source.Left, region.Source.Top, 0);
                imageBlit.SrcOffsets[1] = new Offset3D(region.Source.Right, region.Source.Bottom, 1);
                imageBlit.DstOffsets[0] = new Offset3D(region.Destination.Left, region.Destination.Top, 0);
                imageBlit.DstOffsets[1] = new Offset3D(region.Destination.Right, region.Destination.Bottom, 1);
                _vk.CmdBlitImage(_commandBuffer, source, ImageLayout.TransferSrcOptimal, target, ImageLayout.TransferDstOptimal, 1, &imageBlit, Filter.Nearest);
            }

            Transition(target, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
            var readback = new BufferImageCopy
            {
                ImageSubresource = ColorLayers(),
                ImageExtent = new Extent3D((uint)outputWidth, (uint)outputHeight, 1),
            };
            _vk.CmdCopyImageToBuffer(_commandBuffer, target, ImageLayout.TransferSrcOptimal, _readbackBuffer.Value.Buffer, 1, &readback);
            var hostBarrier = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _readbackBuffer.Value.Buffer,
                Offset = 0,
                Size = Vk.WholeSize,
            };
            _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &hostBarrier, 0, null);
            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            CommandBuffer commandBuffer = _commandBuffer;
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
            Fence fence = _fence;
            Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences");
            Check(_vk.QueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit");
            Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");

            var outputLength = checked(outputWidth * outputHeight * 4);
            if (_output.Length != outputLength)
                _output = new byte[outputLength];

            void* mapped;
            Check(_vk.MapMemory(_device, _readbackBuffer.Value.Memory, 0, (ulong)outputLength, 0, &mapped), "vkMapMemory");
            new ReadOnlySpan<byte>(mapped, outputLength).CopyTo(_output);
            _vk.UnmapMemory(_device, _readbackBuffer.Value.Memory);
            return _output;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_device.Handle != 0)
            {
                _ = _vk.DeviceWaitIdle(_device);
                Destroy(ref _sourceImage);
                Destroy(ref _targetImage);
                Destroy(ref _uploadBuffer);
                Destroy(ref _readbackBuffer);
                if (_fence.Handle != 0)
                    _vk.DestroyFence(_device, _fence, null);
                if (_commandPool.Handle != 0)
                    _vk.DestroyCommandPool(_device, _commandPool, null);
                _vk.DestroyDevice(_device, null);
            }

            if (_instance.Handle != 0)
                _vk.DestroyInstance(_instance, null);
            _vk.Dispose();
        }
    }

    private bool Initialize(out string? failure)
    {
        failure = null;
        var appName = (byte*)SilkMarshal.StringToPtr("FatzBomb.MediaControl");
        try
        {
            var appInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApplicationVersion = Vk.MakeVersion(0, 1, 0),
                PEngineName = appName,
                EngineVersion = Vk.MakeVersion(0, 1, 0),
                ApiVersion = Vk.Version11,
            };
            var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };
            Instance instance;
            Result result = _vk.CreateInstance(&instanceInfo, null, &instance);
            if (result != Result.Success)
            {
                failure = $"vkCreateInstance failed: {result}";
                return false;
            }

            _instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
        }

        if (!SelectDevice(out failure))
            return false;

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var deviceInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
        Device device;
        Result deviceResult = _vk.CreateDevice(_physicalDevice, &deviceInfo, null, &device);
        if (deviceResult != Result.Success)
        {
            failure = $"vkCreateDevice failed: {deviceResult}";
            return false;
        }

        _device = device;
        Queue queue;
        _vk.GetDeviceQueue(_device, _queueFamily, 0, &queue);
        _queue = queue;

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        CommandPool pool;
        Check(_vk.CreateCommandPool(_device, &poolInfo, null, &pool), "vkCreateCommandPool");
        _commandPool = pool;

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer commandBuffer;
        Check(_vk.AllocateCommandBuffers(_device, &allocateInfo, &commandBuffer), "vkAllocateCommandBuffers");
        _commandBuffer = commandBuffer;

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Fence fence;
        Check(_vk.CreateFence(_device, &fenceInfo, null, &fence), "vkCreateFence");
        _fence = fence;
        return true;
    }

    private bool SelectDevice(out string? failure)
    {
        uint count = 0;
        _ = _vk.EnumeratePhysicalDevices(_instance, &count, null);
        if (count == 0)
        {
            failure = "No Vulkan physical device is available.";
            return false;
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pDevices = devices)
            _ = _vk.EnumeratePhysicalDevices(_instance, &count, pDevices);

        var candidates = new List<(PhysicalDevice Device, uint Family, int Rank, string Name, PhysicalDeviceType Type)>();
        foreach (PhysicalDevice device in devices)
        {
            PhysicalDeviceProperties properties;
            _vk.GetPhysicalDeviceProperties(device, &properties);
            FormatProperties formatProperties;
            _vk.GetPhysicalDeviceFormatProperties(device, TargetFormat, &formatProperties);
            const FormatFeatureFlags required = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit | FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
            if ((formatProperties.OptimalTilingFeatures & required) != required)
                continue;

            if (!TryFindGraphicsQueue(device, out var family))
                continue;

            var rank = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 0,
                PhysicalDeviceType.IntegratedGpu => 1,
                PhysicalDeviceType.VirtualGpu => 2,
                PhysicalDeviceType.Cpu => 3,
                _ => 4,
            };
            candidates.Add((device, family, rank, SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown", properties.DeviceType));
        }

        if (candidates.Count == 0)
        {
            failure = "No Vulkan device supports BGRA8 blits on a graphics queue.";
            return false;
        }

        var selected = candidates.OrderBy(static c => c.Rank).First();
        _physicalDevice = selected.Device;
        _queueFamily = selected.Family;
        DeviceName = selected.Name;
        DeviceType = selected.Type;
        PhysicalDeviceMemoryProperties memoryProperties;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memoryProperties);
        _memoryProperties = memoryProperties;
        failure = null;
        return true;
    }

    private bool TryFindGraphicsQueue(PhysicalDevice device, out uint family)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pFamilies = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, pFamilies);

        for (uint index = 0; index < count; index++)
        {
            if ((families[index].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                family = index;
                return true;
            }
        }

        family = 0;
        return false;
    }

    private void Upload(ReadOnlySpan<byte> sourceBgra, int width, int height, int stride)
    {
        BufferResource upload = _uploadBuffer!.Value;
        void* mapped;
        var rowBytes = width * 4;
        Check(_vk.MapMemory(_device, upload.Memory, 0, (ulong)rowBytes * (ulong)height, 0, &mapped), "vkMapMemory");
        var destination = new Span<byte>(mapped, rowBytes * height);
        for (var y = 0; y < height; y++)
            sourceBgra.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        _vk.UnmapMemory(_device, upload.Memory);
    }

    private ImageResource EnsureImage(ImageResource? existing, uint width, uint height)
    {
        if (existing is { } image && image.Width == width && image.Height == height)
            return image;

        ImageResource? previous = existing;
        Destroy(ref previous);
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = TargetFormat,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VkImage handle;
        Check(_vk.CreateImage(_device, &info, null, &handle), "vkCreateImage");
        MemoryRequirements requirements;
        _vk.GetImageMemoryRequirements(_device, handle, &requirements);
        DeviceMemory memory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit, fallback: MemoryPropertyFlags.None);
        Check(_vk.BindImageMemory(_device, handle, memory, 0), "vkBindImageMemory");
        return new ImageResource(handle, memory, width, height);
    }

    private BufferResource EnsureBuffer(BufferResource? existing, ulong size, BufferUsageFlags usage)
    {
        if (existing is { } buffer && buffer.Size >= size)
            return buffer;

        BufferResource? previous = existing;
        Destroy(ref previous);
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        VkBuffer handle;
        Check(_vk.CreateBuffer(_device, &info, null, &handle), "vkCreateBuffer");
        MemoryRequirements requirements;
        _vk.GetBufferMemoryRequirements(_device, handle, &requirements);
        DeviceMemory memory = Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, fallback: null);
        Check(_vk.BindBufferMemory(_device, handle, memory, 0), "vkBindBufferMemory");
        return new BufferResource(handle, memory, size);
    }

    private DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags preferred, MemoryPropertyFlags? fallback)
    {
        var typeIndex = FindMemoryType(requirements.MemoryTypeBits, preferred);
        if (typeIndex < 0 && fallback is { } fallbackFlags)
            typeIndex = FindMemoryType(requirements.MemoryTypeBits, fallbackFlags);
        if (typeIndex < 0)
            throw new InvalidOperationException($"No Vulkan memory type satisfies {preferred}.");

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = (uint)typeIndex,
        };
        DeviceMemory memory;
        Check(_vk.AllocateMemory(_device, &allocateInfo, null, &memory), "vkAllocateMemory");
        return memory;
    }

    private int FindMemoryType(uint typeBits, MemoryPropertyFlags flags)
    {
        for (var index = 0; index < _memoryProperties.MemoryTypeCount; index++)
        {
            if ((typeBits & (1u << index)) != 0 && (_memoryProperties.MemoryTypes[index].PropertyFlags & flags) == flags)
                return index;
        }

        return -1;
    }

    private void Transition(VkImage image, ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = ColorRange(),
        };
        PipelineStageFlags sourceStage = srcAccess == 0 ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit;
        _vk.CmdPipelineBarrier(_commandBuffer, sourceStage, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);
    }

    private void Destroy(ref ImageResource? resource)
    {
        if (resource is not { } image)
            return;

        _vk.DestroyImage(_device, image.Image, null);
        _vk.FreeMemory(_device, image.Memory, null);
        resource = null;
    }

    private void Destroy(ref BufferResource? resource)
    {
        if (resource is not { } buffer)
            return;

        _vk.DestroyBuffer(_device, buffer.Buffer, null);
        _vk.FreeMemory(_device, buffer.Memory, null);
        resource = null;
    }

    private static ImageSubresourceRange ColorRange() => new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private static ImageSubresourceLayers ColorLayers() => new(ImageAspectFlags.ColorBit, 0, 0, 1);

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new VulkanException(operation, result);
    }

    private readonly record struct ImageResource(VkImage Image, DeviceMemory Memory, uint Width, uint Height);

    private readonly record struct BufferResource(VkBuffer Buffer, DeviceMemory Memory, ulong Size);
}

/// <summary>A failed Vulkan call. <see cref="IsDeviceLost"/> marks recoverable device loss.</summary>
internal sealed class VulkanException(string operation, Result result)
    : InvalidOperationException($"{operation} failed: {result}")
{
    public Result Result { get; } = result;

    public bool IsDeviceLost => Result == Result.ErrorDeviceLost;
}
