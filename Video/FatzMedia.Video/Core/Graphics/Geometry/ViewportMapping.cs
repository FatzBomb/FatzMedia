namespace FatzMedia.Video.Core.Graphics.Geometry;

/// <summary>
/// Describes how a source texture maps into a viewport after clipping.
/// Destination is always clipped to the viewport, and Source is cropped to match.
/// This keeps D3D video-processor rectangles valid for modes such as Original and ZoomToFill.
/// </summary>
public readonly record struct ViewportMapping(RectD Source, RectD Destination);
