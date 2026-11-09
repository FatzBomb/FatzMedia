using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FatzMedia.Video.Core.Abstractions;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// Stage 051A: <see cref="MediaSurface.FirstFrameReady"/> on the frame-sink path, raised once per load
/// when the bitmap holds a frame of that load.
/// </summary>
public sealed class FirstFrameReadyTests
{
    static readonly MediaSourceInfo Source = new(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFirstFrame_RaisesOnce_WithTheFrameInTheBitmap(bool autoHide)
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session, AutoHide = autoHide };
        var bitmapAtEvent = new List<bool>();
        surface.FirstFrameReady += (_, _) =>
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            bitmapAtEvent.Add(surface.FrameSink?.Bitmap is not null);
        };
        var window = Show(surface);
        Assert.Empty(bitmapAtEvent);

        Present(recorder);
        Present(recorder);
        Present(recorder);

        Assert.Equal([true], bitmapAtEvent);
        window.Close();
    }

    [AvaloniaFact]
    public void EachLoad_RaisesAgain_AndAudioOnlyMediaNever()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        var raised = 0;
        surface.FirstFrameReady += (_, _) => raised++;
        var window = Show(surface);
        Present(recorder);
        Assert.Equal(1, raised);

        Load(recorder, hasVisualContent: true);
        Present(recorder);
        Assert.Equal(2, raised);

        Load(recorder, hasVisualContent: false);
        Present(recorder);
        Assert.Equal(2, raised);
        window.Close();
    }

    [AvaloniaFact]
    public void AnAnnouncedFrame_WaitsForTheBitmap()
    {
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        var raised = 0;
        surface.FirstFrameReady += (_, _) => raised++;
        var window = Show(surface);

        // The present notice without a frame in the sink (a frame for another surface).
        Task.Run(() => recorder.Raise(nameof(IPlayerSession.PresentationFramePresented), EventArgs.Empty)).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, raised);

        Task.Run(() => SinkOf(recorder).PresentFrame(new byte[Width * Height * 4], Width, Height, Width * 4)).Wait();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, raised);
        window.Close();
    }

    [AvaloniaFact]
    public void ALoadBeforeTheSurfaceAttaches_StillRaisesWhenItAttaches()
    {
        // The startup media of a test player may load before its surface is in the tree (the
        // session is already open here): Core re-presents the current frame when the surface attaches.
        (var session, var recorder) = RecordingSession.Create();
        var surface = new MediaSurface { Player = session };
        var raised = 0;
        surface.FirstFrameReady += (_, _) => raised++;

        var window = Show(surface);
        Assert.Equal(0, raised);
        Present(recorder);

        Assert.Equal(1, raised);
        window.Close();
    }

    const int Width = 8;
    const int Height = 4;

    static void Load(RecordingSession recorder, bool hasVisualContent)
    {
        recorder.Raise(nameof(IPlayerSession.MediaClosed), new MediaClosedEventArgs(Source));
        recorder.HasVisualContent = hasVisualContent;
        recorder.Raise(nameof(IPlayerSession.MediaLoaded), new MediaLoadedEventArgs(Source, TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();
    }

    // As a renderer does, on its own thread: the frame to the sink, then the present notice.
    static void Present(RecordingSession recorder)
    {
        var sink = SinkOf(recorder);
        Task.Run(() =>
        {
            sink.PresentFrame(new byte[Width * Height * 4], Width, Height, Width * 4);
            recorder.Raise(nameof(IPlayerSession.PresentationFramePresented), EventArgs.Empty);
        }).Wait();
        Dispatcher.UIThread.RunJobs();
    }

    static IMediaPresentationFrameSink SinkOf(RecordingSession recorder)
    {
        Assert.True(recorder.LastAttached!.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out var handle));
        Assert.True(MediaPresentationFrameSinkHandle.TryResolve(handle, out var sink));
        return sink!;
    }

    static Window Show(MediaSurface surface)
    {
        var window = new Window { Width = Width, Height = Height, Content = surface };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
