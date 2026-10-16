namespace FatzBomb.MediaControl.Core.Audio.Fade;

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
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(envelope);
        CancellationTokenSource cts = Replace(cancellationToken);
        try
        {
            var session = target.BeginFadeSession();
            if (session == 0 || !target.SetFadeGain(session, envelope.Evaluate(startPosition), TimeSpan.Zero))
                return false;

            foreach (AudioFadeSegment segment in AudioFadeSchedule.Build(envelope, duration, startPosition))
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!target.SetFadeGain(session, segment.ToGain, segment.Duration))
                    return false;

                await _delay(segment.Duration, cts.Token).ConfigureAwait(false);
            }

            return !cts.Token.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
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

        current?.Cancel();
    }

    private CancellationTokenSource Replace(CancellationToken cancellationToken)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _current;
            _current = next;
        }

        previous?.Cancel();
        return next;
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
