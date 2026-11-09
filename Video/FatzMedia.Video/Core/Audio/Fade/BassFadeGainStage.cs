using ManagedBass;
using ManagedBass.Fx;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio.Fade;

/// <summary>
/// The fade envelope stage on one BASS play handle: a dedicated <c>BASS_FX_VOLUME</c> after
/// the normalization gain. It multiplies with the channel volume attribute (user volume) and
/// the normalization stage, so fades never change either of them. Shared by the plain-audio
/// and video-audio engines.
/// </summary>
sealed class BassFadeGainStage(string diagnosticSource) : IAudioFadeGainTarget
{
    readonly Lock _gate = new();
    readonly VolumeParameters _parameters = new()
    {
        fTarget = 1f,
        fCurrent = 1f,
        fTime = 0,
        lCurve = 0,
        lChannel = FXChannelFlags.All,
    };
    int _fxHandle;
    int _session;

    /// <summary>Creates the stage on a new play handle at unity gain, ending any previous session.</summary>
    public void Attach(int playHandle)
    {
        lock (_gate)
        {
            _session = NextSession(_session);
            _fxHandle = MBass.ChannelSetFX(playHandle, EffectType.Volume, 0);
            if (_fxHandle != 0)
            {
                _ = ApplyLocked(1, TimeSpan.Zero);
                return;
            }
        }

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, diagnosticSource, $"Fade FX unavailable on handle {playHandle}: {MBass.LastError}; fades are disabled for this media.");
    }

    /// <summary>
    /// Ends the current fade session so pending ramps are rejected, and restores unity gain on a
    /// still-open stream. Open and Close release the handle because the stream that owns it is
    /// freed.
    /// </summary>
    public void EndSession(bool releaseHandle)
    {
        lock (_gate)
        {
            _session = NextSession(_session);
            if (releaseHandle)
            {
                _fxHandle = 0;
                return;
            }

            if (_fxHandle != 0)
                _ = ApplyLocked(1, TimeSpan.Zero);
        }
    }

    public int BeginFadeSession()
    {
        lock (_gate)
        {
            if (_fxHandle == 0)
                return 0;

            _session = NextSession(_session);
            return _session;
        }
    }

    public bool SetFadeGain(int session, double gain, TimeSpan ramp)
    {
        lock (_gate)
        {
            return session != 0
                && session == _session
                && _fxHandle != 0
                && ApplyLocked(gain, ramp);
        }
    }

    static int NextSession(int session) => session == int.MaxValue ? 1 : session + 1;

    bool ApplyLocked(double gain, TimeSpan ramp)
    {
        var target = (float)(double.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 0);
        var seconds = ramp.TotalSeconds;
        _parameters.fTarget = target;
        _parameters.lCurve = 0;
        if (seconds > 0)
        {
            // fCurrent = -1 continues from the stage's present gain; fTime is the linear ramp length.
            _parameters.fCurrent = -1f;
            _parameters.fTime = (float)seconds;
        }
        else
        {
            _parameters.fCurrent = target;
            _parameters.fTime = 0;
        }

        return MBass.FXSetParameters(_fxHandle, _parameters);
    }
}
