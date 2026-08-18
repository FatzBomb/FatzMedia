using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Threading;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Handles audio effects like volume, pitch, and tempo.
/// </summary>
public sealed class MediaEffects
{
    private readonly IMediaEngine _mediaEngine;
    private IAudioEngine? _subscribedAudioEngine;
    private int _pitch;
    private int _tempo = 100;

    private bool HasAudio => _mediaEngine.Content.HasFlag(MediaContent.Audio);

    private IClockSource? ClockSource => (_mediaEngine as IClockBackedMediaEngine)?.ClockSource;

    // Clock-backed media (video or CDG without a real audio stream) drives timing through the
    // virtual clock directly. Volume, pitch, and normalization remain real-audio only.
    private bool HasClockBackedTempo => !HasAudio
        && ClockSource != null
        && (_mediaEngine.Content.HasFlag(MediaContent.Video) || _mediaEngine.Content.HasFlag(MediaContent.Cdg));

    internal MediaEffects(IMediaEngine mediaEngine)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
        EnsureActiveAudioSubscription();
    }

    private IAudioEngine Audio
    {
        get
        {
            EnsureActiveAudioSubscription();
            return _mediaEngine.Audio;
        }
    }

    private void EnsureActiveAudioSubscription()
    {
        if (!HasAudio)
        {
            _subscribedAudioEngine?.NormalizationGainChanged -= OnNormalizationGainChanged;
            _subscribedAudioEngine = null;

            if (HasClockBackedTempo)
                ClockSource!.SetTempo(_tempo - 100);

            return;
        }

        IAudioEngine current = _mediaEngine.Audio ?? throw new InvalidOperationException("MediaEngine does not have an audio engine");
        if (ReferenceEquals(_subscribedAudioEngine, current)) return;

        _subscribedAudioEngine?.NormalizationGainChanged -= OnNormalizationGainChanged;

        _subscribedAudioEngine = current;
        if (HasAudio)
        {
            _subscribedAudioEngine.NormalizationGainChanged += OnNormalizationGainChanged;
            _subscribedAudioEngine.Normalize = Normalize;
            _subscribedAudioEngine.SetVolume(Volume);
            _subscribedAudioEngine.SetPitch(Pitch);
        }

        _subscribedAudioEngine.SetTempo(_tempo - 100);
    }

    internal void RefreshActiveEngine() => EnsureActiveAudioSubscription();

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
                Audio.SetVolume(field);

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

    private void SetPitch(int value, MediaChangeReason reason)
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

    private void SetTempo(int value, MediaChangeReason reason)
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

    private void OnNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);
}
