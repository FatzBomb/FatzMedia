using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio.Fade;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Envelope fades for ordinary local audio. Fade gain is its own native stage, multiplied with
/// user volume and normalization, and runs in wall-clock time off the control queue.
/// </summary>
public partial class MediaControlPlayer
{
    /// <summary>Ramp used to bring outgoing media back to full gain when a faded transition fails.</summary>
    private static readonly TimeSpan FailedTransitionRestoreRamp = TimeSpan.FromMilliseconds(250);

    private readonly AudioFadeRunner _fadeRunner = new();
    private AudioCrossfadeOptions _preparedNextTransitionFade = AudioCrossfadeOptions.Disabled;

    public AudioCrossfadeOptions PreparedNextTransitionFade
    {
        get => Volatile.Read(ref _preparedNextTransitionFade);
        set => Volatile.Write(ref _preparedNextTransitionFade, value ?? AudioCrossfadeOptions.Disabled);
    }

    public Task<bool> FadeOutAsync(TimeSpan duration, AudioFadeEnvelope? envelope = null, CancellationToken cancellationToken = default) =>
        RunFadeAsync(envelope ?? AudioFadeEnvelope.Outgoing, duration, startPosition: 0, cancellationToken);

    public Task<bool> FadeInAsync(TimeSpan duration, AudioFadeEnvelope? envelope = null, CancellationToken cancellationToken = default)
    {
        envelope ??= AudioFadeEnvelope.Incoming;
        return RunFadeAsync(envelope, duration, envelope.AudibleStart, cancellationToken);
    }

    public void CancelFade() => _fadeRunner.Cancel();

    private Task<bool> RunFadeAsync(AudioFadeEnvelope envelope, TimeSpan duration, double startPosition, CancellationToken cancellationToken)
    {
        ThrowIfResetting();
        duration = new AudioCrossfadeOptions { Duration = duration }.Duration;
        return duration <= TimeSpan.Zero || _mediaEngine.Audio is not IAudioFadeGainTarget target
            ? Task.FromResult(false)
            : _fadeRunner.RunAsync(target, envelope, duration, startPosition, cancellationToken);
    }

    /// <summary>
    /// Fade-through transition.
    /// </summary>
    /// <remarks>
    /// Outgoing envelope on the current media, the incoming envelope's
    /// muted lead time as a gap, then the prepared item loads with its fade gain primed before
    /// playback starts, and the incoming envelope continues from its audible start. Runs off the
    /// control queue except for the load/play step, so controls stay responsive during fades.
    /// An interrupted outgoing fade (stop, <see cref="CancelFade"/>, or a newer fade) abandons
    /// the transition; if the transition fails after the outgoing media was faded out, its
    /// gain is restored so it does not keep playing silently.
    /// </remarks>
    private async Task<bool> RunFadedPreparedNextTransitionAsync(
        long? expectedGeneration,
        bool isAutomatic,
        AudioCrossfadeOptions fade,
        bool fadeOutgoing,
        Func<bool>? stillValid)
    {
        if (!_preparedNext.IsReady(expectedGeneration))
            return false;

        IAudioFadeGainTarget? fadedOutgoing = null;
        if (fadeOutgoing
            && _mediaEngine.State == MediaPlayerState.Playing
            && _mediaEngine.Audio is IAudioFadeGainTarget outgoing)
        {
            AudioFadeOutcome outcome = await _fadeRunner.RunWithOutcomeAsync(outgoing, fade.Outgoing, fade.Duration, 0, CancellationToken.None).ConfigureAwait(false);
            if (outcome == AudioFadeOutcome.Interrupted)
                return false;

            if (outcome == AudioFadeOutcome.Completed)
                fadedOutgoing = outgoing;
        }

        TimeSpan mutedLead = fade.Duration * fade.Incoming.AudibleStart;
        if (mutedLead > TimeSpan.Zero)
            await Task.Delay(mutedLead).ConfigureAwait(false);

        var transitioned = await EnqueueControlOperation(() =>
            stillValid is not null && !stillValid()
                ? Task.FromResult(false)
                : TransitionToPreparedNextCoreAsync(expectedGeneration, isAutomatic, fade)).ConfigureAwait(false);

        if (!transitioned)
        {
            if (fadedOutgoing is not null)
                _ = _fadeRunner.RestoreUnityIfIdle(fadedOutgoing, FailedTransitionRestoreRamp);

            return false;
        }

        if (_mediaEngine.Audio is IAudioFadeGainTarget incoming)
            _ = await _fadeRunner.RunAsync(incoming, fade.Incoming, fade.Duration, fade.Incoming.AudibleStart, CancellationToken.None).ConfigureAwait(false);

        return true;
    }

    /// <summary>Sets the incoming fade gain before playback so the first samples are not at unity.</summary>
    private void PrimeIncomingFade(AudioCrossfadeOptions fade)
    {
        if (_mediaEngine.Audio is not IAudioFadeGainTarget target)
            return;

        var session = target.BeginFadeSession();
        if (session != 0)
            _ = target.SetFadeGain(session, fade.Incoming.Evaluate(fade.Incoming.AudibleStart), TimeSpan.Zero);
    }
}
