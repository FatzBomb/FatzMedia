using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace FatzBomb.MediaControl.Rendering.Vulkan;

/// <summary>What the device can decode with Vulkan Video, when it was created for it (Stage 045A slice 2).</summary>
/// <param name="DecodeFamily">The queue family with <c>VK_QUEUE_VIDEO_DECODE_BIT_KHR</c>.</param>
/// <param name="Codecs">The decode operations both the family and the enabled extensions support.</param>
internal sealed record VulkanVideoDecodeSupport(uint DecodeFamily, VideoCodecOperationFlagsKHR Codecs)
{
    /// <summary>The codecs as a short list for the log, such as <c>H.264, HEVC, AV1</c>.</summary>
    public string Describe()
    {
        List<string> names = [];
        if ((Codecs & VideoCodecOperationFlagsKHR.DecodeH264BitKhr) != 0)
            names.Add("H.264");
        if ((Codecs & VideoCodecOperationFlagsKHR.DecodeH265BitKhr) != 0)
            names.Add("HEVC");
        if ((Codecs & VideoCodecOperationFlagsKHR.DecodeAV1BitKhr) != 0)
            names.Add("AV1");
        if ((Codecs & VideoCodecOperationFlagsKHR.DecodeVP9BitKhr) != 0)
            names.Add("VP9");
        return string.Join(", ", names);
    }
}

/// <summary>
/// The Vulkan instance and device a compositor renders on, shared with FFmpeg when it decodes on
/// them (Stage 045A slice 2). Reference counted: the compositor holds one reference and an FFmpeg
/// device context made from it holds another, so neither outlives the device the other still
/// uses. Every queue submission, the renderer's and FFmpeg's, takes <see cref="QueueLock"/>.
/// </summary>
internal sealed unsafe class VulkanSharedDevice
{
    private static readonly ConcurrentDictionary<nint, VulkanSharedDevice> Devices = new();

    private readonly Lock _contextGate = new();
    private readonly GCHandle _self;
    private readonly byte** _deviceExtensionNames;
    private readonly int _deviceExtensionCount;
    private readonly VulkanFeatureChain? _features;
    private Fence _probeFence;
    private int _references = 1;
    private int _lost;
    private AVBufferRef* _ffmpegContext;
    private string? _ffmpegContextFailure;

    public VulkanSharedDevice(
        Vk vk,
        Instance instance,
        PhysicalDevice physicalDevice,
        Device device,
        uint graphicsFamily,
        QueueFlags graphicsFamilyFlags,
        string[] deviceExtensions,
        VulkanFeatureChain? features,
        VulkanVideoDecodeSupport? videoDecode)
    {
        Vk = vk;
        Instance = instance;
        PhysicalDevice = physicalDevice;
        Device = device;
        GraphicsFamily = graphicsFamily;
        GraphicsFamilyFlags = graphicsFamilyFlags;
        _features = features;
        VideoDecode = videoDecode;
        _deviceExtensionCount = deviceExtensions.Length;
        _deviceExtensionNames = deviceExtensions.Length == 0 ? null : (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions);
        _self = GCHandle.Alloc(this);

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Fence fence;
        if (vk.CreateFence(device, &fenceInfo, null, &fence) == Result.Success)
            _probeFence = fence;

        Devices[device.Handle] = this;
    }

    public Vk Vk { get; }
    public Instance Instance { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public uint GraphicsFamily { get; }
    public QueueFlags GraphicsFamilyFlags { get; }

    /// <summary>Gets what the device decodes, or null when it was created without Vulkan Video.</summary>
    public VulkanVideoDecodeSupport? VideoDecode { get; }

    /// <summary>Held around every <c>vkQueueSubmit</c> and queue or device wait, by the renderer and by FFmpeg.</summary>
    public Lock QueueLock { get; } = new();

    /// <summary>Gets whether the device was lost (marked by the renderer, or found by <see cref="ProbeLost"/>).</summary>
    public bool IsLost => Volatile.Read(ref _lost) != 0;

    /// <summary>Records that a call on this device returned <c>VK_ERROR_DEVICE_LOST</c>.</summary>
    public void MarkLost() => Volatile.Write(ref _lost, 1);

    /// <summary>Finds the shared device behind a native device handle, while it exists.</summary>
    public static VulkanSharedDevice? Find(nint deviceHandle) =>
        deviceHandle != 0 && Devices.TryGetValue(deviceHandle, out VulkanSharedDevice? device) ? device : null;

    /// <summary>Whether the device is lost: already marked, or its status query says so now.</summary>
    public bool ProbeLost()
    {
        if (IsLost)
            return true;

        if (_probeFence.Handle != 0 && Vk.GetFenceStatus(Device, _probeFence) == Result.ErrorDeviceLost)
            MarkLost();
        return IsLost;
    }

    public void AddRef()
    {
        if (Interlocked.Increment(ref _references) <= 1)
        {
            // Already released: undo the increment so the count stays at zero.
            _ = Interlocked.Decrement(ref _references);
            throw new ObjectDisposedException(nameof(VulkanSharedDevice));
        }
    }

    /// <summary>Drops a reference; the last one destroys the device, the instance and the loader.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0)
            return;

        _ = Devices.TryRemove(new KeyValuePair<nint, VulkanSharedDevice>(Device.Handle, this));
        lock (QueueLock)
        {
            if (_probeFence.Handle != 0)
                Vk.DestroyFence(Device, _probeFence, null);
            _probeFence = default;
            Vk.DestroyDevice(Device, null);
        }

        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
        if (_deviceExtensionNames != null)
            _ = SilkMarshal.Free((nint)_deviceExtensionNames);
        _features?.Dispose();
        _self.Free();
    }

    /// <summary>
    /// Returns a new reference to the FFmpeg Vulkan device context on this device (created once),
    /// or 0 with the reason. The caller unrefs it.
    /// </summary>
    public nint CreateFfmpegDeviceContextReference(out string? failure)
    {
        lock (_contextGate)
        {
            if (_ffmpegContext == null && _ffmpegContextFailure is null)
                _ffmpegContext = CreateFfmpegDeviceContext(out _ffmpegContextFailure);

            if (_ffmpegContext == null)
            {
                failure = _ffmpegContextFailure;
                return 0;
            }

            AVBufferRef* reference = ffmpeg.av_buffer_ref(_ffmpegContext);
            failure = reference == null ? "av_buffer_ref failed" : null;
            return (nint)reference;
        }
    }

    /// <summary>Drops this device's own reference to its FFmpeg context (decoders may keep theirs).</summary>
    public void ReleaseFfmpegDeviceContext()
    {
        lock (_contextGate)
        {
            if (_ffmpegContext == null)
                return;

            AVBufferRef* context = _ffmpegContext;
            _ffmpegContext = null;
            ffmpeg.av_buffer_unref(&context);
        }
    }

    private AVBufferRef* CreateFfmpegDeviceContext(out string? failure)
    {
        if (VideoDecode is not { } decode)
        {
            failure = "the Vulkan device has no video decode queue";
            return null;
        }

        AVBufferRef* buffer;
        try
        {
            buffer = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            failure = $"FFmpeg is not loaded ({exception.Message})";
            return null;
        }

        if (buffer == null)
        {
            failure = "this FFmpeg build has no Vulkan hardware context (it was built without --enable-vulkan)";
            return null;
        }

        var context = (AVHWDeviceContext*)buffer->data;
        var vulkan = (AVVulkanDeviceContext*)context->hwctx;
        FillDeviceContext(vulkan, decode);

        // From here the context holds a device reference, dropped by its free callback.
        AddRef();
        context->user_opaque = (void*)GCHandle.ToIntPtr(_self);
        context->free = new AVHWDeviceContext_free_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, void>)&OnContextFree };
        var result = ffmpeg.av_hwdevice_ctx_init(buffer);
        if (result < 0)
        {
            ffmpeg.av_buffer_unref(&buffer);
            failure = $"FFmpeg refused the renderer's Vulkan device ({DescribeError(result)})";
            return null;
        }

        failure = null;
        return buffer;
    }

    /// <summary>Describes this device to FFmpeg (internal for the layout tests).</summary>
    internal void FillDeviceContext(AVVulkanDeviceContext* vulkan, VulkanVideoDecodeSupport decode)
    {
        vulkan->Allocator = null;
        vulkan->GetInstanceProcAddr = Vk.Context.GetProcAddress("vkGetInstanceProcAddr");
        vulkan->Instance = Instance.Handle;
        vulkan->PhysicalDevice = PhysicalDevice.Handle;
        vulkan->Device = Device.Handle;
        vulkan->DeviceFeatures = _features is { } features ? *features.Features : new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2 };
        vulkan->EnabledInstanceExtensions = null;
        vulkan->EnabledInstanceExtensionCount = 0;
        vulkan->EnabledDeviceExtensions = _deviceExtensionNames;
        vulkan->EnabledDeviceExtensionCount = _deviceExtensionCount;
        vulkan->LockQueue = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, uint, uint, void>)&OnLockQueue;
        vulkan->UnlockQueue = (nint)(delegate* unmanaged[Cdecl]<AVHWDeviceContext*, uint, uint, void>)&OnUnlockQueue;

        // Preferentially ordered: the renderer's graphics family (FFmpeg's transfers and compute
        // run there), then the decode family. One queue each, as the device was created with.
        var count = 0;
        vulkan->QueueFamilies[count++] = new AVVulkanDeviceQueueFamily
        {
            Index = (int)GraphicsFamily,
            Count = 1,
            Flags = GraphicsFamilyFlags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit | QueueFlags.TransferBit),
        };
        if (decode.Codecs == VideoCodecOperationFlagsKHR.NoneKhr)
        {
            // Shared without decoding (tests): FFmpeg's transfers only.
        }
        else if (decode.DecodeFamily != GraphicsFamily)
        {
            vulkan->QueueFamilies[count++] = new AVVulkanDeviceQueueFamily
            {
                Index = (int)decode.DecodeFamily,
                Count = 1,
                Flags = QueueFlags.VideoDecodeBitKhr,
                VideoCapabilities = decode.Codecs,
            };
        }
        else
        {
            vulkan->QueueFamilies[0].Flags |= QueueFlags.VideoDecodeBitKhr;
            vulkan->QueueFamilies[0].VideoCapabilities = decode.Codecs;
        }

        vulkan->QueueFamilyCount = count;
        vulkan->QueueCreateFlags = 0;
    }

    private static string DescribeError(int error)
    {
        var buffer = stackalloc byte[256];
        _ = ffmpeg.av_strerror(error, buffer, 256);
        return $"{Marshal.PtrToStringAnsi((nint)buffer)} ({error})";
    }

    private static VulkanSharedDevice? FromContext(AVHWDeviceContext* context) =>
        context == null || context->user_opaque == null
            ? null
            : GCHandle.FromIntPtr((nint)context->user_opaque).Target as VulkanSharedDevice;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLockQueue(AVHWDeviceContext* context, uint family, uint index) => FromContext(context)?.QueueLock.Enter();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUnlockQueue(AVHWDeviceContext* context, uint family, uint index) => FromContext(context)?.QueueLock.Exit();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnContextFree(AVHWDeviceContext* context)
    {
        VulkanSharedDevice? device = FromContext(context);
        context->user_opaque = null;
        device?.Release();
    }
}

/// <summary>
/// The device features enabled for Vulkan Video (Stage 045A slice 2) as a native
/// <c>VkPhysicalDeviceFeatures2</c> chain: passed to <c>vkCreateDevice</c>, then to FFmpeg, which
/// reads it for as long as its device context lives.
/// </summary>
internal sealed unsafe class VulkanFeatureChain : IDisposable
{
    private VulkanFeatureChain(bool withVideoMaintenance1)
    {
        Features = Allocate<PhysicalDeviceFeatures2>();
        Vulkan11 = Allocate<PhysicalDeviceVulkan11Features>();
        Vulkan12 = Allocate<PhysicalDeviceVulkan12Features>();
        Vulkan13 = Allocate<PhysicalDeviceVulkan13Features>();
        Features->SType = StructureType.PhysicalDeviceFeatures2;
        Vulkan11->SType = StructureType.PhysicalDeviceVulkan11Features;
        Vulkan12->SType = StructureType.PhysicalDeviceVulkan12Features;
        Vulkan13->SType = StructureType.PhysicalDeviceVulkan13Features;
        Features->PNext = Vulkan11;
        Vulkan11->PNext = Vulkan12;
        Vulkan12->PNext = Vulkan13;
        if (withVideoMaintenance1)
        {
            VideoMaintenance1 = Allocate<PhysicalDeviceVideoMaintenance1FeaturesKHR>();
            VideoMaintenance1->SType = StructureType.PhysicalDeviceVideoMaintenance1FeaturesKhr;
            Vulkan13->PNext = VideoMaintenance1;
        }
    }

    public PhysicalDeviceFeatures2* Features { get; private set; }
    public PhysicalDeviceVulkan11Features* Vulkan11 { get; private set; }
    public PhysicalDeviceVulkan12Features* Vulkan12 { get; private set; }
    public PhysicalDeviceVulkan13Features* Vulkan13 { get; private set; }
    public PhysicalDeviceVideoMaintenance1FeaturesKHR* VideoMaintenance1 { get; private set; }

    /// <summary>Queries everything <paramref name="physicalDevice"/> supports into a new chain.</summary>
    public static VulkanFeatureChain QuerySupported(Vk vk, PhysicalDevice physicalDevice, bool withVideoMaintenance1)
    {
        var chain = new VulkanFeatureChain(withVideoMaintenance1);
        vk.GetPhysicalDeviceFeatures2(physicalDevice, chain.Features);
        return chain;
    }

    /// <summary>
    /// The features FFmpeg enables when it creates a device itself (<c>device_features_copy_needed</c>
    /// in FFmpeg 9's <c>hwcontext_vulkan.c</c>), as far as <paramref name="supported"/> has them.
    /// FFmpeg reads what a device supports, so enabling the same set keeps it from using a
    /// feature that is supported but off.
    /// </summary>
    public static VulkanFeatureChain EnableForVideo(VulkanFeatureChain supported)
    {
        var enabled = new VulkanFeatureChain(supported.VideoMaintenance1 != null);
        PhysicalDeviceFeatures* s = &supported.Features->Features;
        PhysicalDeviceFeatures* e = &enabled.Features->Features;
        e->ShaderImageGatherExtended = s->ShaderImageGatherExtended;
        e->ShaderStorageImageReadWithoutFormat = s->ShaderStorageImageReadWithoutFormat;
        e->ShaderStorageImageWriteWithoutFormat = s->ShaderStorageImageWriteWithoutFormat;
        e->FragmentStoresAndAtomics = s->FragmentStoresAndAtomics;
        e->VertexPipelineStoresAndAtomics = s->VertexPipelineStoresAndAtomics;
        e->ShaderInt64 = s->ShaderInt64;
        e->ShaderInt16 = s->ShaderInt16;
        e->ShaderFloat64 = s->ShaderFloat64;

        PhysicalDeviceVulkan11Features* s11 = supported.Vulkan11;
        PhysicalDeviceVulkan11Features* e11 = enabled.Vulkan11;
        e11->SamplerYcbcrConversion = s11->SamplerYcbcrConversion;
        e11->StoragePushConstant16 = s11->StoragePushConstant16;
        e11->StorageBuffer16BitAccess = s11->StorageBuffer16BitAccess;
        e11->UniformAndStorageBuffer16BitAccess = s11->UniformAndStorageBuffer16BitAccess;

        PhysicalDeviceVulkan12Features* s12 = supported.Vulkan12;
        PhysicalDeviceVulkan12Features* e12 = enabled.Vulkan12;
        e12->TimelineSemaphore = s12->TimelineSemaphore;
        e12->ScalarBlockLayout = s12->ScalarBlockLayout;
        e12->BufferDeviceAddress = s12->BufferDeviceAddress;
        e12->HostQueryReset = s12->HostQueryReset;
        e12->StoragePushConstant8 = s12->StoragePushConstant8;
        e12->ShaderInt8 = s12->ShaderInt8;
        e12->StorageBuffer8BitAccess = s12->StorageBuffer8BitAccess;
        e12->UniformAndStorageBuffer8BitAccess = s12->UniformAndStorageBuffer8BitAccess;
        e12->ShaderFloat16 = s12->ShaderFloat16;
        e12->ShaderBufferInt64Atomics = s12->ShaderBufferInt64Atomics;
        e12->ShaderSharedInt64Atomics = s12->ShaderSharedInt64Atomics;
        e12->VulkanMemoryModel = s12->VulkanMemoryModel;
        e12->VulkanMemoryModelDeviceScope = s12->VulkanMemoryModelDeviceScope;
        e12->VulkanMemoryModelAvailabilityVisibilityChains = s12->VulkanMemoryModelAvailabilityVisibilityChains;
        e12->UniformBufferStandardLayout = s12->UniformBufferStandardLayout;
        e12->RuntimeDescriptorArray = s12->RuntimeDescriptorArray;
        e12->ShaderSubgroupExtendedTypes = s12->ShaderSubgroupExtendedTypes;
        e12->ShaderUniformBufferArrayNonUniformIndexing = s12->ShaderUniformBufferArrayNonUniformIndexing;
        e12->ShaderSampledImageArrayNonUniformIndexing = s12->ShaderSampledImageArrayNonUniformIndexing;
        e12->ShaderStorageBufferArrayNonUniformIndexing = s12->ShaderStorageBufferArrayNonUniformIndexing;
        e12->ShaderStorageImageArrayNonUniformIndexing = s12->ShaderStorageImageArrayNonUniformIndexing;

        PhysicalDeviceVulkan13Features* s13 = supported.Vulkan13;
        PhysicalDeviceVulkan13Features* e13 = enabled.Vulkan13;
        e13->DynamicRendering = s13->DynamicRendering;
        e13->Maintenance4 = s13->Maintenance4;
        e13->Synchronization2 = s13->Synchronization2;
        e13->ComputeFullSubgroups = s13->ComputeFullSubgroups;
        e13->SubgroupSizeControl = s13->SubgroupSizeControl;
        e13->ShaderZeroInitializeWorkgroupMemory = s13->ShaderZeroInitializeWorkgroupMemory;

        if (enabled.VideoMaintenance1 != null)
            enabled.VideoMaintenance1->VideoMaintenance1 = supported.VideoMaintenance1->VideoMaintenance1;
        return enabled;
    }

    public void Dispose()
    {
        Free(Features);
        Free(Vulkan11);
        Free(Vulkan12);
        Free(Vulkan13);
        Free(VideoMaintenance1);
        Features = null;
        Vulkan11 = null;
        Vulkan12 = null;
        Vulkan13 = null;
        VideoMaintenance1 = null;
    }

    private static T* Allocate<T>()
        where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));

    private static void Free(void* pointer)
    {
        if (pointer != null)
            NativeMemory.Free(pointer);
    }
}
