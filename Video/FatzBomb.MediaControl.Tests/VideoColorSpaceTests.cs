using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Video;
using FFmpeg.AutoGen;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-019: decoded frames carry their own YCbCr matrix and range, so neither swscale nor a GPU
/// video processor falls back to a default (BT.601, or a driver's range guess).
/// </summary>
public sealed class VideoColorSpaceTests
{
    [Theory]
    [InlineData(1920, 1080, VideoColorMatrix.Bt709)]
    [InlineData(1280, 720, VideoColorMatrix.Bt709)]
    [InlineData(3840, 2160, VideoColorMatrix.Bt709)]
    [InlineData(720, 576, VideoColorMatrix.Bt601)]
    [InlineData(720, 480, VideoColorMatrix.Bt601)]
    [InlineData(640, 360, VideoColorMatrix.Bt601)]
    public void AnUnspecifiedMatrix_FollowsTheFrameSize(int width, int height, VideoColorMatrix expected)
    {
        VideoColorSpace resolved = default(VideoColorSpace).Resolve(width, height);

        Assert.Equal(expected, resolved.Matrix);
        Assert.Equal(VideoColorRange.Limited, resolved.Range);
    }

    [Fact]
    public void TaggedValues_AreKept()
    {
        var tagged = new VideoColorSpace(VideoColorMatrix.Bt601, VideoColorRange.Full);

        Assert.Equal(tagged, tagged.Resolve(1920, 1080));
    }

    [Theory]
    [InlineData(AVColorSpace.AVCOL_SPC_BT709, VideoColorMatrix.Bt709)]
    [InlineData(AVColorSpace.AVCOL_SPC_SMPTE240M, VideoColorMatrix.Bt709)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT470BG, VideoColorMatrix.Bt601)]
    [InlineData(AVColorSpace.AVCOL_SPC_SMPTE170M, VideoColorMatrix.Bt601)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT2020_NCL, VideoColorMatrix.Bt2020)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, VideoColorMatrix.Unspecified)]
    [InlineData(AVColorSpace.AVCOL_SPC_RGB, VideoColorMatrix.Unspecified)]
    public void FFmpegMatrixTags_Map(AVColorSpace tag, VideoColorMatrix expected) =>
        Assert.Equal(expected, FFmpegVideoColor.FromTags(tag, AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUV420P).Matrix);

    [Theory]
    [InlineData(AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUV420P, VideoColorRange.Limited)]
    [InlineData(AVColorRange.AVCOL_RANGE_JPEG, AVPixelFormat.AV_PIX_FMT_YUV420P, VideoColorRange.Full)]
    [InlineData(AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUV420P, VideoColorRange.Unspecified)]
    [InlineData(AVColorRange.AVCOL_RANGE_UNSPECIFIED, AVPixelFormat.AV_PIX_FMT_YUVJ420P, VideoColorRange.Full)]
    [InlineData(AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUVJ420P, VideoColorRange.Limited)]
    public void FFmpegRangeTags_Map(AVColorRange tag, AVPixelFormat format, VideoColorRange expected) =>
        Assert.Equal(expected, FFmpegVideoColor.FromTags(AVColorSpace.AVCOL_SPC_UNSPECIFIED, tag, format).Range);

    [Fact]
    public void TheLogDescription_NamesTheColorSpaceAndHdr()
    {
        var tagged = FFmpegVideoColor.FromTags(AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);

        Assert.Equal(
            "BT.2020 limited range; HDR10/PQ, shown as SDR without tone mapping (looks washed out)",
            FFmpegVideoColor.Describe(tagged, 3840, 2160, AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084));
        Assert.Equal(
            "BT.709 limited range (assumed)",
            FFmpegVideoColor.Describe(default, 1920, 1080, AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED));
    }

    [Fact]
    public void TheLogDescription_SaysHlgIsToneMapped()
    {
        var tagged = FFmpegVideoColor.FromTags(AVColorSpace.AVCOL_SPC_BT2020_NCL, AVColorRange.AVCOL_RANGE_MPEG, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);

        // Stage 042A: HLG is tone mapped like HDR10; the line no longer says it is shown untone-mapped.
        var toneMapped = FFmpegVideoColor.Describe(tagged, 1920, 1080, AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67, toneMapsHdr: true);
        Assert.Equal("BT.2020 limited range; HLG HDR, tone mapped to SDR", toneMapped);
        Assert.DoesNotContain("without tone mapping", toneMapped, StringComparison.Ordinal);
        Assert.Equal(
            "BT.2020 limited range; HDR10/PQ, tone mapped to SDR",
            FFmpegVideoColor.Describe(tagged, 1920, 1080, AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, toneMapsHdr: true));
        Assert.Equal(
            "BT.2020 limited range; HLG HDR, shown as SDR without tone mapping",
            FFmpegVideoColor.Describe(tagged, 1920, 1080, AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67, toneMapsHdr: false));
    }

    [Theory]
    [InlineData(VideoColorMatrix.Bt601, 5)]
    [InlineData(VideoColorMatrix.Bt709, 1)]
    [InlineData(VideoColorMatrix.Bt2020, 9)]
    public void SwscaleTables_MatchTheMatrix(VideoColorMatrix matrix, int expected) =>
        Assert.Equal(expected, FFmpegVideoColor.ToSwsColorspace(matrix));
}
