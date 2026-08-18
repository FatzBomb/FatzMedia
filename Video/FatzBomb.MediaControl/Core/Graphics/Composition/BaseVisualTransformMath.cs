namespace FatzBomb.MediaControl.Core.Graphics.Composition;

internal static class BaseVisualTransformMath
{
    private const float DegreesToRadians = MathF.PI / 180f;

    internal static (float Red, float Green, float Blue) ApplyStraight(
        float red,
        float green,
        float blue,
        BaseVisualTransform transform)
    {
        red = Clamp(red);
        green = Clamp(green);
        blue = Clamp(blue);

        if (transform.IsIdentity)
        {
            return (red, green, blue);
        }

        ApplyHue(ref red, ref green, ref blue, transform.HueDegrees);
        ApplySaturation(ref red, ref green, ref blue, transform.Saturation);
        ApplyContrast(ref red, ref green, ref blue, transform.Contrast);
        ApplyBrightness(ref red, ref green, ref blue, transform.Brightness);
        ApplyRgbGains(ref red, ref green, ref blue, transform.RedGain, transform.GreenGain, transform.BlueGain);
        return (red, green, blue);
    }

    internal static uint ApplyPremultipliedBgra(uint bgra, BaseVisualTransform transform)
    {
        if (transform.IsIdentity)
        {
            return bgra;
        }

        var alphaByte = (byte)(bgra >> 24);
        if (alphaByte == 0)
        {
            return 0;
        }

        var alpha = alphaByte / 255f;
        var red = ((byte)(bgra >> 16) / 255f) / alpha;
        var green = ((byte)(bgra >> 8) / 255f) / alpha;
        var blue = ((byte)bgra / 255f) / alpha;
        (red, green, blue) = ApplyStraight(red, green, blue, transform);

        return ((uint)alphaByte << 24)
            | ((uint)Quantize(red * alpha) << 16)
            | ((uint)Quantize(green * alpha) << 8)
            | Quantize(blue * alpha);
    }

    internal static byte Quantize(float channel)
        => (byte)Math.Round(Clamp(channel) * 255d, MidpointRounding.AwayFromZero);

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

    private static float Clamp(float value) => Math.Clamp(value, 0f, 1f);
}
