using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// FFmpeg's Vulkan hardware-context structures (<c>libavutil/hwcontext_vulkan.h</c>, FFmpeg 9.0,
/// libavutil 61) for 64-bit processes. FFmpeg.AutoGen does not bind them. Only the fields this
/// renderer reads or writes matter; the layouts are pinned by tests.
/// </summary>
static class FfmpegVulkanInterop
{
    /// <summary><c>AV_NUM_DATA_POINTERS</c>.</summary>
    public const int DataPointers = 8;

    /// <summary><c>AVVulkanDeviceContext.qf</c> capacity.</summary>
    public const int MaxQueueFamilies = 64;

    /// <summary><c>VK_ACCESS_2_SHADER_READ_BIT</c>, as stored in <see cref="AVVkFrame"/>.</summary>
    public const ulong ShaderReadAccess2 = 0x20;
}

/// <summary><c>AVVulkanDeviceQueueFamily</c>: a queue family FFmpeg may use.</summary>
[StructLayout(LayoutKind.Sequential)]
struct AVVulkanDeviceQueueFamily
{
    public int Index;
    public int Count;
    public QueueFlags Flags;
    public VideoCodecOperationFlagsKHR VideoCapabilities;
}

/// <summary><c>AVVulkanDeviceContext</c> (<c>AVHWDeviceContext.hwctx</c> for <c>AV_HWDEVICE_TYPE_VULKAN</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
unsafe struct AVVulkanDeviceContext
{
    public void* Allocator;
    public nint GetInstanceProcAddr;
    public nint Instance;
    public nint PhysicalDevice;
    public nint Device;

    /// <summary>The features enabled at device creation, with their extension chain (read by FFmpeg for the context's lifetime).</summary>
    public PhysicalDeviceFeatures2 DeviceFeatures;
    public byte** EnabledInstanceExtensions;
    public int EnabledInstanceExtensionCount;
    public byte** EnabledDeviceExtensions;
    public int EnabledDeviceExtensionCount;

    /// <summary><c>lock_queue(AVHWDeviceContext*, uint32_t family, uint32_t index)</c> (deprecated in FFmpeg 9, still used).</summary>
    public nint LockQueue;
    public nint UnlockQueue;
    public QueueFamilyArray QueueFamilies;
    public int QueueFamilyCount;
    public uint QueueCreateFlags;

    [System.Runtime.CompilerServices.InlineArray(FfmpegVulkanInterop.MaxQueueFamilies)]
    public struct QueueFamilyArray
    {
        AVVulkanDeviceQueueFamily _element;
    }
}

/// <summary><c>AVVulkanFramesContext</c> (<c>AVHWFramesContext.hwctx</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
unsafe struct AVVulkanFramesContext
{
    public ImageTiling Tiling;
    public ImageUsageFlags Usage;
    public void* CreatePNext;
    public fixed ulong AllocPNext[FfmpegVulkanInterop.DataPointers];
    public int Flags;
    public ImageCreateFlags ImageFlags;
    public fixed int Format[FfmpegVulkanInterop.DataPointers];
    public int LayerCount;

    /// <summary><c>lock_frame(AVHWFramesContext*, AVVkFrame*)</c>.</summary>
    public nint LockFrame;
    public nint UnlockFrame;
}

/// <summary><c>AVVkFrame</c>: the images of one decoded frame and their synchronization state.</summary>
[StructLayout(LayoutKind.Sequential)]
unsafe struct AVVkFrame
{
    public fixed ulong Image[FfmpegVulkanInterop.DataPointers];
    public ImageTiling Tiling;
    public fixed ulong Memory[FfmpegVulkanInterop.DataPointers];
    public fixed ulong Size[FfmpegVulkanInterop.DataPointers];
    public MemoryPropertyFlags MemoryFlags;

    /// <summary><c>VkAccessFlagBits2</c> (64-bit) per image, updated after every barrier.</summary>
    public fixed ulong Access[FfmpegVulkanInterop.DataPointers];
    public fixed int Layout[FfmpegVulkanInterop.DataPointers];

    /// <summary>Timeline semaphores, one per image: wait on <see cref="SemaphoreValue"/>, signal it + 1.</summary>
    public fixed ulong Semaphore[FfmpegVulkanInterop.DataPointers];
    public fixed ulong SemaphoreValue[FfmpegVulkanInterop.DataPointers];
    public fixed long Offset[FfmpegVulkanInterop.DataPointers];
    public fixed uint QueueFamily[FfmpegVulkanInterop.DataPointers];
    public void* Internal;
}
