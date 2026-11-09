using FatzMedia.Video.Core.Graphics;
using FFmpeg.AutoGen;
using Silk.NET.Vulkan;
using VkImage = Silk.NET.Vulkan.Image;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// Vulkan Video (Stage 045A slice 2): the device is created with a video decode queue when it has
/// one, and FFmpeg decodes on it. A decoded frame (one multi-planar NV12 or P010 image, or one
/// image per plane) is read through per-plane views by <see cref="VulkanVideoPipeline"/> into the
/// video layer, a BGRA image at the display size, which is then composed like an uploaded software
/// frame and kept as the retained background. FFmpeg's timeline semaphores order the read after the
/// decode, and its frame lock guards the frame's layout and semaphore value.
/// </summary>
internal sealed unsafe partial class VulkanOffscreenCompositor
{
    private const string VideoQueueExtension = "VK_KHR_video_queue";
    private const string VideoDecodeQueueExtension = "VK_KHR_video_decode_queue";
    private const string VideoMaintenance1Extension = "VK_KHR_video_maintenance1";
    private const string PushDescriptorExtension = "VK_KHR_push_descriptor";

    private static readonly (string Extension, VideoCodecOperationFlagsKHR Operation)[] DecodeCodecExtensions =
    [
        ("VK_KHR_video_decode_h264", VideoCodecOperationFlagsKHR.DecodeH264BitKhr),
        ("VK_KHR_video_decode_h265", VideoCodecOperationFlagsKHR.DecodeH265BitKhr),
        ("VK_KHR_video_decode_av1", VideoCodecOperationFlagsKHR.DecodeAV1BitKhr),
        ("VK_KHR_video_decode_vp9", VideoCodecOperationFlagsKHR.DecodeVP9BitKhr),
    ];

    private VulkanVideoPipeline? _videoPipeline;
    private ImageResource? _videoLayer;
    private Framebuffer _videoFramebuffer;
    private BufferResource? _videoConstants;
    private long _videoConvertedRevision = -1;
    private nint _videoConvertedFrame;
    private bool _videoLayerValid;
    // Plane views of decoded images, kept per image (ENG-039 P5-10): FFmpeg's frame pool hands
    // the same images out again, so a view is created once per image and format, not per frame.
    // The pool's frames context is referenced while views of its images exist, so no image is
    // destroyed (and its handle reused) under a cached view; a frame from another context
    // releases them all.
    private readonly Dictionary<(ulong Image, Format Format, ImageAspectFlags Aspect), ImageView> _planeViews = [];
    private AVBufferRef* _planeViewFrames;
    private readonly VideoFrameAccess _videoFrameAccess = new();
    private string? _reportedVideoFailure;

    /// <summary>
    /// Test support (Stage 045A slice 2): creates the device with the features FFmpeg's Vulkan
    /// context needs even without a video decode queue, so FFmpeg can share it (lavapipe).
    /// </summary>
    private bool _shareWithFfmpegWithoutDecode;

    /// <summary>Gets what the device decodes with Vulkan Video, or null (see <see cref="VideoDecodeUnavailableReason"/>).</summary>
    public VulkanVideoDecodeSupport? VideoDecode =>
        _shared?.VideoDecode is { Codecs: not VideoCodecOperationFlagsKHR.NoneKhr } decode ? decode : null;

    /// <summary>Gets why the device does not decode with Vulkan Video, or null when it does.</summary>
    public string? VideoDecodeUnavailableReason { get; private set; }

    /// <summary>Gets the native <c>VkDevice</c> handle.</summary>
    public nint DeviceHandle => _device.Handle;

    /// <summary>Gets the shared device (tests).</summary>
    internal VulkanSharedDevice? SharedDevice => _shared;

    /// <summary>Gets how many decoded frames were converted into the video layer (tests).</summary>
    internal int VideoConversionCount { get; private set; }

    /// <summary>The most plane views kept; more (an unexpectedly large pool) releases them all.</summary>
    internal const int MaxPlaneViews = 128;

    /// <summary>Gets how many decoded-frame plane views were created (tests).</summary>
    internal int FrameViewCreateCount { get; private set; }

    /// <summary>Gets how many decoded-frame plane views are kept (tests).</summary>
    internal int PlaneViewCount
    {
        get
        {
            lock (_sync)
                return _planeViews.Count;
        }
    }

    /// <summary>Records that the device was lost, for the decoder's device-loss check.</summary>
    public void MarkDeviceLost(string reason) => _shared?.MarkLost(reason);

    /// <summary>
    /// A new reference to an FFmpeg Vulkan device context on this device, or 0 with the reason
    /// (no Vulkan Video, a build without Vulkan, or FFmpeg refused the device).
    /// </summary>
    public nint CreateVideoDecodeDeviceContext(out string? failure)
    {
        if (VideoDecode is null || _shared is null)
        {
            failure = VideoDecodeUnavailableReason ?? "the Vulkan device does not decode video";
            return 0;
        }

        return _shared.CreateFfmpegDeviceContextReference(out failure);
    }

    /// <summary>Whether a decoded frame lives on this compositor's device.</summary>
    public bool IsOnThisDevice(NativeVideoFrameResource resource) =>
        resource.BackendId == MediaGraphicsBackendId.Vulkan && resource.Device.DeviceHandle == _device.Handle;

    /// <summary>Forgets the retained video layer (Core cleared the background).</summary>
    public void ClearVideoLayer()
    {
        lock (_sync)
        {
            _videoLayerValid = false;
            _videoConvertedRevision = -1;
            _videoConvertedFrame = 0;
            // The decoder's frames may go with it. Every submission has completed (fence wait
            // under this lock); after a loss the views wait for Dispose.
            if (_shared?.IsLost != true)
                DestroyPlaneViews();
        }
    }

    /// <summary>
    /// Describes a decoded frame for the renderer: its <c>AVFrame*</c> (which Core retains) on
    /// <paramref name="device"/>, or null when it is not a Vulkan frame.
    /// </summary>
    public static NativeVideoFrameResource? CreateFrameResource(NativeVideoDeviceResource device, nint ffmpegFrame)
    {
        var frame = (AVFrame*)ffmpegFrame;
        if (frame == null || (AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_VULKAN || frame->data[0] == null || frame->hw_frames_ctx == null)
            return null;

        return new NativeVideoFrameResource(device, ffmpegFrame, 0);
    }

    /// <summary>Describes the device behind an FFmpeg Vulkan device context.</summary>
    public static NativeVideoDeviceResource? CreateDeviceResource(nint ffmpegHardwareDeviceContext)
    {
        var buffer = (AVBufferRef*)ffmpegHardwareDeviceContext;
        if (buffer == null || buffer->data == null)
            return null;

        var context = (AVHWDeviceContext*)buffer->data;
        if (context->type != AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN || context->hwctx == null)
            return null;

        var vulkan = (AVVulkanDeviceContext*)context->hwctx;
        return new NativeVideoDeviceResource(MediaGraphicsBackendId.Vulkan, vulkan->Device, vulkan->PhysicalDevice);
    }

    private sealed class VideoDecodeSetup(VulkanVideoDecodeSupport support, string[] extensions, VulkanFeatureChain features)
    {
        public VulkanVideoDecodeSupport Support { get; } = support;
        public string[] Extensions { get; } = extensions;
        public VulkanFeatureChain Features { get; } = features;
    }

    /// <summary>
    /// Checks the selected device for Vulkan Video decoding as FFmpeg 9 uses it: a 1.3 instance
    /// and device, the video queue extensions with at least one decode codec, a decode queue
    /// family, timeline semaphores, synchronization2 and sampler YCbCr conversion. Returns what to
    /// enable, or null with <see cref="VideoDecodeUnavailableReason"/>.
    /// </summary>
    private VideoDecodeSetup? ProbeVideoDecodeSupport()
    {
        VideoDecodeUnavailableReason = null;
        PhysicalDeviceProperties properties;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &properties);
        if (_instanceApiVersion < Vk.Version13)
            return NoVideo("the Vulkan loader is older than 1.3");
        if (properties.ApiVersion < Vk.Version13)
            return NoVideo("the Vulkan device is older than 1.3");

        var hasVideoQueue = _vk.IsDeviceExtensionPresent(_physicalDevice, VideoQueueExtension)
            && _vk.IsDeviceExtensionPresent(_physicalDevice, VideoDecodeQueueExtension);
        List<string> extensions = [];
        VideoCodecOperationFlagsKHR codecs = VideoCodecOperationFlagsKHR.NoneKhr;
        uint decodeFamily = 0;
        if (hasVideoQueue)
        {
            extensions.Add(VideoQueueExtension);
            extensions.Add(VideoDecodeQueueExtension);
            VideoCodecOperationFlagsKHR extensionCodecs = VideoCodecOperationFlagsKHR.NoneKhr;
            foreach ((var extension, VideoCodecOperationFlagsKHR operation) in DecodeCodecExtensions)
            {
                if (_vk.IsDeviceExtensionPresent(_physicalDevice, extension))
                {
                    extensions.Add(extension);
                    extensionCodecs |= operation;
                }
            }

            (decodeFamily, codecs) = FindDecodeFamily(extensionCodecs);
        }

        if (codecs == VideoCodecOperationFlagsKHR.NoneKhr && !_shareWithFfmpegWithoutDecode)
        {
            return NoVideo(!hasVideoQueue
                ? $"the Vulkan driver has no Vulkan Video decoding ({VideoDecodeQueueExtension})"
                : "the Vulkan device has no video decode queue for H.264, HEVC, AV1 or VP9");
        }

        var hasMaintenance1 = _vk.IsDeviceExtensionPresent(_physicalDevice, VideoMaintenance1Extension);
        using VulkanFeatureChain supported = VulkanFeatureChain.QuerySupported(_vk, _physicalDevice, hasMaintenance1);
        if (!supported.Vulkan12->TimelineSemaphore)
            return NoVideo("the Vulkan device has no timeline semaphores");
        if (!supported.Vulkan13->Synchronization2)
            return NoVideo("the Vulkan device has no synchronization2");
        if (!supported.Vulkan11->SamplerYcbcrConversion)
            return NoVideo("the Vulkan device has no sampler YCbCr conversion");
        if (!SamplesPlanes(Format.R8Unorm) || !SamplesPlanes(Format.R8G8Unorm) || !SamplesPlanes(Format.R16Unorm) || !SamplesPlanes(Format.R16G16Unorm))
            return NoVideo("the Vulkan device cannot sample R8/R8G8 and R16/R16G16 plane views with linear filtering");

        if (hasMaintenance1)
            extensions.Add(VideoMaintenance1Extension);
        if (_vk.IsDeviceExtensionPresent(_physicalDevice, PushDescriptorExtension))
            extensions.Add(PushDescriptorExtension);

        if (codecs == VideoCodecOperationFlagsKHR.NoneKhr)
        {
            // Test support only: FFmpeg can share the device, but it decodes nothing here.
            VideoDecodeUnavailableReason = "the Vulkan device has no video decode queue";
            return new VideoDecodeSetup(new VulkanVideoDecodeSupport(_queueFamily, VideoCodecOperationFlagsKHR.NoneKhr), [], VulkanFeatureChain.EnableForVideo(supported));
        }

        return new VideoDecodeSetup(new VulkanVideoDecodeSupport(decodeFamily, codecs), [.. extensions], VulkanFeatureChain.EnableForVideo(supported));

        VideoDecodeSetup? NoVideo(string reason)
        {
            VideoDecodeUnavailableReason = reason;
            return null;
        }
    }

    /// <summary>The decode queue family that supports the most of <paramref name="extensionCodecs"/>.</summary>
    private (uint Family, VideoCodecOperationFlagsKHR Codecs) FindDecodeFamily(VideoCodecOperationFlagsKHR extensionCodecs)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties2(_physicalDevice, &count, null);
        var video = new QueueFamilyVideoPropertiesKHR[count];
        var families = new QueueFamilyProperties2[count];
        fixed (QueueFamilyVideoPropertiesKHR* pVideo = video)
        fixed (QueueFamilyProperties2* pFamilies = families)
        {
            for (var i = 0; i < count; i++)
            {
                pVideo[i] = new QueueFamilyVideoPropertiesKHR { SType = StructureType.QueueFamilyVideoPropertiesKhr };
                pFamilies[i] = new QueueFamilyProperties2 { SType = StructureType.QueueFamilyProperties2, PNext = &pVideo[i] };
            }

            _vk.GetPhysicalDeviceQueueFamilyProperties2(_physicalDevice, &count, pFamilies);
        }

        (uint Family, VideoCodecOperationFlagsKHR Codecs) best = (0, VideoCodecOperationFlagsKHR.NoneKhr);
        for (uint index = 0; index < count; index++)
        {
            if ((families[index].QueueFamilyProperties.QueueFlags & QueueFlags.VideoDecodeBitKhr) == 0)
                continue;

            VideoCodecOperationFlagsKHR codecs = video[index].VideoCodecOperations & extensionCodecs;
            if (System.Numerics.BitOperations.PopCount((uint)codecs) > System.Numerics.BitOperations.PopCount((uint)best.Codecs))
                best = (index, codecs);
        }

        return best;
    }

    private bool SamplesPlanes(Format format)
    {
        FormatProperties properties;
        _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, &properties);
        const FormatFeatureFlags needed = FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit;
        return (properties.OptimalTilingFeatures & needed) == needed;
    }

    /// <summary>What one decoded frame needs to be converted.</summary>
    private readonly struct PendingVideoConversion(
        nint frameHandle,
        long revision,
        AVHWFramesContext* framesContext,
        AVVkFrame* frame,
        int imageCount,
        ImageView luma,
        ImageView chroma,
        VulkanVideoConstants constants,
        uint width,
        uint height)
    {
        public nint FrameHandle { get; } = frameHandle;
        public long Revision { get; } = revision;
        public AVHWFramesContext* FramesContext { get; } = framesContext;
        public AVVkFrame* Frame { get; } = frame;
        public int ImageCount { get; } = imageCount;
        public ImageView Luma { get; } = luma;
        public ImageView Chroma { get; } = chroma;
        public VulkanVideoConstants Constants { get; } = constants;
        public uint Width { get; } = width;
        public uint Height { get; } = height;
    }

    /// <summary>
    /// Readies the video layer for <paramref name="layer"/>: true when it can be drawn, with
    /// <paramref name="conversion"/> set when this frame converts a new decoded frame (the layer
    /// already holds it otherwise, or it is the retained layer).
    /// </summary>
    private bool PrepareVideoLayer(in VulkanBackgroundLayer layer, out PendingVideoConversion? conversion)
    {
        conversion = null;
        var width = (uint)layer.Width;
        var height = (uint)layer.Height;
        if (layer.Native is not { } resource)
            return _videoLayerValid && _videoLayer is { } retained && retained.Width == width && retained.Height == height;

        if (_videoLayerValid
            && layer.Revision >= 0
            && layer.Revision == _videoConvertedRevision
            && resource.ResourceHandle == _videoConvertedFrame
            && _videoLayer is { } current
            && current.Width == width
            && current.Height == height)
        {
            return true;
        }

        if (!TryDescribeFrame(resource, out FrameDescription description, out var failure))
        {
            ReportVideoFailure(failure!);
            // HDR the renderer cannot draw comes back tone mapped on the CPU (Stage 038A); a frame
            // from another device (until the decoder moves) is not a reason to give up the GPU.
            if (IsOnThisDevice(resource))
                resource.HdrToneMap?.Fallback?.RequestCpuToneMapping(failure!);
            return false;
        }

        if (!UsePlaneViewsOf(description.FramesReference))
        {
            ReportVideoFailure("FFmpeg's frames context could not be referenced");
            return false;
        }

        // Forgotten until this conversion's submission completes, so a failure converts again.
        _videoLayerValid = false;
        _videoPipeline ??= VulkanVideoPipeline.Create(_vk, _device, TargetFormat);
        EnsureVideoLayer(width, height);
        VulkanVideoConstants constants = VulkanVideoConversion.Create(
            resource.ColorSpace,
            resource.HdrToneMap,
            description.BitDepth,
            layer.Width,
            layer.Height,
            description.CodedWidth,
            description.CodedHeight);
        BufferResource constantsBuffer = EnsureBuffer(ref _videoConstants, (ulong)sizeof(VulkanVideoConstants), BufferUsageFlags.UniformBufferBit, UploadMemoryPreferences);
        // The previous conversion's submission has completed (fence wait).
        *(VulkanVideoConstants*)constantsBuffer.Mapped = constants;

        AVVkFrame* frame = description.Frame;
        Format lumaFormat = description.BitDepth == 8 ? Format.R8Unorm : Format.R16Unorm;
        Format chromaFormat = description.BitDepth == 8 ? Format.R8G8Unorm : Format.R16G16Unorm;
        ImageView luma;
        ImageView chroma;
        if (description.ImageCount == 1)
        {
            luma = CreateFrameView(new VkImage(frame->Image[0]), lumaFormat, ImageAspectFlags.Plane0Bit);
            chroma = CreateFrameView(new VkImage(frame->Image[0]), chromaFormat, ImageAspectFlags.Plane1Bit);
        }
        else
        {
            luma = CreateFrameView(new VkImage(frame->Image[0]), lumaFormat, ImageAspectFlags.ColorBit);
            chroma = CreateFrameView(new VkImage(frame->Image[1]), chromaFormat, ImageAspectFlags.ColorBit);
        }

        conversion = new PendingVideoConversion(
            resource.ResourceHandle,
            layer.Revision,
            description.FramesContext,
            frame,
            description.ImageCount,
            luma,
            chroma,
            constants,
            width,
            height);
        return true;
    }

    /// <summary>
    /// Locks the decoded frame and records its conversion: the frame's images to
    /// shader-read layout, the layer drawn, then made a transfer source for the blit.
    /// </summary>
    private VideoFrameAccess RecordVideoConversion(PendingVideoConversion conversion)
    {
        VideoFrameAccess access = _videoFrameAccess;
        access.Begin(conversion.FramesContext, conversion.Frame, conversion.ImageCount);
        try
        {
            AVVkFrame* frame = conversion.Frame;
            for (var i = 0; i < conversion.ImageCount; i++)
            {
                // The colour aspect, as FFmpeg's own barriers use: a multi-planar decoded image is
                // not disjoint, so its barriers may not name planes (ENG-039). The semaphore wait
                // covers FFmpeg's writes; images are shared by the queue families FFmpeg was given
                // (concurrent), so no ownership transfer.
                Barrier(
                    new VkImage(frame->Image[i]),
                    (ImageLayout)frame->Layout[i],
                    ImageLayout.ShaderReadOnlyOptimal,
                    0,
                    AccessFlags.ShaderReadBit,
                    PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.FragmentShaderBit,
                    ColorRange());
            }

            VkImage layer = _videoLayer!.Value.Image;
            Barrier(layer, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, AccessFlags.ColorAttachmentWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit);
            _videoPipeline!.Record(_commandBuffer, _videoFramebuffer, conversion.Width, conversion.Height, conversion.Luma, conversion.Chroma, _videoConstants!.Value.Buffer, conversion.Constants);
            Barrier(layer, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal, AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit);
            return access;
        }
        catch
        {
            access.EndAccess();
            throw;
        }
    }

    /// <summary>The conversion's submission completed: the layer holds the frame.</summary>
    private void CompleteVideoConversion(PendingVideoConversion conversion)
    {
        _videoConvertedRevision = conversion.Revision;
        _videoConvertedFrame = conversion.FrameHandle;
        _videoLayerValid = true;
        _reportedVideoFailure = null;
        VideoConversionCount++;
    }

    private void EnsureVideoLayer(uint width, uint height)
    {
        if (_videoLayer is { } existing && existing.Width == width && existing.Height == height && _videoFramebuffer.Handle != 0)
            return;

        DestroyVideoLayer();
        ImageResource created = EnsureImage(ref _videoLayer, width, height, TransferUsage | ImageUsageFlags.ColorAttachmentBit);
        _videoFramebuffer = _videoPipeline!.CreateFramebuffer(created.View, width, height);
    }

    /// <summary>
    /// Keeps the plane views of <paramref name="frames"/>' images: views of another frames context
    /// are released first, and this one is referenced while views of its images exist. False when
    /// FFmpeg cannot reference it.
    /// </summary>
    private bool UsePlaneViewsOf(AVBufferRef* frames)
    {
        if (_planeViewFrames != null && _planeViewFrames->data == frames->data)
        {
            if (_planeViews.Count + 2 > MaxPlaneViews)
                DestroyPlaneViewsOnly();
            return true;
        }

        DestroyPlaneViews();
        _planeViewFrames = ffmpeg.av_buffer_ref(frames);
        return _planeViewFrames != null;
    }

    private ImageView CreateFrameView(VkImage image, Format format, ImageAspectFlags aspect)
    {
        if (_planeViews.TryGetValue((image.Handle, format, aspect), out ImageView cached))
            return cached;

        // Decoder images also carry video usages; the view is for sampling only (as FFmpeg's own
        // plane views are).
        var usage = new ImageViewUsageCreateInfo { SType = StructureType.ImageViewUsageCreateInfo, Usage = ImageUsageFlags.SampledBit };
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            PNext = &usage,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        ImageView view;
        VulkanResult.Check(_vk.CreateImageView(_device, &info, null, &view), "vkCreateImageView (decoded frame plane)");
        _planeViews.Add((image.Handle, format, aspect), view);
        FrameViewCreateCount++;
        return view;
    }

    /// <summary>Destroys the cached plane views (no submission uses them) and releases their frames context.</summary>
    private void DestroyPlaneViews()
    {
        DestroyPlaneViewsOnly();
        if (_planeViewFrames != null)
        {
            // The views go first: this may be the last reference, which destroys the images.
            fixed (AVBufferRef** frames = &_planeViewFrames)
                ffmpeg.av_buffer_unref(frames);
        }
    }

    private void DestroyPlaneViewsOnly()
    {
        foreach (ImageView view in _planeViews.Values)
            _vk.DestroyImageView(_device, view, null);
        _planeViews.Clear();
    }

    private void DestroyVideoLayer()
    {
        if (_videoFramebuffer.Handle != 0)
            _vk.DestroyFramebuffer(_device, _videoFramebuffer, null);
        _videoFramebuffer = default;
        Destroy(ref _videoLayer);
        _videoLayerValid = false;
    }

    private void DestroyVideoResources()
    {
        DestroyPlaneViews();
        DestroyVideoLayer();
        Destroy(ref _videoConstants);
        _videoPipeline?.Dispose();
        _videoPipeline = null;
    }

    private void ReportVideoFailure(string reason)
    {
        if (_reportedVideoFailure == reason)
            return;

        _reportedVideoFailure = reason;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Vulkan", $"A decoded video frame cannot be drawn: {reason}.");
    }

    private struct FrameDescription
    {
        public AVHWFramesContext* FramesContext;
        public AVBufferRef* FramesReference;
        public AVVkFrame* Frame;
        public int ImageCount;
        public int BitDepth;
        public int CodedWidth;
        public int CodedHeight;
    }

    /// <summary>Reads what the conversion needs from a decoded frame, or says why it cannot be drawn.</summary>
    private bool TryDescribeFrame(NativeVideoFrameResource resource, out FrameDescription description, out string? failure)
    {
        description = default;
        var frame = (AVFrame*)resource.ResourceHandle;
        if (!IsOnThisDevice(resource))
        {
            failure = "the frame was decoded on another Vulkan device";
            return false;
        }

        if (frame == null || (AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_VULKAN || frame->hw_frames_ctx == null || frame->data[0] == null)
        {
            failure = "it is not an FFmpeg Vulkan frame";
            return false;
        }

        var frames = (AVHWFramesContext*)frame->hw_frames_ctx->data;
        AVPixelFormat softwareFormat = frames->sw_format;
        AVPixFmtDescriptor* format = ffmpeg.av_pix_fmt_desc_get(softwareFormat);
        if (format == null || format->nb_components != 3 || ffmpeg.av_pix_fmt_count_planes(softwareFormat) != 2 || (format->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0)
        {
            failure = $"its {FormatName(softwareFormat)} samples are not two-plane YCbCr";
            return false;
        }

        var bitDepth = format->comp[0].depth;
        var vkFrame = (AVVkFrame*)frame->data[0];
        var imageCount = vkFrame->Image[1] != 0 ? 2 : 1;
        if (bitDepth is < 8 or > 16 || vkFrame->Image[0] == 0 || vkFrame->Image[2] != 0)
        {
            failure = $"its {FormatName(softwareFormat)} images are laid out in a way this renderer does not read";
            return false;
        }

        description = new FrameDescription
        {
            FramesContext = frames,
            FramesReference = frame->hw_frames_ctx,
            Frame = vkFrame,
            ImageCount = imageCount,
            BitDepth = bitDepth,
            CodedWidth = frames->width,
            CodedHeight = frames->height,
        };
        failure = null;
        return true;
    }

    // Only for failure messages: FFmpeg.AutoGen marshals a new string on every call.
    private static string FormatName(AVPixelFormat format) => ffmpeg.av_get_pix_fmt_name(format) ?? format.ToString();

    private void Barrier(
        VkImage image,
        ImageLayout from,
        ImageLayout to,
        AccessFlags srcAccess,
        AccessFlags dstAccess,
        PipelineStageFlags srcStage,
        PipelineStageFlags dstStage,
        ImageSubresourceRange range) =>
        OwnershipBarrier(image, from, to, Vk.QueueFamilyIgnored, Vk.QueueFamilyIgnored, srcAccess, dstAccess, srcStage, dstStage, range);

    /// <summary>
    /// A decoded frame's lock and synchronization state for one submission (FFmpeg's
    /// <c>lock_frame</c>/<c>unlock_frame</c>, as its own submissions use them): read while
    /// recording, waited on and advanced by the submit, written back before unlocking. One
    /// instance is reused for every conversion (they run one at a time under the compositor's lock).
    /// </summary>
    private sealed class VideoFrameAccess
    {
        private AVHWFramesContext* _framesContext;
        private AVVkFrame* _frame;
        private int _imageCount;
        private bool _locked;
        private bool _submitted;

        public void Begin(AVHWFramesContext* framesContext, AVVkFrame* frame, int imageCount)
        {
            _framesContext = framesContext;
            _frame = frame;
            _imageCount = imageCount;
            _submitted = false;
            var vulkan = (AVVulkanFramesContext*)framesContext->hwctx;
            if (vulkan->LockFrame != 0)
                ((delegate* unmanaged[Cdecl]<AVHWFramesContext*, AVVkFrame*, void>)vulkan->LockFrame)(framesContext, frame);
            _locked = true;
        }

        /// <summary>Waits on each image's semaphore at its current value and signals the next.</summary>
        public void AddSemaphores(
            VkSemaphore* waitSemaphores,
            PipelineStageFlags* waitStages,
            ulong* waitValues,
            ref uint waitCount,
            VkSemaphore* signalSemaphores,
            ulong* signalValues,
            ref uint signalCount)
        {
            for (var i = 0; i < _imageCount; i++)
            {
                if (_frame->Semaphore[i] == 0)
                    continue;

                waitSemaphores[waitCount] = new VkSemaphore(_frame->Semaphore[i]);
                waitStages[waitCount] = PipelineStageFlags.AllCommandsBit;
                waitValues[waitCount++] = _frame->SemaphoreValue[i];
                signalSemaphores[signalCount] = new VkSemaphore(_frame->Semaphore[i]);
                signalValues[signalCount++] = _frame->SemaphoreValue[i] + 1;
            }
        }

        public void MarkSubmitted() => _submitted = true;

        /// <summary>After the submit: advances the frame's state if it was submitted, then unlocks it.</summary>
        public void EndAccess()
        {
            if (!_locked)
                return;

            if (_submitted)
            {
                for (var i = 0; i < _imageCount; i++)
                {
                    if (_frame->Semaphore[i] != 0)
                        _frame->SemaphoreValue[i]++;
                    _frame->Layout[i] = (int)ImageLayout.ShaderReadOnlyOptimal;
                    _frame->Access[i] = FfmpegVulkanInterop.ShaderReadAccess2;
                }
            }

            _locked = false;
            var vulkan = (AVVulkanFramesContext*)_framesContext->hwctx;
            if (vulkan->UnlockFrame != 0)
                ((delegate* unmanaged[Cdecl]<AVHWFramesContext*, AVVkFrame*, void>)vulkan->UnlockFrame)(_framesContext, _frame);
        }
    }
}
