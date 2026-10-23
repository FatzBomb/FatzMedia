#pragma warning disable IDE0130
using System.Collections.ObjectModel;

namespace FatzBomb.MediaControl;

/// <summary>
/// How the memory of an exported shared GPU image is handed to the consumer (Stage 027A slice 2).
/// The values are plain OS handles; neither side needs the other's GPU API types.
/// </summary>
public enum MediaSharedGpuImageHandleKind
{
    None = 0,

    /// <summary>
    /// DXGI shared NT handle of a Direct3D11 texture (<c>IDXGIResource1::CreateSharedHandle</c>).
    /// Owned by the producer; consumers open (duplicate) it and never close it.
    /// </summary>
    D3D11TextureNtHandle = 1,

    /// <summary>
    /// Legacy (KMT) DXGI shared handle of a Direct3D11 texture (<c>IDXGIResource::GetSharedHandle</c>).
    /// Not a real kernel handle; nothing is closed.
    /// </summary>
    D3D11TextureGlobalSharedHandle = 2,

    /// <summary>
    /// Opaque POSIX file descriptor of Vulkan device memory (<c>VK_KHR_external_memory_fd</c>).
    /// Importing it transfers ownership of the descriptor to the importer.
    /// </summary>
    VulkanOpaqueFd = 3,

    /// <summary>Opaque Win32 NT handle of Vulkan device memory (<c>VK_KHR_external_memory_win32</c>). Producer-owned.</summary>
    VulkanOpaqueNtHandle = 4,
}

/// <summary>How producer and consumer take turns on a shared image.</summary>
[Flags]
public enum MediaSharedGpuImageSyncKinds
{
    None = 0,

    /// <summary>
    /// A DXGI keyed mutex on the image: the producer acquires <see cref="MediaSharedGpuImageRing.ProducerKey"/>
    /// and releases <see cref="MediaSharedGpuImageRing.ConsumerKey"/>; the consumer does the reverse.
    /// </summary>
    KeyedMutex = 1,

    /// <summary>
    /// Two exported binary semaphores per image: the producer signals
    /// <see cref="MediaSharedGpuImageBuffer.RenderCompleteSemaphore"/> when its writes are submitted and
    /// the consumer waits on it; the consumer signals <see cref="MediaSharedGpuImageBuffer.ConsumerReleaseSemaphore"/>
    /// when it has read the image and the producer waits on it before writing that image again.
    /// </summary>
    Semaphores = 2,

    /// <summary>
    /// No GPU primitive: the producer finishes its GPU work before announcing a frame, and the
    /// consumer's <see cref="MediaSharedGpuImageSet.ReleaseFrame"/> call ends its use.
    /// </summary>
    HostSynchronized = 4,
}

/// <summary>How the semaphores of <see cref="MediaSharedGpuImageSyncKinds.Semaphores"/> are shared.</summary>
public enum MediaSharedGpuSemaphoreHandleKind
{
    None = 0,

    /// <summary>Opaque POSIX file descriptor (<c>VK_KHR_external_semaphore_fd</c>); importing it transfers ownership.</summary>
    VulkanOpaqueFd = 1,

    /// <summary>Opaque Win32 NT handle (<c>VK_KHR_external_semaphore_win32</c>). Producer-owned.</summary>
    VulkanOpaqueNtHandle = 2,
}

/// <summary>Pixel format of an exported image. Alpha is always premultiplied.</summary>
public enum MediaSharedGpuImageFormat
{
    Bgra8Unorm = 0,
    Rgba8Unorm = 1,
}

/// <summary>One image handle kind a consumer can import, with the synchronization it supports for it.</summary>
public readonly record struct MediaSharedGpuImageCapability(
    MediaSharedGpuImageHandleKind HandleKind,
    MediaSharedGpuImageSyncKinds SyncKinds);

/// <summary>The handle kind and synchronization a producer chose for one image set.</summary>
public readonly record struct MediaSharedGpuImageSelection(
    MediaSharedGpuImageHandleKind HandleKind,
    MediaSharedGpuImageSyncKinds SyncKind,
    MediaSharedGpuSemaphoreHandleKind SemaphoreHandleKind)
{
    public override string ToString() => SyncKind == MediaSharedGpuImageSyncKinds.Semaphores
        ? $"{HandleKind}, {SyncKind} ({SemaphoreHandleKind})"
        : $"{HandleKind}, {SyncKind}";
}

/// <summary>
/// What a shared-image consumer (a UI compositor) can import: image handle kinds in its order of
/// preference, the semaphore handle kinds it can import, and the identity of its GPU so a
/// producer on another adapter declines instead of exporting images the consumer cannot open.
/// </summary>
public sealed class MediaSharedGpuImageRequest
{
    private readonly ReadOnlyCollection<MediaSharedGpuImageCapability> _imageCapabilities;
    private readonly ReadOnlyCollection<MediaSharedGpuSemaphoreHandleKind> _semaphoreHandleKinds;

    public MediaSharedGpuImageRequest(
        IEnumerable<MediaSharedGpuImageCapability> imageCapabilities,
        IEnumerable<MediaSharedGpuSemaphoreHandleKind>? semaphoreHandleKinds = null,
        ReadOnlyMemory<byte> deviceLuid = default,
        ReadOnlyMemory<byte> deviceUuid = default)
    {
        ArgumentNullException.ThrowIfNull(imageCapabilities);
        _imageCapabilities = Array.AsReadOnly<MediaSharedGpuImageCapability>(
            [.. imageCapabilities.Where(static c => c.HandleKind != MediaSharedGpuImageHandleKind.None && c.SyncKinds != MediaSharedGpuImageSyncKinds.None)]);
        _semaphoreHandleKinds = Array.AsReadOnly<MediaSharedGpuSemaphoreHandleKind>(
            [.. (semaphoreHandleKinds ?? []).Where(static k => k != MediaSharedGpuSemaphoreHandleKind.None).Distinct()]);
        DeviceLuid = deviceLuid.ToArray();
        DeviceUuid = deviceUuid.ToArray();
    }

    /// <summary>Gets the importable image handle kinds, most preferred first.</summary>
    public IReadOnlyList<MediaSharedGpuImageCapability> ImageCapabilities => _imageCapabilities;

    /// <summary>Gets the importable semaphore handle kinds (needed for <see cref="MediaSharedGpuImageSyncKinds.Semaphores"/>).</summary>
    public IReadOnlyList<MediaSharedGpuSemaphoreHandleKind> SemaphoreHandleKinds => _semaphoreHandleKinds;

    /// <summary>Gets the consumer GPU's 8-byte adapter LUID, or empty when unknown.</summary>
    public ReadOnlyMemory<byte> DeviceLuid { get; }

    /// <summary>Gets the consumer GPU's 16-byte device UUID, or empty when unknown.</summary>
    public ReadOnlyMemory<byte> DeviceUuid { get; }

    /// <summary>Gets whether the consumer can import anything.</summary>
    public bool IsValid => _imageCapabilities.Count > 0;

    /// <summary>
    /// Chooses the first consumer-preferred handle kind the producer can export, with the first
    /// common synchronization in the order keyed mutex, semaphores (when a semaphore handle kind
    /// is common too), host-synchronized. Returns <see langword="false"/> with a reason otherwise.
    /// </summary>
    public bool TrySelect(
        IEnumerable<MediaSharedGpuImageCapability> producerCapabilities,
        IEnumerable<MediaSharedGpuSemaphoreHandleKind> producerSemaphoreHandleKinds,
        out MediaSharedGpuImageSelection selection,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(producerCapabilities);
        ArgumentNullException.ThrowIfNull(producerSemaphoreHandleKinds);
        MediaSharedGpuImageCapability[] producer = [.. producerCapabilities];
        MediaSharedGpuSemaphoreHandleKind semaphoreKind = _semaphoreHandleKinds
            .FirstOrDefault(kind => producerSemaphoreHandleKinds.Contains(kind));

        var handleMatched = false;
        foreach (MediaSharedGpuImageCapability wanted in _imageCapabilities)
        {
            foreach (MediaSharedGpuImageCapability offered in producer)
            {
                if (offered.HandleKind != wanted.HandleKind)
                    continue;

                handleMatched = true;
                MediaSharedGpuImageSyncKinds common = wanted.SyncKinds & offered.SyncKinds;
                if ((common & MediaSharedGpuImageSyncKinds.KeyedMutex) != 0)
                    selection = new(wanted.HandleKind, MediaSharedGpuImageSyncKinds.KeyedMutex, MediaSharedGpuSemaphoreHandleKind.None);
                else if ((common & MediaSharedGpuImageSyncKinds.Semaphores) != 0 && semaphoreKind != MediaSharedGpuSemaphoreHandleKind.None)
                    selection = new(wanted.HandleKind, MediaSharedGpuImageSyncKinds.Semaphores, semaphoreKind);
                else if ((common & MediaSharedGpuImageSyncKinds.HostSynchronized) != 0)
                    selection = new(wanted.HandleKind, MediaSharedGpuImageSyncKinds.HostSynchronized, MediaSharedGpuSemaphoreHandleKind.None);
                else
                    continue;

                reason = null;
                return true;
            }
        }

        selection = default;
        reason = !IsValid
            ? "the consumer offers no importable image handle kind"
            : handleMatched
                ? $"no common synchronization (consumer: {Describe(_imageCapabilities)}, semaphores {DescribeKinds(_semaphoreHandleKinds)}; "
                    + $"producer: {Describe(producer)}, semaphores {DescribeKinds(producerSemaphoreHandleKinds)})"
                : $"no common image handle kind (consumer: {Describe(_imageCapabilities)}; producer: {Describe(producer)})";
        return false;
    }

    /// <summary>
    /// Whether a producer GPU identified by <paramref name="luid"/> and <paramref name="uuid"/>
    /// (either may be empty when unknown) can be the consumer's GPU. Identities are compared only
    /// when both sides know them.
    /// </summary>
    public bool MatchesDevice(ReadOnlySpan<byte> luid, ReadOnlySpan<byte> uuid, out string? reason)
    {
        if (!DeviceLuid.IsEmpty && !luid.IsEmpty && !DeviceLuid.Span.SequenceEqual(luid))
        {
            reason = $"the renderer GPU (LUID {Convert.ToHexString(luid)}) is not the compositor GPU (LUID {Convert.ToHexString(DeviceLuid.Span)})";
            return false;
        }

        if (!DeviceUuid.IsEmpty && !uuid.IsEmpty && !DeviceUuid.Span.SequenceEqual(uuid))
        {
            reason = $"the renderer GPU (UUID {Convert.ToHexString(uuid)}) is not the compositor GPU (UUID {Convert.ToHexString(DeviceUuid.Span)})";
            return false;
        }

        reason = null;
        return true;
    }

    public override string ToString() => $"{Describe(_imageCapabilities)}; semaphores {DescribeKinds(_semaphoreHandleKinds)}";

    private static string Describe(IEnumerable<MediaSharedGpuImageCapability> capabilities)
    {
        var text = string.Join(", ", capabilities.Select(static c => $"{c.HandleKind} [{c.SyncKinds}]"));
        return text.Length == 0 ? "none" : text;
    }

    private static string DescribeKinds(IEnumerable<MediaSharedGpuSemaphoreHandleKind> kinds)
    {
        var text = string.Join(", ", kinds);
        return text.Length == 0 ? "none" : text;
    }
}

/// <summary>
/// One exported image: its memory handle (and, for Vulkan, the dedicated allocation size and
/// offset an importer needs) and, with <see cref="MediaSharedGpuImageSyncKinds.Semaphores"/>, its
/// two semaphore handles.
/// </summary>
public readonly record struct MediaSharedGpuImageBuffer(
    nint ImageHandle,
    ulong MemorySize = 0,
    ulong MemoryOffset = 0,
    nint RenderCompleteSemaphore = 0,
    nint ConsumerReleaseSemaphore = 0);

/// <summary>
/// A frame the producer finished in <see cref="BufferIndex"/> of the set with
/// <see cref="Generation"/>. With a keyed mutex the consumer acquires <see cref="AcquireKey"/>
/// and releases <see cref="ReleaseKey"/>.
/// </summary>
public readonly record struct MediaSharedGpuImageFrame(
    long Generation,
    int BufferIndex,
    long FrameNumber,
    ulong AcquireKey,
    ulong ReleaseKey);

/// <summary>
/// A set of exported images (double-buffered) created by the renderer and handed to the consumer
/// through <see cref="IMediaSharedGpuImageConsumer.OnImagesCreated"/>. A newer set replaces it.
/// </summary>
/// <remarks>
/// <para>Handle lifetime: the producer keeps every handle valid until both it has retired the set
/// (<see cref="Retire"/>) and the consumer has called <see cref="CompleteImport"/> exactly once.
/// Handle kinds whose import transfers ownership (POSIX file descriptors) are then closed by the
/// producer only when the consumer reports that it did not pass them to an importer.</para>
/// <para>Frames: the consumer submits every frame it receives, in order, and calls
/// <see cref="ReleaseFrame"/> once per frame when the compositor no longer reads the image.</para>
/// </remarks>
public sealed class MediaSharedGpuImageSet
{
    private readonly ReadOnlyCollection<MediaSharedGpuImageBuffer> _buffers;
    private readonly Action<MediaSharedGpuImageFrame, bool> _releaseFrame;
    private readonly Action<bool>? _releaseHandles;
    private int _outstanding = 2;
    private int _handlesConsumed;
    private int _importCompleted;
    private int _retired;

    /// <param name="generation">Non-decreasing set number; frames carry it.</param>
    /// <param name="selection">The handle kind and synchronization of every image.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="format">Pixel format (premultiplied alpha).</param>
    /// <param name="buffers">The images, at least one.</param>
    /// <param name="releaseFrame">Producer callback for <see cref="ReleaseFrame"/> (frame, consumed).</param>
    /// <param name="releaseHandles">
    /// Producer callback run once both sides are done with the set; its argument says whether the
    /// consumer passed the handles to an importer (so transferred handles must not be closed).
    /// </param>
    /// <param name="vulkanImageLayout">The <c>VkImageLayout</c> each image is left in after a frame (0 for other APIs).</param>
    public MediaSharedGpuImageSet(
        long generation,
        MediaSharedGpuImageSelection selection,
        int width,
        int height,
        MediaSharedGpuImageFormat format,
        IEnumerable<MediaSharedGpuImageBuffer> buffers,
        Action<MediaSharedGpuImageFrame, bool> releaseFrame,
        Action<bool>? releaseHandles = null,
        int vulkanImageLayout = 0)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _releaseFrame = releaseFrame ?? throw new ArgumentNullException(nameof(releaseFrame));
        _releaseHandles = releaseHandles;
        _buffers = Array.AsReadOnly<MediaSharedGpuImageBuffer>([.. buffers]);
        if (_buffers.Count == 0)
            throw new ArgumentException("At least one image is required.", nameof(buffers));

        Generation = generation;
        Selection = selection;
        Width = width;
        Height = height;
        Format = format;
        VulkanImageLayout = vulkanImageLayout;
    }

    public long Generation { get; }
    public MediaSharedGpuImageSelection Selection { get; }
    public MediaSharedGpuImageHandleKind HandleKind => Selection.HandleKind;
    public MediaSharedGpuImageSyncKinds SyncKind => Selection.SyncKind;
    public MediaSharedGpuSemaphoreHandleKind SemaphoreHandleKind => Selection.SemaphoreHandleKind;
    public int Width { get; }
    public int Height { get; }
    public MediaSharedGpuImageFormat Format { get; }

    /// <summary>Gets the <c>VkImageLayout</c> the images are in when a frame is announced (0 for Direct3D).</summary>
    public int VulkanImageLayout { get; }

    public IReadOnlyList<MediaSharedGpuImageBuffer> Buffers => _buffers;

    /// <summary>Gets whether the consumer called <see cref="CompleteImport"/> and the handles went to an importer.</summary>
    public bool HandlesConsumed => Volatile.Read(ref _handlesConsumed) != 0;

    /// <summary>Gets whether the producer retired the set.</summary>
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    /// <summary>Consumer: the compositor no longer reads the frame's image. <paramref name="consumed"/> is false when it never used it.</summary>
    public void ReleaseFrame(MediaSharedGpuImageFrame frame, bool consumed) => _releaseFrame(frame, consumed);

    /// <summary>
    /// Consumer, exactly once per set it received: it is done opening the handles.
    /// <paramref name="handlesConsumed"/> is true when it passed them to an importer (even if an
    /// import then failed), false when it skipped the set.
    /// </summary>
    public void CompleteImport(bool handlesConsumed)
    {
        if (Interlocked.Exchange(ref _importCompleted, 1) != 0)
            return;

        Volatile.Write(ref _handlesConsumed, handlesConsumed ? 1 : 0);
        Settle();
    }

    /// <summary>Producer: the set is replaced or its target detached; no more frames come from it.</summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0)
            Settle();
    }

    public override string ToString() => $"#{Generation} {Width}x{Height} {Format} x{_buffers.Count}: {Selection}";

    private void Settle()
    {
        if (Interlocked.Decrement(ref _outstanding) == 0)
            _releaseHandles?.Invoke(HandlesConsumed);
    }
}

/// <summary>
/// Implemented by a UI adapter that shows renderer frames as shared GPU images in its own
/// compositor (Stage 027A slice 2). The renderer calls it from its presentation thread (or, for
/// attach and resize, from the caller's thread); implementations must not block.
/// </summary>
public interface IMediaSharedGpuImageConsumer
{
    /// <summary>Gets what the consumer can import.</summary>
    MediaSharedGpuImageRequest Request { get; }

    /// <summary>A new image set replaces the previous one (first attach, resize, device re-creation).</summary>
    void OnImagesCreated(MediaSharedGpuImageSet images);

    /// <summary>A finished frame is ready in one image of the current set.</summary>
    void OnFrameReady(MediaSharedGpuImageFrame frame);

    /// <summary>
    /// The renderer cannot present into shared images for this consumer (no common handle kind,
    /// another GPU, export failure). The consumer should switch to another surface kind.
    /// </summary>
    void OnUnavailable(string reason);
}
