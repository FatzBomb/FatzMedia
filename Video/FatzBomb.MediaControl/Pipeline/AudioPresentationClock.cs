
using System.Diagnostics;

namespace FatzBomb.MediaControl.Pipeline;

internal sealed class AudioPresentationClock
{
    private const double DefaultRate = 1.0;
    private const double MinimumRate = 0.05;
    private const double MaximumRate = 4.0;
    private const double MinimumRateSampleSeconds = 0.08;
    private const double SoftCorrectionThresholdSeconds = 0.012;
    private const double HardCorrectionThresholdSeconds = 0.09;
    private const double SoftCorrectionRatio = 0.12;
    private const double RateSmoothingRatio = 0.08;

    private bool _isRunning;
    private double _anchorPositionSeconds;
    private long _anchorTimestamp;
    private double _lastSamplePositionSeconds;
    private long _lastSampleTimestamp;
    private double _lastReturnedPositionSeconds;
    private double _rate = DefaultRate;

    public void Reset(double positionSeconds)
    {
        var now = Stopwatch.GetTimestamp();
        var cleanPosition = Clean(positionSeconds);
        _isRunning = false;
        _anchorPositionSeconds = cleanPosition;
        _anchorTimestamp = now;
        _lastSamplePositionSeconds = cleanPosition;
        _lastSampleTimestamp = now;
        _lastReturnedPositionSeconds = cleanPosition;
        _rate = DefaultRate;
    }

    public void Start(double audioPositionSeconds)
    {
        Reset(audioPositionSeconds);
        _isRunning = true;
    }

    public void Pause(double audioPositionSeconds) => Reset(audioPositionSeconds);

    public double GetPosition(double audioPositionSeconds, double durationSeconds)
    {
        if (!_isRunning)
        {
            return Clamp(Clean(audioPositionSeconds), durationSeconds);
        }

        var now = Stopwatch.GetTimestamp();
        var audioPosition = Clean(audioPositionSeconds);
        UpdateRate(audioPosition, now);

        var estimate = EstimatePosition(now);
        var drift = audioPosition - estimate;
        if (Math.Abs(drift) >= HardCorrectionThresholdSeconds)
        {
            Reanchor(audioPosition, now);
            return ClampMonotonic(audioPosition, durationSeconds);
        }

        if (Math.Abs(drift) >= SoftCorrectionThresholdSeconds)
        {
            estimate += drift * SoftCorrectionRatio;
            Reanchor(estimate, now);
        }

        return ClampMonotonic(estimate, durationSeconds);
    }

    private void UpdateRate(double audioPosition, long now)
    {
        var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
        if (elapsed >= MinimumRateSampleSeconds)
        {
            var audioDelta = audioPosition - _lastSamplePositionSeconds;
            var measuredRate = audioDelta / elapsed;
            if (double.IsFinite(measuredRate) && measuredRate is >= MinimumRate and <= MaximumRate)
            {
                _rate = (_rate * (1.0 - RateSmoothingRatio)) + (measuredRate * RateSmoothingRatio);
            }

            _lastSamplePositionSeconds = audioPosition;
            _lastSampleTimestamp = now;
        }
    }

    private double EstimatePosition(long now) => _anchorPositionSeconds + (Stopwatch.GetElapsedTime(_anchorTimestamp, now).TotalSeconds * _rate);

    private void Reanchor(double positionSeconds, long now)
    {
        _anchorPositionSeconds = Clean(positionSeconds);
        _anchorTimestamp = now;
    }

    private double ClampMonotonic(double positionSeconds, double durationSeconds)
    {
        var clamped = Clamp(positionSeconds, durationSeconds);
        if (clamped + 0.001 < _lastReturnedPositionSeconds)
        {
            clamped = _lastReturnedPositionSeconds;
        }

        _lastReturnedPositionSeconds = clamped;
        return clamped;
    }

    private static double Clamp(double seconds, double durationSeconds)
    {
        var clean = Clean(seconds);
        return durationSeconds > 0 ? Math.Clamp(clean, 0, durationSeconds) : clean;
    }

    private static double Clean(double seconds) => double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
