namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public readonly record struct CompositionLayerSchedule(
    CompositionLayerUpdateCadence Cadence,
    CompositionLayerRenderTargetKind TargetKind,
    double SourceFramesPerSecond,
    bool CanReuseRetainedOutput)
{
    public bool HasSourceRate => double.IsFinite(SourceFramesPerSecond) && SourceFramesPerSecond > 0d;

    public override string ToString()
    {
        var fps = HasSourceRate ? $"@{SourceFramesPerSecond:0.###}fps" : string.Empty;
        return $"{Cadence}{fps}/{TargetKind}/reuse={CanReuseRetainedOutput}";
    }

    public static CompositionLayerSchedule None { get; } = new(
        CompositionLayerUpdateCadence.None,
        CompositionLayerRenderTargetKind.None,
        0d,
        CanReuseRetainedOutput: false);

    public static CompositionLayerSchedule From(MediaCompositionLayer? descriptor) => descriptor == null || !descriptor.IsVisible
            ? None
            : descriptor.SourceKind switch
            {
                MediaCompositionLayerSourceKind.NativeTexture => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.SourceRate,
                    CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    descriptor.FramesPerSecond,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.RetainedNativeTexture => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.Static,
                    CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.BgraPixels => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.OnDemand,
                    CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.SolidColor => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.OnDemand,
                    CompositionLayerRenderTargetKind.ImmediateFinalTarget,
                    0d,
                    CanReuseRetainedOutput: true),

                MediaCompositionLayerSourceKind.CdgSnapshot => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.OnDemand,
                    CompositionLayerRenderTargetKind.RetainedOffscreenTexture,
                    0d,
                    CanReuseRetainedOutput: true),

                _ => new CompositionLayerSchedule(
                    CompositionLayerUpdateCadence.OnDemand,
                    CompositionLayerRenderTargetKind.ImmediateFinalTarget,
                    0d,
                    CanReuseRetainedOutput: false)
            };
}
