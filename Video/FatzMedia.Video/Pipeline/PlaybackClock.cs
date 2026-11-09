
using FatzMedia.Video.Core.Abstractions;
using System.Diagnostics;

namespace FatzMedia.Video.Pipeline;

internal sealed class PlaybackClock : IClockSource
{
    private readonly Stopwatch _stopwatch = new();
    private double _basePositionSeconds;

    public double DurationSeconds
    {
        get;
        set
        {
            field = CleanSeconds(value);
            _basePositionSeconds = ClampToDuration(_basePositionSeconds);
        }
    }

    public double PositionSeconds => ClampToDuration(_basePositionSeconds + (_stopwatch.IsRunning ? _stopwatch.Elapsed.TotalSeconds * Rate : 0));

    public bool IsRunning => _stopwatch.IsRunning;
    public double Rate { get; private set; } = 1.0;
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
        Rate = Math.Max(0.01, 1.0 + (TempoPercent / 100.0));
        _basePositionSeconds = currentPosition;

        if (_stopwatch.IsRunning)
        {
            _stopwatch.Restart();
        }
    }

    private double ClampToDuration(double seconds) =>
        DurationSeconds <= 0 ? Math.Max(0, seconds) : Math.Clamp(seconds, 0, DurationSeconds);

    private static double CleanSeconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
