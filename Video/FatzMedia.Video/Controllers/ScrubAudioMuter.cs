using System.Diagnostics;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// Mutes audio while a scrub keeps moving (<see cref="MediaScrubAudio.SilentWhileMoving"/>):
/// each scrub seek mutes at once, before the seek moves the audio, and audio comes back with a
/// short ramp once no scrub seek has arrived for the quiet period, or when the scrub ends.
/// </summary>
/// <remarks>
/// The mute itself is a separate attenuation applied by <see cref="MediaEffects"/>: user volume,
/// fades and normalization are untouched. The quiet-period timer runs off the control queue;
/// one lock orders it against seeks so the last decision is the one applied.
/// </remarks>
sealed class ScrubAudioMuter : IDisposable
{
    /// <summary>How long a drag must rest before its audio comes back.</summary>
    internal static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(120);

    /// <summary>Ramp that brings audio back, short enough to feel immediate without a click.</summary>
    internal static readonly TimeSpan UnmuteRamp = TimeSpan.FromMilliseconds(20);

    readonly Action<bool, TimeSpan> _setMuted;
    readonly TimeProvider _timeProvider;
    TimeSpan _quietPeriod;
    readonly ITimer _timer;
    readonly Lock _gate = new();
    MediaScrubAudio _mode;
    long _lastSeekTimestamp;
    int _seeksRunning;
    bool _muted;
    bool _armed;
    bool _disposed;

    /// <param name="setMuted">Applies the mute (<see langword="true"/>) or unmute with the given ramp.</param>
    /// <param name="timeProvider">Drives the quiet-period timer; the system clock when null.</param>
    /// <param name="quietPeriod">How long without a seek before audio comes back; the default quiet period when null.</param>
    public ScrubAudioMuter(Action<bool, TimeSpan> setMuted, TimeProvider? timeProvider = null, TimeSpan? quietPeriod = null)
    {
        ArgumentNullException.ThrowIfNull(setMuted);
        _setMuted = setMuted;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        _timer = _timeProvider.CreateTimer(_ => OnQuietPeriodElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public MediaScrubAudio Mode
    {
        get
        {
            lock (_gate)
                return _mode;
        }
        set
        {
            lock (_gate)
            {
                _mode = value;
                if (value == MediaScrubAudio.Audible)
                    UnmuteLocked();
            }
        }
    }

    /// <summary>Gets or sets how long a drag must rest before its audio comes back.</summary>
    internal TimeSpan QuietPeriod
    {
        get
        {
            lock (_gate)
                return _quietPeriod;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            lock (_gate)
                _quietPeriod = value;
        }
    }

    /// <summary>Gets whether audio is currently muted by a moving scrub.</summary>
    public bool IsMuted
    {
        get
        {
            lock (_gate)
                return _muted;
        }
    }

    /// <summary>
    /// Called before each seek of a scrub moves the media. Returns <see langword="true"/> when
    /// the seek is tracked; then call <see cref="OnScrubSeekFinished"/> once it returns.
    /// </summary>
    public bool OnScrubSeek()
    {
        lock (_gate)
        {
            if (_disposed || _mode != MediaScrubAudio.SilentWhileMoving)
                return false;

            _seeksRunning++;
            _lastSeekTimestamp = _timeProvider.GetTimestamp();
            if (!_muted)
            {
                _muted = true;
                Apply(muted: true, TimeSpan.Zero);
            }

            if (!_armed)
            {
                _armed = true;
                _ = _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
            }

            return true;
        }
    }

    /// <summary>
    /// A tracked scrub seek returned. The quiet period counts from here: while a seek runs, the
    /// drag requests behind it wait on the control queue and none of them has reached the muter.
    /// </summary>
    public void OnScrubSeekFinished()
    {
        lock (_gate)
        {
            if (_seeksRunning > 0)
                _seeksRunning--;

            _lastSeekTimestamp = _timeProvider.GetTimestamp();
        }
    }

    /// <summary>The scrub ended or was cancelled: audio comes back now.</summary>
    public void OnScrubEnded()
    {
        lock (_gate)
            UnmuteLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Dispose();
        }
    }

    void OnQuietPeriodElapsed()
    {
        try
        {
            OnQuietPeriodElapsedCore();
        }
        catch (Exception ex)
        {
            // A timer callback must not throw: that would end the process.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, nameof(ScrubAudioMuter), $"Restoring audio after a scrub failed: {ex.Message}");
        }
    }

    void OnQuietPeriodElapsedCore()
    {
        lock (_gate)
        {
            _armed = false;
            if (_disposed || !_muted)
                return;

            // The timer is armed once per quiet period rather than reset by every seek; a seek
            // that arrived meanwhile pushes the unmute out by the rest of its own quiet period,
            // and a seek still running holds it for a whole one.
            var remaining = _seeksRunning > 0
                ? _quietPeriod
                : _quietPeriod - _timeProvider.GetElapsedTime(_lastSeekTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                _armed = true;
                _ = _timer.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }

            UnmuteLocked();
        }
    }

    void UnmuteLocked()
    {
        if (!_muted)
            return;

        _muted = false;
        Apply(muted: false, UnmuteRamp);
    }

    void Apply(bool muted, TimeSpan ramp)
    {
        try
        {
            _setMuted(muted, ramp);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The audio engine closed or was replaced meanwhile; a new one starts unmuted.
            Debug.WriteLine($"[ScrubAudioMuter] {(muted ? "Mute" : "Unmute")} skipped: {ex.Message}");
        }
    }
}
