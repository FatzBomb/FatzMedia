using FatzBomb.MediaControl.Core.Graphics.Composition;
using FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

namespace FatzBomb.MediaControl.Tests;

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

        CompositionLayerSchedule schedule = CompositionLayerSchedule.From(layer);

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
        CompositionTargetPlan activation = registry.CreatePlan();
        Assert.Equal(1, activation.ActiveTargetCount);
        Assert.Equal(1, activation.ResizeTargetCount);
        Assert.True(activation.ShouldPresentTarget(initial.Id));

        registry.MarkPlanConsumed();
        registry.Capture([initial with { Width = 1920, Height = 1080, Revision = 2 }]);
        CompositionTargetPlan resize = registry.CreatePlan();
        Assert.Equal(1, resize.ResizeTargetCount);

        registry.MarkPlanConsumed();
        registry.Capture([]);
        CompositionTargetPlan deactivation = registry.CreatePlan();
        Assert.Equal(1, deactivation.DeactivatedTargetCount);
        Assert.Equal(0, deactivation.ActiveTargetCount);
    }
}
