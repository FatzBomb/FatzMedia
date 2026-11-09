using System.Collections.Concurrent;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-039: the presentation-path bookkeeping the three adapters' media surfaces share.</summary>
public sealed class MediaSurfaceBookkeepingTests
{
    private const string Category = "Test.MediaSurface";

    [Theory]
    [InlineData(null, MediaPresentationSurfaceRole.Primary, MediaPresentationSurfaceDescriptor.PrimarySurfaceId)]
    [InlineData("  ", MediaPresentationSurfaceRole.Primary, MediaPresentationSurfaceDescriptor.PrimarySurfaceId)]
    [InlineData(null, MediaPresentationSurfaceRole.Projector, "secondary")]
    [InlineData(" stage ", MediaPresentationSurfaceRole.Projector, "stage")]
    [InlineData("main", MediaPresentationSurfaceRole.Primary, "main")]
    public void ResolveSurfaceId_UsesTheRolesDefaultWhenBlank(string? surfaceId, MediaPresentationSurfaceRole role, string expected) =>
        Assert.Equal(expected, MediaSurfaceBookkeeping.ResolveSurfaceId(surfaceId, role, "secondary"));

    [Fact]
    public void Surfaces_AreNumberedInCreationOrder()
    {
        var first = new MediaSurfaceBookkeeping(Category);
        var second = new MediaSurfaceBookkeeping(Category);

        Assert.True(second.SurfaceNumber > first.SurfaceNumber);
        Assert.Equal($"Surface #{first.SurfaceNumber} 'primary'", first.Describe("primary"));
    }

    [Fact]
    public void SetPath_ReportsAChangeOnce_AndRaisesInfoOnlyForARealPath()
    {
        var bookkeeping = new MediaSurfaceBookkeeping(Category);
        var changes = new List<bool>();

        var diagnostics = Capture(() =>
        {
            changes.Add(bookkeeping.SetPath(MediaSurfacePresentationPath.FrameSink, "PreferNativeWindow is off", null, "primary"));
            changes.Add(bookkeeping.SetPath(MediaSurfacePresentationPath.FrameSink, "PreferNativeWindow is off", null, "primary"));
            changes.Add(bookkeeping.SetPath(MediaSurfacePresentationPath.None, "", null, "primary"));
        });

        Assert.Equal([true, false, true], changes);
        Assert.Equal(MediaSurfacePresentationPath.None, bookkeeping.Path);
        Assert.Null(bookkeeping.Reason);
        MediaDiagnosticInfo info = Assert.Single(diagnostics);
        Assert.Equal(MediaDiagnosticLevel.Info, info.Level);
        Assert.Equal(
            $"Surface #{bookkeeping.SurfaceNumber} 'primary': frame sink path (PreferNativeWindow is off)",
            info.Message);
    }

    [Fact]
    public void AttachRefusal_WarnsOnceUntilReset()
    {
        var bookkeeping = new MediaSurfaceBookkeeping(Category);
        (IPlayerSession session, _) = MediaSurfaceContentTrackerTests.TrackedSession.Create();

        var diagnostics = Capture(() =>
        {
            bookkeeping.ReportAttachRefused(session, "primary");
            bookkeeping.ReportAttachRefused(session, "primary");
            bookkeeping.ResetAttachRefusal();
            bookkeeping.ReportAttachRefused(session, "primary");
        });

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, info =>
        {
            Assert.Equal(MediaDiagnosticLevel.Warning, info.Level);
            Assert.Same(session, info.Owner);
        });
    }

    private static MediaDiagnosticInfo[] Capture(Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == Category)
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
}
