namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// A monitor's refresh grid locked onto its observed vertical blanks (Stage 017A slice 4). The
/// timestamp taken when <c>WaitForVBlank</c> returns carries the thread's wake-up jitter; the grid
/// follows the blanks with a small gain, so its phase and period are stable, and resynchronizes
/// when blanks were missed or the timing moved (mode change, a different monitor).
/// </summary>
sealed class VblankGrid(long stopwatchFrequency)
{
    const double PhaseGain = 0.1;
    const double PeriodGain = 0.01;
    const int SamplesBeforeValid = 3;
    const int OutliersBeforeRestart = 3;
    const int MismatchedIntervalsBeforeRestart = 4;

    readonly long _frequency = stopwatchFrequency > 0
        ? stopwatchFrequency
        : throw new ArgumentOutOfRangeException(nameof(stopwatchFrequency));

    double _gridTimestamp;
    double _periodTicks;
    double _nominalPeriodTicks;
    long _lastObserved;
    int _samples;
    int _outliers;
    int _mismatchedIntervals;

    /// <summary>Gets whether the grid has enough blanks to predict from.</summary>
    public bool IsValid => _samples >= SamplesBeforeValid && _periodTicks > 0;

    /// <summary>Gets the grid timestamp of the latest blank, in Stopwatch ticks.</summary>
    public long LastVsyncTimestamp => (long)Math.Round(_gridTimestamp);

    /// <summary>Gets the refresh period in Stopwatch ticks.</summary>
    public long PeriodTicks => (long)Math.Round(_periodTicks);

    /// <summary>Gets how many times the grid lost the blanks' rhythm and started over.</summary>
    public long Restarts { get; private set; }

    /// <summary>Gets why the grid last started over, for diagnostics.</summary>
    public string? LastRestartReason { get; private set; }

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
                NoteRestart($"{MismatchedIntervalsBeforeRestart} blank intervals in a row off the refresh period (last {Milliseconds(sinceLast):0.00} ms against {Milliseconds(_periodTicks):0.00} ms)");
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
                NoteRestart($"{OutliersBeforeRestart} blanks in a row off the predicted grid (last {Milliseconds(error):0.00} ms off, refresh {Milliseconds(_periodTicks):0.00} ms)");
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

    void NoteRestart(string reason)
    {
        Restarts++;
        LastRestartReason = reason;
    }

    double Milliseconds(double ticks) => ticks * 1000.0 / _frequency;

    void Restart(long timestamp)
    {
        _gridTimestamp = timestamp;
        _lastObserved = timestamp;
        _samples = 1;
        _outliers = 0;
        _mismatchedIntervals = 0;
    }
}
