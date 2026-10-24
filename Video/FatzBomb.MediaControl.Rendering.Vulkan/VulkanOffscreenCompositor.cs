using FatzBomb.MediaControl.Core.Cdg;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

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
/// layer into it with nearest filtering, draws the wash and CDG overlay with SPIR-V pipelines
/// (<see cref="VulkanOverlayPipelines"/>), and reads the result back. Swapchain presentation
/// is a later slice.
/// </summary>
internal sealed unsafe partial class VulkanOffscreenCompositor : IDisposable
{
    private const Format TargetFormat = Format.B8G8R8A8Unorm;

    // Every use of TargetFormat: clear/blit/readback, the render-pass attachment with blending,
    // and the sampled CDG image.
    private const FormatFeatureFlags RequiredFormatFeatures =
        FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit
        | FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit
        | FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit
        | FormatFeatureFlags.SampledImageBit;

    private const ImageUsageFlags TransferUsage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

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
    private VulkanOverlayPipelines? _overlay;

    private ImageResource? _sourceImage;
    // Render targets by size, most recently used last: each attached surface (Stage 027A slice 5:
    // a primary and secondary screens) renders at its own size every frame, so one target per size
    // is kept instead of re-creating a single one on every alternation.
    private readonly List<RenderTarget> _renderTargets = [];
    private ImageResource? _cdgImage;
    private BufferResource? _uploadBuffer;
    private BufferResource? _cdgUploadBuffer;
    private CdgRenderSnapshot? _cdgUploadedSnapshot;
    private VkImage _cdgUploadedImage;
    private BufferResource? _readbackBuffer;
    private byte[] _output = [];
    private bool _disposed;

    private VulkanOffscreenCompositor(Vk vk) => _vk = vk;

    /// <summary>Gets the selected device's name.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Gets the selected device's type.</summary>
    public PhysicalDeviceType DeviceType { get; private set; }

    /// <summary>Gets how many times a CDG snapshot was expanded and uploaded (tests).</summary>
    internal int CdgUploadCount { get; private set; }

    /// <summary>
    /// Creates the compositor, or returns <see langword="null"/> with a reason when no usable
    /// Vulkan device exists (no loader, no ICD, or no device with the BGRA8 features used here).
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
    /// Draws <paramref name="plan"/> (clear, optional background blit, then wash and CDG
    /// overlay draws) and returns the target as tightly packed premultiplied BGRA8 rows. The
    /// returned span is backed by a buffer the compositor reuses; it is valid until the next
    /// <see cref="Render"/> or <see cref="Dispose"/>, so callers copy it before rendering again.
    /// </summary>
    public ReadOnlySpan<byte> Render(in VulkanFramePlan plan) => RenderCore(plan, export: null);

    private ReadOnlySpan<byte> RenderCore(in VulkanFramePlan plan, ExportTarget? export)
    {
        var outputWidth = plan.OutputWidth;
        var outputHeight = plan.OutputHeight;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputHeight);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            RenderTarget renderTarget = EnsureTarget((uint)outputWidth, (uint)outputHeight);
            ImageResource targetImage = renderTarget.Image;
            if (export is { } exportTarget)
                ValidateExportTarget(exportTarget);

            BufferResource? readbackBuffer = export is null
                ? EnsureBuffer(ref _readbackBuffer, (ulong)outputWidth * (ulong)outputHeight * 4, BufferUsageFlags.TransferDstBit)
                : null;
            if (plan.Background is { } background)
            {
                _ = EnsureImage(ref _sourceImage, (uint)background.Width, (uint)background.Height, TransferUsage);
                BufferResource upload = EnsureBuffer(ref _uploadBuffer, (ulong)background.Width * (ulong)background.Height * 4, BufferUsageFlags.TransferSrcBit);
                Upload(upload, background.Bgra.Span, background.Width, background.Height, background.Stride);
            }

            // Snapshots are immutable and Core hands over the same pixel buffers while the CDG
            // frame is unchanged, so the texture from the last upload is still current for it
            // (ENG-014: about 260 KB expanded and copied per frame otherwise).
            var uploadCdg = false;
            if (plan.Cdg is { } cdg)
            {
                // A texture about to be re-created (or one whose re-creation fails) never
                // counts as holding the last upload, even if the new handle reuses the old value.
                if (_cdgImage is not { } current || current.Width != (uint)cdg.Snapshot.Width || current.Height != (uint)cdg.Snapshot.Height)
                    _cdgUploadedSnapshot = null;

                ImageResource cdgTexture = EnsureImage(ref _cdgImage, (uint)cdg.Snapshot.Width, (uint)cdg.Snapshot.Height, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit);
                uploadCdg = !HasSamePixels(cdg.Snapshot, _cdgUploadedSnapshot) || cdgTexture.Image.Handle != _cdgUploadedImage.Handle;
            }

            if (uploadCdg && plan.Cdg is { } cdgPlan)
            {
                // Forgotten until this frame's submission completes, so a failure re-uploads.
                _cdgUploadedSnapshot = null;
                var length = (ulong)cdgPlan.Snapshot.Width * (ulong)cdgPlan.Snapshot.Height * 4;
                BufferResource cdgUpload = EnsureBuffer(ref _cdgUploadBuffer, length, BufferUsageFlags.TransferSrcBit);
                void* mapped;
                Check(_vk.MapMemory(_device, cdgUpload.Memory, 0, length, 0, &mapped), "vkMapMemory");
                try
                {
                    VulkanCompositionPlanner.ExpandCdg(cdgPlan.Snapshot, new Span<byte>(mapped, (int)length));
                }
                finally
                {
                    _vk.UnmapMemory(_device, cdgUpload.Memory);
                }
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");

            VkImage target = targetImage.Image;
            Transition(target, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
            var clearColor = plan.ClearBgraPremultiplied;
            var clear = new ClearColorValue(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, clearColor.A / 255f);
            var range = ColorRange();
            _vk.CmdClearColorImage(_commandBuffer, target, ImageLayout.TransferDstOptimal, &clear, 1, &range);

            if (plan.Background is { } layer)
            {
                VkImage source = _sourceImage!.Value.Image;
                Transition(source, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
                CopyBufferToImage(_uploadBuffer!.Value, source, layer.Width, layer.Height);
                Transition(source, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
                // Order the blit's writes after the clear's writes on the same target.
                Transition(target, ImageLayout.TransferDstOptimal, ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferWriteBit);

                VulkanBlit region = layer.Blit;
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

            if (plan.HasDraws)
            {
                ImageView cdgView = default;
                if (plan.Cdg is { } overlay)
                {
                    ImageResource cdgImage = _cdgImage!.Value;
                    // A reused texture is still in ShaderReadOnlyOptimal from the frame that
                    // uploaded it, and that frame's submission has completed (fence wait).
                    if (uploadCdg)
                    {
                        Transition(cdgImage.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
                        CopyBufferToImage(_cdgUploadBuffer!.Value, cdgImage.Image, overlay.Snapshot.Width, overlay.Snapshot.Height);
                        Barrier(
                            cdgImage.Image,
                            ImageLayout.TransferDstOptimal,
                            ImageLayout.ShaderReadOnlyOptimal,
                            AccessFlags.TransferWriteBit,
                            AccessFlags.ShaderReadBit,
                            PipelineStageFlags.TransferBit,
                            PipelineStageFlags.FragmentShaderBit);
                    }

                    cdgView = cdgImage.View;
                }

                // Clear/blit writes -> render-pass load and blending, then draws -> readback.
                Barrier(
                    target,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.ColorAttachmentOptimal,
                    AccessFlags.TransferWriteBit,
                    AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.ColorAttachmentOutputBit);
                _overlay!.Record(_commandBuffer, renderTarget.Framebuffer, plan, cdgView);
                Barrier(
                    target,
                    ImageLayout.ColorAttachmentOptimal,
                    ImageLayout.TransferSrcOptimal,
                    AccessFlags.ColorAttachmentWriteBit,
                    AccessFlags.TransferReadBit,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.TransferBit);
            }
            else
            {
                Transition(target, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
            }

            if (export is { } exportTo)
            {
                RecordExportBlit(target, (uint)outputWidth, (uint)outputHeight, exportTo);
            }
            else
            {
                var readback = new BufferImageCopy
                {
                    ImageSubresource = ColorLayers(),
                    ImageExtent = new Extent3D((uint)outputWidth, (uint)outputHeight, 1),
                };
                _vk.CmdCopyImageToBuffer(_commandBuffer, target, ImageLayout.TransferSrcOptimal, readbackBuffer!.Value.Buffer, 1, &readback);
                var hostBarrier = new BufferMemoryBarrier
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Buffer = readbackBuffer.Value.Buffer,
                    Offset = 0,
                    Size = Vk.WholeSize,
                };
                _vk.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &hostBarrier, 0, null);
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            CommandBuffer commandBuffer = _commandBuffer;
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
            VkSemaphore waitSemaphore = default;
            VkSemaphore signalSemaphore = default;
            var waitStage = PipelineStageFlags.TransferBit;
            if (export is { } synchronized)
            {
                // Semaphore sync: wait for the compositor's release of this image before the blit
                // writes it, and tell the compositor when the write is done. The fence below still
                // completes the frame before it is announced (host synchronization).
                if (synchronized.WaitForConsumerRelease && synchronized.Image.ConsumerRelease.Handle != 0)
                {
                    waitSemaphore = synchronized.Image.ConsumerRelease;
                    submit.WaitSemaphoreCount = 1;
                    submit.PWaitSemaphores = &waitSemaphore;
                    submit.PWaitDstStageMask = &waitStage;
                }

                if (synchronized.Image.RenderComplete.Handle != 0)
                {
                    signalSemaphore = synchronized.Image.RenderComplete;
                    submit.SignalSemaphoreCount = 1;
                    submit.PSignalSemaphores = &signalSemaphore;
                }
            }

            Fence fence = _fence;
            Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences");
            Check(_vk.QueueSubmit(_queue, 1, &submit, _fence), "vkQueueSubmit");
            Check(_vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            if (uploadCdg && plan.Cdg is { } uploaded)
            {
                _cdgUploadedSnapshot = uploaded.Snapshot;
                _cdgUploadedImage = _cdgImage!.Value.Image;
                CdgUploadCount++;
            }

            if (export is not null)
                return [];

            // Grown only, so targets of different sizes rendering in turn do not reallocate it.
            var outputLength = checked(outputWidth * outputHeight * 4);
            if (_output.Length < outputLength)
                _output = new byte[outputLength];

            void* mappedOutput;
            Check(_vk.MapMemory(_device, readbackBuffer!.Value.Memory, 0, (ulong)outputLength, 0, &mappedOutput), "vkMapMemory");
            new ReadOnlySpan<byte>(mappedOutput, outputLength).CopyTo(_output);
            _vk.UnmapMemory(_device, readbackBuffer.Value.Memory);
            return _output.AsSpan(0, outputLength);
        }
    }

    /// <summary>
    /// Whether <paramref name="snapshot"/> draws exactly what <paramref name="uploaded"/> drew:
    /// the same object, or one sharing its (immutable) index and palette buffers and background
    /// index, which Core creates when only the dirty-region list changed.
    /// </summary>
    private static bool HasSamePixels(CdgRenderSnapshot snapshot, CdgRenderSnapshot? uploaded) =>
        uploaded is not null
        && (ReferenceEquals(snapshot, uploaded)
            || (snapshot.ColorIndices.Equals(uploaded.ColorIndices)
                && snapshot.PaletteArgb.Equals(uploaded.PaletteArgb)
                && snapshot.BackgroundColorIndex == uploaded.BackgroundColorIndex));

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
                DestroyExportedSets();
                DestroyRenderTargets();
                _overlay?.Dispose();
                _overlay = null;
                Destroy(ref _sourceImage);
                Destroy(ref _cdgImage);
                _cdgUploadedSnapshot = null;
                Destroy(ref _uploadBuffer);
                Destroy(ref _cdgUploadBuffer);
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
        string[] extensions = ProbeExportSupport();
        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        Result deviceResult;
        Device device;
        try
        {
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = extensions.Length == 0 ? null : extensionNames,
            };
            deviceResult = _vk.CreateDevice(_physicalDevice, &deviceInfo, null, &device);
        }
        finally
        {
            _ = SilkMarshal.Free((nint)extensionNames);
        }

        if (deviceResult != Result.Success)
        {
            failure = $"vkCreateDevice failed: {deviceResult}";
            return false;
        }

        _device = device;
        LoadExportFunctions();
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

        _overlay = VulkanOverlayPipelines.Create(_vk, _device, TargetFormat);
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
        var rejected = new List<string>();
        foreach (PhysicalDevice device in devices)
        {
            PhysicalDeviceProperties properties;
            _vk.GetPhysicalDeviceProperties(device, &properties);
            var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";
            FormatProperties formatProperties;
            _vk.GetPhysicalDeviceFormatProperties(device, TargetFormat, &formatProperties);
            FormatFeatureFlags missing = RequiredFormatFeatures & ~formatProperties.OptimalTilingFeatures;
            if (missing != 0)
            {
                rejected.Add($"{name}: missing {missing}");
                continue;
            }

            if (!TryFindGraphicsQueue(device, out var family))
            {
                rejected.Add($"{name}: no graphics queue");
                continue;
            }

            var rank = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 0,
                PhysicalDeviceType.IntegratedGpu => 1,
                PhysicalDeviceType.VirtualGpu => 2,
                PhysicalDeviceType.Cpu => 3,
                _ => 4,
            };
            candidates.Add((device, family, rank, name, properties.DeviceType));
        }

        if (candidates.Count == 0)
        {
            failure = "No Vulkan device supports B8G8R8A8Unorm blits, transfers, blended colour attachments and sampling "
                + $"on a graphics queue ({string.Join("; ", rejected)}).";
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

    private void Upload(BufferResource upload, ReadOnlySpan<byte> sourceBgra, int width, int height, int stride)
    {
        void* mapped;
        var rowBytes = width * 4;
        Check(_vk.MapMemory(_device, upload.Memory, 0, (ulong)rowBytes * (ulong)height, 0, &mapped), "vkMapMemory");
        var destination = new Span<byte>(mapped, rowBytes * height);
        for (var y = 0; y < height; y++)
            sourceBgra.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
        _vk.UnmapMemory(_device, upload.Memory);
    }

    private void CopyBufferToImage(BufferResource upload, VkImage image, int width, int height)
    {
        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = ColorLayers(),
            ImageOffset = default,
            ImageExtent = new Extent3D((uint)width, (uint)height, 1),
        };
        _vk.CmdCopyBufferToImage(_commandBuffer, upload.Buffer, image, ImageLayout.TransferDstOptimal, 1, &copy);
    }

    /// <summary>The most render targets kept (one per attached surface size).</summary>
    internal const int MaxRenderTargets = 4;

    /// <summary>Gets how many render targets exist (tests).</summary>
    internal int RenderTargetCount => _renderTargets.Count;

    /// <summary>
    /// Returns the render target of this size with its framebuffer, creating it when none exists.
    /// The least recently used target goes when <see cref="MaxRenderTargets"/> are kept. A failed
    /// creation leaves no partial target, so the next frame retries.
    /// </summary>
    private RenderTarget EnsureTarget(uint width, uint height)
    {
        for (var i = 0; i < _renderTargets.Count; i++)
        {
            RenderTarget existing = _renderTargets[i];
            if (existing.Image.Width != width || existing.Image.Height != height)
                continue;

            if (i != _renderTargets.Count - 1)
            {
                _renderTargets.RemoveAt(i);
                _renderTargets.Add(existing);
            }

            return existing;
        }

        if (_renderTargets.Count >= MaxRenderTargets)
        {
            // Every earlier submission completed (fence wait), so nothing still uses it.
            DestroyRenderTarget(_renderTargets[0]);
            _renderTargets.RemoveAt(0);
        }

        ImageResource? image = null;
        ImageResource created = EnsureImage(ref image, width, height, TransferUsage | ImageUsageFlags.ColorAttachmentBit);
        Framebuffer framebuffer;
        try
        {
            framebuffer = _overlay!.CreateFramebuffer(created.View, width, height);
        }
        catch
        {
            Destroy(ref image);
            throw;
        }

        var target = new RenderTarget(created, framebuffer);
        _renderTargets.Add(target);
        return target;
    }

    /// <summary>
    /// Re-creates <paramref name="resource"/> when its size differs. The old resource is cleared
    /// before re-creation, and a partially created one is freed on failure, so a failed resize
    /// never leaves destroyed handles in the field. Images used as attachments or sampled get a
    /// colour view.
    /// </summary>
    private ImageResource EnsureImage(ref ImageResource? resource, uint width, uint height, ImageUsageFlags usage)
    {
        if (resource is { } image && image.Width == width && image.Height == height)
            return image;

        Destroy(ref resource);
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
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        VkImage handle = default;
        DeviceMemory memory = default;
        ImageView view = default;
        try
        {
            Check(_vk.CreateImage(_device, &info, null, &handle), "vkCreateImage");
            MemoryRequirements requirements;
            _vk.GetImageMemoryRequirements(_device, handle, &requirements);
            memory = Allocate(requirements, MemoryPropertyFlags.DeviceLocalBit, fallback: MemoryPropertyFlags.None);
            Check(_vk.BindImageMemory(_device, handle, memory, 0), "vkBindImageMemory");
            if ((usage & (ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit)) != 0)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = handle,
                    ViewType = ImageViewType.Type2D,
                    Format = TargetFormat,
                    SubresourceRange = ColorRange(),
                };
                Check(_vk.CreateImageView(_device, &viewInfo, null, &view), "vkCreateImageView");
            }

            ImageResource created = new(handle, memory, view, width, height);
            resource = created;
            return created;
        }
        catch
        {
            if (view.Handle != 0)
                _vk.DestroyImageView(_device, view, null);
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
            if (handle.Handle != 0)
                _vk.DestroyImage(_device, handle, null);
            throw;
        }
    }

    /// <summary>Grows <paramref name="resource"/> when too small, with the same failure safety as <see cref="EnsureImage"/>.</summary>
    private BufferResource EnsureBuffer(ref BufferResource? resource, ulong size, BufferUsageFlags usage)
    {
        if (resource is { } buffer && buffer.Size >= size)
            return buffer;

        Destroy(ref resource);
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        VkBuffer handle = default;
        DeviceMemory memory = default;
        try
        {
            Check(_vk.CreateBuffer(_device, &info, null, &handle), "vkCreateBuffer");
            MemoryRequirements requirements;
            _vk.GetBufferMemoryRequirements(_device, handle, &requirements);
            memory = Allocate(requirements, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, fallback: null);
            Check(_vk.BindBufferMemory(_device, handle, memory, 0), "vkBindBufferMemory");
            BufferResource created = new(handle, memory, size);
            resource = created;
            return created;
        }
        catch
        {
            if (memory.Handle != 0)
                _vk.FreeMemory(_device, memory, null);
            if (handle.Handle != 0)
                _vk.DestroyBuffer(_device, handle, null);
            throw;
        }
    }

    private DeviceMemory Allocate(MemoryRequirements requirements, MemoryPropertyFlags preferred, MemoryPropertyFlags? fallback)
    {
        var typeIndex = FindMemoryType(requirements.MemoryTypeBits, preferred);
        if (typeIndex < 0 && fallback is { } fallbackFlags)
            typeIndex = FindMemoryType(requirements.MemoryTypeBits, fallbackFlags);
        if (typeIndex < 0)
            throw new VulkanException($"No memory type satisfies {preferred}; vkAllocateMemory", Result.ErrorOutOfDeviceMemory);

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

    /// <summary>Transfer-to-transfer layout transition (or from nothing when <paramref name="srcAccess"/> is 0).</summary>
    private void Transition(VkImage image, ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess) =>
        Barrier(image, from, to, srcAccess, dstAccess, srcAccess == 0 ? PipelineStageFlags.TopOfPipeBit : PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

    private void Barrier(
        VkImage image,
        ImageLayout from,
        ImageLayout to,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage)
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
        _vk.CmdPipelineBarrier(_commandBuffer, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    /// <summary>Destroys a render target: the framebuffer before the image view it references.</summary>
    private void DestroyRenderTarget(RenderTarget target)
    {
        if (target.Framebuffer.Handle != 0)
            _vk.DestroyFramebuffer(_device, target.Framebuffer, null);

        ImageResource? image = target.Image;
        Destroy(ref image);
    }

    private void DestroyRenderTargets()
    {
        foreach (RenderTarget target in _renderTargets)
            DestroyRenderTarget(target);

        _renderTargets.Clear();
    }

    private void Destroy(ref ImageResource? resource)
    {
        if (resource is not { } image)
            return;

        if (image.View.Handle != 0)
            _vk.DestroyImageView(_device, image.View, null);
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

    private readonly record struct ImageResource(VkImage Image, DeviceMemory Memory, ImageView View, uint Width, uint Height);

    private readonly record struct BufferResource(VkBuffer Buffer, DeviceMemory Memory, ulong Size);

    private readonly record struct RenderTarget(ImageResource Image, Framebuffer Framebuffer);
}

/// <summary>A failed Vulkan call. <see cref="IsDeviceLost"/> marks recoverable device loss.</summary>
internal sealed class VulkanException(string operation, Result result)
    : InvalidOperationException($"{operation} failed: {result}")
{
    public Result Result { get; } = result;

    public bool IsDeviceLost => Result == Result.ErrorDeviceLost;
}
