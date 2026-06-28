namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

/// <summary>
/// Describes how a composition background/base layer maps into the final viewport.
/// This keeps all media modes on the same compositor path while preserving each mode's
/// intended display behavior.
/// </summary>
public enum MediaCompositionStretchMode
{
    /// <summary>
    /// Compatibility/default value. The compositor resolves this to <see cref="UniformFit" /> unless
    /// the older BackgroundUniformToFill flag is still set by a transitional call site.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Draw the layer at its native pixel size, centered in the viewport.
    /// </summary>
    Original,

    /// <summary>
    /// Scale uniformly so the whole source is visible inside the viewport.
    /// </summary>
    UniformFit,

    /// <summary>
    /// Scale non-uniformly to exactly fill the viewport.
    /// </summary>
    Fill,

    /// <summary>
    /// Scale uniformly until the viewport is filled, cropping excess edges.
    /// </summary>
    UniformToFill
}
