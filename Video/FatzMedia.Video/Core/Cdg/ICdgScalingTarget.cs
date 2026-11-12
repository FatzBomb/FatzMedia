namespace FatzMedia.Video.Core.Cdg;

/// <summary>
/// A CD+G engine that draws the scaled picture (<see cref="CdgScalingOptions"/>) and takes new options while a song
/// plays: <see cref="PlayerCore.CdgScaling"/> passes them on. The next presented frame shows them.
/// </summary>
interface ICdgScalingTarget
{
    /// <summary>Gets or sets the scaled picture's options; turning it on or off switches between it and the classic picture.</summary>
    CdgScalingOptions Scaling { get; set; }

    /// <summary>
    /// Gets or sets the height in pixels of the largest screen or window showing the picture, which
    /// <see cref="CdgScale.Medium"/> and <see cref="CdgScale.High"/> follow; 0 when unknown (drawn for 1080). The
    /// presentation sets it before each overlay draw.
    /// </summary>
    int DisplayHeight { get; set; }
}
