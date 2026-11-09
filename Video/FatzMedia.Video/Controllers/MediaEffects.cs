using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Threading;

namespace FatzMedia.Video.Controllers;

/// <summary>
/// Handles audio effects like volume, pitch, and tempo.
/// </summary>
public sealed class MediaEffects
{
    readonly IMediaEngine _mediaEngine;
    // Orders user-volume writes (host thread) against the scrub mute (control queue and the
    // scrub timer) so the engine always ends on the level the latest decision asked for.
    readonly Lock _volumeGate = new();
    IAudioEngine? _subscribedAudioEngine;
    bool _scrubMuted;
    int _pitch;
    int _tempo = 100;

    bool HasAudio => _mediaEngine.Content.HasFlag(MediaContent.Audio);

    IClockSource? ClockSource => (_mediaEngine as IClockBackedMediaEngine)?.ClockSource;

    // Clock-backed media (video or CDG without a real audio stream) drives timing through the
    // virtual clock directly. Volume, pitch, and normalization remain real-audio only.
    bool HasClockBackedTempo => !HasAudio
        && ClockSource != null
        && (_mediaEngine.Content.HasFlag(MediaContent.Video) || _mediaEngine.Content.HasFlag(MediaContent.Cdg));

    internal MediaEffects(IMediaEngine mediaEngine)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        EnsureActiveAudioSubscription();
    }

    IAudioEngine Audio
    {
        get
        {
            EnsureActiveAudioSubscription();
            return _mediaEngine.Audio;
        }
    }

    void EnsureActiveAudioSubscription()
    {
        if (!HasAudio)
        {
            _subscribedAudioEngine?.NormalizationGainChanged -= OnNormalizationGainChanged;
            _subscribedAudioEngine = null;

            if (HasClockBackedTempo)
                ClockSource!.SetTempo(_tempo - 100);

            return;
        }

        var current = _mediaEngine.Audio ?? throw new InvalidOperationException("MediaEngine does not have an audio engine");
        if (ReferenceEquals(_subscribedAudioEngine, current)) return;

        _subscribedAudioEngine?.NormalizationGainChanged -= OnNormalizationGainChanged;

        _subscribedAudioEngine = current;
        _subscribedAudioEngine.NormalizationGainChanged += OnNormalizationGainChanged;
        ApplyEffectsTo(_subscribedAudioEngine);
    }

    /// <summary>
    /// Called after every load. The audio engine is usually the same singleton across loads, so
    /// settings changed while no audio was open are pushed here even when the subscription is
    /// unchanged; each setter is a no-op when its value already matches.
    /// </summary>
    internal void RefreshActiveEngine()
    {
        var previous = _subscribedAudioEngine;
        EnsureActiveAudioSubscription();
        if (_subscribedAudioEngine is { } current && ReferenceEquals(previous, current))
            ApplyEffectsTo(current);
    }

    void ApplyEffectsTo(IAudioEngine engine)
    {
        engine.Normalize = Normalize;
        ApplyVolume(engine);
        engine.SetPitch(Pitch);
        (engine as IAudioFilterHost)?.SetEqualizer(Equalizer);
        engine.SetTempo(_tempo - 100);
    }

    /// <summary>
    /// Mutes or restores the audio for a moving scrub (<see cref="ScrubAudioMuter"/>) without
    /// changing <see cref="Volume"/>: the engine gets silence while muted and the user volume
    /// again, with <paramref name="ramp"/> when it can slide, once restored.
    /// </summary>
    internal void SetScrubMuted(bool muted, TimeSpan ramp)
    {
        lock (_volumeGate)
        {
            if (_scrubMuted == muted)
                return;

            _scrubMuted = muted;
            // Read without Audio: this runs off the control queue and must not resubscribe.
            if (HasAudio && _mediaEngine.Audio is { } engine)
                ApplyVolumeLocked(engine, ramp);
        }
    }

    void ApplyVolume(IAudioEngine engine)
    {
        lock (_volumeGate)
            ApplyVolumeLocked(engine, TimeSpan.Zero);
    }

    void ApplyVolumeLocked(IAudioEngine engine, TimeSpan ramp)
    {
        var volume = _scrubMuted ? 0 : Volume;
        if (ramp > TimeSpan.Zero && engine is IAudioVolumeSlide slide)
            slide.SlideVolume(volume, ramp);
        else
            engine.SetVolume(volume);
    }

    /// <summary>
    /// Gets or sets the volume (0.0 to 1.0).
    /// </summary>
    public double Volume
    {
        get;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(field - clamped) < 0.000001)
                return;

            var previous = field;
            field = clamped;
            if (HasAudio)
                ApplyVolume(Audio);

            HostEventDispatcher.Raise(
                this,
                VolumeChanged,
                new MediaValueChangedEventArgs<double>(previous, field),
                nameof(VolumeChanged));
        }
    } = 1.0;

    /// <summary>
    /// Gets or sets the pitch in semitones (-60 to +60).
    /// </summary>
    public int Pitch
    {
        get => _pitch;
        set => SetPitch(value, MediaChangeReason.User);
    }

    /// <summary>
    /// Gets or sets the tempo as a percentage (5 to 5000, where 100 = normal speed).
    /// </summary>
    public int Tempo
    {
        get => HasAudio
            ? Audio.CurrentTempo + 100
            : HasClockBackedTempo ? ClockSource!.TempoPercent + 100 : _tempo;
        set => SetTempo(value, MediaChangeReason.User);
    }

    /// <summary>
    /// Gets or sets whether normalization is enabled.
    /// When enabled, applies cached normalization gain. When disabled, uses 1.0 gain.
    /// The gain analysis happens automatically on file load regardless of this setting.
    /// </summary>
    public bool Normalize
    {
        get;
        set
        {
            if (field == value) return;
            var previous = field;
            field = value;
            if (HasAudio)
                Audio.Normalize = value;

            Debug.WriteLine($"[MediaEffects] Normalization {(value ? "enabled" : "disabled")} by user");
            HostEventDispatcher.Raise(
                this,
                NormalizeChanged,
                new MediaNormalizeChangedEventArgs(previous, value, NormalizationGain),
                nameof(NormalizeChanged));
        }
    } = true;

    /// <summary>
    /// Gets the current normalization gain value in dB.
    /// </summary>
    public double NormalizationGain => HasAudio ? Audio.NormalizationGain : 1.0;

    /// <summary>
    /// Event raised when normalization gain changes (after analysis completes).
    /// </summary>
    public event EventHandler<double>? NormalizationGainChanged;

    /// <summary>
    /// Gets or sets the equalizer (Stage 020A). Applied live to the open audio stream and to
    /// every later stream; disabled/neutral settings attach no native filter.
    /// </summary>
    public AudioEqualizerSettings Equalizer
    {
        get;
        set
        {
            value ??= AudioEqualizerSettings.Disabled;
            if (field.Equals(value))
                return;

            var previous = field;
            field = value;
            if (HasAudio)
                (Audio as IAudioFilterHost)?.SetEqualizer(value);

            HostEventDispatcher.Raise(
                this,
                EqualizerChanged,
                new MediaValueChangedEventArgs<AudioEqualizerSettings>(previous, value),
                nameof(EqualizerChanged));
        }
    } = AudioEqualizerSettings.Disabled;

    public event EventHandler<MediaValueChangedEventArgs<AudioEqualizerSettings>>? EqualizerChanged;

    public event EventHandler<MediaValueChangedEventArgs<double>>? VolumeChanged;

    public event EventHandler<MediaValueChangedEventArgs<int>>? PitchChanged;

    public event EventHandler<MediaValueChangedEventArgs<int>>? TempoChanged;

    public event EventHandler<MediaNormalizeChangedEventArgs>? NormalizeChanged;

    internal void ApplyLoadReset(bool resetPitch, bool resetTempo)
    {
        if (resetPitch)
            SetPitch(0, MediaChangeReason.LoadReset);
        if (resetTempo)
            SetTempo(100, MediaChangeReason.LoadReset);
    }

    void SetPitch(int value, MediaChangeReason reason)
    {
        var clamped = Math.Clamp(value, -60, 60);
        if (_pitch == clamped)
            return;

        var previous = _pitch;
        _pitch = clamped;
        if (HasAudio)
            Audio.SetPitch(_pitch);

        HostEventDispatcher.Raise(
            this,
            PitchChanged,
            new MediaValueChangedEventArgs<int>(previous, _pitch, reason),
            nameof(PitchChanged));
    }

    void SetTempo(int value, MediaChangeReason reason)
    {
        var clamped = Math.Clamp(value, 5, 5000);
        var previous = Tempo;
        if (previous == clamped)
            return;

        _tempo = clamped;
        if (HasAudio)
            Audio.SetTempo(_tempo - 100);
        else if (HasClockBackedTempo)
            ClockSource!.SetTempo(_tempo - 100);

        HostEventDispatcher.Raise(
            this,
            TempoChanged,
            new MediaValueChangedEventArgs<int>(previous, Tempo, reason),
            nameof(TempoChanged));
    }

    void OnNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);
}
