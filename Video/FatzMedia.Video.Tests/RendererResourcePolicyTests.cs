using System.Collections.Concurrent;
using FatzMedia.Video.Core.Graphics.Composition.Planning;
using FatzMedia.Video.Core.Graphics.Rendering;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-026: the pure policies behind the renderer robustness and per-frame cost fixes.</summary>
public sealed class RendererResourcePolicyTests
{
    [Fact]
    public void DiagnosticLatch_ReportsOncePerEpisode()
    {
        const string Category = "Eng026Test.Latch";
        var latch = new RendererDiagnosticLatch();
        var raised = Capture(Category, () =>
        {
            Assert.True(latch.Raise(MediaDiagnosticLevel.Warning, Category, "first"));
            Assert.False(latch.Raise(MediaDiagnosticLevel.Warning, Category, "repeat"));
            Assert.False(latch.Raise(MediaDiagnosticLevel.Warning, Category, "repeat"));
            Assert.True(latch.IsLatched);

            latch.Reset();
            Assert.False(latch.IsLatched);
            Assert.True(latch.Raise(MediaDiagnosticLevel.Error, Category, "next episode", new InvalidOperationException("cause")));
        });

        Assert.Equal(["first", "next episode"], raised.Select(static info => info.Message));
        Assert.Equal(MediaDiagnosticLevel.Warning, raised[0].Level);
        Assert.Equal(MediaDiagnosticLevel.Error, raised[1].Level);
        Assert.IsType<InvalidOperationException>(raised[1].InnerException);
    }

    [Fact]
    public void DiagnosticLatch_ConcurrentFailuresRaiseOnce()
    {
        const string Category = "Eng026Test.LatchConcurrent";
        var latch = new RendererDiagnosticLatch();
        var raised = Capture(Category, () =>
            Parallel.For(0, 64, _ => latch.Raise(MediaDiagnosticLevel.Warning, Category, "failure")));

        _ = Assert.Single(raised);
    }

    [Fact]
    public void RetainOnly_DisposesResourcesOfTargetsThatAreGone()
    {
        var primary = new TrackedResource();
        var kept = new TrackedResource();
        var removed = new TrackedResource();
        var resources = new Dictionary<string, TrackedResource>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary-final"] = primary,
            ["offscreen-secondary:Screen-2"] = kept,
            ["native-hwnd-secondary:projector"] = removed,
        };

        // Ids are matched with the dictionary's comparer (the D3D11 post-process map ignores case).
        string[] live = ["PRIMARY-FINAL", "offscreen-secondary:screen-2"];
        var count = TargetResourcePruning.RetainOnly(resources, live, static id => id);

        Assert.Equal(1, count);
        Assert.True(removed.Disposed);
        Assert.False(primary.Disposed);
        Assert.False(kept.Disposed);
        Assert.Equal(2, resources.Count);
        Assert.False(resources.ContainsKey("native-hwnd-secondary:projector"));
    }

    [Fact]
    public void RetainOnly_WithNoLiveTargets_ReleasesEverything()
    {
        var a = new TrackedResource();
        var b = new TrackedResource();
        var resources = new Dictionary<string, TrackedResource> { ["a"] = a, ["b"] = b };

        Assert.Equal(2, TargetResourcePruning.RetainOnly(resources, Array.Empty<string>(), static id => id));
        Assert.Empty(resources);
        Assert.True(a.Disposed && b.Disposed);
        Assert.Equal(0, TargetResourcePruning.RetainOnly(resources, ["a"], static id => id));
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(100, 100, true)]
    [InlineData(100, 60, true)]
    [InlineData(100, 25, true)]
    [InlineData(100, 24, false)]
    [InlineData(100, 101, false)]
    [InlineData(100, -1, false)]
    public void RetainedPixelBuffer_ReusesUntilFarTooLarge(int capacity, int required, bool reuse) =>
        Assert.Equal(reuse, RetainedPixelBuffer.CanReuse(capacity, required));

    [Fact]
    public void RetainedPixelBuffer_SurvivesResizeChurnWithoutReallocating()
    {
        // A live resize: the window grows and shrinks around 1280x720 a few pixels at a time.
        var buffer = RetainedPixelBuffer.Ensure(null, 1300 * 740 * 4);
        var first = buffer;
        foreach (var (width, height) in new[] { (1280, 720), (1290, 730), (1270, 715), (1300, 740), (1250, 700) })
            buffer = RetainedPixelBuffer.Ensure(buffer, width * height * 4);

        Assert.Same(first, buffer);

        // Grows when a larger frame needs it, and lets go of a buffer far too large.
        buffer = RetainedPixelBuffer.Ensure(buffer, 1920 * 1080 * 4);
        Assert.Equal(1920 * 1080 * 4, buffer.Length);
        buffer = RetainedPixelBuffer.Ensure(buffer, 320 * 180 * 4);
        Assert.Equal(320 * 180 * 4, buffer.Length);
    }

    [Fact]
    public void TargetRegistry_RemovedTargetIsDeactivatedOnceThenForgotten()
    {
        var registry = new CompositionTargetRegistry();
        CompositionTargetDescriptor primary = new(CompositionTargetDescriptor.PrimaryFinalTargetId, CompositionTargetKind.FinalPresentation, 1280, 720, "test", 1);
        CompositionTargetDescriptor secondary = new("offscreen-secondary:2", CompositionTargetKind.FinalPresentation, 960, 540, "test", 1);

        registry.Capture([primary, secondary]);
        Assert.Equal(2, registry.ActiveTargetCount);
        Assert.True(registry.HasMultipleActiveTargets);
        registry.MarkPlanConsumed();

        registry.Capture([primary]);
        Assert.Equal(1, registry.ActiveTargetCount);
        Assert.False(registry.HasMultipleActiveTargets);
        var plan = registry.CreatePlan();
        Assert.Equal(1, plan.DeactivatedTargetCount);
        registry.MarkPlanConsumed();

        registry.Capture([primary]);
        Assert.Equal(0, registry.CreatePlan().DeactivatedTargetCount);
        Assert.DoesNotContain("offscreen-secondary:2", registry.LifecycleSummary, StringComparison.Ordinal);
    }

    static List<MediaDiagnosticInfo> Capture(string category, Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == category)
                captured.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            action();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        return [.. captured];
    }

    sealed class TrackedResource : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
