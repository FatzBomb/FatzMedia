using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Software;

namespace FatzMedia.Video.Tests;

public sealed class BaseVisualTransformTests
{
    [Fact]
    public void DefaultsAreIdentity()
    {
        BaseVisualTransform transform = default;

        Assert.Equal(1f, transform.RedGain);
        Assert.Equal(1f, transform.GreenGain);
        Assert.Equal(1f, transform.BlueGain);
        Assert.Equal(0f, transform.HueDegrees);
        Assert.Equal(0f, transform.Brightness);
        Assert.Equal(1f, transform.Contrast);
        Assert.Equal(1f, transform.Saturation);
        Assert.False(transform.IncludeOverlays);
        Assert.True(transform.IsIdentity);
        Assert.Equal(BaseVisualTransform.Identity, new BaseVisualTransform());
    }

    [Fact]
    public void ValuesAreClampedAndNaNUsesIdentityDefault()
    {
        var transform = new BaseVisualTransform
        {
            RedGain = 3f,
            GreenGain = -1f,
            BlueGain = float.NaN,
            HueDegrees = -360f,
            Brightness = float.NaN,
            Contrast = -1f,
            Saturation = float.NaN
        };

        Assert.Equal(2f, transform.RedGain);
        Assert.Equal(0f, transform.GreenGain);
        Assert.Equal(1f, transform.BlueGain);
        Assert.Equal(-180f, transform.HueDegrees);
        Assert.Equal(0f, transform.Brightness);
        Assert.Equal(0f, transform.Contrast);
        Assert.Equal(1f, transform.Saturation);
    }

    [Fact]
    public void ExplicitNeutralGainsAreIdentity()
    {
        var transform = new BaseVisualTransform
        {
            RedGain = 1f,
            GreenGain = 1f,
            BlueGain = 1f
        };

        Assert.Equal(BaseVisualTransform.Identity, transform);
        Assert.True(transform.IsIdentity);
    }

    [Fact]
    public void IncludeOverlaysDoesNotMakeNeutralColorMathNonIdentity()
    {
        var transform = new BaseVisualTransform { IncludeOverlays = true };

        Assert.NotEqual(BaseVisualTransform.Identity, transform);
        Assert.True(transform.IsIdentity);
    }

    [Fact]
    public void TvStyleContrastScalesFromBlackBeforeBrightnessOffset()
    {
        var transform = new BaseVisualTransform { Contrast = 0.5f, Brightness = 0.1f };

        Assert.Equal(
            (0.1f, 0.35f, 0.6f),
            BaseVisualTransformMath.ApplyStraight(0f, 0.5f, 1f, transform));
    }

    [Fact]
    public void HueRotationUsesPinnedYiqEquations()
    {
        var transform = new BaseVisualTransform { HueDegrees = 90f };

        (float red, float green, float blue) = BaseVisualTransformMath.ApplyStraight(1f, 0f, 0f, transform);

        Assert.Equal(0.467f, red, 3);
        Assert.Equal(0f, green);
        Assert.Equal(1f, blue);
    }

    [Fact]
    public void SaturationZeroUsesRec709Luma()
    {
        var transform = new BaseVisualTransform { Saturation = 0f };

        (float red, float green, float blue) = BaseVisualTransformMath.ApplyStraight(1f, 0f, 0f, transform);

        Assert.Equal(0.2126f, red, 4);
        Assert.Equal(red, green);
        Assert.Equal(red, blue);
    }

    [Theory]
    [InlineData(1f, 0f, 0f, 0.2f, 0f, 0f)]
    [InlineData(0f, 1f, 1f, 0f, 0.4f, 0.6f)]
    [InlineData(0f, 0f, 0f, 0f, 0f, 0f)]
    public void RgbGainsIndependentlyRemoveChannels(
        float redGain,
        float greenGain,
        float blueGain,
        float expectedRed,
        float expectedGreen,
        float expectedBlue)
    {
        var transform = new BaseVisualTransform
        {
            RedGain = redGain,
            GreenGain = greenGain,
            BlueGain = blueGain
        };

        (float red, float green, float blue) = BaseVisualTransformMath.ApplyStraight(0.2f, 0.4f, 0.6f, transform);

        Assert.Equal(expectedRed, red, 4);
        Assert.Equal(expectedGreen, green, 4);
        Assert.Equal(expectedBlue, blue, 4);
    }

    [Fact]
    public void BrightnessContrastAndRgbGainsFollowFixedOrder()
    {
        var transform = new BaseVisualTransform
        {
            Brightness = 0.1f,
            Contrast = 1.5f,
            RedGain = 0.5f,
            GreenGain = 1.25f,
            BlueGain = 0f
        };

        (float red, float green, float blue) = BaseVisualTransformMath.ApplyStraight(0.2f, 0.4f, 0.6f, transform);

        Assert.Equal(0.2f, red, 3);
        Assert.Equal(0.875f, green, 3);
        Assert.Equal(0f, blue);
    }

    [Fact]
    public void PremultipliedTransformPreservesAlpha()
    {
        var transform = new BaseVisualTransform { RedGain = 0f };

        uint result = BaseVisualTransformMath.ApplyPremultipliedBgra(0x80402010, transform);

        Assert.Equal(0x80002010u, result);
    }

    [Fact]
    public void PremultipliedIdentityIsByteIdentical()
    {
        const uint pixel = 0x7F123456;

        Assert.Equal(pixel, BaseVisualTransformMath.ApplyPremultipliedBgra(pixel, default));
    }

    [Fact]
    public void SoftwareMathMatchesCoreReference()
    {
        BaseVisualTransform[] transforms =
        [
            new() { HueDegrees = -90f },
            new() { Saturation = 0.35f },
            new() { RedGain = 0f, GreenGain = 1.5f, BlueGain = 0.5f },
            new() { Brightness = 0.2f, Contrast = 1.5f },
            new()
            {
                RedGain = 0.75f,
                GreenGain = 1.1f,
                BlueGain = 1.35f,
                HueDegrees = 45f,
                Brightness = -0.1f,
                Contrast = 1.25f,
                Saturation = 1.4f
            }
        ];
        (byte Blue, byte Green, byte Red, byte Alpha)[] pixels =
        [
            (0, 0, 0, 0),
            (64, 128, 192, 128),
            (240, 20, 100, 255)
        ];

        foreach (BaseVisualTransform transform in transforms)
        {
            foreach ((byte blue, byte green, byte red, byte alpha) in pixels)
            {
                (float transformedRed, float transformedGreen, float transformedBlue) = BaseVisualTransformMath.ApplyStraight(
                    red / 255f,
                    green / 255f,
                    blue / 255f,
                    transform);
                var normalizedAlpha = alpha / 255f;
                var expected = (
                    Blue: BaseVisualTransformMath.Quantize(transformedBlue * normalizedAlpha),
                    Green: BaseVisualTransformMath.Quantize(transformedGreen * normalizedAlpha),
                    Red: BaseVisualTransformMath.Quantize(transformedRed * normalizedAlpha),
                    Alpha: alpha);

                Assert.Equal(
                    expected,
                    SoftwareBaseVisualTransform.ApplyStraightToPremultiplied(blue, green, red, alpha, transform));
            }
        }
    }

    [Fact]
    public void SoftwarePremultipliedMathMatchesCoreReference()
    {
        var transform = new BaseVisualTransform
        {
            RedGain = 0.5f,
            GreenGain = 1.25f,
            BlueGain = 0f,
            Contrast = 1.2f,
            Brightness = 0.05f,
            IncludeOverlays = true
        };
        const uint pixel = 0x80604020;
        uint expected = BaseVisualTransformMath.ApplyPremultipliedBgra(pixel, transform);

        (byte blue, byte green, byte red, byte alpha) = SoftwareBaseVisualTransform.ApplyPremultipliedToPremultiplied(
            blue: 0x20,
            green: 0x40,
            red: 0x60,
            alpha: 0x80,
            transform);

        Assert.Equal(expected, ((uint)alpha << 24) | ((uint)red << 16) | ((uint)green << 8) | blue);
    }
}
