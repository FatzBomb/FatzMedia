using ManagedBass;
using ManagedBass.Fx;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using MBass = ManagedBass.Bass;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// Handles audio peak analysis and normalization gain calculation.
/// Thread-safe implementation optimized for real-time audio processing.
/// </summary>
internal sealed class AudioNormalizer
{
    #region Constants and Static Fields
    
    // Performance optimization: pre-allocated arrays for level analysis
    private static readonly ThreadLocal<float[]> LevelBuffer = new(() => new float[2]);
    
    // Normalization target level (0.8 = industry standard, can be configured)
    private const double NormalizationTarget = 0.8;
    
    #endregion

    #region Private Fields
    
    // Thread-safe normalization gain using Volatile operations
    private double _normalizationGain = 1.0;
    
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
    /// <param name="handle">BASS channel handle for analysis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Peak level (0.0 to 1.0)</returns>
    public async Task<double> AnalyzePeakQuickAsync(int handle, CancellationToken cancellationToken = default, bool updateGain = true)
    {
        if (handle == 0) return 0.0;
        
        const float blockSeconds = 0.25f;
        const int maxSamples = 24;
        
        return await Task.Run(() => AnalyzePeakInternal(handle, blockSeconds, maxSamples, cancellationToken, updateGain), cancellationToken);
    }

    /// <summary>
    /// Performs comprehensive peak analysis for best accuracy.
    /// May take longer but provides more reliable results.
    /// </summary>
    /// <param name="handle">BASS channel handle for analysis</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Peak level (0.0 to 1.0)</returns>
    public async Task<double> AnalyzePeakAsync(int handle, CancellationToken cancellationToken = default, bool updateGain = true)
    {
        if (handle == 0) return 0.0;
        
        return await Task.Run(() => AnalyzePeakFull(handle, cancellationToken, updateGain), cancellationToken);
    }

    /// <summary>
    /// Resets the normalization gain to default value.
    /// Thread-safe operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetGain(bool clearFxHandle = false)
    {
        Volatile.Write(ref _normalizationGain, 1.0);
        if (clearFxHandle)
        {
            _gainFxHandle = 0;
        }
        else
        {
            ApplyGainToFx(normalize: true);
        }
    }

    public double SetNormalizationGainFromPeak(double peak, bool forceNotify = false)
    {
        var gain = CalculateGainFromPeak(peak);
        SetNormalizationGain(gain, forceNotify);
        Debug.WriteLine($"[AudioNormalizer] Peak analysis: peak={peak:F4}, target={NormalizationTarget}, gain={gain:F3}");
        return gain;
    }

    public void SetNormalizationGain(double gain, bool forceNotify = false)
    {
        gain = Math.Clamp(gain, 0.0, 8.0);
        var oldGain = Volatile.Read(ref _normalizationGain);
        if (!forceNotify && Math.Abs(oldGain - gain) < 0.0001)
        {
            return;
        }

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
        if (audioHandle == 0) return false;

        try
        {
            _gainFxHandle = MBass.ChannelSetFX(audioHandle, EffectType.Volume, 0);
            
            if (_gainFxHandle != 0)
            {
                Debug.WriteLine($"[AudioNormalizer] Gain FX created: handle={_gainFxHandle}");
                return true;
            }
            else
            {
                Debug.WriteLine($"[AudioNormalizer] Failed to create gain FX: {MBass.LastError}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioNormalizer] Exception creating gain FX: {ex.Message}");
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
        if (_gainFxHandle == 0) return;

        try
        {
            _gainParam.fTarget = (float)(normalize ? _normalizationGain : 1.0);
            _gainParam.fTime = 0;
            _gainParam.lCurve = 0;

            if (Bass.FXSetParameters(_gainFxHandle, _gainParam))
            {
                Debug.WriteLine($"Applied gain {_gainParam.fTarget:F3}");
            }
            else
            {
                Debug.WriteLine($"[AudioNormalizer] Failed to apply gain: {MBass.LastError}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AudioNormalizer] Exception applying gain: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets whether normalization should be applied to the audio output.
    /// The gain value is cached and ready - this just controls whether it's applied.
    /// </summary>
    /// <param name="normalize">True to apply cached gain, false to use 1.0 gain</param>
    public void SetNormalizeEnabled(bool normalize)
    {
        ApplyGainToFx(normalize);
    }

    /// <summary>
    /// Calculates the effective volume considering normalization.
    /// </summary>
    /// <param name="baseVolume">Base volume (0.0 to 1.0)</param>
    /// <param name="normalize">Whether to apply normalization</param>
    /// <returns>Effective volume with normalization applied</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double CalculateEffectiveVolume(double baseVolume, bool normalize)
    {
        baseVolume = Math.Clamp(baseVolume, 0.0, 1.0);
        return normalize ? baseVolume * Volatile.Read(ref _normalizationGain) : baseVolume;
    }

    #endregion

    #region Private Methods
    
    private double AnalyzePeakInternal(int handle, float blockSeconds, int maxSamples, CancellationToken cancellationToken, bool updateGain)
    {
        const LevelRetrievalFlags flags = LevelRetrievalFlags.Stereo;
        var levelBuffer = LevelBuffer.Value!;
        var peak = 0.0;
        
        for (var i = 0; i < maxSamples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MBass.ChannelGetLevel(handle, levelBuffer, blockSeconds, flags))
                break;
                
            var blockPeak = (double)Math.Max(levelBuffer[0], levelBuffer[1]);
            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);
            
            if (blockPeak > peak)
                peak = blockPeak;
        }
        
        if (updateGain)
        {
            SetNormalizationGainFromPeak(peak);
        }

        return peak;
    }

    private double AnalyzePeakFull(int handle, CancellationToken cancellationToken, bool updateGain)
    {
        const LevelRetrievalFlags flags = LevelRetrievalFlags.Stereo;
        const double blockSeconds = 0.5;
        const int maxWindows = 240;
        
        var levelBuffer = LevelBuffer.Value!;
        double peak;
        var lengthBytes = MBass.ChannelGetLength(handle);
        
        if (lengthBytes > 0)
            peak = AnalyzePeakBySeek(handle, lengthBytes, blockSeconds, maxWindows, levelBuffer, flags, cancellationToken);
        else
            peak = AnalyzePeakSequential(handle, blockSeconds, maxWindows, levelBuffer, flags, cancellationToken);
        
        if (updateGain)
        {
            SetNormalizationGainFromPeak(peak);
        }

        return peak;
    }

    private static double AnalyzePeakBySeek(int handle, long lengthBytes, double blockSeconds, int maxWindows, 
        float[] levelBuffer, LevelRetrievalFlags flags, CancellationToken cancellationToken)
    {
        var duration = MBass.ChannelBytes2Seconds(handle, lengthBytes);
        var stride = Math.Max(1, (int)Math.Ceiling(duration / (blockSeconds * maxWindows)));
        var peak = 0.0;
        
        for (var position = 0.0; position < duration; position += blockSeconds * stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var bytePosition = MBass.ChannelSeconds2Bytes(handle, position);
            if (!MBass.ChannelSetPosition(handle, bytePosition) ||
                !MBass.ChannelGetLevel(handle, levelBuffer, (float)blockSeconds, flags))
                break;
            
            var blockPeak = (double)Math.Max(levelBuffer[0], levelBuffer[1]);
            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);
            
            if (blockPeak > peak)
                peak = blockPeak;
        }
        
        return peak;
    }

    private static double AnalyzePeakSequential(int handle, double blockSeconds, int maxWindows, 
        float[] levelBuffer, LevelRetrievalFlags flags, CancellationToken cancellationToken)
    {
        var peak = 0.0;
        
        for (var i = 0; i < maxWindows; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (!MBass.ChannelGetLevel(handle, levelBuffer, (float)blockSeconds, flags))
                break;
            
            var blockPeak = (double)Math.Max(levelBuffer[0], levelBuffer[1]);
            blockPeak = Math.Clamp(blockPeak, 0.0, 1.0);
            
            if (blockPeak > peak)
                peak = blockPeak;
        }
        
        return peak;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double CalculateGainFromPeak(double peak)
    {
        return peak > 0.0 ? Math.Clamp(NormalizationTarget / peak, 0.0, 8.0) : 1.0;
    }
    
    #endregion
}
