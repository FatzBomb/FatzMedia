using System.Diagnostics;
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
/// layer into it with nearest filtering, draws the wash, CDG overlay and subtitle raster with SPIR-V pipelines
/// (<see cref="VulkanOverlayPipelines"/>), and reads the result back. Swapchain presentation
/// is a later slice.
/// </summary>
internal sealed unsafe partial class VulkanOffscreenCompositor : IDisposable
{
    private const Format TargetFormat = Format.B8G8R8A8Unorm;

    // Every use of TargetFormat: clear/blit/readback, the render-pass attachment with blending,
    // and the sampled CDG and subtitle images.
    private const FormatFeatureFlags RequiredFormatFeatures =
        FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit
        | FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit
        | FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit
        | FormatFeatureFlags.SampledImageBit;

    private const ImageUsageFlags TransferUsage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

    private readonly Vk _vk;
    private readonly Lock _sync = new();
    // Guards what ProbeDeviceLost reads (the shared device and the Direct3D11 interop device)
    // against Dispose, without the frame lock (ENG-039).
    private readonly Lock _probeGate = new();
    private bool _probeClosed;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private QueueFlags _queueFamilyFlags;
    private uint _instanceApiVersion;
    // The device, shared with FFmpeg when it decodes on it (Stage 045A slice 2).
    private VulkanSharedDevice? _shared;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    // A submission signalling _fence that no wait has seen complete yet.
    private bool _submissionPending;
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
    // The subtitle raster held by _subtitleImage: uploaded only when the frame's subtitle revision
    // or pixel buffer changes (a steady cue is not re-uploaded per frame or per target).
    private ImageResource? _subtitleImage;
    private BufferResource? _subtitleUploadBuffer;
    private long _subtitleUploadedRevision = -1;
    private ReadOnlyMemory<byte> _subtitleUploadedPixels;
    private VkImage _subtitleUploadedImage;
    private BufferResource? _readbackBuffer;
    // The background held by _sourceImage (ENG-026): uploaded only when the frame's revision or
    // pixel buffer changes, not once per target per frame. Revision -1 means "not uploaded".
    private long _backgroundUploadedRevision = -1;
    private ReadOnlyMemory<byte> _backgroundUploadedPixels;
    private VkImage _backgroundUploadedImage;
    private byte[] _output = [];
    private bool _disposed;

    private VulkanOffscreenCompositor(Vk vk) => _vk = vk;

    /// <summary>Gets the selected device's name.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Gets the selected device's type.</summary>
    public PhysicalDeviceType DeviceType { get; private set; }

    /// <summary>Gets how many times a CDG snapshot was expanded and uploaded (tests).</summary>
    internal int CdgUploadCount { get; private set; }

    /// <summary>Gets how many times a subtitle raster was uploaded (tests).</summary>
    internal int SubtitleUploadCount { get; private set; }

    /// <summary>Gets how many times a background was uploaded (tests).</summary>
    internal int BackgroundUploadCount { get; private set; }

    /// <summary>Gets whether the readback buffer lives in host-cached memory (tests and diagnostics).</summary>
    internal bool ReadbackIsHostCached => _readbackBuffer is { } buffer && (buffer.Properties & MemoryPropertyFlags.HostCachedBit) != 0;

    /// <summary>
    /// Creates the compositor, or returns <see langword="null"/> with a reason when no usable
    /// Vulkan device exists (no loader, no ICD, or no device with the BGRA8 features used here).
    /// </summary>
    public static VulkanOffscreenCompositor? TryCreate(out string? failure) => TryCreate(out failure, shareWithFfmpegWithoutDecode: false);

    /// <summary>
    /// As <see cref="TryCreate(out string?)"/>; <paramref name="shareWithFfmpegWithoutDecode"/>
    /// (tests) also gives a device without a video decode queue what FFmpeg needs to share it.
    /// </summary>
    internal static VulkanOffscreenCompositor? TryCreate(out string? failure, bool shareWithFfmpegWithoutDecode)
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

        var compositor = new VulkanOffscreenCompositor(vk) { _shareWithFfmpegWithoutDecode = shareWithFfmpegWithoutDecode };
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
    /// Draws <paramref name="plan"/> (clear, optional background blit, then wash, CDG overlay
    /// and subtitle draws) and returns the target as tightly packed premultiplied BGRA8 rows. The
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
                ? EnsureBuffer(ref _readbackBuffer, (ulong)outputWidth * (ulong)outputHeight * 4, BufferUsageFlags.TransferDstBit, ReadbackMemoryPreferences)
                : null;
            var uploadBackground = false;
            // A hardware-decoded frame, converted into the video layer (Stage 045A slice 2), or the
            // layer kept from the last one.
            var drawVideo = false;
            PendingVideoConversion? conversion = null;
            if (plan.Background is { IsVideo: true } video)
            {
                drawVideo = PrepareVideoLayer(video, out conversion);
            }
            else if (plan.Background is { } background)
            {
                // As for the CDG texture below: an image about to be re-created never counts as
                // holding the last upload, even if the new handle reuses the old value.
                if (_sourceImage is not { } currentSource || currentSource.Width != (uint)background.Width || currentSource.Height != (uint)background.Height)
                    _backgroundUploadedRevision = -1;

                ImageResource source = EnsureImage(ref _sourceImage, (uint)background.Width, (uint)background.Height, TransferUsage);
                uploadBackground = !HoldsBackground(background, source.Image);
                if (uploadBackground)
                {
                    // Forgotten until this frame's submission completes, so a failure re-uploads.
                    _backgroundUploadedRevision = -1;
                    BufferResource upload = EnsureBuffer(ref _uploadBuffer, (ulong)background.Width * (ulong)background.Height * 4, BufferUsageFlags.TransferSrcBit, UploadMemoryPreferences);
                    Upload(upload, background.Bgra.Span, background.Width, background.Height, background.Stride);
                }
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
                BufferResource cdgUpload = EnsureBuffer(ref _cdgUploadBuffer, length, BufferUsageFlags.TransferSrcBit, UploadMemoryPreferences);
                VulkanCompositionPlanner.ExpandCdg(cdgPlan.Snapshot, new Span<byte>((void*)cdgUpload.Mapped, (int)length));
            }

            var uploadSubtitle = false;
            if (plan.Subtitle is { } subtitle)
            {
                // As for the CDG texture: a re-created image never counts as holding the last upload.
                if (_subtitleImage is not { } current || current.Width != (uint)subtitle.Width || current.Height != (uint)subtitle.Height)
                    _subtitleUploadedRevision = -1;

                ImageResource subtitleTexture = EnsureImage(ref _subtitleImage, (uint)subtitle.Width, (uint)subtitle.Height, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit);
                uploadSubtitle = !HoldsSubtitle(subtitle, subtitleTexture.Image);
                if (uploadSubtitle)
                {
                    // Forgotten until this frame's submission completes, so a failure re-uploads.
                    _subtitleUploadedRevision = -1;
                    BufferResource upload = EnsureBuffer(ref _subtitleUploadBuffer, (ulong)subtitle.Width * (ulong)subtitle.Height * 4, BufferUsageFlags.TransferSrcBit, UploadMemoryPreferences);
                    Upload(upload, subtitle.Bgra.Span, subtitle.Width, subtitle.Height, subtitle.Stride);
                }
            }

            VulkanResult.Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            VulkanResult.Check(_vk.BeginCommandBuffer(_commandBuffer, &begin), "vkBeginCommandBuffer");

            // Locks the decoded frame until its semaphore signal is submitted. Recording commands
            // does not throw; ending the buffer and submitting can, so the lock is released below.
            VideoFrameAccess? videoFrame = conversion is { } pending ? RecordVideoConversion(pending) : null;
            VkImage target = targetImage.Image;
            Transition(target, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
            var clearColor = plan.ClearBgraPremultiplied;
            var clear = new ClearColorValue(clearColor.R / 255f, clearColor.G / 255f, clearColor.B / 255f, clearColor.A / 255f);
            var range = ColorRange();
            _vk.CmdClearColorImage(_commandBuffer, target, ImageLayout.TransferDstOptimal, &clear, 1, &range);

            if (plan.Background is { } layer && (!layer.IsVideo || drawVideo))
            {
                VkImage source = layer.IsVideo ? _videoLayer!.Value.Image : _sourceImage!.Value.Image;
                // A background uploaded by an earlier frame (another target, or a repeat of the
                // same video frame) is still in TransferSrcOptimal, and that submission has
                // completed (fence wait); its barrier covers reads by later commands.
                if (uploadBackground)
                {
                    Transition(source, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
                    CopyBufferToImage(_uploadBuffer!.Value, source, layer.Width, layer.Height);
                    Transition(source, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
                }

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

                ImageView subtitleView = default;
                if (plan.Subtitle is { } subtitleLayer)
                {
                    ImageResource subtitleImage = _subtitleImage!.Value;
                    // A reused texture is still in ShaderReadOnlyOptimal from the frame that uploaded it.
                    if (uploadSubtitle)
                    {
                        Transition(subtitleImage.Image, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
                        CopyBufferToImage(_subtitleUploadBuffer!.Value, subtitleImage.Image, subtitleLayer.Width, subtitleLayer.Height);
                        Barrier(
                            subtitleImage.Image,
                            ImageLayout.TransferDstOptimal,
                            ImageLayout.ShaderReadOnlyOptimal,
                            AccessFlags.TransferWriteBit,
                            AccessFlags.ShaderReadBit,
                            PipelineStageFlags.TransferBit,
                            PipelineStageFlags.FragmentShaderBit);
                    }

                    subtitleView = subtitleImage.View;
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
                _overlay!.Record(_commandBuffer, renderTarget.Framebuffer, plan, cdgView, subtitleView);
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

            try
            {
                VulkanResult.Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

                CommandBuffer commandBuffer = _commandBuffer;
                var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &commandBuffer };
                // At most the consumer-release semaphore and a decoded frame's two timeline semaphores.
                var waitSemaphores = stackalloc VkSemaphore[3];
                var waitStages = stackalloc PipelineStageFlags[3];
                var waitValues = stackalloc ulong[3];
                var signalSemaphores = stackalloc VkSemaphore[3];
                var signalValues = stackalloc ulong[3];
                uint waitCount = 0;
                uint signalCount = 0;
                if (export is { } synchronized)
                {
                    // Semaphore sync: wait for the compositor's release of this image before the blit
                    // writes it, and tell the compositor when the write is done. The fence below still
                    // completes the frame before it is announced (host synchronization).
                    if (synchronized.WaitForConsumerRelease && synchronized.Image.ConsumerRelease.Handle != 0)
                    {
                        waitStages[waitCount] = PipelineStageFlags.TransferBit;
                        waitValues[waitCount] = 0;
                        waitSemaphores[waitCount++] = synchronized.Image.ConsumerRelease;
                    }

                    if (synchronized.Image.RenderComplete.Handle != 0)
                    {
                        signalValues[signalCount] = 0;
                        signalSemaphores[signalCount++] = synchronized.Image.RenderComplete;
                    }
                }

                // A decoded frame (FFmpeg's timeline semaphores): wait until it is decoded, and signal
                // the next value once the conversion has read it.
                videoFrame?.AddSemaphores(waitSemaphores, waitStages, waitValues, ref waitCount, signalSemaphores, signalValues, ref signalCount);
                submit.WaitSemaphoreCount = waitCount;
                submit.PWaitSemaphores = waitCount == 0 ? null : waitSemaphores;
                submit.PWaitDstStageMask = waitCount == 0 ? null : waitStages;
                submit.SignalSemaphoreCount = signalCount;
                submit.PSignalSemaphores = signalCount == 0 ? null : signalSemaphores;
                var timeline = new TimelineSemaphoreSubmitInfo
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    WaitSemaphoreValueCount = waitCount,
                    PWaitSemaphoreValues = waitValues,
                    SignalSemaphoreValueCount = signalCount,
                    PSignalSemaphoreValues = signalValues,
                };
                if (videoFrame is not null)
                    submit.PNext = &timeline;

                // A Direct3D11 texture (Windows, Stage 045A slice 3): its keyed mutex orders this
                // write against the compositor's reads.
                Win32KeyedMutexAcquireReleaseInfoKHR keyedMutex;
                DeviceMemory keyedMemory;
                ulong acquireKey;
                ulong releaseKey;
                uint keyedTimeout;
                if (export is { Image.HasKeyedMutex: true } keyed)
                {
                    keyedMemory = keyed.Image.Memory;
                    acquireKey = keyed.AcquireKey;
                    ChainKeyedMutex(&submit, &keyedMutex, &keyedMemory, &acquireKey, &releaseKey, &keyedTimeout);
                }

                try
                {
                    Submit(&submit);
                }
                catch (VulkanException exception) when (export is { Image.HasKeyedMutex: true } && exception.Result == Result.Timeout)
                {
                    // The ring hands out only textures the compositor released, so a keyed mutex
                    // that stays taken means the compositor stopped (ENG-039: a GPU reset).
                    throw new VulkanException(
                        $"vkQueueSubmit (the compositor did not release the shared texture's keyed mutex within {KeyedMutexTimeoutMilliseconds} ms)",
                        exception.Result)
                    {
                        IsConsumerStall = true,
                    };
                }

                videoFrame?.MarkSubmitted();
            }
            finally
            {
                // FFmpeg may use the decoded frame again as soon as its semaphore signal is queued.
                videoFrame?.EndAccess();
            }

            WaitForSubmission("vkWaitForFences (frame)");
            if (conversion is { } converted)
                CompleteVideoConversion(converted);
            if (uploadCdg && plan.Cdg is { } uploaded)
            {
                _cdgUploadedSnapshot = uploaded.Snapshot;
                _cdgUploadedImage = _cdgImage!.Value.Image;
                CdgUploadCount++;
            }

            if (uploadSubtitle && plan.Subtitle is { } uploadedSubtitle)
            {
                _subtitleUploadedRevision = uploadedSubtitle.Revision;
                _subtitleUploadedPixels = uploadedSubtitle.Bgra;
                _subtitleUploadedImage = _subtitleImage!.Value.Image;
                SubtitleUploadCount++;
            }

            if (uploadBackground && plan.Background is { } uploadedBackground)
            {
                _backgroundUploadedRevision = uploadedBackground.Revision;
                _backgroundUploadedPixels = uploadedBackground.Bgra;
                _backgroundUploadedImage = _sourceImage!.Value.Image;
                BackgroundUploadCount++;
            }

            if (export is not null)
                return [];

            // Grown only, so targets of different sizes rendering in turn do not reallocate it.
            var outputLength = checked(outputWidth * outputHeight * 4);
            if (_output.Length < outputLength)
                _output = new byte[outputLength];

            ReadBack(readbackBuffer!.Value, _output.AsSpan(0, outputLength));
            return _output.AsSpan(0, outputLength);
        }
    }

    /// <summary>
    /// Whether <see cref="_sourceImage"/> already holds <paramref name="background"/>: the same
    /// revision (Core bumps it whenever the background changes; a negative one is unknown and
    /// always uploads) and the same pixel buffer, uploaded into this image.
    /// </summary>
    private bool HoldsBackground(in VulkanBackgroundLayer background, VkImage image) =>
        background.Revision >= 0
        && background.Revision == _backgroundUploadedRevision
        && background.Bgra.Equals(_backgroundUploadedPixels)
        && image.Handle == _backgroundUploadedImage.Handle;

    /// <summary>
    /// Whether <see cref="_subtitleImage"/> already holds <paramref name="subtitle"/>: the same
    /// known revision and pixel buffer, uploaded into this image.
    /// </summary>
    private bool HoldsSubtitle(in VulkanSubtitleLayer subtitle, VkImage image) =>
        subtitle.Revision > 0
        && subtitle.Revision == _subtitleUploadedRevision
        && subtitle.Bgra.Equals(_subtitleUploadedPixels)
        && image.Handle == _subtitleUploadedImage.Handle;

    /// <summary>
    /// Copies a finished readback into <paramref name="destination"/>. Host-cached memory that is
    /// not coherent is invalidated first, so the CPU sees the GPU's writes.
    /// </summary>
    private void ReadBack(BufferResource buffer, Span<byte> destination)
    {
        if ((buffer.Properties & MemoryPropertyFlags.HostCoherentBit) == 0)
        {
            var range = new MappedMemoryRange { SType = StructureType.MappedMemoryRange, Memory = buffer.Memory, Offset = 0, Size = Vk.WholeSize };
            VulkanResult.Check(_vk.InvalidateMappedMemoryRanges(_device, 1, &range), "vkInvalidateMappedMemoryRanges");
        }

        new ReadOnlySpan<byte>((void*)buffer.Mapped, destination.Length).CopyTo(destination);
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
            // A probe in progress finishes first; later ones find the compositor closed.
            lock (_probeGate)
                _probeClosed = true;

            // A device the driver reported lost may never go idle (ENG-039), and destroying its
            // objects is allowed; one that only timed out is waited for (a slow GPU still uses them),
            // but only for this compositor's own submission and for a bounded time (ENG-050).
            if (_device.Handle != 0 && _shared?.IsLossConfirmed != true)
            {
                if (_shared?.IsLost == true)
                {
                    WaitForPendingSubmissionBounded();
                }
                else
                {
                    var start = Stopwatch.GetTimestamp();
                    lock (QueueLock)
                        _ = _vk.DeviceWaitIdle(_device);
                    VulkanStallMeter.Report(start, "vkDeviceWaitIdle (close)");
                }
            }

            if (_device.Handle != 0)
            {
                DestroyVideoResources();
                DestroyExportedSets();
                DestroyWin32ExportResources();
                DestroyRenderTargets();
                _overlay?.Dispose();
                _overlay = null;
                Destroy(ref _sourceImage);
                Destroy(ref _cdgImage);
                _cdgUploadedSnapshot = null;
                Destroy(ref _subtitleImage);
                Destroy(ref _subtitleUploadBuffer);
                _subtitleUploadedRevision = -1;
                _subtitleUploadedPixels = default;
                Destroy(ref _uploadBuffer);
                Destroy(ref _cdgUploadBuffer);
                Destroy(ref _readbackBuffer);
                _backgroundUploadedRevision = -1;
                if (_fence.Handle != 0)
                    _vk.DestroyFence(_device, _fence, null);
                if (_commandPool.Handle != 0)
                    _vk.DestroyCommandPool(_device, _commandPool, null);
            }

            if (_shared is { } shared)
            {
                // The device, instance and loader go with the last reference: an FFmpeg decoder
                // may still decode on them (Stage 045A slice 2).
                _shared = null;
                var start = Stopwatch.GetTimestamp();
                shared.ReleaseFfmpegDeviceContext();
                shared.Release();
                VulkanStallMeter.Report(start, "Vulkan device release (close)");
                return;
            }

            if (_device.Handle != 0)
                _vk.DestroyDevice(_device, null);
            if (_instance.Handle != 0)
                _vk.DestroyInstance(_instance, null);
            _vk.Dispose();
        }
    }

    private bool Initialize(out string? failure)
    {
        failure = null;
        // FFmpeg's Vulkan Video needs a 1.3 instance (Stage 045A slice 2); 1.1 is enough otherwise.
        uint loaderVersion = Vk.Version11;
        try
        {
            if (_vk.EnumerateInstanceVersion(&loaderVersion) != Result.Success)
                loaderVersion = Vk.Version11;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A 1.0 loader has no vkEnumerateInstanceVersion.
            loaderVersion = Vk.Version10;
        }

        _instanceApiVersion = loaderVersion >= Vk.Version13 ? Vk.Version13 : Vk.Version11;
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
                ApiVersion = _instanceApiVersion,
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

        string[] exportExtensions = ProbeExportSupport();
        VideoDecodeSetup? video = ProbeVideoDecodeSupport();
        Result deviceResult = CreateDevice(exportExtensions, video, out Device device, out string[] extensions);
        if (deviceResult != Result.Success && video is not null)
        {
            // Rendering does not depend on Vulkan Video: without it the device decodes nothing.
            VideoDecodeUnavailableReason = $"vkCreateDevice with Vulkan Video failed ({deviceResult})";
            video.Features.Dispose();
            video = null;
            deviceResult = CreateDevice(exportExtensions, null, out device, out extensions);
        }

        if (deviceResult != Result.Success)
        {
            video?.Features.Dispose();
            failure = $"vkCreateDevice failed: {deviceResult}";
            return false;
        }

        _device = device;
        Queue queue;
        _vk.GetDeviceQueue(_device, _queueFamily, 0, &queue);
        _queue = queue;
        _shared = new VulkanSharedDevice(
            _vk,
            _instance,
            _physicalDevice,
            _device,
            _queue,
            _queueFamily,
            _queueFamilyFlags,
            extensions,
            video?.Features,
            video?.Support);
        LoadExportFunctions();

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        CommandPool pool;
        VulkanResult.Check(_vk.CreateCommandPool(_device, &poolInfo, null, &pool), "vkCreateCommandPool");
        _commandPool = pool;

        var allocateInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer commandBuffer;
        VulkanResult.Check(_vk.AllocateCommandBuffers(_device, &allocateInfo, &commandBuffer), "vkAllocateCommandBuffers");
        _commandBuffer = commandBuffer;

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Fence fence;
        VulkanResult.Check(_vk.CreateFence(_device, &fenceInfo, null, &fence), "vkCreateFence");
        _fence = fence;

        _overlay = VulkanOverlayPipelines.Create(_vk, _device, TargetFormat);
        return true;
    }

    /// <summary>
    /// Creates the device with one graphics queue, plus one video decode queue when
    /// <paramref name="video"/> is set (its extensions and features too).
    /// </summary>
    private Result CreateDevice(string[] exportExtensions, VideoDecodeSetup? video, out Device device, out string[] extensions)
    {
        extensions = video is null ? exportExtensions : [.. exportExtensions.Concat(video.Extensions).Distinct(StringComparer.Ordinal)];
        var priority = 1f;
        var queueInfos = stackalloc DeviceQueueCreateInfo[2];
        queueInfos[0] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        uint queueInfoCount = 1;
        if (video is not null && video.Support.DecodeFamily != _queueFamily)
        {
            queueInfos[queueInfoCount++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = video.Support.DecodeFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        var extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        try
        {
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = video is null ? null : video.Features.Features,
                QueueCreateInfoCount = queueInfoCount,
                PQueueCreateInfos = queueInfos,
                EnabledExtensionCount = (uint)extensions.Length,
                PpEnabledExtensionNames = extensions.Length == 0 ? null : extensionNames,
            };
            Device created;
            Result result = _vk.CreateDevice(_physicalDevice, &deviceInfo, null, &created);
            device = created;
            return result;
        }
        finally
        {
            _ = SilkMarshal.Free((nint)extensionNames);
        }
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

        var candidates = new List<(PhysicalDevice Device, uint Family, QueueFlags FamilyFlags, int Rank, string Name, PhysicalDeviceType Type)>();
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

            if (!TryFindGraphicsQueue(device, out var family, out QueueFlags familyFlags))
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
            candidates.Add((device, family, familyFlags, rank, name, properties.DeviceType));
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
        _queueFamilyFlags = selected.FamilyFlags;
        DeviceName = selected.Name;
        DeviceType = selected.Type;
        PhysicalDeviceMemoryProperties memoryProperties;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, &memoryProperties);
        _memoryProperties = memoryProperties;
        failure = null;
        return true;
    }

    private bool TryFindGraphicsQueue(PhysicalDevice device, out uint family, out QueueFlags flags)
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
                flags = families[index].QueueFlags;
                return true;
            }
        }

        family = 0;
        flags = QueueFlags.None;
        return false;
    }

    private static void Upload(BufferResource upload, ReadOnlySpan<byte> sourceBgra, int width, int height, int stride)
    {
        var rowBytes = width * 4;
        var destination = new Span<byte>((void*)upload.Mapped, rowBytes * height);
        for (var y = 0; y < height; y++)
            sourceBgra.Slice(y * stride, rowBytes).CopyTo(destination.Slice(y * rowBytes, rowBytes));
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
            VulkanResult.Check(_vk.CreateImage(_device, &info, null, &handle), "vkCreateImage");
            MemoryRequirements requirements;
            _vk.GetImageMemoryRequirements(_device, handle, &requirements);
            memory = AllocateMemory(requirements.MemoryTypeBits, requirements.Size, ImageMemoryPreferences, null, "vkAllocateMemory", out _);
            VulkanResult.Check(_vk.BindImageMemory(_device, handle, memory, 0), "vkBindImageMemory");
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
                VulkanResult.Check(_vk.CreateImageView(_device, &viewInfo, null, &view), "vkCreateImageView");
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

    /// <summary>Device-local memory for images, or any allowed type when the device has none.</summary>
    private static readonly MemoryPropertyFlags[] ImageMemoryPreferences =
    [
        MemoryPropertyFlags.DeviceLocalBit,
        MemoryPropertyFlags.None,
    ];

    /// <summary>Host-visible memory for buffers the CPU writes and the GPU reads once (uploads).</summary>
    private static readonly MemoryPropertyFlags[] UploadMemoryPreferences =
    [
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
    ];

    /// <summary>
    /// Host-visible memory for the readback buffer the CPU reads every frame (ENG-026): host-cached
    /// first, since uncached (write-combined) memory is very slow to read on discrete GPUs; coherent
    /// when possible, otherwise invalidated before each read; plain coherent memory as the fallback.
    /// </summary>
    internal static readonly MemoryPropertyFlags[] ReadbackMemoryPreferences =
    [
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit,
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
    ];

    /// <summary>
    /// The first memory type allowed by <paramref name="typeBits"/> that has every flag of the
    /// earliest satisfiable entry of <paramref name="preferences"/>, or -1.
    /// </summary>
    internal static int SelectMemoryType(ReadOnlySpan<MemoryPropertyFlags> typeFlags, uint typeBits, ReadOnlySpan<MemoryPropertyFlags> preferences)
    {
        foreach (MemoryPropertyFlags wanted in preferences)
        {
            for (var index = 0; index < typeFlags.Length && index < 32; index++)
            {
                if ((typeBits & (1u << index)) != 0 && (typeFlags[index] & wanted) == wanted)
                    return index;
            }
        }

        return -1;
    }

    /// <summary>Grows <paramref name="resource"/> when too small, with the same failure safety as <see cref="EnsureImage"/>.</summary>
    private BufferResource EnsureBuffer(ref BufferResource? resource, ulong size, BufferUsageFlags usage, MemoryPropertyFlags[] memoryPreferences)
    {
        if (resource is { } buffer && buffer.Size >= size)
            return buffer;

        Destroy(ref resource);
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        VkBuffer handle = default;
        DeviceMemory memory = default;
        try
        {
            VulkanResult.Check(_vk.CreateBuffer(_device, &info, null, &handle), "vkCreateBuffer");
            MemoryRequirements requirements;
            _vk.GetBufferMemoryRequirements(_device, handle, &requirements);
            memory = AllocateMemory(requirements.MemoryTypeBits, requirements.Size, memoryPreferences, null, "vkAllocateMemory", out MemoryPropertyFlags properties);
            VulkanResult.Check(_vk.BindBufferMemory(_device, handle, memory, 0), "vkBindBufferMemory");
            // Mapped for its lifetime (ENG-039 P5-10): every buffer here is host-visible, and
            // freeing the memory unmaps it.
            void* mapped;
            VulkanResult.Check(_vk.MapMemory(_device, memory, 0, Vk.WholeSize, 0, &mapped), "vkMapMemory");
            BufferResource created = new(handle, memory, size, properties, (nint)mapped);
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

    /// <summary>
    /// Allocates <paramref name="size"/> bytes from the first memory type allowed by
    /// <paramref name="typeBits"/> that satisfies the earliest entry of
    /// <paramref name="preferences"/> it can, chaining <paramref name="next"/> (dedicated, export or
    /// import info). <paramref name="operation"/> names the step in a failure.
    /// </summary>
    private DeviceMemory AllocateMemory(
        uint typeBits,
        ulong size,
        ReadOnlySpan<MemoryPropertyFlags> preferences,
        void* next,
        string operation,
        out MemoryPropertyFlags properties)
    {
        Span<MemoryPropertyFlags> typeFlags = stackalloc MemoryPropertyFlags[32];
        typeFlags = typeFlags[..GetMemoryTypeFlags(typeFlags)];
        var typeIndex = SelectMemoryType(typeFlags, typeBits, preferences);
        if (typeIndex < 0)
        {
            // The image preferences end in None (any allowed type), which names nothing useful.
            var wanted = preferences[^1] == MemoryPropertyFlags.None ? "the resource's allowed types" : preferences[^1].ToString();
            throw new VulkanException($"No memory type satisfies {wanted}; {operation}", Result.ErrorOutOfDeviceMemory);
        }

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            PNext = next,
            AllocationSize = size,
            MemoryTypeIndex = (uint)typeIndex,
        };
        DeviceMemory memory;
        VulkanResult.Check(_vk.AllocateMemory(_device, &allocateInfo, null, &memory), operation);
        properties = typeFlags[typeIndex];
        return memory;
    }

    /// <summary>Writes the selected device's memory type flags into <paramref name="destination"/> (32 entries) and returns the count.</summary>
    private int GetMemoryTypeFlags(Span<MemoryPropertyFlags> destination)
    {
        var count = (int)Math.Min(_memoryProperties.MemoryTypeCount, (uint)destination.Length);
        for (var index = 0; index < count; index++)
            destination[index] = _memoryProperties.MemoryTypes[index].PropertyFlags;

        return count;
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
        PipelineStageFlags dstStage) =>
        Barrier(image, from, to, srcAccess, dstAccess, srcStage, dstStage, ColorRange());

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
        _vk.UnmapMemory(_device, buffer.Memory);
        _vk.FreeMemory(_device, buffer.Memory, null);
        resource = null;
    }

    /// <summary>The lock every submission to this device's queues takes (FFmpeg's too).</summary>
    private Lock QueueLock => _shared?.QueueLock ?? _sync;

    /// <summary>Submits to the graphics queue under <see cref="QueueLock"/>, signalling <see cref="_fence"/>.</summary>
    private void Submit(SubmitInfo* submit)
    {
        Fence fence = _fence;
        VulkanResult.Check(_vk.ResetFences(_device, 1, &fence), "vkResetFences");
        // Measured with the queue lock: FFmpeg's submissions and a keyed-mutex acquire both wait here.
        var start = Stopwatch.GetTimestamp();
        Result result;
        lock (QueueLock)
            result = _vk.QueueSubmit(_queue, 1, submit, _fence);
        VulkanStallMeter.Report(start, "vkQueueSubmit");
        VulkanResult.Check(result, "vkQueueSubmit");
        _submissionPending = true;
    }

    /// <summary>How long closing waits for the last submission on a device that stopped completing work.</summary>
    internal static readonly TimeSpan LostDeviceCloseWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Closing a device that stopped completing work but that the driver has not reported lost
    /// (ENG-050). <c>vkDeviceWaitIdle</c> has no timeout: after an NVIDIA GPU reset it blocked
    /// recovery for about 14 s, until the driver finished the reset. Each <see cref="Submit"/> is
    /// waited for, so only this compositor's last submission can still be in flight; it is waited
    /// for up to <see cref="LostDeviceCloseWait"/>, and the loss counts as confirmed as soon as the
    /// driver reports it. Windows resets a GPU that made no progress for 2 s, so work still pending
    /// after the 2 s fence timeout and this wait is on a device being reset, whose objects may be
    /// destroyed. FFmpeg waits for its own submissions.
    /// </summary>
    private void WaitForPendingSubmissionBounded()
    {
        if (!_submissionPending || _fence.Handle == 0)
            return;

        Fence fence = _fence;
        var start = Stopwatch.GetTimestamp();
        Result result = _vk.WaitForFences(_device, 1, &fence, true, (ulong)LostDeviceCloseWait.Ticks * 100);
        VulkanStallMeter.Report(start, "vkWaitForFences (close, device lost)");
        if (result == Result.Timeout)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Vulkan", $"The last submission did not complete within {LostDeviceCloseWait.TotalSeconds:0} s more; the lost device is closed without waiting for it.");
        else if (result == Result.ErrorDeviceLost)
            _shared?.MarkLost("vkWaitForFences reported VK_ERROR_DEVICE_LOST while closing");
        _submissionPending = false;
    }

    /// <summary>How long a submission may take before the device counts as lost (ns): the Windows GPU-reset interval.</summary>
    internal const ulong FenceTimeoutNanoseconds = 2_000_000_000;

    /// <summary>
    /// Waits for the last <see cref="Submit"/>. Offscreen composition that has not finished in 2 s
    /// means the GPU stopped: the device is marked lost and the wait throws as a device loss
    /// (ENG-039), instead of blocking the presentation thread for good.
    /// </summary>
    private void WaitForSubmission(string operation)
    {
        Fence fence = _fence;
        var start = Stopwatch.GetTimestamp();
        Result result = _vk.WaitForFences(_device, 1, &fence, true, FenceTimeoutNanoseconds);
        VulkanStallMeter.Report(start, operation);
        if (result != Result.Timeout)
            _submissionPending = false;
        if (result == Result.Timeout)
        {
            _shared?.MarkLost($"{operation} did not complete within 2 s", confirmed: false);
            throw new VulkanException($"{operation} (not completed within 2 s)", Result.ErrorDeviceLost);
        }

        VulkanResult.Check(result, operation);
    }

    /// <summary>
    /// Whether the device is lost (ENG-039): marked lost, the Direct3D11 device the Windows export
    /// shares textures through was removed, or the heartbeat fails (see
    /// <see cref="VulkanSharedDevice.Heartbeat"/>). Takes no frame lock and waits at most about
    /// 350 ms; false once disposed.
    /// </summary>
    public bool ProbeDeviceLost(out string? reason)
    {
        reason = null;
        if (!_probeGate.TryEnter(ProbeGateTimeout))
            return false;

        try
        {
            if (_probeClosed || _shared is not { } shared)
                return false;

            if (OperatingSystem.IsWindows() && _d3d11Device != 0)
            {
                var removed = VulkanD3D11Interop.GetDeviceRemovedReason(_d3d11Device);
                if (removed < 0)
                {
                    shared.MarkLost($"the Direct3D11 device that shares textures with the compositor was removed (0x{removed:X8})");
                    reason = shared.LostReason;
                    return true;
                }
            }

            return shared.Heartbeat(out reason);
        }
        finally
        {
            _probeGate.Exit();
        }
    }

    private static readonly TimeSpan ProbeGateTimeout = TimeSpan.FromMilliseconds(500);

    private static ImageSubresourceRange ColorRange() => new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private static ImageSubresourceLayers ColorLayers() => new(ImageAspectFlags.ColorBit, 0, 0, 1);

    private readonly record struct ImageResource(VkImage Image, DeviceMemory Memory, ImageView View, uint Width, uint Height);

    /// <summary>A host-visible buffer, persistently mapped at <see cref="Mapped"/>.</summary>
    private readonly record struct BufferResource(VkBuffer Buffer, DeviceMemory Memory, ulong Size, MemoryPropertyFlags Properties, nint Mapped);

    private readonly record struct RenderTarget(ImageResource Image, Framebuffer Framebuffer);
}

/// <summary>A failed Vulkan call. <see cref="IsDeviceLost"/> marks recoverable device loss.</summary>
internal sealed class VulkanException(string operation, Result result)
    : InvalidOperationException($"{operation} failed: {result}")
{
    public Result Result { get; } = result;

    public bool IsDeviceLost => Result == Result.ErrorDeviceLost;

    /// <summary>Gets whether an allocation failed (after a GPU reset, the first failure NVIDIA reports).</summary>
    public bool IsOutOfMemory => Result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory;

    /// <summary>Gets whether the compositor did not give a shared image back in time (its keyed mutex stayed taken).</summary>
    public bool IsConsumerStall { get; init; }
}
