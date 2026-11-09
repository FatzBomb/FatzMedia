using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics;

// Stage 038A slice 1: HDR10 to SDR tone mapping on the CPU; Stage 042A slice 1: HLG.

namespace FatzMedia.Video.Core.Video.Hdr;

/// <summary>SMPTE ST 2084 (PQ), the transfer function of HDR10 video.</summary>
static class PqCurve
{
    const double M1 = 2610.0 / 16384;
    const double M2 = 2523.0 / 4096 * 128;
    const double C1 = 3424.0 / 4096;
    const double C2 = 2413.0 / 4096 * 32;
    const double C3 = 2392.0 / 4096 * 32;

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
/// ARIB STD-B67 / ITU-R BT.2100 hybrid log-gamma (HLG): the OETF, its inverse and the OOTF's
/// system gamma (Stage 042A).
/// </summary>
static class HlgCurve
{
    const double A = 0.17883277;
    const double B = 1 - (4 * A);
    static readonly double C = 0.5 - (A * Math.Log(4 * A));

    /// <summary>The nominal display peak HLG is shown for: BT.2100's reference display, 1000 nits.</summary>
    public const double NominalPeakNits = 1000;

    /// <summary>BT.2020 luminance weights, which the HLG OOTF takes scene luminance with.</summary>
    public const double Kr = 0.2627, Kg = 0.6780, Kb = 0.0593;

    /// <summary>Normalized scene light in [0, 1] for an HLG signal in [0, 1] (the inverse OETF).</summary>
    public static double ToSceneLinear(double signal)
    {
        var e = Math.Clamp(signal, 0, 1);
        return e <= 0.5 ? e * e / 3 : (Math.Exp((e - C) / A) + B) / 12;
    }

    /// <summary>The HLG signal in [0, 1] for normalized scene light in [0, 1] (the OETF).</summary>
    public static double FromSceneLinear(double light)
    {
        var e = Math.Clamp(light, 0, 1);
        return e <= 1.0 / 12 ? Math.Sqrt(3 * e) : (A * Math.Log((12 * e) - B)) + C;
    }

    /// <summary>
    /// BT.2100's system gamma for a display peaking at <paramref name="nominalPeakNits"/>:
    /// 1.2 + 0.42 log10(Lw / 1000), so 1.2 at 1000 nits.
    /// </summary>
    public static double SystemGamma(double nominalPeakNits) => 1.2 + (0.42 * Math.Log10(nominalPeakNits / 1000));

    /// <summary>
    /// Display luminance of one channel in nits through the BT.2100 OOTF,
    /// Lw * Ys^(gamma - 1) * E, for scene light <paramref name="channel"/> and scene luminance
    /// <paramref name="sceneLuminance"/> (both normalized). Black level is 0.
    /// </summary>
    public static double ToDisplayNits(double channel, double sceneLuminance, double nominalPeakNits) =>
        sceneLuminance > 0
            ? nominalPeakNits * Math.Pow(sceneLuminance, SystemGamma(nominalPeakNits) - 1) * channel
            : 0;
}

/// <summary>
/// The BT.2390 EETF: compresses luminance above a knee into the target's range with a
/// Hermite spline in PQ space, leaving everything below the knee unchanged. Black level
/// lift is not applied (target black is 0).
/// </summary>
static class Bt2390ToneCurve
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
/// Converts full-range HDR RGB (PQ or HLG, BT.2020 primaries; 16-bit RGB48 as swscale produces it
/// with the BT.2020 matrix) into SDR BGRA8 (BT.709 primaries, BT.1886 gamma 2.4) - Stages 038A
/// (PQ) and 042A (HLG).
/// </summary>
/// <remarks>
/// <para>
/// PQ, per pixel: PQ to linear light; the BT.2390 curve applied to the brightest channel, the same
/// gain for all three (hue preserved, no per-channel desaturation); BT.2020 to BT.709 primaries,
/// clipped; then SDR gamma. The PQ decode, the curve gain (indexed by the brightest channel's
/// signal, since PQ is monotonic) and the gamma encode are 4096-entry tables small enough to
/// stay in cache, so a pixel costs a few table reads, a 3x3 matrix and three square roots. The
/// PQ tables take the top 12 bits of the 16-bit signal (the source is 10-bit); the encode table
/// is indexed by the square root of linear light, which keeps shadow steps far below one 8-bit
/// level. The curve compresses the source's range into [0, reference white], so the source peak
/// is SDR white and the reference white itself lands a little below it (about 230 of 255 for a
/// 1000-nit source at the default 203 nits), as with mpv's default SDR output.
/// </para>
/// <para>
/// HLG, per pixel: the inverse OETF per channel to scene light E (a 4096-entry table like PQ's);
/// scene luminance Ys (BT.2020 weights); the BT.2100 OOTF for the nominal peak Lw gives display
/// light Lw * Ys^(gamma - 1) * E, so the brightest channel shows m * Lw nits with
/// m = Ys^(gamma - 1) * max(E); the BT.2390 curve maps that peak, and every channel keeps its
/// ratio to it (hue preserved, as for PQ): linear = E / max(E) * curve(m * Lw) / reference white.
/// Ys^(gamma - 1) and curve(m * Lw) are tables indexed by the float's exponent and top 7
/// mantissa bits (128 steps per octave over 24 octaves), so their relative error stays below
/// 0.4% at every level without a pow per pixel; the curve table holds the mapped luminance,
/// which is monotonic, so gray levels can only rise. Gamut and gamma steps are PQ's.
/// </para>
/// </remarks>
sealed unsafe class HdrToneMapper
{
    const int TableBits = 12;
    const int TableSize = 1 << TableBits;
    const int SignalShift = 16 - TableBits;
    const float EncodeScale = TableSize - 1;
    const double SdrGamma = 2.4;

    // BT.2020 to BT.709 primaries, linear light (ITU-R BT.2087).
    const float R0 = 1.660491f, R1 = -0.587641f, R2 = -0.072850f;
    const float G0 = -0.124550f, G1 = 1.132900f, G2 = -0.008349f;
    const float B0 = -0.018151f, B1 = -0.100579f, B2 = 1.118730f;

    const int RowsPerBand = 16;

    // HLG's log-indexed tables (Stage 042A): 2^LogMantissaBits steps per octave, from
    // 2^-LogOctaves to 1; values below the first step use entry 0, above 1 the last entry.
    const int LogMantissaBits = 7;
    const int LogOctaves = 24;
    const int LogTableSize = (LogOctaves << LogMantissaBits) + 1;
    const int LogIndexShift = 23 - LogMantissaBits;
    const int LogIndexBase = (127 - LogOctaves) << LogMantissaBits;
    const float HlgKr = (float)HlgCurve.Kr, HlgKg = (float)HlgCurve.Kg, HlgKb = (float)HlgCurve.Kb;

    readonly float[] _linear = new float[TableSize];
    readonly float[] _gain = new float[TableSize];
    readonly byte[] _encode = new byte[TableSize];
    readonly float[]? _inverseScene;
    readonly float[]? _ootf;
    readonly float[]? _mapped;

    public HdrToneMapper(double sourcePeakNits, double referenceWhiteNits, HdrTransferFunction transfer = HdrTransferFunction.Pq)
    {
        SourcePeakNits = sourcePeakNits;
        ReferenceWhiteNits = referenceWhiteNits;
        Transfer = transfer;
        for (var index = 0; index < TableSize; index++)
        {
            // Each signal entry stands for the 16-bit signals index << 4 .. (index << 4) + 15.
            var signal = index == 0 ? 0 : ((index << SignalShift) + (1 << (SignalShift - 1))) / 65535.0;
            if (transfer == HdrTransferFunction.Hlg)
            {
                _linear[index] = (float)HlgCurve.ToSceneLinear(signal);
            }
            else
            {
                var nits = PqCurve.ToNits(signal);
                _linear[index] = (float)(nits / referenceWhiteNits);
                _gain[index] = nits > 0
                    ? (float)(Bt2390ToneCurve.Map(nits, sourcePeakNits, referenceWhiteNits) / nits)
                    : 1f;
            }

            var root = index / (double)(TableSize - 1);
            _encode[index] = (byte)Math.Round(255 * Math.Pow(root * root, 1 / SdrGamma), MidpointRounding.AwayFromZero);
        }

        if (transfer == HdrTransferFunction.Hlg)
        {
            _inverseScene = new float[TableSize];
            for (var index = 1; index < TableSize; index++)
                _inverseScene[index] = 1f / _linear[index];

            var ootfExponent = HlgCurve.SystemGamma(sourcePeakNits) - 1;
            _ootf = new float[LogTableSize];
            _mapped = new float[LogTableSize];
            for (var index = 0; index < LogTableSize; index++)
            {
                // The middle of the step: 2^octave * (1 + (mantissa + 0.5) / 2^LogMantissaBits).
                var octave = (index >> LogMantissaBits) - LogOctaves;
                var mantissa = index & ((1 << LogMantissaBits) - 1);
                var value = Math.Min(Math.ScaleB(1 + ((mantissa + 0.5) / (1 << LogMantissaBits)), octave), 1);
                _ootf[index] = (float)Math.Pow(value, ootfExponent);
                _mapped[index] = index == 0
                    ? 0f
                    : (float)(Bt2390ToneCurve.Map(value * sourcePeakNits, sourcePeakNits, referenceWhiteNits) / referenceWhiteNits);
            }
        }
    }

    /// <summary>The source peak the curve compresses from, in nits (for HLG, the nominal display peak).</summary>
    public double SourcePeakNits { get; }

    /// <summary>The luminance shown as SDR white, in nits.</summary>
    public double ReferenceWhiteNits { get; }

    /// <summary>The source's transfer function.</summary>
    public HdrTransferFunction Transfer { get; }

    /// <summary>Converts one row of RGB48 (R, G, B per pixel) into BGRA8 with opaque alpha.</summary>
    public void ConvertRow(ReadOnlySpan<ushort> rgb48, Span<byte> bgra)
    {
        if (Transfer == HdrTransferFunction.Hlg)
        {
            ConvertHlgRow(rgb48, bgra);
            return;
        }

        var pixels = Math.Min(rgb48.Length / 3, bgra.Length / 4);
        fixed (ushort* source = rgb48)
        fixed (byte* destination = bgra)
        fixed (float* linear = _linear)
        fixed (float* gain = _gain)
        fixed (byte* encode = _encode)
        {
            var input = source;
            var output = destination;
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

    void ConvertHlgRow(ReadOnlySpan<ushort> rgb48, Span<byte> bgra)
    {
        var pixels = Math.Min(rgb48.Length / 3, bgra.Length / 4);
        fixed (ushort* source = rgb48)
        fixed (byte* destination = bgra)
        fixed (float* scene = _linear)
        fixed (float* inverseScene = _inverseScene)
        fixed (float* ootf = _ootf)
        fixed (float* mapped = _mapped)
        fixed (byte* encode = _encode)
        {
            var input = source;
            var output = destination;
            for (var pixel = 0; pixel < pixels; pixel++, input += 3, output += 4)
            {
                var r = input[0] >> SignalShift;
                var g = input[1] >> SignalShift;
                var b = input[2] >> SignalShift;
                var top = Math.Max(r, Math.Max(g, b));
                if (top == 0)
                {
                    output[0] = output[1] = output[2] = 0;
                    output[3] = 255;
                    continue;
                }

                // The OOTF's luminance-dependent gain, then the curve on the brightest channel's
                // display light; each channel keeps its ratio to the brightest.
                var er = scene[r];
                var eg = scene[g];
                var eb = scene[b];
                var luminance = (HlgKr * er) + (HlgKg * eg) + (HlgKb * eb);
                var peak = ootf[ToLogIndex(luminance)] * scene[top];
                var scale = mapped[ToLogIndex(peak)] * inverseScene[top];
                var lr = er * scale;
                var lg = eg * scale;
                var lb = eb * scale;
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

    /// <summary>The log-table index of a non-negative value: its exponent and top mantissa bits, clamped.</summary>
    static int ToLogIndex(float value) =>
        Math.Clamp((BitConverter.SingleToInt32Bits(value) >> LogIndexShift) - LogIndexBase, 0, LogTableSize - 1);

    /// <summary>The encode-table index for linear light: its square root, clipped to [0, 1].</summary>
    static int ToIndex(float value) =>
        (int)((MathF.Sqrt(MathF.Max(0f, MathF.Min(value, 1f))) * EncodeScale) + 0.5f);
}
