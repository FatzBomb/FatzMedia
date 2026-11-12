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
/// The scaled CD+G picture of the FatzAudio engine (037A): full colour at a whole-number multiple of the CD+G screen,
/// smoothed and optionally outlined, shadowed and bevelled, instead of the classic 300x216 picture scaled up on the GPU.
/// Off by default (<c>default(CdgScalingOptions)</c>): the classic picture. A player changes it while a song plays with
/// <see cref="PlayerCore.CdgScaling"/>.
/// </summary>
/// <example>
/// <code>
/// player.CdgScaling = new CdgScalingOptions { Enabled = true, Smoothness = 6, EdgeBlend = true }
///     .WithDefaultEffects() with { BevelStrength = 0.5f, BevelSize = 0.5f };
/// </code>
/// </example>
public readonly record struct CdgScalingOptions
{
    /// <summary>The outline width of <see cref="WithDefaultEffects"/>.</summary>
    public const float DefaultOutlineSize = 0.5f;

    /// <summary>The shadow spread of <see cref="WithDefaultEffects"/>.</summary>
    public const float DefaultShadowSize = 0.3f;

    /// <summary>The shadow offset of <see cref="WithDefaultEffects"/>.</summary>
    public const float DefaultShadowDistance = 0.5f;

    /// <summary>The smallest <see cref="RenderHeight"/> other than 0: the CD+G screen at 1x.</summary>
    public const int MinimumRenderHeight = 216;

    /// <summary>The largest <see cref="RenderHeight"/> (2160, 4K).</summary>
    public const int MaximumRenderHeight = 2160;

    /// <summary>Gets whether CD+G is drawn scaled (otherwise the classic picture).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the smoothing, 0 to 10: 0 sharp pixels, 1 to 4 softer, 5 to 10 traced outlines with diagonals
    /// straightened, corners rounded and curves at 9 and 10.
    /// </summary>
    public int Smoothness
    {
        get;
        init => field = Math.Clamp(value, 0, 10);
    }

    /// <summary>Gets whether stair steps along colour edges are anti-aliased (no effect at smoothness 5 and up).</summary>
    public bool EdgeBlend { get; init; }

    /// <summary>Gets which part of the screen is shown.</summary>
    public CdgSafetyZone SafetyZone { get; init; }

    /// <summary>Gets the outline width around what the song draws, 0 (none) to 1 (1.5 CD+G pixels).</summary>
    public float OutlineSize
    {
        get;
        init => field = Clamp01(value);
    }

    /// <summary>Gets how far the shadow spreads beyond the shape, 0 to 1 (2 CD+G pixels), half of it soft; a shadow is drawn when this or <see cref="ShadowDistance"/> is above 0.</summary>
    public float ShadowSize
    {
        get;
        init => field = Clamp01(value);
    }

    /// <summary>Gets how far down and right the shadow falls, 0 to 1 (2 CD+G pixels).</summary>
    public float ShadowDistance
    {
        get;
        init => field = Clamp01(value);
    }

    /// <summary>Gets the bevel's strength, 0 (none) to 1: a highlight on the top-left edges and a shade on the bottom-right ones.</summary>
    public float BevelStrength
    {
        get;
        init => field = Clamp01(value);
    }

    /// <summary>Gets how far the bevel reaches in from the edge, 0 to 1 (1 CD+G pixel).</summary>
    public float BevelSize
    {
        get;
        init => field = Clamp01(value);
    }

    /// <summary>
    /// Gets the height in pixels the picture is drawn at before the GPU scales it to the screen, or 0 (the default) for
    /// the presentation's own 1920x1080 canvas; otherwise <see cref="MinimumRenderHeight"/> to
    /// <see cref="MaximumRenderHeight"/>, at 16:9. The picture is drawn at the largest whole multiple of the CD+G screen
    /// that fits, so 1080 draws 5x and 2160 10x.
    /// </summary>
    public int RenderHeight
    {
        get;
        init => field = value <= 0 ? 0 : Math.Clamp(value, MinimumRenderHeight, MaximumRenderHeight);
    }

    /// <summary>Gets whether the picture has an outline, a shadow or a bevel.</summary>
    public bool HasEffects => OutlineSize > 0 || ShadowSize > 0 || ShadowDistance > 0 || BevelStrength > 0;

    /// <summary>These options with the default outline and soft shadow (<c>--cdg-effects</c> in the test players) and no bevel.</summary>
    public CdgScalingOptions WithDefaultEffects() => this with
    {
        OutlineSize = DefaultOutlineSize,
        ShadowSize = DefaultShadowSize,
        ShadowDistance = DefaultShadowDistance,
        BevelStrength = 0,
    };

    // NaN counts as 0, so a bad value turns the effect off instead of reaching FatzAudio.
    static float Clamp01(float value) => value > 0 ? Math.Min(value, 1) : 0;
}
