namespace FatzMedia.Video.Pipeline;

/// <summary>
/// Per-item timeline corrections. Audio remains the physical clock; its offset shifts video
/// presentation relative to that clock while CDG and subtitle offsets shift their own timelines.
/// </summary>
internal sealed class PlaybackSyncState
{
    private const double MaximumOffsetSeconds = 30;
    private readonly Lock _gate = new();
    private double _audioVideoOffsetSeconds;
    private double _cdgOffsetSeconds;
    private double _subtitleOffsetSeconds;

    public double AudioVideoOffsetSeconds { get { lock (_gate) return _audioVideoOffsetSeconds; } }
    public double CdgOffsetSeconds { get { lock (_gate) return _cdgOffsetSeconds; } }
    public double SubtitleOffsetSeconds { get { lock (_gate) return _subtitleOffsetSeconds; } }

    public double SetAudioVideoOffset(double seconds) => Set(PlaybackSyncTarget.AudioVideo, seconds);
    public double SetCdgOffset(double seconds) => Set(PlaybackSyncTarget.Cdg, seconds);
    public double SetSubtitleOffset(double seconds) => Set(PlaybackSyncTarget.Subtitle, seconds);

    public double AdjustAudioVideoOffset(double seconds) => Adjust(PlaybackSyncTarget.AudioVideo, seconds);
    public double AdjustCdgOffset(double seconds) => Adjust(PlaybackSyncTarget.Cdg, seconds);
    public double AdjustSubtitleOffset(double seconds) => Adjust(PlaybackSyncTarget.Subtitle, seconds);

    public void Reset()
    {
        lock (_gate)
        {
            _audioVideoOffsetSeconds = 0;
            _cdgOffsetSeconds = 0;
            _subtitleOffsetSeconds = 0;
        }
    }

    private double Set(PlaybackSyncTarget target, double seconds)
    {
        var value = double.IsFinite(seconds) ? Math.Clamp(seconds, -MaximumOffsetSeconds, MaximumOffsetSeconds) : 0;
        lock (_gate)
        {
            return SetNoLock(target, value);
        }
    }

    private double Adjust(PlaybackSyncTarget target, double seconds)
    {
        lock (_gate)
        {
            var current = target switch
            {
                PlaybackSyncTarget.AudioVideo => _audioVideoOffsetSeconds,
                PlaybackSyncTarget.Cdg => _cdgOffsetSeconds,
                _ => _subtitleOffsetSeconds
            };
            var value = double.IsFinite(seconds) ? Math.Clamp(current + seconds, -MaximumOffsetSeconds, MaximumOffsetSeconds) : current;
            return SetNoLock(target, value);
        }
    }

    private double SetNoLock(PlaybackSyncTarget target, double value)
    {
        switch (target)
        {
            case PlaybackSyncTarget.AudioVideo:
                _audioVideoOffsetSeconds = value;
                return _audioVideoOffsetSeconds;
            case PlaybackSyncTarget.Cdg:
                _cdgOffsetSeconds = value;
                return _cdgOffsetSeconds;
            default:
                _subtitleOffsetSeconds = value;
                return _subtitleOffsetSeconds;
        }
    }

    private enum PlaybackSyncTarget
    {
        AudioVideo,
        Cdg,
        Subtitle
    }
}
