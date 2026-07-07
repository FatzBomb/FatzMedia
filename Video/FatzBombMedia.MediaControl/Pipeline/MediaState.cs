using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class MediaState
{
    private readonly Lock _gate = new();
    private MediaPlayerState _playbackState = MediaPlayerState.Null;
    private double _positionSeconds;
    private double _durationSeconds;

    public MediaContent Content
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    } = MediaContent.None;

    public MediaPlayerState PlaybackState
    {
        get
        {
            lock (_gate)
            {
                return _playbackState;
            }
        }
    }

    public string? SourcePath
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    }

    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                return _positionSeconds;
            }
        }
    }

    public double DurationSeconds
    {
        get
        {
            lock (_gate)
            {
                return _durationSeconds;
            }
        }
    }

    public bool IsOpen => Content != MediaContent.None;

    public void SetOpened(MediaContent content, string? sourcePath, double durationSeconds)
    {
        lock (_gate)
        {
            Content = content;
            SourcePath = sourcePath;
            _positionSeconds = 0;
            _durationSeconds = CleanSeconds(durationSeconds);
            _playbackState = MediaPlayerState.Stopped;
        }
    }

    public void SetPlaybackState(MediaPlayerState state)
    {
        lock (_gate)
        {
            if (_playbackState == state)
            {
                return;
            }

            _playbackState = state;
        }
    }

    public void SetPosition(double positionSeconds)
    {
        lock (_gate)
        {
            var cleanPosition = CleanSeconds(positionSeconds);
            if (Math.Abs(_positionSeconds - cleanPosition) < 0.001)
            {
                return;
            }

            _positionSeconds = cleanPosition;
        }
    }

    public void SetDuration(double durationSeconds)
    {
        lock (_gate)
        {
            _durationSeconds = CleanSeconds(durationSeconds);
            if (_positionSeconds > _durationSeconds && _durationSeconds > 0)
            {
                _positionSeconds = _durationSeconds;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Content = MediaContent.None;
            _playbackState = MediaPlayerState.Null;
            SourcePath = null;
            _positionSeconds = 0;
            _durationSeconds = 0;
        }
    }

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
