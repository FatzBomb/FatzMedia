using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Controls CDG visual presentation settings.
/// </summary>
public sealed class CdgVisuals
{
    private readonly IMediaEngine _mediaEngine;

    internal CdgVisuals(IMediaEngine mediaEngine)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
    }

    public byte BackgroundOpacity
    {
        get;
        set
        {
            field = Math.Clamp(value, (byte)0, (byte)100);
            Apply();
        }
    } = 100;

    /// <summary>
    /// Stage 043A <c>TransparentBackground</c>: the CD+G background colour is drawn fully
    /// transparent, whatever <see cref="BackgroundOpacity"/> is set to.
    /// </summary>
    internal bool ForceTransparentBackground
    {
        get;
        set
        {
            field = value;
            Apply();
        }
    }

    /// <summary>The opacity applied to the CD+G engine.</summary>
    internal byte EffectiveBackgroundOpacity => ForceTransparentBackground ? (byte)0 : BackgroundOpacity;

    internal void Apply()
    {
        ICdgEngine? cdg = _mediaEngine.Cdg;
        if (cdg == null) return;

        cdg.BackgroundOpacity = EffectiveBackgroundOpacity;
    }
}
