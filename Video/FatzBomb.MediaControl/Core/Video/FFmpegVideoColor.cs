using FatzBomb.MediaControl.Core.Graphics;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Core.Video;

/// <summary>
/// Maps FFmpeg's color tags on a decoded frame to <see cref="VideoColorSpace"/>, and that to
/// swscale's coefficient tables, so every conversion to RGB uses the stream's own matrix and
/// range instead of a default (swscale: BT.601 limited; GPU video processors: driver's guess).
/// </summary>
internal static unsafe class FFmpegVideoColor
{
    /// <summary>The peak assumed for HDR10 video without usable metadata (a common mastering peak).</summary>
    public const double DefaultHdrPeakNits = 1000;

    public static VideoColorSpace FromTags(AVColorSpace colorSpace, AVColorRange colorRange, AVPixelFormat pixelFormat)
    {
        VideoColorMatrix matrix = colorSpace switch
        {
            AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_SMPTE240M => VideoColorMatrix.Bt709,
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M or AVColorSpace.AVCOL_SPC_FCC => VideoColorMatrix.Bt601,
            AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => VideoColorMatrix.Bt2020,
            _ => VideoColorMatrix.Unspecified
        };

        VideoColorRange range = colorRange switch
        {
            AVColorRange.AVCOL_RANGE_MPEG => VideoColorRange.Limited,
            AVColorRange.AVCOL_RANGE_JPEG => VideoColorRange.Full,
            _ => IsJpegRangeFormat(pixelFormat) ? VideoColorRange.Full : VideoColorRange.Unspecified
        };

        return new VideoColorSpace(matrix, range);
    }

    /// <summary>
    /// The stream's color space for the decode-path log line, for example "BT.709 limited
    /// range" or "BT.601 limited range (assumed)" when the stream has no tags, plus a note on
    /// HDR video, which is shown without tone mapping and so looks washed out.
    /// </summary>
    public static string Describe(VideoColorSpace tagged, int width, int height, AVColorTransferCharacteristic transfer, bool toneMapsHdr10 = false)
    {
        VideoColorSpace resolved = tagged.Resolve(width, height);
        var matrix = resolved.Matrix switch
        {
            VideoColorMatrix.Bt709 => "BT.709",
            VideoColorMatrix.Bt2020 => "BT.2020",
            _ => "BT.601"
        };
        var range = resolved.Range == VideoColorRange.Full ? "full range" : "limited range";
        var assumed = tagged.Matrix == VideoColorMatrix.Unspecified || tagged.Range == VideoColorRange.Unspecified ? " (assumed)" : string.Empty;
        var hdr = transfer switch
        {
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => toneMapsHdr10
                ? "; HDR10/PQ, tone mapped to SDR"
                : "; HDR10/PQ, shown as SDR without tone mapping (looks washed out)",
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => "; HLG HDR, shown as SDR without tone mapping",
            _ => string.Empty
        };
        return $"{matrix} {range}{assumed}{hdr}";
    }

    /// <summary>
    /// The HDR10 peak to tone map from: the content's MaxCLL, else the mastering display's peak,
    /// read from the frame's side data or the stream's, else <see cref="DefaultHdrPeakNits"/>.
    /// Values below <paramref name="referenceWhiteNits"/> or above PQ's 10000 nits are ignored as
    /// unusable (encoders write 0 or placeholders).
    /// </summary>
    public static (double Nits, string Source) GetHdrPeakNits(AVFrame* frame, AVStream* stream, double referenceWhiteNits)
    {
        double frameMaxCll = 0, frameMastering = 0, streamMaxCll = 0, streamMastering = 0;
        if (frame != null)
        {
            AVFrameSideData* light = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
            AVFrameSideData* mastering = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
            frameMaxCll = light != null ? ReadMaxCll((AVContentLightMetadata*)light->data) : 0;
            frameMastering = mastering != null ? ReadMasteringPeak((AVMasteringDisplayMetadata*)mastering->data) : 0;
        }

        if (stream != null && stream->codecpar != null)
        {
            AVCodecParameters* parameters = stream->codecpar;
            AVPacketSideData* light = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
            AVPacketSideData* mastering = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
            streamMaxCll = light != null ? ReadMaxCll((AVContentLightMetadata*)light->data) : 0;
            streamMastering = mastering != null ? ReadMasteringPeak((AVMasteringDisplayMetadata*)mastering->data) : 0;
        }

        // A frame value an encoder left at 0 or a placeholder falls back to the stream's.
        var maxCll = IsUsablePeak(frameMaxCll, referenceWhiteNits) ? frameMaxCll : streamMaxCll;
        var masteringPeak = IsUsablePeak(frameMastering, referenceWhiteNits) ? frameMastering : streamMastering;
        return ChoosePeak(maxCll, masteringPeak, referenceWhiteNits);

        static double ReadMaxCll(AVContentLightMetadata* light) => light != null ? light->MaxCLL : 0;

        static double ReadMasteringPeak(AVMasteringDisplayMetadata* mastering) =>
            mastering != null && mastering->has_luminance != 0 && mastering->max_luminance.den != 0
                ? ffmpeg.av_q2d(mastering->max_luminance)
                : 0;
    }

    /// <summary>The peak policy of <see cref="GetHdrPeakNits"/> on plain values.</summary>
    public static (double Nits, string Source) ChoosePeak(double maxCll, double masteringPeak, double referenceWhiteNits)
    {
        if (IsUsablePeak(maxCll, referenceWhiteNits))
            return (maxCll, "MaxCLL");
        if (IsUsablePeak(masteringPeak, referenceWhiteNits))
            return (masteringPeak, "mastering display");
        return (DefaultHdrPeakNits, "assumed, no usable metadata");
    }

    private static bool IsUsablePeak(double nits, double referenceWhiteNits) =>
        nits >= referenceWhiteNits && nits <= 10000;

    /// <summary>swscale's <c>SWS_CS_*</c> table for a resolved matrix.</summary>
    public static int ToSwsColorspace(VideoColorMatrix matrix) => matrix switch
    {
        VideoColorMatrix.Bt709 => ffmpeg.SWS_CS_ITU709,
        VideoColorMatrix.Bt2020 => ffmpeg.SWS_CS_BT2020,
        _ => ffmpeg.SWS_CS_ITU601
    };

    private static bool IsJpegRangeFormat(AVPixelFormat pixelFormat) => pixelFormat is
        AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or AVPixelFormat.AV_PIX_FMT_YUVJ422P
        or AVPixelFormat.AV_PIX_FMT_YUVJ444P
        or AVPixelFormat.AV_PIX_FMT_YUVJ440P
        or AVPixelFormat.AV_PIX_FMT_YUVJ411P;
}
