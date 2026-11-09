using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Wpf;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 051A: the WPF surface's <see cref="MediaSurface.FirstFrameReady"/> in composited mode, in a
/// shown window (Windows only; listed in Portable.Tests' Exclude).
/// </summary>
public sealed class WpfFirstFrameReadyTests
{
    static readonly MediaSourceInfo Source = new(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video);

    [Fact]
    public void Composited_RaisesOncePerLoad_AfterTheBitmapWrite_AndCountsTheUiThreadTime() => RunSta(() =>
    {
        (var session, var recorder) = WinFormsMediaSurfaceTests.SurfaceRecorder.Create();
        var surface = new MediaSurface { PresentationMode = WpfPresentationMode.WpfComposited, Player = session };
        var window = new Window { Width = 160, Height = 90, Content = surface, ShowActivated = false, ShowInTaskbar = false };
        var published = new List<long>();
        surface.FirstFrameReady += (_, _) =>
        {
            Assert.True(surface.Dispatcher.CheckAccess());
            published.Add(surface.CompositedFramesPublished);
        };
        window.Show();
        Drain();
        Assert.True(surface.IsPrimarySurfaceAttached);
        Assert.True(recorder.LastAttached!.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out var handle));
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(handle, out var sink));
        var (width, height) = (recorder.LastAttached.PixelWidth, recorder.LastAttached.PixelHeight);
        var pixels = new byte[width * height * 4];
        void Present()
        {
            // As a renderer does, on its own thread: the frame to the sink, then the present notice.
            Task.Run(() =>
            {
                sink!.PresentFrame(pixels, width, height, width * 4);
                recorder.Raise(nameof(IPlayerSession.PresentationFramePresented), EventArgs.Empty);
            }).Wait();
            Drain();
        }

        Present();
        Present();
        Assert.Single(published);
        Assert.True(published[0] >= 1, "the bitmap holds the frame when the event is raised");

        recorder.Raise(nameof(IPlayerSession.MediaClosed), new MediaClosedEventArgs(Source));
        recorder.Raise(nameof(IPlayerSession.MediaLoaded), new MediaLoadedEventArgs(Source, TimeSpan.Zero));
        Present();
        Assert.Equal(2, published.Count);
        Assert.True(surface.CompositedFramesPublished >= 3);
        Assert.True(surface.CompositedUiThreadTime > TimeSpan.Zero);

        surface.Player = null;
        window.Close();
    });

    static void Drain()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WPF test thread did not complete.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
