#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>Which part of the 300x216 CD+G screen a scaled picture shows.</summary>
public enum CdgSafetyZone
{
    /// <summary>All of it, border included (as the classic picture).</summary>
    Show = 0,

    /// <summary>The 288x192 picture area, without the border.</summary>
    Remove,

    /// <summary><see cref="Remove"/>, unless the song draws in the border area; then <see cref="Show"/>.</summary>
    Auto,

    /// <summary>The box around everything the song ever draws, fixed for the whole song.</summary>
    Super,
}

/// <summary>
/// How big the scaled CD+G picture is drawn, as a whole multiple of the 216-pixel-high CD+G screen, before the GPU fits
/// it to the screen. Med and High follow the height of the largest screen or window showing it, so the look is the same
/// at any resolution; a higher scale costs more CPU (most of it for the outline, shadow and bevel), like a game's
/// graphics setting.
/// </summary>
public enum CdgScale
{
    /// <summary>No scaling: the classic 300x216 picture, scaled up on the GPU.</summary>
    None = 0,

    /// <summary>2x (432 high) at any screen size.</summary>
    Low,

    /// <summary>The largest multiple at or below half the screen height, at least 2x (1080p: 2x, 1440p: 3x, 4K: 5x).</summary>
    Medium,

    /// <summary>The largest multiple at or below the screen height (720p: 3x, 1080p: 5x, 1440p: 6x, 4K: 10x).</summary>
    High,
}

/// <summary>How the scaled CD+G picture turns each CD+G pixel into many.</summary>
public enum CdgScaleStyle
{
    /// <summary>Sharp square pixels.</summary>
    Pixel = 0,

    /// <summary>Sharp pixels with the stair steps along colour edges anti-aliased. Superseded by <see cref="Clean"/>; not offered in the player.</summary>
    AntiAliased,

    /// <summary>A pixel-art scaler: diagonal steps become clean slopes, square corners and single pixels stay.</summary>
    PixelArt,

    /// <summary>Traced outlines with straight diagonals and slopes and sharp corners. Superseded by <see cref="Clean"/>; not offered in the player.</summary>
    Traced,

    /// <summary>Traced outlines fitted with curves. <see cref="Clean"/> draws the same; not offered in the player.</summary>
    Curved,

    /// <summary>
    /// The clean look, the player's default: crisp even strokes with square ends and smooth curves, edges about one output
    /// pixel soft at any scale (outlines fitted with curves). One setting that follows the scale; nothing to tune.
    /// </summary>
    Clean,
}

/// <summary>
/// The scaled CD+G picture of the FatzAudio engine (037A): full colour at a whole-number multiple of the CD+G screen
/// (<see cref="CdgScale"/>), in a <see cref="CdgScaleStyle"/>, optionally outlined, shadowed and bevelled, instead of the
/// classic 300x216 picture scaled up on the GPU. Off by default (<c>default(CdgScalingOptions)</c>): the classic picture.
/// The effect sizes count half CD+G pixels, 0 (off) to 10 (5 CD+G pixels), whatever the scale. A player changes it while
/// a song plays with <see cref="PlayerCore.CdgScaling"/>.
/// </summary>
/// <example>
/// <code>
/// player.CdgScaling = new CdgScalingOptions { Scale = CdgScale.High, Style = CdgScaleStyle.Traced }
///     .WithDefaultEffects() with { BevelStrength = 5, BevelSize = 2 };
/// </code>
/// </example>
public readonly record struct CdgScalingOptions
{
    /// <summary>The largest effect size or strength: 10 half CD+G pixels (5 CD+G pixels), or full strength.</summary>
    public const int MaximumEffect = 10;

    /// <summary>The outline width of <see cref="WithDefaultEffects"/>: 2, one CD+G pixel.</summary>
    public const int DefaultOutlineSize = 2;

    /// <summary>The shadow spread of <see cref="WithDefaultEffects"/>: 1, half a CD+G pixel.</summary>
    public const int DefaultShadowSize = 1;

    /// <summary>The shadow offset of <see cref="WithDefaultEffects"/>: 2, one CD+G pixel.</summary>
    public const int DefaultShadowDistance = 2;

    /// <summary>Gets how big the picture is drawn; <see cref="CdgScale.None"/> (the default) is the classic picture.</summary>
    public CdgScale Scale
    {
        get;
        init => field = Enum.IsDefined(value) ? value : CdgScale.None;
    }

    /// <summary>Gets whether CD+G is drawn scaled (<see cref="Scale"/> is not <see cref="CdgScale.None"/>).</summary>
    public bool Enabled => Scale != CdgScale.None;

    /// <summary>Gets how the pixels are scaled; <see cref="CdgScaleStyle.Pixel"/> by default.</summary>
    public CdgScaleStyle Style
    {
        get;
        init => field = Enum.IsDefined(value) ? value : CdgScaleStyle.Pixel;
    }

    /// <summary>Gets which part of the screen is shown.</summary>
    public CdgSafetyZone SafetyZone { get; init; }

    /// <summary>Gets the outline width around what the song draws, in half CD+G pixels, 0 (none) to 10.</summary>
    public int OutlineSize
    {
        get;
        init => field = ClampEffect(value);
    }

    /// <summary>
    /// Gets how far the shadow spreads beyond the shape (half of it soft), in half CD+G pixels, 0 to 10; a shadow is
    /// drawn when this or <see cref="ShadowDistance"/> is above 0.
    /// </summary>
    public int ShadowSize
    {
        get;
        init => field = ClampEffect(value);
    }

    /// <summary>Gets how far down and right the shadow falls, in half CD+G pixels, 0 to 10.</summary>
    public int ShadowDistance
    {
        get;
        init => field = ClampEffect(value);
    }

    /// <summary>Gets the bevel's strength, 0 (none) to 10 (full): a highlight on the top-left edges, a shade on the bottom-right ones.</summary>
    public int BevelStrength
    {
        get;
        init => field = ClampEffect(value);
    }

    /// <summary>Gets how far the bevel reaches in from the edge, in half CD+G pixels, 0 to 10.</summary>
    public int BevelSize
    {
        get;
        init => field = ClampEffect(value);
    }

    /// <summary>Gets whether the picture has an outline, a shadow or a bevel.</summary>
    public bool HasEffects => OutlineSize > 0 || ShadowSize > 0 || ShadowDistance > 0 || (BevelStrength > 0 && BevelSize > 0);

    /// <summary>These options with the default outline and soft shadow (<c>--cdg-effects</c> in the test players) and no bevel.</summary>
    public CdgScalingOptions WithDefaultEffects() => this with
    {
        OutlineSize = DefaultOutlineSize,
        ShadowSize = DefaultShadowSize,
        ShadowDistance = DefaultShadowDistance,
        BevelStrength = 0,
    };

    static int ClampEffect(int value) => Math.Clamp(value, 0, MaximumEffect);
}
