using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Core.Video.Hdr;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Stage 038A slice 1: HDR10 (PQ) to SDR tone mapping.</summary>
public sealed class HdrToneMapperTests
{
    private const double Reference = HdrToneMappingOptions.DefaultReferenceWhiteNits;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0.5081)]
    [InlineData(203, 0.5806)]
    [InlineData(1000, 0.7518)]
    [InlineData(10000, 1)]
    public void Pq_MatchesSt2084(double nits, double signal)
    {
        Assert.InRange(PqCurve.FromNits(nits), signal - 0.0005, signal + 0.0005);
        Assert.InRange(PqCurve.ToNits(signal), nits - Math.Max(0.5, nits * 0.002), nits + Math.Max(0.5, nits * 0.002));
    }

    [Fact]
    public void TheCurve_MapsThePeakToTheTargetAndLeavesShadowsAlone()
    {
        Assert.InRange(Bt2390ToneCurve.Map(1000, 1000, Reference), Reference - 0.5, Reference + 0.01);
        Assert.InRange(Bt2390ToneCurve.Map(4000, 1000, Reference), Reference - 0.5, Reference + 0.01);
        Assert.Equal(50, Bt2390ToneCurve.Map(50, 1000, Reference), 6);
        Assert.Equal(0, Bt2390ToneCurve.Map(0, 1000, Reference));
    }

    [Fact]
    public void TheCurve_IsMonotonic()
    {
        double previous = 0;
        for (var nits = 1.0; nits <= 4000; nits *= 1.01)
        {
            var mapped = Bt2390ToneCurve.Map(nits, 4000, Reference);
            Assert.True(mapped >= previous - 1e-9, $"{nits} nits mapped to {mapped}, below {previous}");
            Assert.True(mapped <= nits + 1e-9);
            previous = mapped;
        }
    }

    [Fact]
    public void ASourceNoBrighterThanTheTarget_IsOnlyClipped() =>
        Assert.Equal(150, Bt2390ToneCurve.Map(150, 180, Reference));

    [Fact]
    public void BlackAndPeakWhite_MapToSdrBlackAndWhite()
    {
        var mapper = new HdrToneMapper(1000, Reference);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Convert(mapper, 0, 0, 0));
        var peak = Code(1000);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Convert(mapper, peak, peak, peak));
    }

    [Fact]
    public void GrayLevels_StayNeutralAndOrdered()
    {
        var mapper = new HdrToneMapper(1000, Reference);
        var previous = -1;
        foreach (var nits in new[] { 1.0, 10, 50, 100, 203, 500, 1000 })
        {
            var code = Code(nits);
            byte[] bgra = Convert(mapper, code, code, code);
            Assert.True(bgra[0] == bgra[1] && bgra[1] == bgra[2], $"{nits} nits gray is not neutral: {string.Join(",", bgra)}");
            Assert.True(bgra[0] > previous, $"{nits} nits ({bgra[0]}) is not brighter than the level below ({previous})");
            previous = bgra[0];
        }
    }

    [Fact]
    public void ReferenceWhite_IsBrightButBelowThePeak()
    {
        var mapper = new HdrToneMapper(1000, Reference);
        var code = Code(Reference);
        byte value = Convert(mapper, code, code, code)[0];
        Assert.InRange(value, 200, 250);
    }

    [Fact]
    public void Bt2020Red_StaysRedAfterGamutClipping()
    {
        var mapper = new HdrToneMapper(1000, Reference);
        byte[] bgra = Convert(mapper, Code(100), 0, 0);
        Assert.True(bgra[2] > 200, $"red {bgra[2]}");
        Assert.Equal(0, bgra[1]);
        Assert.Equal(0, bgra[0]);
    }

    [Fact]
    public void AnImage_ConvertsEveryRowWithItsStrides()
    {
        var mapper = new HdrToneMapper(1000, Reference);
        const int width = 3, height = 40, sourceStride = (width * 6) + 4, destinationStride = (width * 4) + 8;
        var source = new byte[sourceStride * height];
        var code = Code(Reference);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width * 3; x++)
                BitConverter.TryWriteBytes(source.AsSpan((y * sourceStride) + (x * 2), 2), code);
        }

        var destination = new byte[destinationStride * height];
        mapper.Convert(source, sourceStride, destination, destinationStride, width, height);

        byte expected = Convert(mapper, code, code, code)[0];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * destinationStride) + (x * 4);
                Assert.Equal(new[] { expected, expected, expected, (byte)255 }, destination.AsSpan(offset, 4).ToArray());
            }

            Assert.Equal(0, destination[(y * destinationStride) + (width * 4)]);
        }
    }

    [Theory]
    [InlineData(1100, 4000, 1100, "MaxCLL")]
    [InlineData(0, 4000, 4000, "mastering display")]
    [InlineData(50, 0, FFmpegVideoColor.DefaultHdrPeakNits, "assumed, no usable metadata")]
    [InlineData(20000, 1000, 1000, "mastering display")]
    public void ThePeak_ComesFromUsableMetadata(double maxCll, double mastering, double expected, string source)
    {
        (double nits, string actualSource) = FFmpegVideoColor.ChoosePeak(maxCll, mastering, Reference);
        Assert.Equal(expected, nits);
        Assert.Equal(source, actualSource);
    }

    [Fact]
    public void Options_DefaultToTheBt2408ReferenceWhite()
    {
        Assert.Equal(203, default(HdrToneMappingOptions).EffectiveReferenceWhiteNits);
        Assert.Equal(100, new HdrToneMappingOptions(ReferenceWhiteNits: 100).EffectiveReferenceWhiteNits);
        Assert.False(new MediaPlayerConfiguration().HdrToneMapping.Disabled);
    }

    private static ushort Code(double nits) => (ushort)Math.Round(PqCurve.FromNits(nits) * 65535);

    private static byte[] Convert(HdrToneMapper mapper, ushort r, ushort g, ushort b)
    {
        var output = new byte[4];
        mapper.ConvertRow([r, g, b], output);
        return output;
    }
}
