using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// Stage 046A slice 2: the Avalonia <see cref="KaraokeSurface"/> and <see cref="BackgroundSurface"/>
/// over Core's lightweight player controller (the player rules are tested in the portable tests).
/// </summary>
public sealed class LightweightSurfacesTests
{
    [AvaloniaFact]
    public void KaraokeSurface_CentresA43SurfaceOnItsCanvasColour()
    {
        var karaoke = new KaraokeSurface { CanvasColor = Colors.Navy };
        Window window = Show(karaoke, 400, 200);

        Rect bounds = karaoke.Surface.Bounds;
        // Layout rounding snaps to whole pixels.
        Assert.Equal(200, bounds.Height, tolerance: 1.0);
        Assert.Equal(200 * 4.0 / 3.0, bounds.Width, tolerance: 1.0);
        Assert.Equal((400 - bounds.Width) / 2, bounds.X, tolerance: 1.0);
        Assert.Equal(Colors.Navy, Assert.IsAssignableFrom<ISolidColorBrush>(karaoke.Background).Color);
        Assert.True(karaoke.AutoPlay);
        Assert.True(karaoke.AutoHide);
        Assert.False(karaoke.IsContentVisible);
        window.Close();
        karaoke.Dispose();
    }

    [AvaloniaFact]
    public void BackgroundSurface_ShowsItsColourWithoutAPlayer_AndTheOverlayAboveTheSurface()
    {
        var overlay = new TextBlock { Text = "overlay" };
        var background = new BackgroundSurface { OverlayContent = overlay };
        Window window = Show(background, 160, 90);

        background.SetColor(Color.FromArgb(0x80, 0x10, 0x20, 0x30));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(background.Session);
        Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), Assert.IsAssignableFrom<ISolidColorBrush>(background.Surface.Background).Color);
        Assert.Same(overlay, background.OverlayPresenter.Content);
        Assert.True(background.OverlayPresenter.IsVisible);
        Assert.False(background.OverlayPresenter.IsHitTestVisible);
        Assert.Same(background.OverlayPresenter, background.Children[^1]);

        background.OverlayHitTestVisible = true;
        Assert.True(background.OverlayPresenter.IsHitTestVisible);
        background.OverlayContent = null;
        Assert.False(background.OverlayPresenter.IsVisible);
        window.Close();
        background.Dispose();
    }

    [AvaloniaFact]
    public void BackgroundSurface_WithoutPlatformFactory_WarnsAndLoadsNothing()
    {
        ConcurrentQueue<MediaDiagnosticInfo> notices = Record(BackgroundSurface.DiagnosticCategory, out Action stop);
        try
        {
            var background = new BackgroundSurface { Source = "picture.png" };
            Window window = Show(background, 160, 90);
            PumpUntil(() => !notices.IsEmpty);

            MediaDiagnosticInfo warning = Assert.Single(notices);
            Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
            Assert.Null(background.Session);
            window.Close();
        }
        finally
        {
            stop();
        }
    }

    [AvaloniaFact]
    public void KaraokeSurface_CoalescesSourceChanges_AndReportsAFailedPlayerCreation()
    {
        ConcurrentQueue<MediaDiagnosticInfo> notices = Record(KaraokeSurface.DiagnosticCategory, out Action stop);
        try
        {
            var factory = new FailingPlatformFactory();
            var karaoke = new KaraokeSurface(factory);
            Window window = Show(karaoke, 160, 120);

            // Two changes in one turn reload once: one creation attempt, one Error.
            karaoke.Source = "first.cdg";
            karaoke.Source = "second.cdg";
            PumpUntil(() => !notices.IsEmpty);
            Dispatcher.UIThread.RunJobs();

            MediaDiagnosticInfo error = Assert.Single(notices);
            Assert.Equal(MediaDiagnosticLevel.Error, error.Level);
            Assert.Equal(1, factory.Attempts);
            Assert.Null(karaoke.Session);

            // No player exists after the failure, so the factory may change.
            karaoke.PlatformFactory = new FailingPlatformFactory();
            window.Close();
            karaoke.Dispose();
        }
        finally
        {
            stop();
        }
    }

    [AvaloniaFact]
    public void PassThroughProperties_ReachTheInnerSurface()
    {
        var background = new BackgroundSurface { AutoHide = false, PreferGpuSharedSurface = false };
        var karaoke = new KaraokeSurface { AutoHide = false, PreferGpuSharedSurface = false };

        Assert.False(background.Surface.AutoHide);
        Assert.False(background.Surface.PreferGpuSharedSurface);
        Assert.False(karaoke.Surface.AutoHide);
        Assert.False(karaoke.Surface.PreferGpuSharedSurface);
    }

    private static ConcurrentQueue<MediaDiagnosticInfo> Record(string category, out Action stop)
    {
        var notices = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Handler(MediaDiagnosticInfo info)
        {
            if (info.Category == category)
                notices.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Handler;
        stop = () => DiagnosticRouter.DiagnosticOccurred -= Handler;
        return notices;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private sealed class FailingPlatformFactory : IMediaControlPlatformFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            throw new NotSupportedException();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths)
        {
            _ = Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("native libraries missing");
        }
    }
}
