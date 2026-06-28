using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Controllers;

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

    public uint? BackgroundOverrideArgb
    {
        get;
        set
        {
            field = value;
            Apply();
        }
    }

    /// <summary>
    /// Gets the current cached CDG wash color in ARGB format. This is the color Layer 2
    /// draws across the full viewport behind transparent CDG lyrics.
    /// </summary>
    public uint BackgroundWashArgb => _mediaEngine.Cdg?.BackgroundWashArgb ?? 0;

    internal void Apply()
    {
        ICdgEngine? cdg = _mediaEngine.Cdg;
        if (cdg == null) return;

        cdg.BackgroundOpacity = BackgroundOpacity;
        cdg.BackgroundOverrideArgb = BackgroundOverrideArgb;
    }
}
