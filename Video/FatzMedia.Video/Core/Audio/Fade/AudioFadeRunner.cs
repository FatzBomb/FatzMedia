namespace FatzMedia.Video.Core.Audio.Fade;

/// <summary>
/// Audio stage that owns the fade envelope gain, independent of user volume and normalization.
/// </summary>
/// <remarks>
/// Each fade runs inside a session. Opening, closing, or stopping media ends the current
/// session and restores unity gain, so ramps from an older session are rejected and an
/// interrupted fade never leaves the next playback muted.
/// </remarks>
internal interface IAudioFadeGainTarget
{
    /// <summary>
    /// Starts a new fade session, superseding the previous one. Returns <c>0</c> when the fade
    /// stage is unavailable for the current media.
    /// </summary>
    int BeginFadeSession();

    /// <summary>
    /// Starts a native linear ramp from the current fade gain to <paramref name="gain"/> over
    /// <paramref name="ramp"/>; a zero ramp sets the gain immediately. Returns
    /// <see langword="false"/> when <paramref name="session"/> is no longer current.
    /// </summary>
    bool SetFadeGain(int session, double gain, TimeSpan ramp);
}

/// <summary>How a fade run ended.</summary>
internal enum AudioFadeOutcome
{
    /// <summary>The whole envelope was applied.</summary>
    Completed,

    /// <summary>The current media has no fade stage; nothing was applied.</summary>
    Unavailable,

    /// <summary>The fade was cancelled, superseded, or its session ended (stop, open, close).</summary>
    Interrupted,
}

/// <summary>
/// Runs one fade envelope as chained native ramps. Latest wins: starting a fade cancels the
/// previous one, which leaves the gain where its last ramp had reached.
/// </summary>
internal sealed class AudioFadeRunner(Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>
    /// Runs the envelope from <paramref name="startPosition"/>. Returns <see langword="true"/> when
    /// the whole envelope was applied, <see langword="false"/> when the stage is unavailable, a ramp
    /// was rejected, or the fade was cancelled or superseded.
    /// </summary>
    public async Task<bool> RunAsync(
        IAudioFadeGainTarget target,
        AudioFadeEnvelope envelope,
        TimeSpan duration,
        double startPosition,
        CancellationToken cancellationToken) =>
        await RunWithOutcomeAsync(target, envelope, duration, startPosition, cancellationToken).ConfigureAwait(false)
            == AudioFadeOutcome.Completed;

    /// <summary>Runs the envelope like <see cref="RunAsync"/> and reports how it ended.</summary>
    public async Task<AudioFadeOutcome> RunWithOutcomeAsync(
        IAudioFadeGainTarget target,
        AudioFadeEnvelope envelope,
        TimeSpan duration,
        double startPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);
        CancellationTokenSource cts = Replace(cancellationToken, out var superseded);
        try
        {
            var session = target.BeginFadeSession();
            if (session == 0)
                return AudioFadeOutcome.Unavailable;

            // A fade that interrupts a running one continues from the gain that fade reached:
            // jumping to this envelope's start would be an audible step. Otherwise the envelope
            // starts exactly at its own start gain.
            IReadOnlyList<AudioFadeSegment> segments = AudioFadeSchedule.Build(envelope, duration, startPosition);
            if ((!superseded || segments.Count == 0)
                && !target.SetFadeGain(session, envelope.Evaluate(startPosition), TimeSpan.Zero))
            {
                return AudioFadeOutcome.Interrupted;
            }

            foreach (AudioFadeSegment segment in segments)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!target.SetFadeGain(session, segment.ToGain, segment.Duration))
                    return AudioFadeOutcome.Interrupted;

                await _delay(segment.Duration, cts.Token).ConfigureAwait(false);
            }

            return cts.Token.IsCancellationRequested ? AudioFadeOutcome.Interrupted : AudioFadeOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            return AudioFadeOutcome.Interrupted;
        }
        finally
        {
            Release(cts);
        }
    }

    /// <summary>Cancels the running fade, if any.</summary>
    public void Cancel()
    {
        CancellationTokenSource? current;
        lock (_gate)
        {
            current = _current;
            _current = null;
        }

        CancelQuietly(current);
    }

    /// <summary>
    /// Returns the fade stage to full gain over <paramref name="ramp"/> unless another fade is
    /// running, in which case that fade owns the gain. Returns <see langword="true"/> when a
    /// restore ramp was applied.
    /// </summary>
    public bool RestoreUnityIfIdle(IAudioFadeGainTarget target, TimeSpan ramp)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            if (_current != null)
                return false;

            var session = target.BeginFadeSession();
            return session != 0 && target.SetFadeGain(session, 1, ramp);
        }
    }

    private CancellationTokenSource Replace(CancellationToken cancellationToken, out bool superseded)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _current;
            _current = next;
        }

        superseded = previous is not null;
        CancelQuietly(previous);
        return next;
    }

    /// <summary>
    /// A detached source may already be disposed by its finishing run; that fade is over, so
    /// there is nothing left to cancel.
    /// </summary>
    private static void CancelQuietly(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Release(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, cts))
                _current = null;
        }

        cts.Dispose();
    }
}
