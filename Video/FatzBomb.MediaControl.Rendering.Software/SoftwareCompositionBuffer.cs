namespace FatzBomb.MediaControl.Rendering.Software;

internal sealed class SoftwareCompositionBuffer(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = checked(width * 4);
    public byte[] Pixels { get; } = new byte[checked(width * height * 4)];

    public void Clear(uint argb)
    {
        var alpha = (byte)(argb >> 24);
        var red = (byte)(argb >> 16);
        var green = (byte)(argb >> 8);
        var blue = (byte)argb;
        var premultipliedRed = MultiplyByAlpha(red, alpha);
        var premultipliedGreen = MultiplyByAlpha(green, alpha);
        var premultipliedBlue = MultiplyByAlpha(blue, alpha);

        for (var offset = 0; offset < Pixels.Length; offset += 4)
        {
            Pixels[offset] = premultipliedBlue;
            Pixels[offset + 1] = premultipliedGreen;
            Pixels[offset + 2] = premultipliedRed;
            Pixels[offset + 3] = alpha;
        }
    }

    public void BlendStraight(int x, int y, byte blue, byte green, byte red, byte alpha)
    {
        var offset = GetOffset(x, y);
        var inverseAlpha = 255 - alpha;
        Pixels[offset] = BlendStraightChannel(blue, alpha, Pixels[offset], inverseAlpha);
        Pixels[offset + 1] = BlendStraightChannel(green, alpha, Pixels[offset + 1], inverseAlpha);
        Pixels[offset + 2] = BlendStraightChannel(red, alpha, Pixels[offset + 2], inverseAlpha);
        Pixels[offset + 3] = AddAlpha(alpha, Pixels[offset + 3], inverseAlpha);
    }

    public void BlendPremultiplied(int x, int y, byte blue, byte green, byte red, byte alpha)
    {
        var offset = GetOffset(x, y);
        var inverseAlpha = 255 - alpha;
        Pixels[offset] = AddPremultiplied(blue, Pixels[offset], inverseAlpha);
        Pixels[offset + 1] = AddPremultiplied(green, Pixels[offset + 1], inverseAlpha);
        Pixels[offset + 2] = AddPremultiplied(red, Pixels[offset + 2], inverseAlpha);
        Pixels[offset + 3] = AddAlpha(alpha, Pixels[offset + 3], inverseAlpha);
    }

    private int GetOffset(int x, int y) => checked((y * Stride) + (x * 4));

    private static byte BlendStraightChannel(byte source, byte alpha, byte destination, int inverseAlpha) =>
        (byte)Math.Min(255, ((source * alpha) + (destination * inverseAlpha) + 127) / 255);

    private static byte AddPremultiplied(byte source, byte destination, int inverseAlpha) =>
        (byte)Math.Min(255, source + (((destination * inverseAlpha) + 127) / 255));

    private static byte AddAlpha(byte sourceAlpha, byte destinationAlpha, int inverseAlpha) =>
        (byte)Math.Min(255, sourceAlpha + (((destinationAlpha * inverseAlpha) + 127) / 255));

    private static byte MultiplyByAlpha(byte value, byte alpha) =>
        (byte)(((value * alpha) + 127) / 255);
}
