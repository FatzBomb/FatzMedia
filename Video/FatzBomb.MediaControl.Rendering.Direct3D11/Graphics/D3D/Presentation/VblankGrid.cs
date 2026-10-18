namespace FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// A monitor's refresh grid locked onto its observed vertical blanks (Stage 017A slice 4). The
/// timestamp taken when <c>WaitForVBlank</c> returns carries the thread's wake-up jitter; the grid
/// follows the blanks with a small gain, so its phase and period are stable, and resynchronizes
/// when blanks were missed or the timing moved (mode change, a different monitor).
/// </summary>
internal sealed class VblankGrid(long stopwatchFrequency)
{
    private const double PhaseGain = 0.1;
    private const double PeriodGain = 0.01;
    private const int SamplesBeforeValid = 3;
    private const int OutliersBeforeRestart = 3;
    private const int MismatchedIntervalsBeforeRestart = 4;

    private readonly long _frequency = stopwatchFrequency > 0
        ? stopwatchFrequency
        : throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));

    private double _gridTimestamp;
    private double _periodTicks;
    private double _nominalPeriodTicks;
    private long _lastObserved;
    private int _samples;
    private int _outliers;
    private int _mismatchedIntervals;

    /// <summary>Gets whether the grid has enough blanks to predict from.</summary>
    public bool IsValid => _samples >= SamplesBeforeValid && _periodTicks > 0;

    /// <summary>Gets the grid timestamp of the latest blank, in Stopwatch ticks.</summary>
    public long LastVsyncTimestamp => (long)Math.Round(_gridTimestamp);

    /// <summary>Gets the refresh period in Stopwatch ticks.</summary>
    public long PeriodTicks => (long)Math.Round(_periodTicks);

    /// <summary>
    /// Sets the monitor's nominal rate (QueryDisplayConfig). It seeds the period until blanks
    /// measure it, and again after the grid starts over when it matches the blanks.
    /// </summary>
    public void SeedRefreshRate(double? hertz)
    {
        if (hertz is not (> 1 and < 1000))
            return;

        _nominalPeriodTicks = _frequency / hertz.Value;
        if (_samples == 0)
            _periodTicks = _nominalPeriodTicks;
    }

    /// <summary>Forgets the blanks (the clock stopped for a while); the nominal period is kept.</summary>
    public void Reset()
    {
        _samples = 0;
        _outliers = 0;
        _mismatchedIntervals = 0;
    }

    /// <summary>Records a blank observed at <paramref name="timestamp"/> (Stopwatch ticks).</summary>
    public void Observe(long timestamp)
    {
        if (_samples == 0)
        {
            Restart(timestamp);
            return;
        }

        var sinceLast = timestamp - _lastObserved;
        _lastObserved = timestamp;
        if (_periodTicks <= 0)
        {
            // The grid started over: the nominal period when the first interval agrees with it
            // (one interval carries the wake-up jitter), else that interval.
            _periodTicks = _nominalPeriodTicks > 0 && Math.Abs(sinceLast - _nominalPeriodTicks) < _nominalPeriodTicks * 0.1
                ? _nominalPeriodTicks
                : sinceLast;
            _gridTimestamp = timestamp;
            _samples++;
            return;
        }

        // One wake-up per blank: intervals well off the period, several in a row, mean the refresh
        // rate changed (the grid would otherwise alias onto the new blanks). A single long one is a
        // missed blank.
        if (sinceLast < _periodTicks * 0.75 || sinceLast > _periodTicks * 1.5)
        {
            if (++_mismatchedIntervals >= MismatchedIntervalsBeforeRestart)
            {
                _periodTicks = 0;
                Restart(timestamp);
                return;
            }
        }
        else
        {
            _mismatchedIntervals = 0;
        }

        // Advance the grid by the whole number of periods since its last blank (one, or more when
        // blanks were missed), then pull it a little toward what was observed. A blank far off
        // the grid is a late wake-up and is skipped; several in a row mean the timing moved (a
        // mode change), so the grid starts over and measures the period again.
        var periods = Math.Round((timestamp - _gridTimestamp) / _periodTicks);
        var predicted = _gridTimestamp + (periods * _periodTicks);
        var error = timestamp - predicted;
        if (periods < 1 || periods > 8 || Math.Abs(error) > _periodTicks * 0.25)
        {
            if (++_outliers >= OutliersBeforeRestart)
            {
                _periodTicks = 0;
                Restart(timestamp);
            }

            return;
        }

        _outliers = 0;
        _gridTimestamp = predicted + (PhaseGain * error);
        _periodTicks += PeriodGain * error / periods;
        _samples++;
    }

    private void Restart(long timestamp)
    {
        _gridTimestamp = timestamp;
        _lastObserved = timestamp;
        _samples = 1;
        _outliers = 0;
        _mismatchedIntervals = 0;
    }
}
