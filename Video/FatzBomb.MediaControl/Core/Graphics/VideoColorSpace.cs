namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>The YCbCr matrix a decoded video frame was encoded with.</summary>
public enum VideoColorMatrix
{
    /// <summary>The stream does not say; <see cref="VideoColorSpace.Resolve"/> picks by frame size.</summary>
    Unspecified = 0,
    /// <summary>ITU-R BT.601 (standard-definition video).</summary>
    Bt601,
    /// <summary>ITU-R BT.709 (high-definition video).</summary>
    Bt709,
    /// <summary>ITU-R BT.2020 non-constant luminance (ultra-high-definition video).</summary>
    Bt2020
}

/// <summary>The range of a decoded video frame's YCbCr values.</summary>
public enum VideoColorRange
{
    /// <summary>The stream does not say; treated as <see cref="Limited"/>, as video almost always is.</summary>
    Unspecified = 0,
    /// <summary>Studio range: luma 16 is black and 235 is white (8-bit).</summary>
    Limited,
    /// <summary>Full range: luma 0 is black and 255 is white (8-bit).</summary>
    Full
}

/// <summary>
/// How a decoded YCbCr frame's values map to RGB. Renderers converting native frames use it
/// instead of leaving the choice to a driver, which may assume another range and show video
/// washed out or crushed.
/// </summary>
public readonly record struct VideoColorSpace(VideoColorMatrix Matrix, VideoColorRange Range)
{
    /// <summary>
    /// This color space with unspecified parts filled in the way players commonly do: BT.709
    /// for HD-sized frames (at least 1280 wide or more than 576 tall), BT.601 otherwise, and
    /// limited range.
    /// </summary>
    public VideoColorSpace Resolve(int width, int height) => new(
        Matrix != VideoColorMatrix.Unspecified
            ? Matrix
            : width >= 1280 || height > 576 ? VideoColorMatrix.Bt709 : VideoColorMatrix.Bt601,
        Range != VideoColorRange.Unspecified ? Range : VideoColorRange.Limited);
}
