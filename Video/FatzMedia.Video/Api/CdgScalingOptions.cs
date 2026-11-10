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
/// smoothed and optionally outlined and shadowed, instead of the classic 300x216 picture scaled up on the GPU. Off by
/// default (<c>default(CdgScalingOptions)</c>): the classic picture.
/// </summary>
/// <remarks>The BASS engine ignores it: its CD+G is always the classic picture.</remarks>
public readonly record struct CdgScalingOptions
{
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

    /// <summary>Gets whether the picture gets an outline and a soft shadow around what it draws.</summary>
    public bool Effects { get; init; }
}
