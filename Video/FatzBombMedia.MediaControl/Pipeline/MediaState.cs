
using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Pipeline;

internal sealed class MediaState
{
    public MediaContent Content { get; private set; } = MediaContent.None;
    public MediaPlayerState PlaybackState { get; private set; } = MediaPlayerState.Null;
    public string? SourcePath { get; private set; }
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public string? LastError { get; private set; }

    public bool IsOpen => Content != MediaContent.None;

    public event EventHandler? Changed;

    public void SetOpened(MediaContent content, string? sourcePath, double durationSeconds)
    {
        Content = content;
        SourcePath = sourcePath;
        PositionSeconds = 0;
        DurationSeconds = CleanSeconds(durationSeconds);
        LastError = null;
        PlaybackState = MediaPlayerState.Stopped;
        OnChanged();
    }

    public void SetPlaybackState(MediaPlayerState state)
    {
        if (PlaybackState == state)
        {
            return;
        }

        PlaybackState = state;
        OnChanged();
    }

    public void SetPosition(double positionSeconds)
    {
        var cleanPosition = CleanSeconds(positionSeconds);
        if (Math.Abs(PositionSeconds - cleanPosition) < 0.001)
        {
            return;
        }

        PositionSeconds = cleanPosition;
        OnChanged();
    }

    public void SetDuration(double durationSeconds)
    {
        DurationSeconds = CleanSeconds(durationSeconds);
        if (PositionSeconds > DurationSeconds && DurationSeconds > 0)
        {
            PositionSeconds = DurationSeconds;
        }

        OnChanged();
    }

    public void SetError(string? message)
    {
        LastError = string.IsNullOrWhiteSpace(message) ? null : message;
        OnChanged();
    }

    public void Reset()
    {
        Content = MediaContent.None;
        PlaybackState = MediaPlayerState.Null;
        SourcePath = null;
        PositionSeconds = 0;
        DurationSeconds = 0;
        LastError = null;
        OnChanged();
    }

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
