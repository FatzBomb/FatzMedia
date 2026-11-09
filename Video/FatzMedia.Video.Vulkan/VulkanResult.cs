using Silk.NET.Vulkan;

namespace FatzMedia.Video.Vulkan;

/// <summary>Turns a failed Vulkan result into a <see cref="VulkanException"/> naming the step.</summary>
internal static class VulkanResult
{
    public static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new VulkanException(operation, result);
    }
}
