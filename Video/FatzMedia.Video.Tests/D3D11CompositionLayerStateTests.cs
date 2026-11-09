using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Composition.D3D11;

namespace FatzMedia.Video.Tests;

public sealed class D3D11CompositionLayerStateTests
{
    [Fact]
    public void BaseVisualPixelShaderCompiles()
    {
        byte[] bytecode = D3D11MediaCompositionPresenter.CompileShaderBytecode(
            D3D11MediaCompositionPresenter.CdgShaderSource,
            "PSBaseVisual",
            "ps_5_0");

        Assert.NotEmpty(bytecode);
    }

    [Fact]
    public void BaseVisualPremultipliedPixelShaderCompiles()
    {
        byte[] bytecode = D3D11MediaCompositionPresenter.CompileShaderBytecode(
            D3D11MediaCompositionPresenter.CdgShaderSource,
            "PSBaseVisualPremultiplied",
            "ps_5_0");

        Assert.NotEmpty(bytecode);
    }

    [Fact]
    public void ShaderBytecodeIsCompiledOncePerProcess()
    {
        byte[] first = D3D11MediaCompositionPresenter.GetShaderBytecode(
            D3D11MediaCompositionPresenter.CdgShaderSource,
            "PSSolid",
            "ps_5_0");
        byte[] second = D3D11MediaCompositionPresenter.GetShaderBytecode(
            D3D11MediaCompositionPresenter.CdgShaderSource,
            "PSSolid",
            "ps_5_0");

        Assert.NotEmpty(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void TransformChangeRequiresRenderWithoutUpload()
    {
        using var state = new D3D11CompositionLayerState(MediaCompositionLayerId.Background);
        var initial = new MediaCompositionLayer
        {
            Id = MediaCompositionLayerId.Background,
            SourceKind = MediaCompositionLayerSourceKind.BgraPixels,
            Revision = 1,
            Width = 1,
            Height = 1,
            Stride = 4
        };
        state.Update(initial);
        state.MarkUploaded(initial.Revision);
        state.MarkRendered(initial.Revision);
        state.MarkPlanConsumed();

        state.Update(initial with
        {
            BaseVisualTransform = new BaseVisualTransform { Contrast = 1.5f }
        });

        Assert.True(state.DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.TransformChanged));
        Assert.True(state.DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.RenderRequired));
        Assert.False(state.DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.UploadRequired));
        Assert.True(state.NeedsRender);
        Assert.False(state.NeedsUpload);
    }
}
