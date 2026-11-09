using FatzMedia.Video.Core.Abstractions;
using System.Collections.Concurrent;
using WinFormsBackgroundSurface = FatzMedia.Video.WinForms.BackgroundSurface;
using WinFormsKaraokeSurface = FatzMedia.Video.WinForms.KaraokeSurface;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 046A slice 2: the WinForms <c>KaraokeSurface</c> and <c>BackgroundSurface</c> over Core's
/// lightweight player controller (the player rules are tested in <see cref="LightweightPlayerControllerTests"/>).
/// </summary>
public sealed class WinFormsLightweightSurfaceTests
{
    [Fact]
    public void KaraokeSurface_CentresA43Surface() => RunSta(() =>
    {
        using var karaoke = new WinFormsKaraokeSurface { Size = new System.Drawing.Size(400, 200), CanvasColor = System.Drawing.Color.Navy };
        _ = karaoke.Handle;
        karaoke.PerformLayout();

        System.Drawing.Rectangle bounds = karaoke.Surface.Bounds;
        Assert.Equal(200, bounds.Height);
        Assert.InRange(bounds.Width, 266, 267);
        Assert.InRange(bounds.X, 66, 67);
        Assert.Equal(System.Drawing.Color.Navy.ToArgb(), karaoke.CanvasColor.ToArgb());
        Assert.True(karaoke.AutoPlay);
        Assert.True(karaoke.AutoHide);
    });

    [Fact]
    public void BackgroundSurface_ShowsAnOpaqueColourWithoutAPlayer_AndPassesItsOverlayOn() => RunSta(() =>
    {
        using var background = new WinFormsBackgroundSurface { Size = new System.Drawing.Size(160, 90) };
        using var overlay = new System.Windows.Forms.Label();
        background.OverlayContent = overlay;
        background.OverlayHitTestVisible = true;
        _ = background.Handle;

        background.SetColor(System.Drawing.Color.FromArgb(0x80, 0x10, 0x20, 0x30));
        System.Windows.Forms.Application.DoEvents();

        Assert.Null(background.Session);
        Assert.Equal(System.Drawing.Color.FromArgb(0xFF, 0x10, 0x20, 0x30).ToArgb(), background.Surface.BackColor.ToArgb());
        Assert.Same(overlay, background.Surface.OverlayContent);
        Assert.True(background.Surface.OverlayHitTestVisible);
    });

    [Fact]
    public void BackgroundSurface_WithoutPlatformFactory_WarnsAndLoadsNothing() => RunSta(() =>
    {
        var notices = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == WinFormsBackgroundSurface.DiagnosticCategory)
                notices.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using var background = new WinFormsBackgroundSurface { Size = new System.Drawing.Size(160, 90), Source = "picture.png" };
            _ = background.Handle;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (notices.IsEmpty && DateTime.UtcNow < deadline)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(5);
            }

            MediaDiagnosticInfo warning = Assert.Single(notices);
            Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
            Assert.Null(background.Session);
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WinForms test thread did not complete.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
