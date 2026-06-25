using FatzBombMedia.MediaControl.Core.Abstractions;

namespace FatzBombMedia.MediaControl.Controllers;

/// <summary>
/// Controls CDG visual presentation settings.
/// </summary>
public sealed class CdgVisuals
{
    private readonly IMediaEngine _mediaEngine;
    private byte _backgroundOpacity = 100;
    private uint? _backgroundOverrideArgb;
    private bool _useWidescreenPresentation = true;

    internal CdgVisuals(IMediaEngine mediaEngine)
    {
        ArgumentNullException.ThrowIfNull(mediaEngine);
        _mediaEngine = mediaEngine;
    }

    public byte BackgroundOpacity
    {
        get => _backgroundOpacity;
        set
        {
            _backgroundOpacity = Math.Clamp(value, (byte)0, (byte)100);
            Apply();
        }
    }

    public uint? BackgroundOverrideArgb
    {
        get => _backgroundOverrideArgb;
        set
        {
            _backgroundOverrideArgb = value;
            Apply();
        }
    }

    public bool UseWidescreenPresentation
    {
        get => _useWidescreenPresentation;
        set
        {
            _useWidescreenPresentation = value;
            Apply();
        }
    }

    internal void Apply()
    {
        var cdg = _mediaEngine.Cdg;
        if (cdg == null) return;

        cdg.BackgroundOpacity = _backgroundOpacity;
        cdg.BackgroundOverrideArgb = _backgroundOverrideArgb;
        cdg.UseWidescreenPresentation = _useWidescreenPresentation;
    }
}
