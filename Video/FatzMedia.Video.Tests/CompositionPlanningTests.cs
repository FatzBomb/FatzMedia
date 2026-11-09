using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Composition.Planning;

namespace FatzMedia.Video.Tests;

public sealed class CompositionPlanningTests
{
    [Fact]
    public void NativeTexture_UsesSourceRateRetainedSchedule()
    {
        var layer = new MediaCompositionLayer
        {
            SourceKind = MediaCompositionLayerSourceKind.NativeTexture,
            FramesPerSecond = 59.94
        };

        var schedule = CompositionLayerSchedule.From(layer);

        Assert.Equal(CompositionLayerUpdateCadence.SourceRate, schedule.Cadence);
        Assert.Equal(CompositionLayerRenderTargetKind.RetainedOffscreenTexture, schedule.TargetKind);
        Assert.Equal(59.94, schedule.SourceFramesPerSecond);
        Assert.True(schedule.CanReuseRetainedOutput);
    }

    [Fact]
    public void TargetRegistry_TracksActivationResizeAndDeactivation()
    {
        var registry = new CompositionTargetRegistry();
        var initial = new CompositionTargetDescriptor(
            CompositionTargetDescriptor.PrimaryFinalTargetId,
            CompositionTargetKind.FinalPresentation,
            Width: 1280,
            Height: 720,
            BackendName: "test",
            Revision: 1);

        registry.Capture([initial]);
        var activation = registry.CreatePlan();
        Assert.Equal(1, activation.ActiveTargetCount);
        Assert.Equal(1, activation.ResizeTargetCount);
        Assert.True(activation.ShouldPresentTarget(initial.Id));

        registry.MarkPlanConsumed();
        registry.Capture([initial with { Width = 1920, Height = 1080, Revision = 2 }]);
        var resize = registry.CreatePlan();
        Assert.Equal(1, resize.ResizeTargetCount);

        registry.MarkPlanConsumed();
        registry.Capture([]);
        var deactivation = registry.CreatePlan();
        Assert.Equal(1, deactivation.DeactivatedTargetCount);
        Assert.Equal(0, deactivation.ActiveTargetCount);
    }
}
