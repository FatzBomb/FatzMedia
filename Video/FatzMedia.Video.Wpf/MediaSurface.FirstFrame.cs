using System.Windows;

namespace FatzMedia.Video.Wpf;

/// <summary>
/// The first-frame-ready signal (Stage 051A) and the composited UI-thread counter (047A's before
/// numbers). Core's <see cref="MediaSurfaceContentTracker"/> numbers each load and reports when its
/// first frame was presented; this partial raises <see cref="FirstFrameReady"/> once per load, when
/// the frame is on this surface and its presentation host is shown.
/// </summary>
public sealed partial class MediaSurface
{
    private int _firstFrameReadyLoad;
    private long _retiredCompositedFrames;
    private TimeSpan _retiredCompositedTime;

    /// <summary>
    /// Raised on the UI thread once per load of media with visual content (and once when
    /// <see cref="Player"/> is set to a session that already has such media open), when a frame of
    /// that load is on this surface and its presentation host is shown. In
    /// <see cref="WpfPresentationMode.WpfComposited"/> the bitmap holds the frame (a frame still
    /// waiting is written in the same dispatcher operation, after the host is made visible), so
    /// showing this surface or hiding another one in the handler changes the screen in one WPF
    /// frame: the A/B swap. In <see cref="WpfPresentationMode.NativeHwndOverlay"/> it is raised after
    /// the swap chain presented it; the desktop shows it at the next refresh, so WPF content changed
    /// in the handler may lead or lag it by a frame. Not raised for audio-only media or in Audio
    /// mode. Subscribe before loading.
    /// </summary>
    /// <remarks>
    /// Hide a surface that waits for its first frame with <see cref="Visibility.Hidden"/> (or
    /// <c>Opacity = 0</c> in composited mode), not <see cref="Visibility.Collapsed"/>: a collapsed
    /// surface is laid out at no size, so its first frame would be rendered at 1x1.
    /// </remarks>
    public event EventHandler? FirstFrameReady;

    /// <summary>
    /// Gets the UI-thread time this surface spent writing composited frames into its bitmap
    /// (<see cref="WpfPresentationMode.WpfComposited"/>), since it was created. Read on the UI thread;
    /// with <see cref="CompositedFramesPublished"/> it gives the per-frame cost.
    /// </summary>
    public TimeSpan CompositedUiThreadTime => _retiredCompositedTime + (_compositedHost?.UiThreadTime ?? TimeSpan.Zero);

    /// <summary>Gets how many composited frames this surface wrote into its bitmap since it was created (UI thread).</summary>
    public long CompositedFramesPublished => _retiredCompositedFrames + (_compositedHost?.FramesPublished ?? 0);

    // UI thread: after content state, host visibility or a composited publish changed.
    private void RaiseFirstFrameReadyIfDue()
    {
        var load = _contentTracker.FirstFrameLoad;
        if (load == 0 || load == _firstFrameReadyLoad || !_primaryAttached || !_contentTracker.HasPresentation)
            return;

        if (PresentationMode == WpfPresentationMode.WpfComposited)
        {
            if (_compositedHost is not { Visibility: Visibility.Visible } host)
                return;

            // Core hands the frame to the sink before it announces it, so a frame still
            // waiting is at least as new as the announced one: write it now, in this operation.
            _ = host.PublishPendingFrame();
            if (!host.HasFrame)
                return;
        }
        else if (_surfaceHost.Visibility != Visibility.Visible)
        {
            return;
        }

        _firstFrameReadyLoad = load;
        FirstFrameReady?.Invoke(this, EventArgs.Empty);
    }

    private void RetireCompositedCounters(WpfCompositedSurfaceHost host)
    {
        _retiredCompositedFrames += host.FramesPublished;
        _retiredCompositedTime += host.UiThreadTime;
    }
}
