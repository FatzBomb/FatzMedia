using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Rendering.Software;

internal static class SoftwareBaseVisualTransform
{
    /// <summary>Transforms straight BGRA8 input and returns premultiplied BGRA8 output.</summary>
    internal static (byte Blue, byte Green, byte Red, byte Alpha) ApplyStraightToPremultiplied(
        byte blue,
        byte green,
        byte red,
        byte alpha,
        BaseVisualTransform transform)
    {
        (var transformedRed, var transformedGreen, var transformedBlue) = BaseVisualTransformMath.ApplyStraight(
            red / 255f,
            green / 255f,
            blue / 255f,
            transform);

        var normalizedAlpha = alpha / 255f;
        return (
            BaseVisualTransformMath.Quantize(transformedBlue * normalizedAlpha),
            BaseVisualTransformMath.Quantize(transformedGreen * normalizedAlpha),
            BaseVisualTransformMath.Quantize(transformedRed * normalizedAlpha),
            alpha);
    }

    internal static (byte Blue, byte Green, byte Red, byte Alpha) ApplyPremultipliedToPremultiplied(
        byte blue,
        byte green,
        byte red,
        byte alpha,
        BaseVisualTransform transform)
    {
        if (transform.IsIdentity || alpha == 0)
        {
            return alpha == 0 ? ((byte)0, (byte)0, (byte)0, (byte)0) : (blue, green, red, alpha);
        }

        var normalizedAlpha = alpha / 255f;
        var transformedRed = (red / 255f) / normalizedAlpha;
        var transformedGreen = (green / 255f) / normalizedAlpha;
        var transformedBlue = (blue / 255f) / normalizedAlpha;
        (transformedRed, transformedGreen, transformedBlue) = BaseVisualTransformMath.ApplyStraight(
            transformedRed,
            transformedGreen,
            transformedBlue,
            transform);

        return (
            BaseVisualTransformMath.Quantize(transformedBlue * normalizedAlpha),
            BaseVisualTransformMath.Quantize(transformedGreen * normalizedAlpha),
            BaseVisualTransformMath.Quantize(transformedRed * normalizedAlpha),
            alpha);
    }
}
