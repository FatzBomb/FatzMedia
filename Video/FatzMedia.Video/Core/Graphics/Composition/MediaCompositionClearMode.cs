namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// Clear behavior for a composition frame when no layer supplies pixels for part or all of the target.
/// </summary>
public enum MediaCompositionClearMode
{
    Black,
    Transparent,
    ConfiguredColor,
    PreservePrevious
}
