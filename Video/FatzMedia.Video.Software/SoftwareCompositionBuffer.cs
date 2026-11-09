using System.Runtime.InteropServices;

namespace FatzMedia.Video.Software;

sealed class SoftwareCompositionBuffer(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = checked(width * 4);
    public byte[] Pixels { get; } = new byte[checked(width * height * 4)];
    int[] _columns = [];

    /// <summary>Gets the pixels as packed BGRA values (blue in the low byte, alpha in the high byte).</summary>
    public Span<uint> Pixels32 => MemoryMarshal.Cast<byte, uint>(Pixels.AsSpan());

    /// <summary>
    /// Gets a scratch array of at least <paramref name="length"/> source-column indexes. The
    /// buffer is used by one render at a time, so the scratch is too; parallel row bands of
    /// that render only read it.
    /// </summary>
    public int[] GetColumnScratch(int length)
    {
        if (_columns.Length < length)
            _columns = new int[length];
        return _columns;
    }

    public static uint Pack(byte blue, byte green, byte red, byte alpha) =>
        blue | ((uint)green << 8) | ((uint)red << 16) | ((uint)alpha << 24);

    public void BlendStraightAt(int offset, byte blue, byte green, byte red, byte alpha)
    {
        var inverseAlpha = 255 - alpha;
        Pixels[offset] = BlendStraightChannel(blue, alpha, Pixels[offset], inverseAlpha);
        Pixels[offset + 1] = BlendStraightChannel(green, alpha, Pixels[offset + 1], inverseAlpha);
        Pixels[offset + 2] = BlendStraightChannel(red, alpha, Pixels[offset + 2], inverseAlpha);
        Pixels[offset + 3] = AddAlpha(alpha, Pixels[offset + 3], inverseAlpha);
    }

    public void BlendPremultipliedAt(int offset, byte blue, byte green, byte red, byte alpha)
    {
        var inverseAlpha = 255 - alpha;
        Pixels[offset] = AddPremultiplied(blue, Pixels[offset], inverseAlpha);
        Pixels[offset + 1] = AddPremultiplied(green, Pixels[offset + 1], inverseAlpha);
        Pixels[offset + 2] = AddPremultiplied(red, Pixels[offset + 2], inverseAlpha);
        Pixels[offset + 3] = AddAlpha(alpha, Pixels[offset + 3], inverseAlpha);
    }

    static byte BlendStraightChannel(byte source, byte alpha, byte destination, int inverseAlpha) =>
        (byte)Math.Min(255, ((source * alpha) + (destination * inverseAlpha) + 127) / 255);

    static byte AddPremultiplied(byte source, byte destination, int inverseAlpha) =>
        (byte)Math.Min(255, source + (((destination * inverseAlpha) + 127) / 255));

    static byte AddAlpha(byte sourceAlpha, byte destinationAlpha, int inverseAlpha) =>
        (byte)Math.Min(255, sourceAlpha + (((destinationAlpha * inverseAlpha) + 127) / 255));
}
