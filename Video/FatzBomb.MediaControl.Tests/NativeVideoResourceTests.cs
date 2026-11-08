using FatzBomb.MediaControl.Core.Graphics;

namespace FatzBomb.MediaControl.Tests;

public sealed class NativeVideoResourceTests
{
    [Fact]
    public void FrameResource_PreservesBackendDeviceAndSynchronizationIdentity()
    {
        var device = new NativeVideoDeviceResource(
            MediaGraphicsBackendId.Direct3D11,
            DeviceHandle: 101,
            DeviceContextHandle: 202);
        var resource = new NativeVideoFrameResource(
            device,
            ResourceHandle: 303,
            SubresourceIndex: 4,
            SynchronizationHandle: 505,
            SynchronizationValue: 606);

        Assert.Equal(MediaGraphicsBackendId.Direct3D11, resource.BackendId);
        Assert.Equal(101, resource.Device.DeviceHandle);
        Assert.Equal(202, resource.Device.DeviceContextHandle);
        Assert.Equal(303, resource.ResourceHandle);
        Assert.Equal(4, resource.SubresourceIndex);
        Assert.Equal(505, resource.SynchronizationHandle);
        Assert.Equal(606UL, resource.SynchronizationValue);
        Assert.Equal(NativeVideoResourceLifetime.ProducerOwned, resource.Lifetime);
        Assert.True(resource.IsValid);
    }

    [Fact]
    public void FrameResource_WithNoResourceHandle_IsInvalid()
    {
        var device = new NativeVideoDeviceResource(new MediaGraphicsBackendId("test"), DeviceHandle: 1);
        var resource = new NativeVideoFrameResource(device, ResourceHandle: 0, SubresourceIndex: 0);

        Assert.False(resource.IsValid);
    }
}
