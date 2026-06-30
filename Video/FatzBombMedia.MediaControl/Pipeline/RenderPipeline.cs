
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Cdg;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class RenderPipeline(IMediaEngine mediaEngine)
{
    private readonly IMediaEngine _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
    private IVideoEngine? _video;
    private ICdgEngine? _cdg;
    private long _lastRenderedCdgPacketIndex = -1;
    private double _lastRenderedCdgPosition = double.NaN;

    private double CurrentPosition =>
        _mediaEngine.Content.HasFlag(MediaContent.Audio)
            ? _mediaEngine.Audio.Position
            : _mediaEngine.Content.HasFlag(MediaContent.Video) && _mediaEngine.Video != null
                ? _mediaEngine.Video.Position
                : _mediaEngine.Position;

    public void Attach(IVideoEngine? video, ICdgEngine? cdg)
    {
        _video = video;
        _cdg = cdg;
        ResetCdgRenderGate();
    }

    public void Clear()
    {
        _video = null;
        _cdg = null;
        ResetCdgRenderGate();
    }

    public void RenderCdgAtCurrentPosition(bool force = false)
    {
        if (_cdg == null || !_mediaEngine.Content.HasFlag(MediaContent.Cdg))
        {
            return;
        }

        // CDG is now a visual layer, not an alternative to video. Do not skip CDG
        // just because a video/background layer is active; the renderer decides how to
        // compose the current layers.

        var currentPosition = CurrentPosition;
        if (!ShouldRenderCdg(currentPosition, force))
        {
            return;
        }

        _cdg.RenderAtSeconds(currentPosition, force);
    }

    public void RenderVideoAtCurrentPosition(bool allowWhilePlaying = false)
    {
        if (_video is VideoPipeline videoPipeline && _mediaEngine.Content.HasFlag(MediaContent.Video))
        {
            videoPipeline.RenderCurrentFrame(allowWhilePlaying);
        }
    }

    private bool ShouldRenderCdg(double currentPosition, bool force)
    {
        if (!double.IsFinite(currentPosition))
        {
            return false;
        }

        var packetIndex = Math.Max(0, (long)Math.Floor(currentPosition * CdgConstants.PacketsPerSecond));
        if (force || packetIndex < _lastRenderedCdgPacketIndex)
        {
            RememberCdgRender(currentPosition, packetIndex);
            return true;
        }

        if (_mediaEngine.State != MediaPlayerState.Playing && packetIndex == _lastRenderedCdgPacketIndex)
        {
            return false;
        }

        if (packetIndex == _lastRenderedCdgPacketIndex || NearlySamePosition(currentPosition, _lastRenderedCdgPosition))
        {
            return false;
        }

        RememberCdgRender(currentPosition, packetIndex);
        return true;
    }

    private void RememberCdgRender(double position, long packetIndex)
    {
        _lastRenderedCdgPosition = position;
        _lastRenderedCdgPacketIndex = packetIndex;
    }

    private void ResetCdgRenderGate()
    {
        _lastRenderedCdgPacketIndex = -1;
        _lastRenderedCdgPosition = double.NaN;
    }

    private static bool NearlySamePosition(double currentPosition, double previousPosition) => double.IsFinite(previousPosition) && Math.Abs(currentPosition - previousPosition) < 0.001;
}
