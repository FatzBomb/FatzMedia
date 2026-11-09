using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Software;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-039 G7: the Direct3D11 renderer draws a CD+G overlay as the software renderer does. Before
/// a memory preset sets the background colour (index -1) the whole plane is transparent, so the
/// clear colour shows; afterwards only the background index is. WARP, at the CD+G frame size so
/// both renderers sample the same pixel.
/// </summary>
public sealed class D3D11CdgParityTests
{
    const uint BlueClear = 0xFF0000FF;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void CdgOverlay_MatchesTheSoftwareRenderer(int backgroundColorIndex)
    {
        // Pixel 0 uses palette entry 1 (red); every other pixel entry 0 (black). The renderers
        // need the full palette.
        var indices = new byte[CdgConstants.TotalPixels];
        indices[0] = 1;
        var palette = Enumerable.Repeat(unchecked((int)0xFF000000), CdgConstants.ColorCount).ToArray();
        palette[1] = unchecked((int)0xFFFF0000);
        var snapshot = new CdgRenderSnapshot(
            1,
            1,
            indices,
            palette,
            0,
            backgroundColorIndex,
            []);
        var frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, BlueClear) with
        {
            CdgOverlay = snapshot
        };

        var software = new SoftwareCompositionRenderer()
            .Render(frame, CdgConstants.FrameWidth, CdgConstants.FrameHeight)
            .Pixels.ToArray();

        using var bridge = D3D11MultiTargetTests.CreateWarpBridge();
        var sink = new D3D11MultiTargetTests.CopyingFrameSink();
        using var handle = new MediaPresentationFrameSinkHandle(sink);
        Assert.True(bridge.AttachSurface(new MediaPresentationSurfaceDescriptor(
            MediaPresentationSurfaceDescriptor.PrimarySurfaceId,
            MediaPresentationSurfaceRole.Primary,
            handle.CreateSurfaceDescriptor(),
            CdgConstants.FrameWidth,
            CdgConstants.FrameHeight)));
        Assert.True(bridge.PresentComposition(frame), bridge.LastPresentFailureReason);

        Assert.Equal(software.Length, sink.Pixels.Length);
        foreach (var pixel in new[] { 0, 1, (CdgConstants.FrameWidth * 100) + 150 })
            Assert.Equal(software.AsSpan(pixel * 4, 4).ToArray(), sink.Pixels.AsSpan(pixel * 4, 4).ToArray());

        if (backgroundColorIndex < 0)
        {
            // Transparent plane: the blue clear everywhere, the lyric pixel included.
            Assert.Equal(new byte[] { 0xFF, 0, 0, 0xFF }, sink.Pixels[..4]);
        }
        else
        {
            Assert.Equal(new byte[] { 0, 0, 0xFF, 0xFF }, sink.Pixels[..4]);
        }
    }
}
