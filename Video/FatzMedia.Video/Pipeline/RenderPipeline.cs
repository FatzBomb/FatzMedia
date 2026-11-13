
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Pipeline;

sealed class RenderPipeline(IMediaEngine mediaEngine)
{
    readonly IMediaEngine _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
    IVideoEngine? _video;
    ICdgEngine? _cdg;
    long _lastRenderedCdgPacketIndex = -1;
    double _lastRenderedCdgPosition = double.NaN;

    double CurrentPosition =>
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

        var currentPosition = Math.Max(0, CurrentPosition + _mediaEngine.CdgOffsetSeconds);
        if (!ShouldRenderCdg(currentPosition, force))
        {
            return;
        }

        _cdg.RenderAtSeconds(currentPosition, force);
    }

    public void RenderVideoAtCurrentPosition(bool allowWhilePlaying = false, bool forceReissue = false)
    {
        if (_video != null && _mediaEngine.Content.HasFlag(MediaContent.Video))
            _video.RenderCurrentFrame(allowWhilePlaying, forceReissue);
    }

    bool ShouldRenderCdg(double currentPosition, bool force)
    {
        if (!double.IsFinite(currentPosition))
        {
            return false;
        }

        var packetIndex = CdgConstants.PacketCountAt(currentPosition);
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

    void RememberCdgRender(double position, long packetIndex)
    {
        _lastRenderedCdgPosition = position;
        _lastRenderedCdgPacketIndex = packetIndex;
    }

    void ResetCdgRenderGate()
    {
        _lastRenderedCdgPacketIndex = -1;
        _lastRenderedCdgPosition = double.NaN;
    }

    static bool NearlySamePosition(double currentPosition, double previousPosition) => double.IsFinite(previousPosition) && Math.Abs(currentPosition - previousPosition) < 0.001;
}
