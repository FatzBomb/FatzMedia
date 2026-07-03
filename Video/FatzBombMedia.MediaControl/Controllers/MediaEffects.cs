using FatzBombMedia.MediaControl.Core.Abstractions;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Handles audio effects like volume, pitch, and tempo.
/// </summary>
public sealed class MediaEffects
{
    private readonly IMediaEngine _mediaEngine;
    private IAudioEngine? _subscribedAudioEngine;
    private int _tempo = 100;

    private bool HasAudio => _mediaEngine.Content.HasFlag(MediaContent.Audio);

    // Clock-backed media (video or CDG without a real audio stream) drives timing through the virtual
    // PlaybackClock via ClockAudioEngine, which supports tempo. Volume/pitch/normalization remain
    // real-audio only.
    private bool HasClockBackedTempo => !HasAudio
        && (_mediaEngine.Content.HasFlag(MediaContent.Video) || _mediaEngine.Content.HasFlag(MediaContent.Cdg));

    private bool SupportsTempo => HasAudio || HasClockBackedTempo;

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

        if (SupportsTempo)
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
            field = Math.Clamp(value, 0.0, 1.0);
            if (HasAudio)
                Audio.SetVolume(field);
        }
    } = 1.0;

    /// <summary>
    /// Gets or sets the pitch in semitones (-60 to +60).
    /// </summary>
    public int Pitch
    {
        get;
        set
        {
            field = Math.Clamp(value, -60, 60);
            if (HasAudio)
                Audio.SetPitch(field);
        }
    } = 0;

    /// <summary>
    /// Gets or sets the tempo as a percentage (5 to 5000, where 100 = normal speed).
    /// </summary>
    public int Tempo
    {
        get => SupportsTempo ? Audio.CurrentTempo + 100 : _tempo;
        set
        {
            _tempo = Math.Clamp(value, 5, 5000);
            if (SupportsTempo)
                Audio.SetTempo(_tempo - 100);
        }
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
            field = value;
            if (HasAudio)
                Audio.Normalize = value;

            Debug.WriteLine($"[MediaEffects] Normalization {(value ? "enabled" : "disabled")} by user");
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

    private void OnNormalizationGainChanged(object? sender, double gain) => NormalizationGainChanged?.Invoke(this, gain);
}
