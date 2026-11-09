using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Composition.Planning;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;

namespace FatzMedia.Video.Tests;

public sealed class D3D11RendererPresentationServicesTests
{
    [Fact]
    public void DirectCompositionBaseStateChangesWhenStaticImageTransformChanges()
    {
        var target = new D3D11PresentationTargetSnapshot(
            "overlay",
            null!,
            1,
            1,
            IntPtr.Zero,
            D3D11PresentationBackendKind.DirectCompositionSingleTarget,
            "test",
            1,
            CompositionTargetKind.FinalPresentation);
        MediaCompositionFrame frame = MediaCompositionFrame.Empty() with
        {
            BackgroundBgra = new byte[4],
            BackgroundWidth = 1,
            BackgroundHeight = 1,
            BackgroundStride = 4,
            BackgroundRevision = 7
        };

        D3D11RendererPresentationServices.DirectCompositionOverlaySplitBaseState original =
            D3D11RendererPresentationServices.CreateDirectCompositionOverlaySplitBaseState(frame, target);
        D3D11RendererPresentationServices.DirectCompositionOverlaySplitBaseState transformed =
            D3D11RendererPresentationServices.CreateDirectCompositionOverlaySplitBaseState(
                frame with { BaseVisualTransform = new BaseVisualTransform { RedGain = 0.5f } },
                target);

        Assert.NotEqual(original, transformed);
    }
}
