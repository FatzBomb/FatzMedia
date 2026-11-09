using FatzMedia.Video.Core.Threading;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// ENG-024: <see cref="Reset"/> starts the runtime settings from their defaults again; hosts hear
/// about each one that changed through its usual change event.
/// </summary>
public partial class PlayerCore
{
    /// <summary>Reads the settings Reset returns to defaults; <see langword="null"/> if the graph cannot say.</summary>
    private ResetChanges? TryCaptureResetChanges()
    {
        try
        {
            return new ResetChanges(
                Effects.Volume,
                Effects.Pitch,
                Effects.Tempo,
                Effects.Normalize,
                Effects.Equalizer,
                Controls.LoopMedia,
                _presentationCoordinator.BaseVisualTransform);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerCore] Reading settings for Reset failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Queues the change events for the settings Reset changed, on the new control queue after
    /// Reset returned (handlers may call the player again).
    /// </summary>
    private void RaiseResetChanges(ResetChanges? before)
    {
        if (before is null || _disposed)
            return;

        if (TryCaptureResetChanges() is not { } after || after == before)
            return;

        const MediaChangeReason reason = MediaChangeReason.PlayerReset;
        var normalizationGain = Effects.NormalizationGain;
        _ = _controlOperations.Enqueue(() =>
        {
            if (before.Volume != after.Volume)
                HostEventDispatcher.Raise(this, VolumeChanged, new MediaValueChangedEventArgs<double>(before.Volume, after.Volume, reason), nameof(VolumeChanged));
            if (before.Pitch != after.Pitch)
                HostEventDispatcher.Raise(this, PitchChanged, new MediaValueChangedEventArgs<int>(before.Pitch, after.Pitch, reason), nameof(PitchChanged));
            if (before.Tempo != after.Tempo)
                HostEventDispatcher.Raise(this, TempoChanged, new MediaValueChangedEventArgs<int>(before.Tempo, after.Tempo, reason), nameof(TempoChanged));
            if (before.Normalize != after.Normalize)
                HostEventDispatcher.Raise(this, NormalizeChanged, new MediaNormalizeChangedEventArgs(before.Normalize, after.Normalize, normalizationGain), nameof(NormalizeChanged));
            if (before.Equalizer != after.Equalizer)
                HostEventDispatcher.Raise(this, EqualizerChanged, new MediaValueChangedEventArgs<AudioEqualizerSettings>(before.Equalizer, after.Equalizer, reason), nameof(EqualizerChanged));
            if (before.LoopMedia != after.LoopMedia)
                HostEventDispatcher.Raise(this, LoopMediaChanged, new MediaValueChangedEventArgs<bool>(before.LoopMedia, after.LoopMedia, reason), nameof(LoopMediaChanged));
            if (before.BaseVisualTransform != after.BaseVisualTransform)
                HostEventDispatcher.Raise(this, BaseVisualTransformChanged, new MediaValueChangedEventArgs<BaseVisualTransform>(before.BaseVisualTransform, after.BaseVisualTransform, reason), nameof(BaseVisualTransformChanged));
        });
    }

    private sealed record ResetChanges(
        double Volume,
        int Pitch,
        int Tempo,
        bool Normalize,
        AudioEqualizerSettings Equalizer,
        bool LoopMedia,
        BaseVisualTransform BaseVisualTransform);
}
