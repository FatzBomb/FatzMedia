
using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class MediaState
{
    public MediaContent Content { get; private set; } = MediaContent.None;
    public MediaPlayerState PlaybackState { get; private set; } = MediaPlayerState.Null;
    public string? SourcePath { get; private set; }
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }

    public bool IsOpen => Content != MediaContent.None;

    public void SetOpened(MediaContent content, string? sourcePath, double durationSeconds)
    {
        Content = content;
        SourcePath = sourcePath;
        PositionSeconds = 0;
        DurationSeconds = CleanSeconds(durationSeconds);
        PlaybackState = MediaPlayerState.Stopped;
    }

    public void SetPlaybackState(MediaPlayerState state)
    {
        if (PlaybackState == state)
        {
            return;
        }

        PlaybackState = state;
    }

    public void SetPosition(double positionSeconds)
    {
        var cleanPosition = CleanSeconds(positionSeconds);
        if (Math.Abs(PositionSeconds - cleanPosition) < 0.001)
        {
            return;
        }

        PositionSeconds = cleanPosition;
    }

    public void SetDuration(double durationSeconds)
    {
        DurationSeconds = CleanSeconds(durationSeconds);
        if (PositionSeconds > DurationSeconds && DurationSeconds > 0)
        {
            PositionSeconds = DurationSeconds;
        }
    }

    public void Reset()
    {
        Content = MediaContent.None;
        PlaybackState = MediaPlayerState.Null;
        SourcePath = null;
        PositionSeconds = 0;
        DurationSeconds = 0;
    }

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
