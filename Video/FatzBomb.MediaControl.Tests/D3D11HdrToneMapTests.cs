using FatzBomb.MediaControl.Core.Graphics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using FatzBomb.MediaControl.Core.Video.Hdr;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 038A slice 2 (PQ) and Stage 042A (HLG): the Direct3D11 shader's constants reproduce the
/// CPU tone mapper. The shader's formula is mirrored here with
/// <see cref="D3D11HdrToneMapPresenter.CreateConstants"/> and compared with
/// <see cref="HdrToneMapper"/> on the same 10-bit YCbCr input.
/// </summary>
public sealed class D3D11HdrToneMapTests
{
    private static readonly VideoColorSpace Hdr10 = new(VideoColorMatrix.Bt2020, VideoColorRange.Limited);

    [Theory]
    [InlineData(64, 512, 512)]    // black
    [InlineData(940, 512, 512)]   // peak white
    [InlineData(500, 512, 512)]   // mid gray
    [InlineData(600, 400, 700)]   // reddish
    [InlineData(450, 650, 450)]   // bluish
    [InlineData(700, 300, 300)]   // greenish
    [InlineData(800, 512, 960)]   // saturated red past the BT.709 gamut
    public void TheShaderFormula_MatchesTheCpuMapper(int y, int cb, int cr)
    {
        var request = new HdrToneMapRequest(1000, 203);
        byte[] cpu = Cpu(y, cb, cr, request);
        byte[] gpu = Shader(y, cb, cr, request);

        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(gpu[channel], cpu[channel] - 2, cpu[channel] + 2);
    }

    [Theory]
    [InlineData(64, 512, 512)]    // black
    [InlineData(940, 512, 512)]   // peak white
    [InlineData(721, 512, 512)]   // 75% HLG, reference white
    [InlineData(500, 512, 512)]   // mid gray
    [InlineData(200, 512, 512)]   // shadow
    [InlineData(600, 400, 700)]   // reddish
    [InlineData(450, 650, 450)]   // bluish
    [InlineData(700, 300, 300)]   // greenish
    [InlineData(800, 512, 960)]   // saturated red past the BT.709 gamut
    public void TheShaderFormula_MatchesTheCpuMapper_ForHlg(int y, int cb, int cr)
    {
        var request = new HdrToneMapRequest(1000, 203) { Transfer = HdrTransferFunction.Hlg };
        byte[] cpu = Cpu(y, cb, cr, request);
        byte[] gpu = Shader(y, cb, cr, request);

        for (var channel = 0; channel < 3; channel++)
            Assert.InRange(gpu[channel], cpu[channel] - 2, cpu[channel] + 2);
    }

    [Fact]
    public void TheTransferConstant_SelectsHlgAndItsOotf()
    {
        D3D11HdrToneMapPresenter.ToneMapConstants pq = D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10, new HdrToneMapRequest(1000, 203), 16, 16, 16, 16,
            new Vortice.RawRect(0, 0, 16, 16), new Vortice.RawRect(0, 0, 16, 16));
        D3D11HdrToneMapPresenter.ToneMapConstants hlg = D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10, new HdrToneMapRequest(1000, 203) { Transfer = HdrTransferFunction.Hlg }, 16, 16, 16, 16,
            new Vortice.RawRect(0, 0, 16, 16), new Vortice.RawRect(0, 0, 16, 16));

        Assert.Equal(0f, pq.Transfer.X);
        Assert.Equal(1f, hlg.Transfer.X);
        Assert.Equal(1000f, hlg.Transfer.Y);
        Assert.Equal(0.2f, hlg.Transfer.Z, 6);
        Assert.Equal(pq.Curve, hlg.Curve);
    }

    [Fact]
    public void Rectangles_MapToClipSpaceAndTextureSpace()
    {
        D3D11HdrToneMapPresenter.ToneMapConstants constants = D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10,
            new HdrToneMapRequest(1000, 203),
            textureWidth: 3840,
            textureHeight: 2176,
            targetWidth: 1920,
            targetHeight: 1080,
            sourceRect: new Vortice.RawRect(0, 0, 3840, 2160),
            destinationRect: new Vortice.RawRect(0, 0, 1920, 1080));

        Assert.Equal(new System.Numerics.Vector4(-1, 1, 1, -1), constants.DestinationRect);
        Assert.Equal(0f, constants.TextureRect.X);
        Assert.Equal(1f, constants.TextureRect.Z);
        Assert.Equal(2160f / 2176f, constants.TextureRect.W, 6);
        Assert.Equal(2159f / 2176f, constants.SampleClamp.W, 6);
        Assert.Equal(1f / 3840f, constants.SampleClamp.X, 6);
    }

    [Fact]
    public void ADimSource_IsOnlyClipped() =>
        Assert.Equal(0f, D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10, new HdrToneMapRequest(150, 203), 16, 16, 16, 16,
            new Vortice.RawRect(0, 0, 16, 16), new Vortice.RawRect(0, 0, 16, 16)).Curve.W);

    internal static byte[] Cpu(int y, int cb, int cr, HdrToneMapRequest request)
    {
        // What swscale produces: full-range 16-bit R'G'B' from limited-range BT.2020 YCbCr.
        double yn = (y - 64) / 876.0, cbn = (cb - 512) / 896.0, crn = (cr - 512) / 896.0;
        const double kr = 0.2627, kb = 0.0593, kg = 1 - kr - kb;
        var r = yn + (2 * (1 - kr) * crn);
        var b = yn + (2 * (1 - kb) * cbn);
        var g = (yn - (kr * r) - (kb * b)) / kg;
        ushort ToCode(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535);

        var output = new byte[4];
        new HdrToneMapper(request.SourcePeakNits, request.TargetPeakNits, request.Transfer).ConvertRow([ToCode(r), ToCode(g), ToCode(b)], output);
        return [output[2], output[1], output[0]];
    }

    // The PSToneMap shader, line for line, with the presenter's constants.
    private static byte[] Shader(int y, int cb, int cr, HdrToneMapRequest request)
    {
        D3D11HdrToneMapPresenter.ToneMapConstants k = D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10, request, 16, 16, 16, 16, new Vortice.RawRect(0, 0, 16, 16), new Vortice.RawRect(0, 0, 16, 16));
        double Sample(int code) => code * 64 / 65535.0;

        var yy = (Sample(y) * k.YuvScaleOffset.X) + k.YuvScaleOffset.Y;
        var u = (Sample(cb) * k.YuvScaleOffset.Z) + k.YuvScaleOffset.W;
        var v = (Sample(cr) * k.YuvScaleOffset.Z) + k.YuvScaleOffset.W;
        double[] signal = [yy + (k.Matrix.X * v), yy - (k.Matrix.Y * u) - (k.Matrix.Z * v), yy + (k.Matrix.W * u)];

        double[] nits = k.Transfer.X > 0.5 ? HlgToNits(signal, k) : [.. signal.Select(s => PqCurve.ToNits(Math.Clamp(s, 0, 1)))];
        var brightest = nits.Max();
        var gain = brightest > 0 ? ToneCurve(brightest, k) / brightest : 1;
        double[] l = [.. nits.Select(n => n * gain * k.Output.X)];
        double[] linear709 =
        [
            (1.660491 * l[0]) - (0.587641 * l[1]) - (0.072850 * l[2]),
            (-0.124550 * l[0]) + (1.132900 * l[1]) - (0.008349 * l[2]),
            (-0.018151 * l[0]) - (0.100579 * l[1]) + (1.118730 * l[2]),
        ];
        return [.. linear709.Select(c => (byte)Math.Round(255 * Math.Pow(Math.Clamp(c, 0, 1), k.Output.Y)))];
    }

    // The shader's HlgToNits: inverse OETF per channel, then the OOTF on BT.2020 luminance.
    private static double[] HlgToNits(double[] signal, D3D11HdrToneMapPresenter.ToneMapConstants k)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        double[] scene = [.. signal.Select(s => Math.Clamp(s, 0, 1)).Select(s => s <= 0.5 ? s * s / 3 : (Math.Exp((s - c) / a) + b) / 12)];
        var luminance = (0.2627 * scene[0]) + (0.6780 * scene[1]) + (0.0593 * scene[2]);
        var ootf = luminance > 0 ? k.Transfer.Y * Math.Pow(luminance, k.Transfer.Z) : 0;
        return [.. scene.Select(e => e * ootf)];
    }

    private static double ToneCurve(double nits, D3D11HdrToneMapPresenter.ToneMapConstants k)
    {
        if (k.Curve.W < 0.5)
            return Math.Min(nits, k.Output.Z);

        var e1 = Math.Min(PqCurve.FromNits(nits) / k.Curve.X, 1);
        double kneeStart = k.Curve.Z, e2 = e1;
        if (e1 > kneeStart)
        {
            var t = (e1 - kneeStart) / (1 - kneeStart);
            var t2 = t * t;
            var t3 = t2 * t;
            e2 = (((2 * t3) - (3 * t2) + 1) * kneeStart) + ((t3 - (2 * t2) + t) * (1 - kneeStart)) + (((-2 * t3) + (3 * t2)) * k.Curve.Y);
        }

        return Math.Min(PqCurve.ToNits(e2 * k.Curve.X), k.Output.Z);
    }
}
