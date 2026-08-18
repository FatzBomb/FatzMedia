namespace FatzBomb.MediaControl.Rendering.Software;

internal static class SoftwareBaseVisualTransform
{
    private const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>Transforms straight BGRA8 input and returns premultiplied BGRA8 output.</summary>
    internal static (byte Blue, byte Green, byte Red, byte Alpha) ApplyStraightToPremultiplied(
        byte blue,
        byte green,
        byte red,
        byte alpha,
        BaseVisualTransform transform)
    {
        var transformedRed = red / 255f;
        var transformedGreen = green / 255f;
        var transformedBlue = blue / 255f;

        ApplyHue(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.HueDegrees);
        ApplySaturation(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Saturation);
        ApplyContrast(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Contrast);
        ApplyBrightness(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Brightness);
        ApplyRgbGains(
            ref transformedRed,
            ref transformedGreen,
            ref transformedBlue,
            transform.RedGain,
            transform.GreenGain,
            transform.BlueGain);

        var normalizedAlpha = alpha / 255f;
        return (
            Quantize(transformedBlue * normalizedAlpha),
            Quantize(transformedGreen * normalizedAlpha),
            Quantize(transformedRed * normalizedAlpha),
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
        ApplyHue(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.HueDegrees);
        ApplySaturation(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Saturation);
        ApplyContrast(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Contrast);
        ApplyBrightness(ref transformedRed, ref transformedGreen, ref transformedBlue, transform.Brightness);
        ApplyRgbGains(
            ref transformedRed,
            ref transformedGreen,
            ref transformedBlue,
            transform.RedGain,
            transform.GreenGain,
            transform.BlueGain);

        return (
            Quantize(transformedBlue * normalizedAlpha),
            Quantize(transformedGreen * normalizedAlpha),
            Quantize(transformedRed * normalizedAlpha),
            alpha);
    }

    private static void ApplyHue(ref float red, ref float green, ref float blue, float degrees)
    {
        if (degrees == 0f)
        {
            return;
        }

        var y = (0.299f * red) + (0.587f * green) + (0.114f * blue);
        var i = (0.596f * red) - (0.274f * green) - (0.322f * blue);
        var q = (0.211f * red) - (0.523f * green) + (0.312f * blue);
        var angle = degrees * DegreesToRadians;
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);
        var rotatedI = (i * cosine) - (q * sine);
        var rotatedQ = (i * sine) + (q * cosine);
        red = Clamp(y + (0.956f * rotatedI) + (0.621f * rotatedQ));
        green = Clamp(y - (0.272f * rotatedI) - (0.647f * rotatedQ));
        blue = Clamp(y - (1.106f * rotatedI) + (1.703f * rotatedQ));
    }

    private static void ApplySaturation(ref float red, ref float green, ref float blue, float saturation)
    {
        if (saturation == 1f)
        {
            return;
        }

        var luma = (0.2126f * red) + (0.7152f * green) + (0.0722f * blue);
        red = Clamp(luma + ((red - luma) * saturation));
        green = Clamp(luma + ((green - luma) * saturation));
        blue = Clamp(luma + ((blue - luma) * saturation));
    }

    private static void ApplyBrightness(ref float red, ref float green, ref float blue, float brightness)
    {
        if (brightness == 0f)
        {
            return;
        }

        red = Clamp(red + brightness);
        green = Clamp(green + brightness);
        blue = Clamp(blue + brightness);
    }

    private static void ApplyContrast(ref float red, ref float green, ref float blue, float contrast)
    {
        if (contrast == 1f)
        {
            return;
        }

        red = Clamp(red * contrast);
        green = Clamp(green * contrast);
        blue = Clamp(blue * contrast);
    }

    private static void ApplyRgbGains(
        ref float red,
        ref float green,
        ref float blue,
        float redGain,
        float greenGain,
        float blueGain)
    {
        if (redGain == 1f && greenGain == 1f && blueGain == 1f)
        {
            return;
        }

        red = Clamp(red * redGain);
        green = Clamp(green * greenGain);
        blue = Clamp(blue * blueGain);
    }

    private static byte Quantize(float channel)
        => (byte)Math.Round(Clamp(channel) * 255d, MidpointRounding.AwayFromZero);

    private static float Clamp(float value) => Math.Clamp(value, 0f, 1f);
}
