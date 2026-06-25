namespace FatzBombMedia.MediaControl.Pipeline;

using System.Diagnostics;

internal sealed class PlaybackClock
{
    private readonly Stopwatch _stopwatch = new();
    private double _basePositionSeconds;
    private double _durationSeconds;
    private double _rate = 1.0;

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            _durationSeconds = CleanSeconds(value);
            _basePositionSeconds = ClampToDuration(_basePositionSeconds);
        }
    }

    public double PositionSeconds => ClampToDuration(_basePositionSeconds + (_stopwatch.IsRunning ? _stopwatch.Elapsed.TotalSeconds * _rate : 0));

    public bool IsRunning => _stopwatch.IsRunning;
    public double Rate => _rate;
    public int TempoPercent { get; private set; }

    public void Start(double? positionSeconds = null)
    {
        if (positionSeconds.HasValue)
        {
            _basePositionSeconds = ClampToDuration(CleanSeconds(positionSeconds.Value));
        }

        _stopwatch.Restart();
    }

    public void Pause()
    {
        _basePositionSeconds = PositionSeconds;
        _stopwatch.Reset();
    }

    public void Stop()
    {
        _basePositionSeconds = 0;
        _stopwatch.Reset();
    }

    public void Seek(double positionSeconds)
    {
        var wasRunning = _stopwatch.IsRunning;
        _basePositionSeconds = ClampToDuration(CleanSeconds(positionSeconds));
        _stopwatch.Reset();

        if (wasRunning)
        {
            _stopwatch.Start();
        }
    }

    public void SetTempo(int tempoPercent)
    {
        var currentPosition = PositionSeconds;
        TempoPercent = Math.Clamp(tempoPercent, -95, 5000);
        _rate = Math.Max(0.01, 1.0 + TempoPercent / 100.0);
        _basePositionSeconds = currentPosition;

        if (_stopwatch.IsRunning)
        {
            _stopwatch.Restart();
        }
    }

    private double ClampToDuration(double seconds)
    {
        if (_durationSeconds <= 0)
        {
            return Math.Max(0, seconds);
        }

        return Math.Clamp(seconds, 0, _durationSeconds);
    }

    private static double CleanSeconds(double seconds)
    {
        return double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
    }
}
