using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 042A slice 1: HLG to SDR tone mapping (inverse OETF, BT.2100 OOTF, BT.2390 curve).</summary>
public sealed class HlgToneMapperTests
{
    const double Reference = HdrToneMappingOptions.DefaultReferenceWhiteNits;
    const double Peak = HlgCurve.NominalPeakNits;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.25, 0.0208333)]
    [InlineData(0.5, 1.0 / 12)]
    [InlineData(0.75, 0.2649650)]
    [InlineData(1, 1)]
    public void TheInverseOetf_MatchesBt2100(double signal, double light)
    {
        Assert.Equal(light, HlgCurve.ToSceneLinear(signal), 5);
        Assert.Equal(signal, HlgCurve.FromSceneLinear(light), 5);
    }

    [Theory]
    [InlineData(1000, 1.2)]
    [InlineData(2000, 1.32643)]
    [InlineData(400, 1.03287)]
    public void TheSystemGamma_FollowsTheBt2100Formula(double nominalPeak, double gamma) =>
        Assert.Equal(gamma, HlgCurve.SystemGamma(nominalPeak), 4);

    [Fact]
    public void TheOotf_ShowsHlgReferenceWhiteAt203Nits()
    {
        // BT.2408: 75% HLG is reference white, 203 nits on a 1000-nit display.
        var light = HlgCurve.ToSceneLinear(0.75);
        Assert.InRange(HlgCurve.ToDisplayNits(light, light, Peak), 202, 204);
        Assert.Equal(Peak, HlgCurve.ToDisplayNits(1, 1, Peak), 6);

        // A saturated color is dimmer than white at the same signal: the OOTF works on luminance.
        Assert.InRange(HlgCurve.ToDisplayNits(1, HlgCurve.Kb, Peak), 560, 575);
    }

    [Fact]
    public void TheRequest_CarriesHlgAndItsSystemGamma()
    {
        var request = new HdrToneMapRequest(Peak, Reference) { Transfer = HdrTransferFunction.Hlg };
        Assert.Equal(1.2, request.HlgSystemGamma, 9);
        Assert.Equal(1, new HdrToneMapRequest(1000, Reference).HlgSystemGamma);
        Assert.Equal(HdrTransferFunction.Pq, default(HdrToneMapRequest).Transfer);
    }

    [Fact]
    public void BlackAndPeakWhite_MapToSdrBlackAndWhite()
    {
        var mapper = CreateMapper();
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Convert(mapper, 0, 0, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Convert(mapper, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue));
    }

    [Fact]
    public void GrayLevels_StayNeutralAndRiseMonotonically()
    {
        var mapper = CreateMapper();
        var previous = 0;
        for (var code = 0; code <= 1023; code++)
        {
            var value = (ushort)Math.Round(code * 65535 / 1023.0);
            var bgra = Convert(mapper, value, value, value);
            Assert.True(bgra[0] == bgra[1] && bgra[1] == bgra[2], $"10-bit gray {code} is not neutral: {string.Join(",", bgra)}");
            Assert.True(bgra[0] >= previous, $"10-bit gray {code} ({bgra[0]}) is darker than the level below ({previous})");
            previous = bgra[0];
        }

        // Distinct levels are distinct.
        var last = -1;
        foreach (var signal in new[] { 0.1, 0.25, 0.4, 0.5, 0.6, 0.75, 0.9, 1.0 })
        {
            var level = Convert(mapper, Code(signal), Code(signal), Code(signal))[0];
            Assert.True(level > last, $"HLG {signal} ({level}) is not brighter than the level below ({last})");
            last = level;
        }
    }

    [Fact]
    public void HlgReferenceWhite_LooksLikePqReferenceWhite()
    {
        // 75% HLG and 203-nit PQ are both BT.2408 reference white; with 1000-nit peaks on both
        // sides they come out the same (about 230 of 255 at the default SDR white).
        var hlg = Convert(CreateMapper(), Code(0.75), Code(0.75), Code(0.75))[0];
        var pqCode = (ushort)Math.Round(PqCurve.FromNits(203) * 65535);
        var pq = Convert(new HdrToneMapper(Peak, Reference), pqCode, pqCode, pqCode)[0];
        Assert.InRange(hlg, pq - 1, pq + 1);
        Assert.InRange(hlg, 200, 250);
    }

    [Fact]
    public void Bt2020Red_StaysRedAfterGamutClipping()
    {
        var bgra = Convert(CreateMapper(), Code(0.75), 0, 0);
        Assert.True(bgra[2] > 150, $"red {bgra[2]}");
        Assert.Equal(0, bgra[1]);
        Assert.Equal(0, bgra[0]);
    }

    [Fact]
    public void TheTables_MatchTheExactMathWithinOneLevel()
    {
        var mapper = CreateMapper();
        var random = new Random(42);
        for (var sample = 0; sample < 4000; sample++)
        {
            // 10-bit codes in the 16-bit container, as swscale writes them.
            int r = random.Next(1024), g = random.Next(1024), b = random.Next(1024);
            if (sample % 4 == 0)
                g = b = r;

            var actual = Convert(mapper, Ten(r), Ten(g), Ten(b));
            var expected = Exact(Ten(r), Ten(g), Ten(b));
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.True(
                    Math.Abs(actual[channel] - expected[channel]) <= 1,
                    $"({r},{g},{b}) channel {channel}: {actual[channel]}, exact {expected[channel]}");
            }
        }
    }

    [Fact]
    public void AnImage_ConvertsEveryRow()
    {
        var mapper = CreateMapper();
        const int Width = 5, Height = 37, SourceStride = Width * 6, DestinationStride = (Width * 4) + 4;
        var source = new byte[SourceStride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width * 3; x++)
                BitConverter.TryWriteBytes(source.AsSpan((y * SourceStride) + (x * 2), 2), Code(y / (double)Height));
        }

        var destination = new byte[DestinationStride * Height];
        mapper.Convert(source, SourceStride, destination, DestinationStride, Width, Height);

        for (var y = 0; y < Height; y++)
        {
            var expected = Convert(mapper, Code(y / (double)Height), Code(y / (double)Height), Code(y / (double)Height))[0];
            Assert.Equal(expected, destination[(y * DestinationStride) + ((Width - 1) * 4)]);
        }
    }

    static HdrToneMapper CreateMapper() => new(Peak, Reference, HdrTransferFunction.Hlg);

    static ushort Code(double signal) => (ushort)Math.Round(signal * 65535);

    static ushort Ten(int code) => (ushort)Math.Round(code * 65535 / 1023.0);

    static byte[] Convert(HdrToneMapper mapper, ushort r, ushort g, ushort b)
    {
        var output = new byte[4];
        mapper.ConvertRow([r, g, b], output);
        return output;
    }

    /// <summary>The mapping in double precision, without tables.</summary>
    internal static byte[] Exact(ushort r, ushort g, ushort b)
    {
        double[] light = [HlgCurve.ToSceneLinear(r / 65535.0), HlgCurve.ToSceneLinear(g / 65535.0), HlgCurve.ToSceneLinear(b / 65535.0)];
        var luminance = (HlgCurve.Kr * light[0]) + (HlgCurve.Kg * light[1]) + (HlgCurve.Kb * light[2]);
        double[] nits = [.. light.Select(e => HlgCurve.ToDisplayNits(e, luminance, Peak))];
        var brightest = nits.Max();
        var gain = brightest > 0 ? Bt2390ToneCurve.Map(brightest, Peak, Reference) / brightest : 0;
        double[] l = [.. nits.Select(n => n * gain / Reference)];
        double[] linear709 =
        [
            (1.660491 * l[0]) - (0.587641 * l[1]) - (0.072850 * l[2]),
            (-0.124550 * l[0]) + (1.132900 * l[1]) - (0.008349 * l[2]),
            (-0.018151 * l[0]) - (0.100579 * l[1]) + (1.118730 * l[2]),
        ];
        byte[] rgb = [.. linear709.Select(c => (byte)Math.Round(255 * Math.Pow(Math.Clamp(c, 0, 1), 1 / 2.4)))];
        return [rgb[2], rgb[1], rgb[0], 255];
    }
}
