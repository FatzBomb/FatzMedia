using FFmpeg.AutoGen;

namespace FatzMedia.Video.Windows.Video;

static class DxvaProfileGuids
{
    public static readonly Guid H264_VLD_NoFGT = new("1b81be68-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid H264_VLD_FGT = new("1b81be69-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid HEVC_VLD_Main = new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0");
    public static readonly Guid HEVC_VLD_Main10 = new("107af0e0-ef1a-4d19-aba8-67a163073d13");
    public static readonly Guid MPEG2_VLD = new("ee27417f-5e28-4e65-beea-1d26b508adc9");
    public static readonly Guid VC1_VLD = new("1b81beA3-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid VC1_VLD2010 = new("1b81beA4-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid WMV9_A_VLD = new("1b81be90-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid WMV9_B_VLD = new("1b81be91-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid WMV9_C_VLD = new("1b81be92-a0c7-11d3-b984-00c04f2e73c5");
    public static readonly Guid VP9_VLD_Profile0 = new("463707f8-a1d0-4585-876d-83aa6d60b89e");
    public static readonly Guid VP9_VLD_10bit_Profile2 = new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7");
    public static readonly Guid AV1_VLD_Profile0 = new("6a9bd3c3-19e1-4a6c-bb60-0365a9582a48");
    public static readonly Guid AV1_VLD_Profile1 = new("b0bade3b-1f66-4d78-909b-3bc0a68c9c1f");
    public static readonly Guid AV1_VLD_Profile2 = new("9b0b4c9f-6664-4dd3-8ccc-a567f7af4012");
    public static readonly Guid VP8_VLD = new("90b5ea5a-93e1-40b1-b6c8-eda6a3011cc4");

    public static IReadOnlyList<Guid> GetProfilesForCodec(AVCodecID codecId) => codecId switch
    {
        AVCodecID.AV_CODEC_ID_H264 => [H264_VLD_NoFGT, H264_VLD_FGT],
        AVCodecID.AV_CODEC_ID_HEVC => [HEVC_VLD_Main, HEVC_VLD_Main10],
        AVCodecID.AV_CODEC_ID_AV1 => [AV1_VLD_Profile0, AV1_VLD_Profile1, AV1_VLD_Profile2],
        AVCodecID.AV_CODEC_ID_VP9 => [VP9_VLD_Profile0, VP9_VLD_10bit_Profile2],
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => [MPEG2_VLD],
        AVCodecID.AV_CODEC_ID_VC1 => [VC1_VLD, VC1_VLD2010],
        AVCodecID.AV_CODEC_ID_WMV3 => [WMV9_A_VLD, WMV9_B_VLD, WMV9_C_VLD],
        AVCodecID.AV_CODEC_ID_VP8 => [VP8_VLD],
        _ => []
    };
}
