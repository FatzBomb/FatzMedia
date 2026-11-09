#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Exposes an <see cref="IMediaSharedGpuImageConsumer"/> through the neutral native-surface
/// descriptor as an opaque, never-reused token (the pattern of
/// <see cref="MediaPresentationFrameSinkHandle"/>). Dispose only after the surface is detached.
/// </summary>
public sealed class MediaSharedGpuImageConsumerHandle : IDisposable
{
    private static readonly OpaqueTokenRegistry<IMediaSharedGpuImageConsumer> Registry =
        new("The shared GPU image consumer token space has been exhausted.");

    private long _token;

    public MediaSharedGpuImageConsumerHandle(IMediaSharedGpuImageConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        _token = Registry.Register(consumer);
    }

    /// <summary>Gets whether the handle is registered (not disposed).</summary>
    public bool IsAllocated => Volatile.Read(ref _token) != 0;

    /// <summary>Gets the opaque nonzero token, or 0 once disposed.</summary>
    public nint Value => (nint)Volatile.Read(ref _token);

    /// <summary>Creates a <see cref="MediaNativeSurfaceTypeIds.SharedGpuImage"/> descriptor carrying the token.</summary>
    /// <exception cref="ObjectDisposedException">The handle was disposed.</exception>
    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor()
    {
        long token = Volatile.Read(ref _token);
        ObjectDisposedException.ThrowIf(token == 0, this);
        return new(
            MediaNativeSurfaceTypeIds.SharedGpuImage,
            [new MediaNativeHandle(MediaNativeHandleIds.SharedImageConsumer, (nint)token)]);
    }

    /// <summary>Resolves a token to its consumer while the owning handle is not disposed.</summary>
    public static bool TryResolve(nint value, out IMediaSharedGpuImageConsumer? consumer)
    {
        return Registry.TryResolve(value, out consumer);
    }

    /// <summary>Resolves the consumer of a <see cref="MediaNativeSurfaceTypeIds.SharedGpuImage"/> surface.</summary>
    public static bool TryResolve(MediaPresentationSurfaceDescriptor? surface, out IMediaSharedGpuImageConsumer? consumer)
    {
        consumer = null;
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.SharedGpuImage
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.SharedImageConsumer, out nint handle)
            && TryResolve(handle, out consumer)
            && consumer is not null;
    }

    public void Dispose()
    {
        long token = Interlocked.Exchange(ref _token, 0);
        if (token != 0)
            Registry.Remove(token);
    }
}
