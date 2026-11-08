using ManagedBass;
using ManagedBass.Fx;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Handles audio peak analysis and normalization gain calculation.
/// Thread-safe implementation optimized for real-time audio processing.
/// </summary>
internal sealed class AudioNormalizer
{
    #region Constants and Static Fields

    // Normalization target level (0.8 = industry standard, can be configured)
    private const double NormalizationTarget = 0.8;

    #endregion

    #region Private Fields

    // Thread-safe normalization gain using Volatile operations
    private double _normalizationGain = 1.0;

    // Guards the FX handle and the reused parameter object: the gain is applied from the
    // analysis thread and from the Normalize setter, and the handle is replaced on open.
    private readonly Lock _gainFxLock = new();
    private int _gainFxHandle = 0;

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the current normalization gain value (thread-safe).
    /// </summary>
    public double NormalizationGain => Volatile.Read(ref _normalizationGain);

    /// <summary>
    /// Event raised when normalization gain changes.
    /// </summary>
    public event EventHandler<double>? NormalizationGainChanged;

    #endregion

    #region Public Methods

    /// <summary>
    /// Performs quick peak analysis for immediate use.
    /// Optimized for speed over accuracy.
    /// </summary>
    /// <param name="source">Level source to analyze (037A slice 1; BASS: <see cref="BassChannelLevelSource"/>), null when none</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Peak level (0.0 to 1.0)</returns>
    public async Task<double> AnalyzePeakQuickAsync(IAudioLevelSource? source, bool updateGain = true, CancellationToken cancellationToken = default)
    {
        if (source is null) return 0.0;

        const float blockSeconds = 0.25f;
        const int maxSamples = 24;

        return await Task.Run(() => AnalyzePeakInternal(source, blockSeconds, maxSamples, updateGain, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs comprehensive peak analysis for best accuracy.
    /// May take longer but provides more reliable results.
    /// </summary>
    /// <param name="source">Level source to analyze (037A slice 1; BASS: <see cref="BassChannelLevelSource"/>), null when none</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Peak level (0.0 to 1.0)</returns>
    public async Task<double> AnalyzePeakAsync(IAudioLevelSource? source, bool updateGain = true, CancellationToken cancellationToken = default) =>
        source is null
            ? 0.0
            : await Task.Run(() => AnalyzePeakFull(source, updateGain, cancellationToken), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Resets the normalization gain to default value. Thread-safe operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetGain(bool clearFxHandle = false)
    {
        Volatile.Write(ref _normalizationGain, 1.0);
        if (clearFxHandle)
        {
            lock (_gainFxLock)
                _gainFxHandle = 0;
        }
        else
            ApplyGainToFx(normalize: true);
    }

    public double SetNormalizationGainFromPeak(double peak, bool forceNotify = false)
    {
        var gain = CalculateGainFromPeak(peak);
        SetNormalizationGain(gain, forceNotify);
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioNormalizer", $"Peak analysis: peak={peak:F4}, target={NormalizationTarget}, gain={gain:F3}");
        return gain;
    }

    public void SetNormalizationGain(double gain, bool forceNotify = false)
    {
        gain = Math.Clamp(gain, 0.0, 8.0);
        var oldGain = Volatile.Read(ref _normalizationGain);
        if (!forceNotify && Math.Abs(oldGain - gain) < 0.0001)
            return;

        Volatile.Write(ref _normalizationGain, gain);
        NormalizationGainChanged?.Invoke(this, gain);
    }

    /// <summary>
    /// Sets up gain FX on the specified audio handle.
    /// This creates a separate volume FX for normalization gain control.
    /// </summary>
    /// <param name="audioHandle">BASS audio handle to attach gain FX to</param>
    /// <returns>True if FX was created successfully</returns>
    public bool SetupGainFx(int audioHandle)
    {
        if (audioHandle == 0)
            return false;

        try
        {
            int fxHandle = MBass.ChannelSetFX(audioHandle, EffectType.Volume, 0);
            lock (_gainFxLock)
                _gainFxHandle = fxHandle;

            if (fxHandle != 0)
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "AudioNormalizer", $"Gain FX created: handle={fxHandle}");
                return true;
            }
            else
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioNormalizer", $"Failed to create gain FX: {MBass.LastError}");
                return false;
            }
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioNormalizer", $"Exception creating gain FX: {ex.Message}", innerException: ex);
            return false;
        }
    }

    private readonly VolumeParameters _gainParam = new()
    {
        fTarget = 1f,
        lChannel = FXChannelFlags.All,
        fCurrent = 1f, // Frequency for the bass effect
        fTime = 0,
        lCurve = 0,
    };

    /// <summary>
    /// Applies the current normalization gain to the FX handle.
    /// This is called when normalization is enabled/disabled or gain changes.
    /// </summary>
    private void ApplyGainToFx(bool normalize)
    {
        lock (_gainFxLock)
            ApplyGainToFxCore(normalize);
    }

    private void ApplyGainToFxCore(bool normalize)
    {
        if (_gainFxHandle == 0)
            return;

        try
        {
            _gainParam.fTarget = (float)(normalize ? _normalizationGain : 1.0);
            _gainParam.fTime = 0;
            _gainParam.lCurve = 0;

            if (MBass.FXSetParameters(_gainFxHandle, _gainParam))
                Debug.WriteLine($"Applied gain {_gainParam.fTarget:F3}");
            else
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioNormalizer", $"Failed to apply gain: {MBass.LastError}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "AudioNormalizer", $"Exception applying gain: {ex.Message}", innerException: ex);
        }
    }

    /// <summary>
    /// Sets whether normalization should be applied to the audio output.
    /// The gain value is cached and ready - this just controls whether it's applied.
    /// </summary>
    /// <param name="normalize">True to apply cached gain, false to use 1.0 gain</param>
    public void SetNormalizeEnabled(bool normalize) => ApplyGainToFx(normalize);

    #endregion

    #region Private Methods

    private double AnalyzePeakInternal(IAudioLevelSource source, float blockSeconds, int maxSamples, bool updateGain, CancellationToken cancellationToken)
    {
        var peak = 0.0;

        for (var i = 0; i < maxSamples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!source.TryReadPeak(blockSeconds, out var blockPeak))
                break;

            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);

            if (blockPeak > peak)
                peak = blockPeak;
        }

        if (updateGain)
        {
            _ = SetNormalizationGainFromPeak(peak);
        }

        return peak;
    }

    private double AnalyzePeakFull(IAudioLevelSource source, bool updateGain, CancellationToken cancellationToken)
    {
        const double blockSeconds = 0.5;
        const int maxWindows = 240;

        double peak;

        peak = source.TryGetDuration(out var duration)
            ? AnalyzePeakBySeek(source, duration, blockSeconds, maxWindows, cancellationToken)
            : AnalyzePeakSequential(source, blockSeconds, maxWindows, cancellationToken);

        if (updateGain)
            _ = SetNormalizationGainFromPeak(peak);

        return peak;
    }

    private static double AnalyzePeakBySeek(IAudioLevelSource source, double duration, double blockSeconds, int maxWindows,
        CancellationToken cancellationToken)
    {
        var stride = Math.Max(1, (int)Math.Ceiling(duration / (blockSeconds * maxWindows)));
        var peak = 0.0;

        for (var position = 0.0; position < duration; position += blockSeconds * stride)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!source.TrySeek(position) ||
                !source.TryReadPeak((float)blockSeconds, out var blockPeak))
                break;

            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);

            if (blockPeak > peak)
                peak = blockPeak;
        }

        return peak;
    }

    private static double AnalyzePeakSequential(IAudioLevelSource source, double blockSeconds, int maxWindows,
        CancellationToken cancellationToken)
    {
        var peak = 0.0;

        for (var i = 0; i < maxWindows; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!source.TryReadPeak((float)blockSeconds, out var blockPeak))
                break;

            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);

            if (blockPeak > peak)
                peak = blockPeak;
        }

        return peak;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static double CalculateGainFromPeak(double peak) => peak > 0.0 ? Math.Clamp(NormalizationTarget / peak, 0.0, 8.0) : 1.0;

    #endregion
}
