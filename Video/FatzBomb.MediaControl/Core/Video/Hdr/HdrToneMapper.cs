using System.Runtime.InteropServices;

// Stage 038A slice 1: HDR10 to SDR tone mapping on the CPU.

namespace FatzBomb.MediaControl.Core.Video.Hdr;

/// <summary>SMPTE ST 2084 (PQ), the transfer function of HDR10 video.</summary>
internal static class PqCurve
{
    private const double M1 = 2610.0 / 16384;
    private const double M2 = 2523.0 / 4096 * 128;
    private const double C1 = 3424.0 / 4096;
    private const double C2 = 2413.0 / 4096 * 32;
    private const double C3 = 2392.0 / 4096 * 32;

    /// <summary>PQ's peak luminance in nits (signal 1.0).</summary>
    public const double PeakNits = 10000;

    /// <summary>Display luminance in nits for a PQ signal in [0, 1].</summary>
    public static double ToNits(double signal)
    {
        var e = Math.Pow(Math.Clamp(signal, 0, 1), 1 / M2);
        return PeakNits * Math.Pow(Math.Max(e - C1, 0) / (C2 - (C3 * e)), 1 / M1);
    }

    /// <summary>The PQ signal in [0, 1] for a luminance in nits.</summary>
    public static double FromNits(double nits)
    {
        var y = Math.Pow(Math.Clamp(nits / PeakNits, 0, 1), M1);
        return Math.Pow((C1 + (C2 * y)) / (1 + (C3 * y)), M2);
    }
}

/// <summary>
/// The BT.2390 EETF: compresses luminance above a knee into the target's range with a
/// Hermite spline in PQ space, leaving everything below the knee unchanged. Black level
/// lift is not applied (target black is 0).
/// </summary>
internal static class Bt2390ToneCurve
{
    /// <summary>Maps <paramref name="nits"/> from a source peaking at <paramref name="sourcePeakNits"/> into [0, <paramref name="targetPeakNits"/>].</summary>
    public static double Map(double nits, double sourcePeakNits, double targetPeakNits)
    {
        if (!(nits > 0))
            return 0;
        if (sourcePeakNits <= targetPeakNits)
            return Math.Min(nits, targetPeakNits);

        var sourcePeak = PqCurve.FromNits(sourcePeakNits);
        var e1 = Math.Min(PqCurve.FromNits(nits) / sourcePeak, 1);
        var maxLum = PqCurve.FromNits(targetPeakNits) / sourcePeak;
        var kneeStart = Math.Max((1.5 * maxLum) - 0.5, 0);
        var e2 = e1;
        if (e1 > kneeStart)
        {
            var t = (e1 - kneeStart) / (1 - kneeStart);
            var t2 = t * t;
            var t3 = t2 * t;
            e2 = (((2 * t3) - (3 * t2) + 1) * kneeStart)
                + ((t3 - (2 * t2) + t) * (1 - kneeStart))
                + (((-2 * t3) + (3 * t2)) * maxLum);
        }

        return Math.Min(PqCurve.ToNits(e2 * sourcePeak), targetPeakNits);
    }
}

/// <summary>
/// Converts full-range HDR10 RGB (PQ, BT.2020 primaries; 16-bit RGB48 as swscale produces it
/// with the BT.2020 matrix) into SDR BGRA8 (BT.709 primaries, BT.1886 gamma 2.4) - Stage 038A.
/// </summary>
/// <remarks>
/// Per pixel: PQ to linear light; the BT.2390 curve applied to the brightest channel, the same
/// gain for all three (hue preserved, no per-channel desaturation); BT.2020 to BT.709 primaries,
/// clipped; then SDR gamma. The PQ decode, the curve gain (indexed by the brightest channel's
/// signal, since PQ is monotonic) and the gamma encode are 4096-entry tables small enough to
/// stay in cache, so a pixel costs a few table reads, a 3x3 matrix and three square roots. The
/// PQ tables take the top 12 bits of the 16-bit signal (the source is 10-bit); the encode table
/// is indexed by the square root of linear light, which keeps shadow steps far below one 8-bit
/// level. The curve compresses the source's range into [0, reference white], so the source peak
/// is SDR white and the reference white itself lands a little below it (about 230 of 255 for a
/// 1000-nit source at the default 203 nits), as with mpv's default SDR output.
/// </remarks>
internal sealed unsafe class HdrToneMapper
{
    private const int TableBits = 12;
    private const int TableSize = 1 << TableBits;
    private const int SignalShift = 16 - TableBits;
    private const float EncodeScale = TableSize - 1;
    private const double SdrGamma = 2.4;

    // BT.2020 to BT.709 primaries, linear light (ITU-R BT.2087).
    private const float R0 = 1.660491f, R1 = -0.587641f, R2 = -0.072850f;
    private const float G0 = -0.124550f, G1 = 1.132900f, G2 = -0.008349f;
    private const float B0 = -0.018151f, B1 = -0.100579f, B2 = 1.118730f;

    private const int RowsPerBand = 16;

    private readonly float[] _linear = new float[TableSize];
    private readonly float[] _gain = new float[TableSize];
    private readonly byte[] _encode = new byte[TableSize];

    public HdrToneMapper(double sourcePeakNits, double referenceWhiteNits)
    {
        SourcePeakNits = sourcePeakNits;
        ReferenceWhiteNits = referenceWhiteNits;
        for (var index = 0; index < TableSize; index++)
        {
            // Each PQ entry stands for the 16-bit signals index << 4 .. (index << 4) + 15.
            var signal = index == 0 ? 0 : ((index << SignalShift) + (1 << (SignalShift - 1))) / 65535.0;
            var nits = PqCurve.ToNits(signal);
            _linear[index] = (float)(nits / referenceWhiteNits);
            _gain[index] = nits > 0
                ? (float)(Bt2390ToneCurve.Map(nits, sourcePeakNits, referenceWhiteNits) / nits)
                : 1f;

            var root = index / (double)(TableSize - 1);
            _encode[index] = (byte)Math.Round(255 * Math.Pow(root * root, 1 / SdrGamma), MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>The source peak the curve compresses from, in nits.</summary>
    public double SourcePeakNits { get; }

    /// <summary>The luminance shown as SDR white, in nits.</summary>
    public double ReferenceWhiteNits { get; }

    /// <summary>Converts one row of RGB48 (R, G, B per pixel) into BGRA8 with opaque alpha.</summary>
    public void ConvertRow(ReadOnlySpan<ushort> rgb48, Span<byte> bgra)
    {
        var pixels = Math.Min(rgb48.Length / 3, bgra.Length / 4);
        fixed (ushort* source = rgb48)
        fixed (byte* destination = bgra)
        fixed (float* linear = _linear)
        fixed (float* gain = _gain)
        fixed (byte* encode = _encode)
        {
            ushort* input = source;
            byte* output = destination;
            for (var pixel = 0; pixel < pixels; pixel++, input += 3, output += 4)
            {
                var r = input[0] >> SignalShift;
                var g = input[1] >> SignalShift;
                var b = input[2] >> SignalShift;
                var scale = gain[Math.Max(r, Math.Max(g, b))];
                var lr = linear[r] * scale;
                var lg = linear[g] * scale;
                var lb = linear[b] * scale;
                output[0] = encode[ToIndex((B0 * lr) + (B1 * lg) + (B2 * lb))];
                output[1] = encode[ToIndex((G0 * lr) + (G1 * lg) + (G2 * lb))];
                output[2] = encode[ToIndex((R0 * lr) + (R1 * lg) + (R2 * lb))];
                output[3] = 255;
            }
        }
    }

    /// <summary>
    /// Converts a <paramref name="width"/> x <paramref name="height"/> RGB48 image into BGRA8,
    /// in parallel row bands.
    /// </summary>
    public void Convert(byte[] source, int sourceStride, byte[] destination, int destinationStride, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, width * 6);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationStride, width * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(source.Length, (long)sourceStride * height);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, (long)destinationStride * height);

        var bands = (height + RowsPerBand - 1) / RowsPerBand;
        Parallel.For(0, bands, band =>
        {
            var end = Math.Min(height, (band + 1) * RowsPerBand);
            for (var y = band * RowsPerBand; y < end; y++)
            {
                ReadOnlySpan<ushort> row = MemoryMarshal.Cast<byte, ushort>(source.AsSpan(y * sourceStride, width * 6));
                ConvertRow(row, destination.AsSpan(y * destinationStride, width * 4));
            }
        });
    }

    /// <summary>The encode-table index for linear light: its square root, clipped to [0, 1].</summary>
    private static int ToIndex(float value) =>
        (int)((MathF.Sqrt(MathF.Max(0f, MathF.Min(value, 1f))) * EncodeScale) + 0.5f);
}
