namespace FatzBombMedia.MediaControl.Pipeline;

using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.DxCdg;

internal sealed class RenderPipeline
{
    private readonly IMediaEngine _mediaEngine;
    private IVideoEngine? _video;
    private ICdgEngine? _cdg;
    private long _lastRenderedDxCdgPacketIndex = -1;
    private double _lastRenderedCdgPosition = double.NaN;

    public RenderPipeline(IMediaEngine mediaEngine)
    {
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
    }

    public double CurrentPosition
    {
        get
        {
            if (_mediaEngine.Content.HasFlag(MediaContent.Audio))
            {
                return _mediaEngine.Audio.Position;
            }

            if (_mediaEngine.Content.HasFlag(MediaContent.Video) && _mediaEngine.Video != null)
            {
                return _mediaEngine.Video.Position;
            }

            return _mediaEngine.Position;
        }
    }

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

    public void RenderCdgAtCurrentPosition(bool hasActiveVideo, bool force = false)
    {
        if (_cdg == null || !_mediaEngine.Content.HasFlag(MediaContent.Cdg))
        {
            return;
        }

        // DXCDG is now a visual layer, not an alternative to video. Do not skip CDG
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

        var packetIndex = Math.Max(0, (long)Math.Floor(currentPosition * DxCdgConstants.PacketsPerSecond));
        if (force || packetIndex < _lastRenderedDxCdgPacketIndex)
        {
            RememberCdgRender(currentPosition, packetIndex);
            return true;
        }

        if (_mediaEngine.State != MediaPlayerState.Playing && packetIndex == _lastRenderedDxCdgPacketIndex)
        {
            return false;
        }

        if (packetIndex == _lastRenderedDxCdgPacketIndex || NearlySamePosition(currentPosition, _lastRenderedCdgPosition))
        {
            return false;
        }

        RememberCdgRender(currentPosition, packetIndex);
        return true;
    }

    private void RememberCdgRender(double position, long packetIndex)
    {
        _lastRenderedCdgPosition = position;
        _lastRenderedDxCdgPacketIndex = packetIndex;
    }

    private void ResetCdgRenderGate()
    {
        _lastRenderedDxCdgPacketIndex = -1;
        _lastRenderedCdgPosition = double.NaN;
    }

    private static bool NearlySamePosition(double currentPosition, double previousPosition)
    {
        return double.IsFinite(previousPosition) && Math.Abs(currentPosition - previousPosition) < 0.001;
    }
}
