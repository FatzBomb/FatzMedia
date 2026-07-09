using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBombMedia.MediaControl.Core.Configuration;
using FFmpeg.AutoGen;

namespace FatzBombMedia.MediaControl.Core.Video;

/// <summary>
/// Handles FFmpeg video decoding operations separate from timing and audio concerns
/// </summary>
internal sealed unsafe class VideoDecoder(
    VideoDecodingMethod decodingMethod = VideoDecodingMethod.Auto,
    bool enableHardwareFrameOutput = true,
    bool enableDiagnostics = true,
    bool enablePerformanceLogging = false,
    bool enableVerboseLogging = false,
    bool scaleMaxFramesByResolution = false) : IDisposable
{
    private static readonly AVHWDeviceType[] DefaultPreferredHardwareDevices =
    [
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2
    ];

    private readonly VideoDecodingMethod _decodingMethod = decodingMethod;
    private readonly bool _enableHardwareFrameOutput = enableHardwareFrameOutput;
    private readonly bool _enableDiagnostics = enableDiagnostics;
    private readonly bool _enablePerformanceLogging = enablePerformanceLogging;
    private readonly bool _enableVerboseLogging = enableVerboseLogging;
    private readonly bool _scaleMaxFramesByResolution = scaleMaxFramesByResolution;
    private readonly AVFormatContext* _fmt;
    private AVCodecContext* _videoCodecCtx;
    private AVStream* _videoStream;
    private SwsContext* _swsVideo;
    private int _videoIndex = -1;

    private AVFrame* _videoFrame;
    private AVFrame* _softwareFrame;
    private AVPacket* _packet;
    private AVBufferRef* _hardwareDeviceContext;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVPixelFormat _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
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
    public IntPtr D3D11DevicePointer { get; private set; }
    public IntPtr D3D11DeviceContextPointer { get; private set; }
    public bool EndOfStream { get; private set; }
    public int MaxFrames { get; private set; } = MaxFramesCap;

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

    /// <summary>
    /// True when hardware decoding is configured and the hardware path has failed to produce a
    /// frame several times in a row with no successful frame in between — a strong hint that the
    /// underlying hardware-decode D3D11 device may have been lost. The caller confirms via
    /// <see cref="D3D11DevicePointer"/> and a device-removed-reason check before acting.
    /// </summary>
    public bool IsHardwareDecodeRepeatedlyFailing =>
        HardwareAccelerationMethod != null && _consecutiveHardwareDecodeFailures >= HardwareDecodeLossFailureThreshold;

    public static VideoDecoder FromConfiguration(MediaPlayerConfiguration configuration, bool enableHardwareFrameOutput = true, bool scaleMaxFramesByResolution = false) =>
        new(configuration.VideoDecoding,
            enableHardwareFrameOutput,
            configuration.EnableLogging,
            configuration.EnablePerformanceLogging,
            configuration.EnableVerboseLogging,
            scaleMaxFramesByResolution);

    public bool Open(string path)
    {
        Close();

        fixed (AVFormatContext** pfmt = &_fmt)
        {
            if (ffmpeg.avformat_open_input(pfmt, path, null, null) < 0) return false;
        }

        if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
        {
            Close();
            return false;
        }

        _videoIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (_videoIndex < 0) { Close(); return false; }

        _videoStream = _fmt->streams[_videoIndex];

        // Discard non-video streams so av_read_frame only returns video packets. This
        // matters for background video looping: audio packets that outlive the video
        // stream cause a gap (no video output) before EOF triggers a restart, and
        // interleaved AVI audio chunks waste I/O on every decode pass. VideoAudioEngine
        // opens its own format context, so this does not affect main-pipeline audio.
        for (uint i = 0; i < _fmt->nb_streams; i++)
        {
            if ((int)i != _videoIndex)
                _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
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
        var formatDuration = _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        DurationSeconds = videoStreamDuration > 0 ? videoStreamDuration : formatDuration;

        if (_enableDiagnostics) Debug.WriteLine($"[VideoDecoder] Opened: {Width}x{Height}, {FramesPerSecond:F2}fps, {CodecName}, duration={DurationSeconds:F3}s");
        return true;
    }

    public bool DecodeNextFrame()
    {
        if (_fmt == null || _packet == null) return false;
        if (EndOfStream) return false;
        if (_frameQueue.Count >= MaxFrames) return false;

        var readResult = ffmpeg.av_read_frame(_fmt, _packet);
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

        var isVideoPacket = _packet->stream_index == _videoIndex;
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

    private static bool HasPreferredHardwareConfig(AVCodec* codec)
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

            foreach (AVHWDeviceType deviceType in DefaultPreferredHardwareDevices)
            {
                if (config->device_type == deviceType)
                {
                    return true;
                }
            }
        }
    }

    private static bool IsPreferredHardwareDecoderName(string codecName) => codecName.Contains("d3d11", StringComparison.OrdinalIgnoreCase)
            || codecName.Contains("dxva", StringComparison.OrdinalIgnoreCase)
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
        foreach (AVHWDeviceType deviceType in DefaultPreferredHardwareDevices)
        {
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
                    var isBestPixelFormat = IsPreferredHardwarePixelFormat(config->device_type, config->pix_fmt);
                    if ((preferencePass == 0 && !isBestPixelFormat) || (preferencePass == 1 && isBestPixelFormat))
                    {
                        continue;
                    }

                    if (TryCreateHardwareDevice(config->device_type, config->pix_fmt))
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

    private static bool IsPreferredHardwarePixelFormat(AVHWDeviceType deviceType, AVPixelFormat pixelFormat) => deviceType switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => pixelFormat == AVPixelFormat.AV_PIX_FMT_D3D11,
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D12VA => true,
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => true,
        _ => false
    };

    private bool TryCreateHardwareDevice(AVHWDeviceType deviceType, AVPixelFormat pixelFormat)
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
        HardwareAccelerationMethod = ffmpeg.av_hwdevice_get_type_name(deviceType);
        UpdateD3D11HardwarePointers(deviceType);

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

    private void UpdateD3D11HardwarePointers(AVHWDeviceType deviceType)
    {
        D3D11DevicePointer = IntPtr.Zero;
        D3D11DeviceContextPointer = IntPtr.Zero;

        if (deviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA || _hardwareDeviceContext == null)
            return;

        var deviceContext = (AVHWDeviceContext*)_hardwareDeviceContext->data;
        if (deviceContext == null || deviceContext->hwctx == null)
            return;

        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        D3D11DevicePointer = (IntPtr)d3d11Context->device;
        D3D11DeviceContextPointer = (IntPtr)d3d11Context->device_context;
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
        if (TryQueueD3D11HardwareFrame(pts, totalStart))
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
            if (!EnsureScaler(sourceFormat))
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
            fixed (byte* framePointer = frameData)
            {
                dstData[0] = framePointer;
                dstLinesize[0] = _videoLinesize;

                var scaleStart = Stopwatch.GetTimestamp();
                _ = ffmpeg.sws_scale(_swsVideo, sourceFrame->data, sourceFrame->linesize,
                    0, Height, dstData, dstLinesize);
                scaleMs = GetElapsedMilliseconds(scaleStart);
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

    private bool TryQueueD3D11HardwareFrame(double pts, long totalStart)
    {
        if (!_enableHardwareFrameOutput
            || _hardwareDeviceContext == null
            || D3D11DevicePointer == IntPtr.Zero
            || (AVPixelFormat)_videoFrame->format != AVPixelFormat.AV_PIX_FMT_D3D11
            || _videoFrame->data[0] == null)
        {
            return false;
        }

        AVFrame* clonedFrame = ffmpeg.av_frame_clone(_videoFrame);
        if (clonedFrame == null)
        {
            return false;
        }

        _frameQueue.Enqueue(new VideoFrameData
        {
            Kind = VideoFrameKind.D3D11Hardware,
            HardwareFrame = clonedFrame,
            D3D11DevicePointer = D3D11DevicePointer,
            D3D11DeviceContextPointer = D3D11DeviceContextPointer,
            D3D11TexturePointer = (IntPtr)clonedFrame->data[0],
            D3D11ArraySlice = (int)(nint)clonedFrame->data[1],
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

    private bool EnsureScaler(AVPixelFormat sourceFormat)
    {
        if (_swsVideo != null && sourceFormat == _scalerSourcePixelFormat)
        {
            return true;
        }

        if (_swsVideo != null)
        {
            ffmpeg.sws_freeContext(_swsVideo);
            _swsVideo = null;
        }

        _scalerSourcePixelFormat = sourceFormat;
        _swsVideo = ffmpeg.sws_getContext(
            Width, Height, sourceFormat,
            Width, Height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)SwsFlags.SWS_BILINEAR, null, null, null);

        if (_swsVideo == null)
        {
            if (_enableVerboseLogging) Debug.WriteLine($"[VideoDecoder] Failed to create scaler for {ffmpeg.av_get_pix_fmt_name(sourceFormat)}.");
            _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            return false;
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

        if (_fmt != null)
        {
            fixed (AVFormatContext** fmt = &_fmt)
                ffmpeg.avformat_close_input(fmt);
        }

        ClearFrameQueue();

        _videoStream = null;
        _videoIndex = -1;
        _streamStartSeconds = 0;
        _streamStartInitialized = false;
        _discardFramesBeforeSeconds = null;
        EndOfStream = false;
        MaxFrames = MaxFramesCap;
        _scalerSourcePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        D3D11DevicePointer = IntPtr.Zero;
        D3D11DeviceContextPointer = IntPtr.Zero;
        _getFormatCallback = null;
        IsHardwareAccelerated = false;
        HardwareAccelerationMethod = null;
        _packetSendIssueLogs = 0;
        _frameReceiveIssueLogs = 0;
        _hardwareTransferIssueLogs = 0;
        _loggedHardwarePixelFormat = false;
        _consecutiveHardwareDecodeFailures = 0;
    }

    private void CleanupCodecContext()
    {
        if (_videoCodecCtx == null)
        {
            return;
        }

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
        D3D11DevicePointer = IntPtr.Zero;
        D3D11DeviceContextPointer = IntPtr.Zero;
        _getFormatCallback = null;
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    public void Flush(bool clearQueue = true)
    {
        if (_videoCodecCtx != null)
        {
            ffmpeg.avcodec_flush_buffers(_videoCodecCtx);
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
        if (_fmt == null || _videoStream == null)
            return false;

        var target = Math.Max(0, seconds);
        var streamStartSeconds = _streamStartInitialized ? _streamStartSeconds : GetStreamStartSeconds(_videoStream);
        var timestamp = (long)((target + streamStartSeconds) / ffmpeg.av_q2d(_videoStream->time_base));
        if (ffmpeg.av_seek_frame(_fmt, _videoIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
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
    D3D11Hardware
}

internal sealed unsafe class VideoFrameData
{
    public VideoFrameKind Kind { get; init; } = VideoFrameKind.CpuBgra;
    public byte[]? Data { get; init; }
    public AVFrame* HardwareFrame { get; set; }
    public IntPtr D3D11DevicePointer { get; init; }
    public IntPtr D3D11DeviceContextPointer { get; init; }
    public IntPtr D3D11TexturePointer { get; init; }
    public int D3D11ArraySlice { get; init; }
    public required int Length { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Stride { get; init; }
    public required double PresentationTime { get; init; }

    public bool IsD3D11HardwareFrame => Kind == VideoFrameKind.D3D11Hardware && D3D11TexturePointer != IntPtr.Zero;

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
