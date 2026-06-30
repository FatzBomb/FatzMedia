namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Scheduling intent for a retained composition layer. This is deliberately
/// descriptive in Stage 10K so future multi-rate composition can consume it
/// without forcing the current compositor to change behavior.
/// </summary>
internal readonly record struct D3D11CompositionLayerSchedule(
    D3D11CompositionLayerUpdateCadence Cadence,
    D3D11CompositionLayerRenderTargetKind TargetKind,
    double SourceFramesPerSecond,
    bool CanReuseRetainedOutput)
{
    public bool HasSourceRate => double.IsFinite(SourceFramesPerSecond) && SourceFramesPerSecond > 0d;

    public TimeSpan? SourceFrameInterval => HasSourceRate
        ? TimeSpan.FromSeconds(1d / SourceFramesPerSecond)
        : null;

    public override string ToString()
    {
        var fps = HasSourceRate ? $"@{SourceFramesPerSecond:0.###}fps" : string.Empty;
        return $"{Cadence}{fps}/{TargetKind}/reuse={CanReuseRetainedOutput}";
    }

    public static D3D11CompositionLayerSchedule None { get; } = new(
        D3D11CompositionLayerUpdateCadence.None,
        D3D11CompositionLayerRenderTargetKind.None,
        0d,
        CanReuseRetainedOutput: false);

    public static D3D11CompositionLayerSchedule From(MediaCompositionLayer? descriptor) => descriptor == null || !descriptor.IsVisible
            ? None
            : descriptor.SourceKind switch
            {
                MediaCompositionLayerSourceKind.D3D11Texture => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.SourceRate,
                    D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    descriptor.FramesPerSecond,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.RetainedD3D11Texture => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.Static,
                    D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.BgraPixels => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.OnDemand,
                    D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.SolidColor => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.OnDemand,
                    D3D11CompositionLayerRenderTargetKind.ImmediateFinalTarget,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.DxCdgSnapshot => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.OnDemand,
                    D3D11CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                _ => new D3D11CompositionLayerSchedule(
                    D3D11CompositionLayerUpdateCadence.OnDemand,
                    D3D11CompositionLayerRenderTargetKind.ImmediateFinalTarget,
                    0d,
                    CanReuseRetainedOutput: false)
            };
}
