using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using Vortice.DXGI;

namespace FatzBomb.MediaControl.Tests;

/// <summary>ENG-019: the video processor is told the frame's matrix and range.</summary>
public sealed class D3D11VideoColorSpaceTests
{
    [Theory]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Limited, ColorSpaceType.YcbcrStudioG22LeftP601)]
    [InlineData(VideoColorMatrix.Bt601, VideoColorRange.Full, ColorSpaceType.YcbcrFullG22LeftP601)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Limited, ColorSpaceType.YcbcrStudioG22LeftP709)]
    [InlineData(VideoColorMatrix.Bt709, VideoColorRange.Full, ColorSpaceType.YcbcrFullG22LeftP709)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Limited, ColorSpaceType.YcbcrStudioG22LeftP2020)]
    [InlineData(VideoColorMatrix.Bt2020, VideoColorRange.Full, ColorSpaceType.YcbcrFullG22LeftP2020)]
    public void ResolvedColorSpaces_MapToDxgi(VideoColorMatrix matrix, VideoColorRange range, ColorSpaceType expected) =>
        Assert.Equal(expected, D3D11VideoProcessorPresenter.ToDxgiColorSpace(new VideoColorSpace(matrix, range)));
}
