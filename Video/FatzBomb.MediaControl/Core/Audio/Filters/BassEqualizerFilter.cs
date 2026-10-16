using ManagedBass;
using ManagedBass.Fx;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio.Filters;

/// <summary>
/// Parametric equalizer on BASS_FX <c>BASS_FX_BFX_PEAKEQ</c>: one native FX with one band per
/// configured band. Native IIR processing (no managed per-sample callback, no added latency).
/// </summary>
internal sealed class BassEqualizerFilter : IAudioFilter
{
    private readonly Lock _gate = new();
    private readonly PeakEQParameters _parameters = new() { lChannel = FXChannelFlags.All };
    private AudioEqualizerSettings _settings = AudioEqualizerSettings.Disabled;
    private int _playHandle;
    private int _fxHandle;
    private int _attachedBands;

    public string Id => "equalizer";

    public int Priority => AudioFilterStage.EqualizerPriority;

    public double LatencySeconds => 0;

    public bool IsActive
    {
        get
        {
            lock (_gate)
                return _settings.IsEffective;
        }
    }

    public AudioEqualizerSettings Settings
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    /// <summary>Stores new settings and applies them to the current stream, if any.</summary>
    public bool Configure(AudioEqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _settings = settings;
            return _playHandle == 0 || ApplyLocked(_playHandle);
        }
    }

    public bool Apply(int playHandle)
    {
        lock (_gate)
            return ApplyLocked(playHandle);
    }

    /// <summary>
    /// Forgets the native stage when its stream is being freed (the FX is released with the
    /// stream, so no BASS call is made).
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            _fxHandle = 0;
            _attachedBands = 0;
            _playHandle = 0;
        }
    }

    public void Remove()
    {
        lock (_gate)
        {
            RemoveLocked();
            _playHandle = 0;
        }
    }

    private bool ApplyLocked(int playHandle)
    {
        if (playHandle != _playHandle)
        {
            // A new stream: FX on the old handle were freed with it.
            _fxHandle = 0;
            _attachedBands = 0;
            _playHandle = playHandle;
        }

        if (playHandle == 0)
            return false;

        EqualizerUpdateAction action = EqualizerUpdatePlanner.Plan(_attachedBands, _fxHandle != 0, _settings);
        switch (action)
        {
            case EqualizerUpdateAction.None:
                return true;
            case EqualizerUpdateAction.Remove:
                RemoveLocked();
                return true;
            case EqualizerUpdateAction.Rebuild:
                RemoveLocked();
                goto case EqualizerUpdateAction.Create;
            case EqualizerUpdateAction.Create:
                _fxHandle = MBass.ChannelSetFX(playHandle, EffectType.PeakEQ, Priority);
                if (_fxHandle == 0)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioFilters", $"Equalizer FX unavailable: {MBass.LastError}; audio continues unequalized.");
                    return false;
                }

                break;
        }

        var ok = true;
        for (var band = 0; band < _settings.Bands.Count; band++)
        {
            AudioEqualizerBand settings = _settings.Bands[band];
            _parameters.lBand = band;
            _parameters.fCenter = (float)settings.CenterHz;
            _parameters.fGain = (float)settings.GainDb;
            _parameters.fBandwidth = (float)settings.BandwidthOctaves;
            _parameters.fQ = 0;
            ok &= MBass.FXSetParameters(_fxHandle, _parameters);
        }

        _attachedBands = _settings.Bands.Count;
        if (!ok)
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioFilters", $"Equalizer band update failed: {MBass.LastError}");
        return ok;
    }

    private void RemoveLocked()
    {
        if (_fxHandle != 0 && _playHandle != 0)
            _ = MBass.ChannelRemoveFX(_playHandle, _fxHandle);

        _fxHandle = 0;
        _attachedBands = 0;
    }
}
