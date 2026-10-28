using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using FatzBomb.MediaControl.Rendering.Software;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// ENG-039 G7: the Direct3D11 renderer draws a CD+G overlay as the software renderer does. Before
/// a memory preset sets the background colour (index -1) the whole plane is transparent, so the
/// clear colour shows; afterwards only the background index is. WARP, at the CD+G frame size so
/// both renderers sample the same pixel.
/// </summary>
public sealed class D3D11CdgParityTests
{
    private const uint BlueClear = 0xFF0000FF;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void CdgOverlay_MatchesTheSoftwareRenderer(int backgroundColorIndex)
    {
        // Pixel 0 uses palette entry 1 (red); every other pixel entry 0 (black).
        var indices = new byte[CdgConstants.TotalPixels];
        indices[0] = 1;
        var snapshot = new CdgRenderSnapshot(
            1,
            1,
            indices,
            new[] { unchecked((int)0xFF000000), unchecked((int)0xFFFF0000) },
            0,
            backgroundColorIndex,
            []);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, BlueClear) with
        {
            CdgOverlay = snapshot
        };

        byte[] software = new SoftwareCompositionRenderer()
            .Render(frame, CdgConstants.FrameWidth, CdgConstants.FrameHeight)
            .Pixels.ToArray();

        using NativeD3D11PresentationBridge bridge = D3D11MultiTargetTests.CreateWarpBridge();
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
