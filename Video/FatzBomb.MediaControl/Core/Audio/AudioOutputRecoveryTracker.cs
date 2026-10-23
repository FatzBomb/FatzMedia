namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>What BASS reports about a device's output (<c>BASS_IsStarted</c>).</summary>
internal enum BassOutputStatus
{
    /// <summary>The status could not be read (an older BASS without <c>BASS_IsStarted</c>).</summary>
    Unknown = 0,

    /// <summary>The output is stopped or paused, by BASS itself when the device became unavailable.</summary>
    Stopped,

    /// <summary>The output is started.</summary>
    Started,
}

/// <summary>One sample of a BASS device, taken over the engines that intend to play on it.</summary>
/// <param name="Output">The device's output status.</param>
/// <param name="PlayingChannels">Channels whose engine intends to be playing.</param>
/// <param name="ChannelsPausedByDevice">
/// Of those, the channels BASS reports as <c>BASS_ACTIVE_PAUSED_DEVICE</c> (paused because the
/// output device is unavailable).
/// </param>
internal readonly record struct AudioOutputObservation(
    BassOutputStatus Output,
    int PlayingChannels,
    int ChannelsPausedByDevice);

/// <summary>The next step of output-loss recovery for one BASS device.</summary>
internal enum AudioOutputRecoveryAction
{
    /// <summary>Nothing to do now.</summary>
    None = 0,

    /// <summary>Report that the output became unavailable (once per outage).</summary>
    ReportLost,

    /// <summary>Restart the device's output (<c>BASS_Start</c>).</summary>
    Start,

    /// <summary>Reinitialize the device in place (<c>BASS_Init</c> with <c>BASS_DEVICE_REINIT</c>).</summary>
    Reinit,

    /// <summary>Resume each channel that should be playing but is still paused by the device.</summary>
    ResumeChannels,

    /// <summary>Report that the output is available again and playback resumed.</summary>
    ReportRecovered,
}

/// <summary>
/// Pure decision logic for recovering a BASS output device that another application took (for
/// example in WASAPI exclusive mode) or that was unplugged (ENG-032). It holds the outage state
/// of one device; the caller samples BASS, asks <see cref="Decide"/> for the next action, carries
/// it out and reports the outcome through <see cref="Record"/>. Time is passed in, so the unit
/// needs neither BASS nor a clock.
/// </summary>
internal sealed class AudioOutputRecoveryTracker
{
    /// <summary>How often a lost output is retried.</summary>
    internal static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _retryInterval;
    private TimeSpan? _lastAttempt;
    private bool _reinitNext;
    private bool _resumeAttempted;

    public AudioOutputRecoveryTracker()
        : this(DefaultRetryInterval)
    {
    }

    public AudioOutputRecoveryTracker(TimeSpan retryInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);
        _retryInterval = retryInterval;
    }

    /// <summary>True between a reported loss and the matching recovery.</summary>
    public bool OutageReported { get; private set; }

    /// <summary>
    /// True when a restart of the output may run now: no attempt yet in this outage, or the
    /// last one is at least the retry interval old.
    /// </summary>
    public bool CanAttempt(TimeSpan now) => _lastAttempt is not { } last || now - last >= _retryInterval;

    /// <summary>Returns the next action for the observed device state.</summary>
    public AudioOutputRecoveryAction Decide(AudioOutputObservation observation, TimeSpan now)
    {
        // Only channels we mean to play count: a host-paused or stopped player is never touched,
        // and an output that stopped while nothing plays is not ours to restart.
        if (observation.PlayingChannels <= 0)
            return AudioOutputRecoveryAction.None;

        var outputDown = observation.Output == BassOutputStatus.Stopped;
        var channelsDown = observation.ChannelsPausedByDevice > 0;
        if (!outputDown && !channelsDown)
            return OutageReported ? AudioOutputRecoveryAction.ReportRecovered : AudioOutputRecoveryAction.None;

        if (!OutageReported)
            return AudioOutputRecoveryAction.ReportLost;

        // The output is back (or its status is unknown) but channels are still parked: resume
        // them once before restarting the output again.
        if (!outputDown && !_resumeAttempted)
            return AudioOutputRecoveryAction.ResumeChannels;

        if (!CanAttempt(now))
            return AudioOutputRecoveryAction.None;

        return _reinitNext ? AudioOutputRecoveryAction.Reinit : AudioOutputRecoveryAction.Start;
    }

    /// <summary>Records the outcome of an action returned by <see cref="Decide"/>.</summary>
    /// <param name="action">The action that was carried out.</param>
    /// <param name="succeeded">
    /// For <see cref="AudioOutputRecoveryAction.Start"/> and <see cref="AudioOutputRecoveryAction.Reinit"/>:
    /// the BASS call succeeded. For <see cref="AudioOutputRecoveryAction.ResumeChannels"/>: every
    /// parked channel is playing again. Ignored for the report actions.
    /// </param>
    /// <param name="now">The time of the action.</param>
    public void Record(AudioOutputRecoveryAction action, bool succeeded, TimeSpan now)
    {
        switch (action)
        {
            case AudioOutputRecoveryAction.ReportLost:
                OutageReported = true;
                _lastAttempt = null;
                _reinitNext = false;
                _resumeAttempted = false;
                break;

            case AudioOutputRecoveryAction.Start:
                RecordAttempt(now);
                // BASS_Start failed (BASS_ERROR_REINIT, or the device is still unavailable): the
                // next attempt reinitializes the device.
                _reinitNext = !succeeded;
                break;

            case AudioOutputRecoveryAction.Reinit:
                RecordAttempt(now);
                // A failed reinit (the device is still held exclusively) alternates with a plain
                // restart, in case BASS brings the device back by itself.
                _reinitNext = false;
                break;

            case AudioOutputRecoveryAction.ResumeChannels:
                _resumeAttempted = true;
                // The output reads as started but channels stay parked: only a reinit helps.
                if (!succeeded)
                    _reinitNext = true;
                break;

            case AudioOutputRecoveryAction.ReportRecovered:
                Reset();
                break;
        }
    }

    /// <summary>Records a restart made outside <see cref="Decide"/> (a failed Play restarting the output).</summary>
    public void RecordExternalAttempt(bool reinit, bool succeeded, TimeSpan now) =>
        Record(reinit ? AudioOutputRecoveryAction.Reinit : AudioOutputRecoveryAction.Start, succeeded, now);

    /// <summary>Forgets the outage (nothing is left playing on the device).</summary>
    public void Reset()
    {
        OutageReported = false;
        _lastAttempt = null;
        _reinitNext = false;
        _resumeAttempted = false;
    }

    private void RecordAttempt(TimeSpan now)
    {
        _lastAttempt = now;
        _resumeAttempted = false;
    }
}
