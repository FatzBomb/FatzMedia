namespace FatzMedia.Video.Core.Graphics.Composition;

/// <summary>
/// 8-bit colour helpers shared by the renderers so their output stays bit-identical.
/// </summary>
public static class ColorMath
{
    /// <summary>
    /// Premultiplies a straight-alpha 0xAARRGGBB colour (BGRA8 in memory) by its alpha, rounding
    /// each colour channel to nearest, and returns it in the same packing.
    /// </summary>
    public static uint PremultiplyBgra(uint argb)
    {
        var alpha = (byte)(argb >> 24);
        if (alpha == 255)
            return argb;

        return MultiplyByAlpha((byte)argb, alpha)
            | ((uint)MultiplyByAlpha((byte)(argb >> 8), alpha) << 8)
            | ((uint)MultiplyByAlpha((byte)(argb >> 16), alpha) << 16)
            | ((uint)alpha << 24);
    }

    private static byte MultiplyByAlpha(byte value, byte alpha) =>
        (byte)(((value * alpha) + 127) / 255);
}
