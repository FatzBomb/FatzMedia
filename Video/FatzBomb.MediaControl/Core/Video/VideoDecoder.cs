using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Video.Hdr;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// Handles FFmpeg video decoding operations separate from timing and audio concerns
/// </summary>
internal sealed unsafe class VideoDecoder(
    VideoDecodingMethod decodingMethod = VideoDecodingMethod.Auto,
    bool enableHardwareFrameOutput = true,
    bool enableDiagnostics = true,
    bool enablePerformanceLogging = false,
    bool enableVerboseLogging = false,
    bool scaleMaxFramesByResolution = false,
    SharedDemuxer? sharedDemuxer = null,
    IVideoHardwareAccelerationServices? hardwareAccelerationServices = null,
    HdrToneMappingOptions hdrToneMapping = default) : IDisposable
{
    private readonly HdrToneMappingOptions _hdrToneMapping = hdrToneMapping;
    private readonly VideoDecodingMethod _decodingMethod = decodingMethod;
    private readonly bool _enableHardwareFrameOutput = enableHardwareFrameOutput;
    private readonly bool _enableDiagnostics = enableDiagnostics;
    private readonly bool _enablePerformanceLogging = enablePerformanceLogging;
    private readonly bool _enableVerboseLogging = enableVerboseLogging;
    private readonly bool _scaleMaxFramesByResolution = scaleMaxFramesByResolution;
    private readonly SharedDemuxer _demuxer = sharedDemuxer ?? new SharedDemuxer();
    private readonly bool _ownsDemuxer = sharedDemuxer == null;
    private readonly IVideoHardwareAccelerationServices _hardwareAccelerationServices =
        hardwareAccelerationServices ?? SoftwareOnlyVideoHardwareAccelerationServices.Instance;
    private AVCodecContext* _videoCodecCtx;
    private AVStream* _videoStream;
    private SwsContext* _swsVideo;
    private AVFrame* _videoFrame;
    private AVFrame* _softwareFrame;
    private AVPacket* _packet;
    private AVBufferRef* _hardwareDeviceContext;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private string? _hardwareDeviceTypeName;
    private AVPixelFormat _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private VideoColorSpace _scalerColorSpace;
    private AVPixelFormat _scalerTargetPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private HdrToneMapper? _toneMapper;
    private byte[]? _toneMapScratch;
    private bool _toneMapReported;
    private AVCodecContext_get_format? _getFormatCallback;

    private int _videoLinesize;

    private double _streamStartSeconds;
    private bool _streamStartInitialized;
    private double? _discardFramesBeforeSeconds;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double FramesPerSecond { get; private set; }
    public string? CodecName { get; private set; }
    public double DurationSeconds { get; private set; }
    public bool IsHardwareAccelerated { get; private set; }
    public string? HardwareAccelerationMethod { get; private set; }
    public NativeVideoDeviceResource? HardwareDevice { get; private set; }
    /// <summary>The FFmpeg decoder in use, such as <c>av1</c>, <c>libdav1d</c>, or <c>h264</c>.</summary>
    public string? DecoderName { get; private set; }
    public bool EndOfStream { get; private set; }
    public int MaxFrames { get; private set; } = MaxFramesCap;
    internal int VideoStreamIndex { get; private set; } = -1;

    private readonly ConcurrentQueue<VideoFrameData> _frameQueue = new();
    private readonly ArrayPool<byte> _framePool = ArrayPool<byte>.Shared;
    private const int MaxFramesCap = 24;
    private const int MinFrames = 3;
    private const long SoftwareFrameMemoryBudget = 128L * 1024 * 1024;
    private const int DecodeDiagnosticsFrameInterval = 120;
    private const int DecodeIssueLogLimit = 8;
    private const int AvErrorAgain = -11;
    private const int HardwareDecodeLossFailureThreshold = 3;
    private int _decodeDiagnosticFrames;
    private double _decodeDiagnosticTransferMs;
    private double _decodeDiagnosticScaleMs;
    private double _decodeDiagnosticBufferMs;
    private double _decodeDiagnosticTotalMs;
    private int _packetSendIssueLogs;
    private int _frameReceiveIssueLogs;
    private int _hardwareTransferIssueLogs;
    private bool _loggedHardwarePixelFormat;
    private int _consecutiveHardwareDecodeFailures;
    private int _hardwareFormatAccepted;
    private int _hardwareFormatMissing;
    private string? _formatsOfferedWithoutHardware;
    private int _lastDecodeError;
    private bool _logCaptureActive;
    private Av1MetadataObuFilter? _av1MetadataFilter;

    /// <summary>
    /// True when hardware decoding is configured and the hardware path has failed to produce a
    /// frame several times in a row with no successful frame in between — a strong hint that the
    /// underlying hardware-decode device may have been lost. The platform confirms the
    /// native device state before recovery begins.
    /// </summary>
    public bool IsHardwareDecodeRepeatedlyFailing =>
        HardwareAccelerationMethod != null && _consecutiveHardwareDecodeFailures >= HardwareDecodeLossFailureThreshold;

    public static VideoDecoder FromConfiguration(
        MediaPlayerConfiguration configuration,
        IVideoHardwareAccelerationServices hardwareAccelerationServices,
        bool enableHardwareFrameOutput = true,
        bool scaleMaxFramesByResolution = false) =>
        new(configuration.VideoDecoding,
            enableHardwareFrameOutput,
            configuration.EnableLogging,
            configuration.EnablePerformanceLogging,
            configuration.EnableVerboseLogging,
            scaleMaxFramesByResolution,
            hardwareAccelerationServices: hardwareAccelerationServices,
            hdrToneMapping: configuration.HdrToneMapping);

    public static VideoDecoder FromConfiguration(
        MediaPlayerConfiguration configuration,
        SharedDemuxer sharedDemuxer,
        IVideoHardwareAccelerationServices hardwareAccelerationServices,
        bool enableHardwareFrameOutput = true,
        bool scaleMaxFramesByResolution = false) =>
        new(configuration.VideoDecoding,
            enableHardwareFrameOutput,
            configuration.EnableLogging,
            configuration.EnablePerformanceLogging,
            configuration.EnableVerboseLogging,
            scaleMaxFramesByResolution,
            sharedDemuxer,
            hardwareAccelerationServices,
            configuration.HdrToneMapping);

    public bool Open(string path)
    {
        Close();

        if (!_demuxer.IsOpen && !_demuxer.Open(path))
            return false;

        VideoStreamIndex = _demuxer.FindBestStream(AVMediaType.AVMEDIA_TYPE_VIDEO);
        if (VideoStreamIndex < 0) { Close(); return false; }

        _demuxer.RegisterVideoStream(VideoStreamIndex);

        _videoStream = _demuxer.GetStream(VideoStreamIndex);

        // Discard non-video streams so av_read_frame only returns video packets — but
        // only when we own the demuxer (single-stream mode: background video, hardware
        // probe). When sharing the demuxer (main video+audio path), non-video packets
        // are dispatched to the audio consumer by SharedDemuxer, so we must NOT discard.
        if (_ownsDemuxer)
        {
            for (uint i = 0; i < _demuxer.StreamCount; i++)
            {
                if ((int)i != VideoStreamIndex)
                    _demuxer.SetStreamDiscard((int)i, AVDiscard.AVDISCARD_ALL);
            }
        }

        if (!InitializeVideoDecoder())
        {
            Close();
            return false;
        }

        _packet = ffmpeg.av_packet_alloc();
        MaxFrames = ComputeMaxFrames();

        // Prefer the video stream's own duration over the format-level duration. The
        // format duration is the max across all streams (often audio for music videos),
        // which can be longer than the video — causing a perceived gap at the loop
        // boundary even though video frames have stopped.
        var videoStreamDuration = _videoStream->duration != ffmpeg.AV_NOPTS_VALUE
            ? _videoStream->duration * ffmpeg.av_q2d(_videoStream->time_base)
            : 0;
        var formatDurationTicks = _demuxer.FormatDurationTicks;
        var formatDuration = formatDurationTicks > 0 ? formatDurationTicks / (double)ffmpeg.AV_TIME_BASE : 0;
        DurationSeconds = videoStreamDuration > 0 ? videoStreamDuration : formatDuration;

        if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] Opened: {Width}x{Height}, {FramesPerSecond:F2}fps, {CodecName}, duration={DurationSeconds:F3}s");
        return true;
    }

    public bool DecodeNextFrame()
    {
        if (!_demuxer.IsOpen || _packet == null) return false;
        if (EndOfStream) return false;
        if (_frameQueue.Count >= MaxFrames) return false;

        var readResult = _demuxer.ReadVideoPacket(_packet);
        if (readResult < 0)
        {
            if (readResult == ffmpeg.AVERROR_EOF)
            {
                ProcessRemainingFrames();
                EndOfStream = true;
                return false; // No more frames
            }
            return false; // Error
        }

        var isVideoPacket = _packet->stream_index == VideoStreamIndex;
        var frameDecoded = false;
        if (isVideoPacket)
        {
            frameDecoded = ProcessVideoPacket();
        }

        ffmpeg.av_packet_unref(_packet);
        return frameDecoded || !isVideoPacket;
    }

    /// <summary>
    /// Computes the maximum number of frames to buffer based on frame size and decode path.
    /// Only applied when <see cref="_scaleMaxFramesByResolution"/> is true (background video);
    /// the main video pipeline uses the fixed cap because it has its own buffer target
    /// (<c>MaxBufferedVideoFrames</c>) and byte limit (<c>MaxBufferedVideoBytes</c>) in
    /// VideoPipeline, and needs the full queue depth for slow software decoders (e.g. AV1).
    /// Hardware frames are cheap (cloned AVFrame referencing an existing D3D11 texture), so
    /// the cap is used. Software frames are full BGRA buffers, so the count is scaled down
    /// for larger resolutions to stay within a memory budget.
    /// </summary>
    private int ComputeMaxFrames()
    {
        if (!_scaleMaxFramesByResolution || IsHardwareAccelerated)
            return MaxFramesCap;

        var frameSizeBytes = (long)_videoLinesize * Height;
        if (frameSizeBytes <= 0)
            return MaxFramesCap;

        var byBudget = (int)(SoftwareFrameMemoryBudget / frameSizeBytes);
        return Math.Clamp(byBudget, MinFrames, MaxFramesCap);
    }

    public VideoFrameData? GetNextFrame() => _frameQueue.TryDequeue(out VideoFrameData? frame) ? frame : null;

    public VideoFrameData? PeekNextFrame() => _frameQueue.TryPeek(out VideoFrameData? frame) ? frame : null;

    public VideoFrameData[] GetQueuedFramesSnapshot() => [.. _frameQueue];

    public IEnumerable<VideoFrameData> EnumerateQueuedFrames() => _frameQueue;

    public int FrameCount => _frameQueue.Count;

    private bool InitializeVideoDecoder()
    {
        AVCodecID codecId = _videoStream->codecpar->codec_id;
        AVCodec* softwareCodec = ffmpeg.avcodec_find_decoder(codecId);
        if (softwareCodec == null) return false;

        var allowHardware = _decodingMethod is VideoDecodingMethod.Auto or VideoDecodingMethod.Hardware;
        AVCodec* hardwareCodec = null;
        if (allowHardware)
            hardwareCodec = FindHardwareCapableDecoder(codecId);

        AVCodec* preferredCodec = hardwareCodec != null ? hardwareCodec : softwareCodec;

        if (allowHardware && TryOpenCodec(preferredCodec, useHardware: true))
        {
            // Some hardware-capable decoders (FFmpeg's native av1) cannot decode in software. If no
            // hardware device was configured, use the software decoder instead of one that yields
            // no frames.
            if (HardwareAccelerationMethod == null && preferredCodec != softwareCodec)
            {
                if (!TryOpenCodec(softwareCodec, useHardware: false))
                    return false;

                preferredCodec = softwareCodec;
            }

            if (_enableDiagnostics)
            {
                Debug.WriteLine(!string.IsNullOrWhiteSpace(HardwareAccelerationMethod)
                    ? $"[VideoDecoder] Hardware decode configured: {HardwareAccelerationMethod} using {GetCodecName(preferredCodec)}"
                    : _decodingMethod == VideoDecodingMethod.Hardware
                        ? "[VideoDecoder] Hardware decoding requested, but this stream is using the software decoder."
                        : "[VideoDecoder] Using software decoder.");
            }
        }
        else if (!TryOpenCodec(softwareCodec, useHardware: false))
        {
            return false;
        }
        else
        {
            preferredCodec = softwareCodec;
        }

        DecoderName = GetCodecName(preferredCodec);
        if (DecoderName == "av1")
            _av1MetadataFilter = Av1MetadataObuFilter.TryCreate(_videoStream->codecpar, _videoStream->time_base);
        _videoFrame = ffmpeg.av_frame_alloc();
        if (_videoFrame == null) return false;

        if (_hardwareDeviceContext != null)
        {
            _softwareFrame = ffmpeg.av_frame_alloc();
            if (_softwareFrame == null) return false;
        }

        Width = _videoCodecCtx->width;
        Height = _videoCodecCtx->height;

        _videoLinesize = Width * 4;

        AVRational avg = _videoStream->avg_frame_rate;
        FramesPerSecond = avg.num > 0 && avg.den > 0 ? avg.num / (double)avg.den : 30.0;

        AVCodecDescriptor* codecDescr = ffmpeg.avcodec_descriptor_get(_videoStream->codecpar->codec_id);
        CodecName = codecDescr != null ? Marshal.PtrToStringAnsi((IntPtr)codecDescr->name) : "Unknown";

        return true;
    }

    private AVCodec* FindHardwareCapableDecoder(AVCodecID codecId)
    {
        void* opaque = null;
        AVCodec* fallbackHardwareCodec = null;
        var sawDecoder = false;

        while (true)
        {
            AVCodec* codec = ffmpeg.av_codec_iterate(&opaque);
            if (codec == null)
            {
                break;
            }

            if (ffmpeg.av_codec_is_decoder(codec) == 0 || codec->id != codecId)
            {
                continue;
            }

            sawDecoder = true;
            if (_enableVerboseLogging) LogDecoderHardwareConfigs(codec);

            if (!HasPreferredHardwareConfig(codec))
            {
                continue;
            }

            var codecName = GetCodecName(codec);
            if (IsPreferredHardwareDecoderName(codecName))
            {
                if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] Selected preferred hardware-capable decoder: {codecName}");
                return codec;
            }

            if (fallbackHardwareCodec == null)
            {
                fallbackHardwareCodec = codec;
            }
        }

        if (fallbackHardwareCodec != null)
        {
            if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] Selected hardware-capable decoder: {GetCodecName(fallbackHardwareCodec)}");
        }
        else if (!sawDecoder)
        {
            if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] No decoder candidates were reported for codec id {codecId}.");
        }

        return fallbackHardwareCodec;
    }

    private static void LogDecoderHardwareConfigs(AVCodec* codec)
    {
        var codecName = GetCodecName(codec);
        var configs = new List<string>();

        for (var index = 0; ; index++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, index);
            if (config == null)
            {
                break;
            }

            var deviceName = ffmpeg.av_hwdevice_get_type_name(config->device_type) ?? "unknown";
            var pixelFormatName = ffmpeg.av_get_pix_fmt_name(config->pix_fmt) ?? "unknown";
            configs.Add($"{deviceName}/{pixelFormatName}/methods={config->methods}");
        }

        Debug.WriteLine(configs.Count > 0
            ? $"[VideoDecoder] Decoder candidate {codecName}: hw={string.Join(", ", configs)}"
            : $"[VideoDecoder] Decoder candidate {codecName}: hw=none");
    }

    private bool HasPreferredHardwareConfig(AVCodec* codec)
    {
        for (var index = 0; ; index++)
        {
            AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, index);
            if (config == null)
            {
                return false;
            }

            var supportsDeviceContext =
                (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0;
            if (!supportsDeviceContext)
            {
                continue;
            }

            foreach (var deviceTypeName in _hardwareAccelerationServices.PreferredDeviceTypeNames)
            {
                var deviceType = ffmpeg.av_hwdevice_find_type_by_name(deviceTypeName);
                if (config->device_type == deviceType)
                {
                    return true;
                }
            }
        }
    }

    private bool IsPreferredHardwareDecoderName(string codecName) =>
        _hardwareAccelerationServices.PreferredDeviceTypeNames.Any(
            deviceType => codecName.Contains(deviceType, StringComparison.OrdinalIgnoreCase))
        || codecName.Equals("av1", StringComparison.OrdinalIgnoreCase)
        || codecName.Equals("hevc", StringComparison.OrdinalIgnoreCase)
        || codecName.Equals("h264", StringComparison.OrdinalIgnoreCase);

    private static string GetCodecName(AVCodec* codec) => codec != null && codec->name != null
            ? Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown"
            : "unknown";

    private bool TryOpenCodec(AVCodec* codec, bool useHardware)
    {
        CleanupCodecContext();
        CleanupHardwareDevice();

        _videoCodecCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_videoCodecCtx == null) return false;

        // Keep FFmpeg's own messages until the first frame, so a failed hardware start can say why.
        _logCaptureActive = useHardware;
        if (useHardware)
            FfmpegLogCapture.Begin(_videoCodecCtx);

        if (ffmpeg.avcodec_parameters_to_context(_videoCodecCtx, _videoStream->codecpar) < 0)
        {
            CleanupCodecContext();
            return false;
        }

        _videoCodecCtx->thread_count = 0;
        _videoCodecCtx->thread_type = 3; // FF_THREAD_FRAME | FF_THREAD_SLICE

        var hardwareConfigured = useHardware && TryConfigureHardwareDecoder(codec);
        if (ffmpeg.avcodec_open2(_videoCodecCtx, codec, null) < 0)
        {
            if (_enableDiagnostics)
            {
                Debug.WriteLine(hardwareConfigured
                    ? $"[VideoDecoder] Failed to open codec with {HardwareAccelerationMethod}; falling back to software."
                    : "[VideoDecoder] Failed to open codec.");
            }

            CleanupCodecContext();
            CleanupHardwareDevice();
            return false;
        }

        if (!hardwareConfigured)
        {
            IsHardwareAccelerated = false;
            HardwareAccelerationMethod = null;
        }

        return true;
    }

    private bool TryConfigureHardwareDecoder(AVCodec* codec)
    {
        var codecName = GetCodecName(codec);
        var sawPreferredDevice = false;
        foreach (var deviceTypeName in _hardwareAccelerationServices.PreferredDeviceTypeNames)
        {
            var deviceType = ffmpeg.av_hwdevice_find_type_by_name(deviceTypeName);
            if (deviceType == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                continue;
            }

            for (var preferencePass = 0; preferencePass < 2; preferencePass++)
            {
                for (var index = 0; ; index++)
                {
                    AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, index);
                    if (config == null)
                    {
                        break;
                    }

                    var supportsDeviceContext =
                        (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0;
                    if (!supportsDeviceContext || config->device_type != deviceType)
                    {
                        continue;
                    }

                    sawPreferredDevice = true;
                    var isBestPixelFormat = _hardwareAccelerationServices.IsPreferredPixelFormat(
                        deviceTypeName,
                        (int)config->pix_fmt);
                    if ((preferencePass == 0 && !isBestPixelFormat) || (preferencePass == 1 && isBestPixelFormat))
                    {
                        continue;
                    }

                    if (TryCreateHardwareDevice(deviceTypeName, config->device_type, config->pix_fmt))
                    {
                        return true;
                    }
                }
            }
        }

        if (!sawPreferredDevice)
        {
            if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] No preferred hardware config reported for codec {codecName} in this FFmpeg build.");
        }
        else if (_decodingMethod == VideoDecodingMethod.Hardware)
        {
            if (_enableDiagnostics) Debug.WriteLine("[VideoDecoder] Hardware decoding was requested but no supported FFmpeg hardware device was available.");
        }

        return false;
    }

    private bool TryCreateHardwareDevice(
        string deviceTypeName,
        AVHWDeviceType deviceType,
        AVPixelFormat pixelFormat)
    {
        AVBufferRef* deviceContext = null;
        var result = ffmpeg.av_hwdevice_ctx_create(&deviceContext, deviceType, null, null, 0);
        if (result < 0 || deviceContext == null)
        {
            if (_enableVerboseLogging) Debug.WriteLine($"[VideoDecoder] Failed to create hardware device {ffmpeg.av_hwdevice_get_type_name(deviceType)}: {result}");
            return false;
        }

        _hardwareDeviceContext = deviceContext;
        _hardwarePixelFormat = pixelFormat;
        _hardwareDeviceTypeName = deviceTypeName;
        HardwareAccelerationMethod = deviceTypeName;
        HardwareDevice = _hardwareAccelerationServices.CreateDeviceResource(
            deviceTypeName,
            (nint)_hardwareDeviceContext);

        _videoCodecCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hardwareDeviceContext);
        if (_videoCodecCtx->hw_device_ctx == null)
        {
            CleanupHardwareDevice();
            return false;
        }

        _getFormatCallback = SelectHardwarePixelFormat;
        _videoCodecCtx->get_format = new AVCodecContext_get_format_func
        {
            Pointer = Marshal.GetFunctionPointerForDelegate(_getFormatCallback)
        };

        return true;
    }

    private AVPixelFormat SelectHardwarePixelFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        if (formats == null)
        {
            IsHardwareAccelerated = false;
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == _hardwarePixelFormat)
            {
                _hardwareFormatAccepted++;
                IsHardwareAccelerated = true;
                if (!_loggedHardwarePixelFormat)
                {
                    if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] Selected hardware pixel format: {ffmpeg.av_get_pix_fmt_name(*format)}");
                    _loggedHardwarePixelFormat = true;
                }
                return *format;
            }
        }

        IsHardwareAccelerated = false;
        _hardwareFormatMissing++;
        _formatsOfferedWithoutHardware = DescribeOfferedFormats(formats);
        if (!_loggedHardwarePixelFormat)
        {
            if (_enableDiagnostics)
                Debug.WriteLine(
                    $"[VideoDecoder] Hardware pixel format {ffmpeg.av_get_pix_fmt_name(_hardwarePixelFormat)} was not offered by the decoder; using {ffmpeg.av_get_pix_fmt_name(*formats)}.");
            _loggedHardwarePixelFormat = true;
        }
        return *formats;
    }

    private bool ProcessVideoPacket()
    {
        if (_frameQueue.Count >= MaxFrames) return false;

        var frameDecoded = DrainDecodedFrames();
        if (_av1MetadataFilter != null && !_av1MetadataFilter.Filter(_packet))
            return frameDecoded;

        var sendResult = ffmpeg.avcodec_send_packet(_videoCodecCtx, _packet);
        if (sendResult == AvErrorAgain)
        {
            frameDecoded |= DrainDecodedFrames();
            if (_frameQueue.Count < MaxFrames)
            {
                sendResult = ffmpeg.avcodec_send_packet(_videoCodecCtx, _packet);
            }
        }

        if (sendResult == 0)
        {
            return DrainDecodedFrames() || frameDecoded;
        }

        if (sendResult != ffmpeg.AVERROR_EOF)
            _lastDecodeError = sendResult;

        if (sendResult != ffmpeg.AVERROR_EOF && _enableVerboseLogging && ShouldLogDecodeIssue(ref _packetSendIssueLogs))
        {
            Debug.WriteLine($"[VideoDecoder] avcodec_send_packet failed: {FormatFFmpegError(sendResult)} ({sendResult})");
        }

        return frameDecoded;
    }

    private bool DrainDecodedFrames()
    {
        var frameDecoded = false;
        while (_frameQueue.Count < MaxFrames)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_videoCodecCtx, _videoFrame);
            if (receiveResult == 0)
            {
                frameDecoded = true;
                ProcessVideoFrameFromDecoder();
                continue;
            }

            if (receiveResult != AvErrorAgain && receiveResult != ffmpeg.AVERROR_EOF)
            {
                _lastDecodeError = receiveResult;
                NoteHardwareDecodeFailure();
                if (_enableVerboseLogging && ShouldLogDecodeIssue(ref _frameReceiveIssueLogs))
                {
                    Debug.WriteLine($"[VideoDecoder] avcodec_receive_frame failed: {FormatFFmpegError(receiveResult)} ({receiveResult})");
                }
            }

            break;
        }

        return frameDecoded;
    }

    private void NoteHardwareDecodeFailure()
    {
        if (HardwareAccelerationMethod != null && _consecutiveHardwareDecodeFailures < int.MaxValue)
        {
            _consecutiveHardwareDecodeFailures++;
        }
    }

    private void ProcessVideoFrameFromDecoder()
    {
        // A frame was successfully received from the decoder — the hardware path is working, so
        // clear the consecutive-failure gate used for device-lost detection.
        _consecutiveHardwareDecodeFailures = 0;
        if (_logCaptureActive)
        {
            _logCaptureActive = false;
            FfmpegLogCapture.End(_videoCodecCtx);
        }

        double rawPts = 0;
        var timebase = ffmpeg.av_q2d(_videoStream->time_base);

        if (_videoFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
            rawPts = _videoFrame->best_effort_timestamp * timebase;
        else if (_videoFrame->pts != ffmpeg.AV_NOPTS_VALUE)
            rawPts = _videoFrame->pts * timebase;
        else if (_videoFrame->pkt_dts != ffmpeg.AV_NOPTS_VALUE)
            rawPts = _videoFrame->pkt_dts * timebase;

        if (!_streamStartInitialized)
        {
            _streamStartSeconds = rawPts;
            _streamStartInitialized = true;
        }

        var pts = Math.Max(0, rawPts - _streamStartSeconds);

        if (_discardFramesBeforeSeconds.HasValue)
        {
            if (pts + 0.001 < _discardFramesBeforeSeconds.Value)
            {
                return;
            }

            _discardFramesBeforeSeconds = null;
        }

        var totalStart = Stopwatch.GetTimestamp();
        if (TryQueueNativeHardwareFrame(pts, totalStart))
        {
            return;
        }

        var transferStart = totalStart;
        AVFrame* sourceFrame = GetSoftwareFrameForProcessing();
        var transferMs = GetElapsedMilliseconds(transferStart);
        if (sourceFrame == null)
        {
            return;
        }

        double scaleMs = 0;
        double bufferMs = 0;
        byte[]? rentedFrameData = null;
        try
        {
            var sourceFormat = (AVPixelFormat)sourceFrame->format;
            HdrToneMapper? toneMapper = IsToneMappedFrame() ? GetToneMapper() : null;
            AVPixelFormat targetFormat = toneMapper != null ? AVPixelFormat.AV_PIX_FMT_RGB48LE : AVPixelFormat.AV_PIX_FMT_BGRA;
            if (!EnsureScaler(sourceFormat, GetFrameColorSpace(sourceFormat), targetFormat))
            {
                return;
            }

            var frameSize = _videoLinesize * Height;
            var bufferStart = Stopwatch.GetTimestamp();
            var frameData = _framePool.Rent(frameSize);
            rentedFrameData = frameData;
            bufferMs = GetElapsedMilliseconds(bufferStart);

            byte_ptrArray8 dstData = new();
            int_array8 dstLinesize = new();
            if (toneMapper != null)
            {
                // HDR10: swscale converts to full-range 16-bit RGB (still PQ, BT.2020), then
                // the tone mapper writes SDR BGRA (Stage 038A).
                var scratchStride = Width * 6;
                var scratch = _toneMapScratch is { } existing && existing.Length >= scratchStride * Height
                    ? existing
                    : _toneMapScratch = new byte[scratchStride * Height];
                var scaleStart = Stopwatch.GetTimestamp();
                fixed (byte* scratchPointer = scratch)
                {
                    dstData[0] = scratchPointer;
                    dstLinesize[0] = scratchStride;
                    _ = ffmpeg.sws_scale(_swsVideo, sourceFrame->data, sourceFrame->linesize,
                        0, Height, dstData, dstLinesize);
                }

                toneMapper.Convert(scratch, scratchStride, frameData, _videoLinesize, Width, Height);
                scaleMs = GetElapsedMilliseconds(scaleStart);
            }
            else
            {
                fixed (byte* framePointer = frameData)
                {
                    dstData[0] = framePointer;
                    dstLinesize[0] = _videoLinesize;

                    var scaleStart = Stopwatch.GetTimestamp();
                    _ = ffmpeg.sws_scale(_swsVideo, sourceFrame->data, sourceFrame->linesize,
                        0, Height, dstData, dstLinesize);
                    scaleMs = GetElapsedMilliseconds(scaleStart);
                }
            }

            _frameQueue.Enqueue(new VideoFrameData
            {
                Data = frameData,
                Length = frameSize,
                Width = Width,
                Height = Height,
                Stride = _videoLinesize,
                PresentationTime = pts
            });
            rentedFrameData = null;
        }
        finally
        {
            if (rentedFrameData != null)
            {
                _framePool.Return(rentedFrameData);
            }

            if (sourceFrame == _softwareFrame)
            {
                ffmpeg.av_frame_unref(_softwareFrame);
            }

            RecordDecodeDiagnostics(
                transferMs,
                scaleMs,
                bufferMs,
                GetElapsedMilliseconds(totalStart));
        }
    }

    private bool TryQueueNativeHardwareFrame(double pts, long totalStart)
    {
        // HDR10 frames are tone mapped on the CPU path (Stage 038A slice 1); the GPU still decodes them.
        if (!_enableHardwareFrameOutput
            || IsToneMappedFrame()
            || _hardwareDeviceContext == null
            || _hardwareDeviceTypeName == null
            || HardwareDevice == null)
        {
            return false;
        }

        AVFrame* clonedFrame = ffmpeg.av_frame_clone(_videoFrame);
        if (clonedFrame == null)
        {
            return false;
        }

        NativeVideoFrameResource? nativeResource = _hardwareAccelerationServices.CreateFrameResource(
            _hardwareDeviceTypeName,
            HardwareDevice,
            (nint)clonedFrame);
        if (nativeResource is not { IsValid: true })
        {
            ffmpeg.av_frame_free(&clonedFrame);
            return false;
        }

        nativeResource = nativeResource with { ColorSpace = GetFrameColorSpace(_videoCodecCtx != null ? _videoCodecCtx->sw_pix_fmt : AVPixelFormat.AV_PIX_FMT_NONE) };

        _frameQueue.Enqueue(new VideoFrameData
        {
            Kind = VideoFrameKind.NativeHardware,
            HardwareFrame = clonedFrame,
            NativeResource = nativeResource,
            Length = 0,
            Width = Width,
            Height = Height,
            Stride = 0,
            PresentationTime = pts
        });

        RecordDecodeDiagnostics(0, 0, 0, GetElapsedMilliseconds(totalStart));
        return true;
    }

    private void RecordDecodeDiagnostics(double transferMs, double scaleMs, double bufferMs, double totalMs)
    {
        _decodeDiagnosticFrames++;
        _decodeDiagnosticTransferMs += transferMs;
        _decodeDiagnosticScaleMs += scaleMs;
        _decodeDiagnosticBufferMs += bufferMs;
        _decodeDiagnosticTotalMs += totalMs;

        if (_decodeDiagnosticFrames < DecodeDiagnosticsFrameInterval)
        {
            return;
        }

        var frameCount = _decodeDiagnosticFrames;
        if (_enablePerformanceLogging)
        {
            Debug.WriteLine(
                $"[VideoDecoder:Perf] frames={frameCount}, avgTotal={_decodeDiagnosticTotalMs / frameCount:F2}ms, " +
                $"scale={_decodeDiagnosticScaleMs / frameCount:F2}ms, queue={FrameCount}/{MaxFrames}, hw={HardwareAccelerationMethod ?? "software"}");
        }

        _decodeDiagnosticFrames = 0;
        _decodeDiagnosticTransferMs = 0;
        _decodeDiagnosticScaleMs = 0;
        _decodeDiagnosticBufferMs = 0;
        _decodeDiagnosticTotalMs = 0;
    }

    private static double GetElapsedMilliseconds(long startTimestamp) => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private static bool ShouldLogDecodeIssue(ref int count)
    {
        count++;
        return count <= DecodeIssueLogLimit || count % DecodeDiagnosticsFrameInterval == 0;
    }

    /// <summary>
    /// Describes the stream and the decoder for the host log, for example
    /// <c>AV1 Main 10-bit 3840x1608 (yuv420p10le), decoder av1 via d3d11va</c>.
    /// </summary>
    public string DescribeDecodePath()
    {
        if (_videoStream == null)
            return "no video stream";

        AVCodecParameters* parameters = _videoStream->codecpar;
        var codec = CodecName ?? "unknown";
        var profile = ffmpeg.avcodec_profile_name(parameters->codec_id, parameters->profile);
        var pixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format) ?? "unknown format";
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)parameters->format);
        var bitDepth = descriptor != null ? $" {descriptor->comp[0].depth}-bit" : string.Empty;
        var path = IsHardwareAccelerated
            ? $"via {HardwareAccelerationMethod}"
            : HardwareAccelerationMethod != null ? $"in software ({HardwareAccelerationMethod} configured but not used)" : "in software";
        var filtered = _av1MetadataFilter != null ? ", metadata OBUs removed" : string.Empty;
        var color = FFmpegVideoColor.Describe(
            FFmpegVideoColor.FromTags(parameters->color_space, parameters->color_range, (AVPixelFormat)parameters->format),
            parameters->width,
            parameters->height,
            parameters->color_trc,
            toneMapsHdr10: !_hdrToneMapping.Disabled);
        return $"{codec.ToUpperInvariant()}{(string.IsNullOrEmpty(profile) ? string.Empty : $" {profile}")}{bitDepth} " +
            $"{parameters->width}x{parameters->height} ({pixelFormat}, {color}), decoder {DecoderName ?? "unknown"} {path}{filtered}";
    }

    /// <summary>
    /// Explains why the hardware decoder produced no frame: whether FFmpeg offered the GPU
    /// format, dropped it after the GPU decoder failed to start (an unsupported profile, bit
    /// depth, or size), or decoded with errors.
    /// </summary>
    public string DescribeHardwareStartupFailure()
    {
        var reasons = new List<string>();
        if (_hardwareFormatAccepted == 0 && _hardwareFormatMissing == 0)
            reasons.Add("the decoder never asked for an output format (no decodable picture in the first packets)");
        else if (_hardwareFormatAccepted > 0 && _hardwareFormatMissing > 0)
            reasons.Add($"the GPU decoder failed to start, so FFmpeg withdrew {HardwareAccelerationMethod} (left: {_formatsOfferedWithoutHardware}); this GPU or driver likely does not support this profile, bit depth, or size");
        else if (_hardwareFormatMissing > 0)
            reasons.Add($"FFmpeg did not offer {HardwareAccelerationMethod} for this stream (offered: {_formatsOfferedWithoutHardware})");

        if (_lastDecodeError != 0)
            reasons.Add($"last FFmpeg error: {FormatFFmpegError(_lastDecodeError)}");

        IReadOnlyList<string> ffmpegLines = _videoCodecCtx != null ? FfmpegLogCapture.Peek(_videoCodecCtx) : [];
        if (ffmpegLines.Count > 0)
            reasons.Add($"FFmpeg reported: {string.Join(" | ", ffmpegLines)}");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "no decoder error was reported";
    }

    private static string DescribeOfferedFormats(AVPixelFormat* formats)
    {
        var names = new List<string>();
        for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE && names.Count < 8; format++)
            names.Add(ffmpeg.av_get_pix_fmt_name(*format) ?? ((int)*format).ToString(System.Globalization.CultureInfo.InvariantCulture));

        return names.Count > 0 ? string.Join(", ", names) : "none";
    }

    private static string FormatFFmpegError(int errorCode)
    {
        var buffer = stackalloc byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        return ffmpeg.av_strerror(errorCode, buffer, ffmpeg.AV_ERROR_MAX_STRING_SIZE) == 0
            ? Marshal.PtrToStringAnsi((IntPtr)buffer) ?? errorCode.ToString()
            : errorCode.ToString();
    }

    private AVFrame* GetSoftwareFrameForProcessing()
    {
        var frameFormat = (AVPixelFormat)_videoFrame->format;
        if (_hardwareDeviceContext == null || frameFormat != _hardwarePixelFormat)
        {
            return _videoFrame;
        }

        if (_softwareFrame == null)
        {
            return null;
        }

        ffmpeg.av_frame_unref(_softwareFrame);
        var transferResult = ffmpeg.av_hwframe_transfer_data(_softwareFrame, _videoFrame, 0);
        if (transferResult < 0)
        {
            NoteHardwareDecodeFailure();
            if (_enableVerboseLogging && ShouldLogDecodeIssue(ref _hardwareTransferIssueLogs))
            {
                Debug.WriteLine(
                    $"[VideoDecoder] Hardware frame transfer failed: {FormatFFmpegError(transferResult)} ({transferResult}); " +
                    $"source={ffmpeg.av_get_pix_fmt_name(frameFormat)}, target={ffmpeg.av_get_pix_fmt_name((AVPixelFormat)_softwareFrame->format)}.");
            }
            return null;
        }

        return _softwareFrame;
    }

    /// <summary>The decoded frame's color tags, unspecified parts resolved by frame size.</summary>
    private VideoColorSpace GetFrameColorSpace(AVPixelFormat softwareFormat) =>
        FFmpegVideoColor.FromTags(_videoFrame->colorspace, _videoFrame->color_range, softwareFormat)
            .Resolve(Width, Height);

    /// <summary>Whether the current frame is HDR10 (PQ) video the engine tone maps to SDR.</summary>
    private bool IsToneMappedFrame()
    {
        if (_hdrToneMapping.Disabled)
            return false;

        AVColorTransferCharacteristic transfer = _videoFrame->color_trc;
        if (transfer == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED && _videoStream != null)
            transfer = _videoStream->codecpar->color_trc;
        return transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
    }

    /// <summary>
    /// The tone mapper for the stream's peak luminance (its HDR10 metadata), created again only
    /// when that peak changes.
    /// </summary>
    private HdrToneMapper GetToneMapper()
    {
        var referenceWhite = _hdrToneMapping.EffectiveReferenceWhiteNits;
        var peak = FFmpegVideoColor.GetHdrPeakNits(_videoFrame, _videoStream, referenceWhite);
        if (_toneMapper is { } current
            && Math.Abs(current.SourcePeakNits - peak.Nits) < 1
            && current.ReferenceWhiteNits == referenceWhite)
        {
            return current;
        }

        _toneMapper = new HdrToneMapper(peak.Nits, referenceWhite);
        if (!_toneMapReported)
        {
            _toneMapReported = true;
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                "VideoDecoder",
                $"HDR10 video is tone mapped to SDR: peak {peak.Nits:0} nits ({peak.Source}), SDR white at {referenceWhite:0} nits.");
        }

        return _toneMapper;
    }

    private bool EnsureScaler(AVPixelFormat sourceFormat, VideoColorSpace colorSpace, AVPixelFormat targetFormat)
    {
        if (_swsVideo != null
            && sourceFormat == _scalerSourcePixelFormat
            && colorSpace == _scalerColorSpace
            && targetFormat == _scalerTargetPixelFormat)
        {
            return true;
        }

        if (_swsVideo != null)
        {
            ffmpeg.sws_freeContext(_swsVideo);
            _swsVideo = null;
        }

        _scalerSourcePixelFormat = sourceFormat;
        _scalerTargetPixelFormat = targetFormat;
        _swsVideo = ffmpeg.sws_getContext(
            Width, Height, sourceFormat,
            Width, Height, targetFormat,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);

        if (_swsVideo == null)
        {
            if (_enableVerboseLogging) Debug.WriteLine($"[VideoDecoder] Failed to create scaler for {ffmpeg.av_get_pix_fmt_name(sourceFormat)}.");
            _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            return false;
        }

        // Without this swscale converts every YUV frame as BT.601 limited range: HD video's
        // colors come out dull and full-range video's blacks and whites clip.
        _scalerColorSpace = colorSpace;
        AVPixFmtDescriptor* descriptor = ffmpeg.av_pix_fmt_desc_get(sourceFormat);
        if (descriptor != null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) == 0)
        {
            int* coefficients = ffmpeg.sws_getCoefficients(FFmpegVideoColor.ToSwsColorspace(colorSpace.Matrix));
            var table = new int_array4();
            for (uint i = 0; i < 4; i++)
                table[i] = coefficients[i];
            var sourceFullRange = colorSpace.Range == VideoColorRange.Full ? 1 : 0;
            _ = ffmpeg.sws_setColorspaceDetails(_swsVideo, in table, sourceFullRange, in table, 1, 0, 1 << 16, 1 << 16);
        }

        return true;
    }

    private void ProcessRemainingFrames()
    {
        if (_videoCodecCtx == null) return;

        var sendResult = ffmpeg.avcodec_send_packet(_videoCodecCtx, null);
        if (sendResult < 0
            && sendResult != ffmpeg.AVERROR_EOF
            && ShouldLogDecodeIssue(ref _packetSendIssueLogs))
        {
            if (_enableVerboseLogging) Debug.WriteLine($"[VideoDecoder] avcodec_send_packet flush failed: {FormatFFmpegError(sendResult)} ({sendResult})");
        }

        var processed = 0;
        while (_frameQueue.Count < MaxFrames && processed < MaxFrames)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_videoCodecCtx, _videoFrame);
            if (receiveResult != 0)
            {
                if (receiveResult != AvErrorAgain
                    && receiveResult != ffmpeg.AVERROR_EOF
                    && ShouldLogDecodeIssue(ref _frameReceiveIssueLogs))
                {
                    if (_enableVerboseLogging) Debug.WriteLine($"[VideoDecoder] avcodec_receive_frame flush failed: {FormatFFmpegError(receiveResult)} ({receiveResult})");
                }

                break;
            }

            ProcessVideoFrameFromDecoder();
            processed++;
        }
    }

    public void Close()
    {
        if (_swsVideo != null)
        {
            ffmpeg.sws_freeContext(_swsVideo);
            _swsVideo = null;
        }

        if (_videoFrame != null)
        {
            fixed (AVFrame** frame = &_videoFrame)
                ffmpeg.av_frame_free(frame);
        }

        if (_softwareFrame != null)
        {
            fixed (AVFrame** frame = &_softwareFrame)
                ffmpeg.av_frame_free(frame);
        }

        CleanupCodecContext();
        CleanupHardwareDevice();

        if (_packet != null)
        {
            fixed (AVPacket** pkt = &_packet)
                ffmpeg.av_packet_free(pkt);
        }

        if (_ownsDemuxer)
            _demuxer.Close();

        ClearFrameQueue();

        _videoStream = null;
        VideoStreamIndex = -1;
        _streamStartSeconds = 0;
        _streamStartInitialized = false;
        _discardFramesBeforeSeconds = null;
        EndOfStream = false;
        MaxFrames = MaxFramesCap;
        _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _scalerTargetPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _toneMapper = null;
        _toneMapScratch = null;
        _toneMapReported = false;
        _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        HardwareDevice = null;
        _hardwareDeviceTypeName = null;
        _getFormatCallback = null;
        IsHardwareAccelerated = false;
        HardwareAccelerationMethod = null;
        _packetSendIssueLogs = 0;
        _frameReceiveIssueLogs = 0;
        _hardwareTransferIssueLogs = 0;
        _loggedHardwarePixelFormat = false;
        _consecutiveHardwareDecodeFailures = 0;
        _hardwareFormatAccepted = 0;
        _hardwareFormatMissing = 0;
        _formatsOfferedWithoutHardware = null;
        _lastDecodeError = 0;
        DecoderName = null;
    }

    private void CleanupCodecContext()
    {
        if (_videoCodecCtx == null)
        {
            return;
        }

        FfmpegLogCapture.End(_videoCodecCtx);
        _logCaptureActive = false;
        _av1MetadataFilter?.Dispose();
        _av1MetadataFilter = null;
        fixed (AVCodecContext** ctx = &_videoCodecCtx)
            ffmpeg.avcodec_free_context(ctx);
    }

    private void CleanupHardwareDevice()
    {
        if (_hardwareDeviceContext == null)
        {
            return;
        }

        AVBufferRef* deviceContext = _hardwareDeviceContext;
        ffmpeg.av_buffer_unref(&deviceContext);
        _hardwareDeviceContext = null;
        HardwareDevice = null;
        _hardwareDeviceTypeName = null;
        _getFormatCallback = null;
    }

    public void Dispose()
    {
        Close();
        if (_ownsDemuxer)
            _demuxer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Called after <see cref="SharedDemuxer.Seek"/> has already seeked the
    /// container. Flushes codec buffers, clears <see cref="EndOfStream"/>, and
    /// sets the discard window — but does NOT seek the container. Only used in
    /// shared-demuxer mode; in standalone mode, <see cref="Seek"/> handles both.
    /// </summary>
    internal void OnContainerSeeked(double target, bool exact, bool preserveQueue = false)
    {
        Flush(clearQueue: !preserveQueue);
        EndOfStream = false;
        _discardFramesBeforeSeconds = exact ? target : null;
    }

    /// <summary>
    /// Seeks the shared demuxer's container to <paramref name="target"/> using
    /// the decoder's own <c>_streamStartSeconds</c> (first decoded frame PTS),
    /// which may differ from <c>stream->start_time</c>. Flushes both packet
    /// queues and clears both EOF flags. Called by
    /// <c>VideoPipeline.SeekCore</c> BEFORE either engine sets up its state.
    /// </summary>
    internal bool SeekSharedDemuxer(double target)
    {
        if (!_demuxer.IsOpen || _videoStream == null)
            return false;

        var streamStartSeconds = _streamStartInitialized ? _streamStartSeconds : GetStreamStartSeconds(_videoStream);
        var timestamp = (long)((target + streamStartSeconds) / ffmpeg.av_q2d(_videoStream->time_base));
        return _demuxer.Seek(VideoStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0;
    }

    public void Flush(bool clearQueue = true)
    {
        if (_videoCodecCtx != null)
        {
            ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
            _av1MetadataFilter?.Flush();
            if (_enableVerboseLogging) Debug.WriteLine("[VideoDecoder] Flushed codec buffers after seek");
        }
        if (clearQueue)
            ClearFrameQueue();
        EndOfStream = false;
    }

    public void ReturnFrame(VideoFrameData? frame)
    {
        if (frame == null)
        {
            return;
        }

        if (frame.Data != null)
        {
            _framePool.Return(frame.Data);
            return;
        }

        frame.ReleaseHardwareFrame();
    }

    private void ClearFrameQueue()
    {
        while (_frameQueue.TryDequeue(out VideoFrameData? frame))
        {
            ReturnFrame(frame);
        }
    }

    public bool Seek(double seconds, bool exact = true, bool preserveQueue = false)
    {
        if (!_demuxer.IsOpen || _videoStream == null)
            return false;

        var target = Math.Max(0, seconds);

        // The pipeline seeks a shared container once before either codec is
        // reset. A second seek here would invalidate the audio consumer's
        // coordinated packet queues.
        if (!_ownsDemuxer)
        {
            OnContainerSeeked(target, exact, preserveQueue);
            return true;
        }

        var streamStartSeconds = _streamStartInitialized ? _streamStartSeconds : GetStreamStartSeconds(_videoStream);
        var timestamp = (long)((target + streamStartSeconds) / ffmpeg.av_q2d(_videoStream->time_base));
        if (_demuxer.Seek(VideoStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            return false;

        Flush(clearQueue: !preserveQueue);
        EndOfStream = false;
        _discardFramesBeforeSeconds = exact ? target : null;
        return true;
    }

    private static double GetStreamStartSeconds(AVStream* stream) =>
        stream == null || stream->start_time == ffmpeg.AV_NOPTS_VALUE
            ? 0
            : Math.Max(0, stream->start_time * ffmpeg.av_q2d(stream->time_base));

}

internal enum VideoFrameKind
{
    CpuBgra,
    NativeHardware
}

internal sealed unsafe class VideoFrameData
{
    public VideoFrameKind Kind { get; init; } = VideoFrameKind.CpuBgra;
    public byte[]? Data { get; init; }
    public AVFrame* HardwareFrame { get; set; }
    public NativeVideoFrameResource? NativeResource { get; init; }
    public required int Length { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required double PresentationTime { get; init; }

    public bool IsNativeHardwareFrame => Kind == VideoFrameKind.NativeHardware && NativeResource is { IsValid: true };

    public void ReleaseHardwareFrame()
    {
        if (HardwareFrame == null)
        {
            return;
        }

        AVFrame* frame = HardwareFrame;
        ffmpeg.av_frame_free(&frame);
        HardwareFrame = null;
    }
}
