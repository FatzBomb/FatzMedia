#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Defines color adjustments applied only to the base visual composition layer.
/// </summary>
public readonly record struct BaseVisualTransform
{
    readonly float _redGainOffset;
    readonly float _greenGainOffset;
    readonly float _blueGainOffset;
    readonly float _contrastOffset;
    readonly float _saturationOffset;

    public static BaseVisualTransform Identity => default;

    public float RedGain
    {
        get => _redGainOffset + 1f;
        init => _redGainOffset = Normalize(value, 0f, 2f, 1f) - 1f;
    }

    public float GreenGain
    {
        get => _greenGainOffset + 1f;
        init => _greenGainOffset = Normalize(value, 0f, 2f, 1f) - 1f;
    }

    public float BlueGain
    {
        get => _blueGainOffset + 1f;
        init => _blueGainOffset = Normalize(value, 0f, 2f, 1f) - 1f;
    }

    public float HueDegrees
    {
        get;
        init => field = Normalize(value, -180f, 180f, 0f);
    }

    public float Brightness
    {
        get;
        init => field = Normalize(value, -1f, 1f, 0f);
    }

    public float Contrast
    {
        get => _contrastOffset + 1f;
        init => _contrastOffset = Normalize(value, 0f, 2f, 1f) - 1f;
    }

    public float Saturation
    {
        get => _saturationOffset + 1f;
        init => _saturationOffset = Normalize(value, 0f, 2f, 1f) - 1f;
    }

    public bool IncludeOverlays { get; init; }

    public bool IsIdentity => HueDegrees == 0f
        && Brightness == 0f
        && _contrastOffset == 0f
        && _saturationOffset == 0f
        && _redGainOffset == 0f
        && _greenGainOffset == 0f
        && _blueGainOffset == 0f;

    static float Normalize(float value, float minimum, float maximum, float fallback)
        => float.IsNaN(value) ? fallback : Math.Clamp(value, minimum, maximum);
}
