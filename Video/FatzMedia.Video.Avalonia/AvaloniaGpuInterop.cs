using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace FatzMedia.Video.Avalonia;

/// <summary>
/// What the compositor's GPU interop offers, captured once per attach decision. <see cref="Interop"/>
/// says what the compositor accepts and whether its context was lost; <see cref="Importer"/> imports the
/// renderer's images on the render thread. Tests describe a compositor with neither (Avalonia's interop
/// interfaces cannot be implemented outside Avalonia) and supply their own importer.
/// </summary>
sealed record GpuInteropCapabilities(
    IReadOnlyList<string> ImageHandleTypes,
    Func<string, CompositionGpuImportedImageSynchronizationCapabilities> Synchronization,
    IReadOnlyList<string> SemaphoreHandleTypes,
    byte[]? DeviceLuid,
    byte[]? DeviceUuid,
    ICompositionGpuInterop? Interop,
    IGpuImageImporter? Importer = null)
{
    public bool IsLost => Interop?.IsLost == true;

    public static GpuInteropCapabilities From(ICompositionGpuInterop interop) => new(
        interop.SupportedImageHandleTypes ?? [],
        interop.GetSynchronizationCapabilities,
        interop.SupportedSemaphoreTypes ?? [],
        interop.DeviceLuid,
        interop.DeviceUuid,
        interop);

    /// <summary>Asks the compositor for its interop and for the render-thread import feature (null when it has no interop).</summary>
    public static ValueTask<GpuInteropCapabilities?> QueryAsync(Compositor compositor)
    {
        return AwaitAsync(compositor);

        static async ValueTask<GpuInteropCapabilities?> AwaitAsync(Compositor compositor)
        {
            var interop = await compositor.TryGetCompositionGpuInterop();
            if (interop is null)
                return null;

            return From(interop) with { Importer = await AvaloniaGpuImageImporter.QueryAsync(compositor) };
        }
    }

    public MediaSharedGpuImageRequest CreateRequest() =>
        AvaloniaGpuInterop.CreateRequest(ImageHandleTypes, Synchronization, SemaphoreHandleTypes, DeviceLuid, DeviceUuid);
}

/// <summary>
/// Maps between Avalonia's GPU interop vocabulary (handle-type strings and synchronization
/// capabilities) and Core's neutral shared-image contract. Plain values only: the adapter never
/// touches a graphics API.
/// </summary>
static class AvaloniaGpuInterop
{
    // Preference order: NT handles before legacy global handles (closable, securable), then the
    // portable descriptor kinds.
    static readonly (string Avalonia, MediaSharedGpuImageHandleKind Kind)[] ImageHandleTypes =
    [
        (KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle, MediaSharedGpuImageHandleKind.D3D11TextureNtHandle),
        (KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle),
        (KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor, MediaSharedGpuImageHandleKind.VulkanOpaqueFd),
        (KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle, MediaSharedGpuImageHandleKind.VulkanOpaqueNtHandle),
    ];

    static readonly (string Avalonia, MediaSharedGpuSemaphoreHandleKind Kind)[] SemaphoreHandleTypes =
    [
        (KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor, MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueFd),
        (KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle, MediaSharedGpuSemaphoreHandleKind.VulkanOpaqueNtHandle),
    ];

    internal static MediaSharedGpuImageRequest CreateRequest(
        IReadOnlyList<string> imageHandleTypes,
        Func<string, CompositionGpuImportedImageSynchronizationCapabilities> synchronization,
        IReadOnlyList<string> semaphoreHandleTypes,
        byte[]? deviceLuid,
        byte[]? deviceUuid)
    {
        List<MediaSharedGpuImageCapability> capabilities = [];
        foreach ((var avaloniaType, var kind) in ImageHandleTypes)
        {
            if (!imageHandleTypes.Contains(avaloniaType))
                continue;

            var sync = ToSyncKinds(synchronization(avaloniaType));
            if (sync != MediaSharedGpuImageSyncKinds.None)
                capabilities.Add(new MediaSharedGpuImageCapability(kind, sync));
        }

        return new MediaSharedGpuImageRequest(
            capabilities,
            SemaphoreHandleTypes.Where(type => semaphoreHandleTypes.Contains(type.Avalonia)).Select(static type => type.Kind),
            deviceLuid ?? [],
            deviceUuid ?? []);
    }

    internal static MediaSharedGpuImageSyncKinds ToSyncKinds(CompositionGpuImportedImageSynchronizationCapabilities capabilities)
    {
        var sync = MediaSharedGpuImageSyncKinds.None;
        if ((capabilities & CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex) != 0)
            sync |= MediaSharedGpuImageSyncKinds.KeyedMutex;
        if ((capabilities & CompositionGpuImportedImageSynchronizationCapabilities.Semaphores) != 0)
            sync |= MediaSharedGpuImageSyncKinds.Semaphores;
        // The compositor synchronizes the image itself, so a producer that finished its GPU work
        // before announcing the frame needs nothing more.
        if ((capabilities & CompositionGpuImportedImageSynchronizationCapabilities.Automatic) != 0)
            sync |= MediaSharedGpuImageSyncKinds.HostSynchronized;
        return sync;
    }

    public static string ToAvaloniaImageHandleType(MediaSharedGpuImageHandleKind kind) =>
        ImageHandleTypes.FirstOrDefault(type => type.Kind == kind).Avalonia
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No Avalonia handle type.");

    public static string ToAvaloniaSemaphoreHandleType(MediaSharedGpuSemaphoreHandleKind kind) =>
        SemaphoreHandleTypes.FirstOrDefault(type => type.Kind == kind).Avalonia
        ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "No Avalonia handle type.");

    public static PlatformGraphicsExternalImageFormat ToAvaloniaFormat(MediaSharedGpuImageFormat format) => format switch
    {
        MediaSharedGpuImageFormat.Rgba8Unorm => PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
        _ => PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
    };

    /// <summary>The import description of one image of <paramref name="images"/>.</summary>
    public static PlatformGraphicsExternalImageProperties ToImageProperties(MediaSharedGpuImageSet images, MediaSharedGpuImageBuffer buffer) => new()
    {
        Width = images.Width,
        Height = images.Height,
        Format = ToAvaloniaFormat(images.Format),
        MemorySize = buffer.MemorySize,
        MemoryOffset = buffer.MemoryOffset,
        // Row 0 is the top row in every exporting renderer.
        TopLeftOrigin = true,
        VulkanProperties = images.VulkanImageLayout == 0
            ? null
            : new PlatformGraphicsExternalImageVulkanProperties { Layout = images.VulkanImageLayout },
    };
}
