using System.Numerics;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics;

namespace FatzMedia.Video.Vulkan;

/// <summary>
/// The uniform block of <c>Shaders/video.frag</c> (std140, eight vec4): how a two-plane YCbCr
/// frame's samples become RGB, and for HDR10 and HLG the tone mapping to SDR. The same terms as
/// the Direct3D11 tone-map shader's constants (Stage 038A), so both renderers draw the same picture.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
struct VulkanVideoConstants
{
    /// <summary>xy: texture coordinates at the display's bottom-right edge (display / coded size); zw unused.</summary>
    public Vector4 TextureScale;

    /// <summary>Luma scale, luma offset, chroma scale, chroma offset: sample to normalized Y' and Cb/Cr.</summary>
    public Vector4 YuvScaleOffset;

    /// <summary>R = Y + x Cr; G = Y - y Cb - z Cr; B = Y + w Cb.</summary>
    public Vector4 YuvMatrix;

    /// <summary>Source peak (PQ signal), target / source peak (PQ), knee start, compress (1) or clip (0).</summary>
    public Vector4 Curve;

    /// <summary>1 / target nits, 1 / SDR gamma, target nits, unused.</summary>
    public Vector4 Output;

    /// <summary>Normalized sampling bounds one luma texel inside the picture: left, top, right, bottom.</summary>
    public Vector4 SampleClamp;

    /// <summary>HLG (1) or PQ (0), HLG nominal peak nits, HLG system gamma - 1, tone map (1) or SDR conversion only (0).</summary>
    public Vector4 Transfer;

    /// <summary>Reserved (keeps the block a whole number of vec4).</summary>
    public Vector4 Reserved;
}

/// <summary>Pure planning of the YCbCr-to-RGB conversion and HDR tone mapping of a native frame (tested).</summary>
static class VulkanVideoConversion
{
    /// <summary>
    /// The shader constants for a frame of <paramref name="bitDepth"/> bits per sample, shown
    /// at <paramref name="displayWidth"/> x <paramref name="displayHeight"/> from planes of the
    /// coded size. 8-bit planes are sampled as R8/R8G8 (code / 255); deeper ones are 16-bit
    /// containers with the code in the top bits (P010: code * 64 / 65535), sampled as R16/R16G16.
    /// </summary>
    public static VulkanVideoConstants Create(
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
        int bitDepth,
        int displayWidth,
        int displayHeight,
        int codedWidth,
        int codedHeight)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitDepth, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitDepth, 16);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayHeight);
        codedWidth = Math.Max(codedWidth, displayWidth);
        codedHeight = Math.Max(codedHeight, displayHeight);
        colorSpace = colorSpace.Resolve(displayWidth, displayHeight);

        // The code value one sample stands for, then the BT.601/709/2020 ranges for that depth.
        var codeScale = bitDepth == 8 ? 255.0 : 65535.0 / (1 << (16 - bitDepth));
        var step = (double)(1 << (bitDepth - 8));
        var maxCode = (1 << bitDepth) - 1;
        var full = colorSpace.Range == VideoColorRange.Full;
        var lumaScale = codeScale / (full ? maxCode : 219 * step);
        var lumaOffset = full ? 0 : -16 * step / (219 * step);
        var chromaScale = codeScale / (full ? maxCode : 224 * step);
        var chromaOffset = -128 * step / (full ? maxCode : 224 * step);

        (var kr, var kb) = colorSpace.Matrix switch
        {
            VideoColorMatrix.Bt601 => (0.299, 0.114),
            VideoColorMatrix.Bt709 => (0.2126, 0.0722),
            _ => (0.2627, 0.0593),
        };
        var kg = 1 - kr - kb;

        Vector4 curve = default;
        Vector4 output = new(1, 1, 1, 0);
        Vector4 transfer = default;
        if (toneMap is { } request)
        {
            var hlg = request.Transfer == HdrTransferFunction.Hlg;
            var sourcePeak = request.SourcePeakSignal;
            var maxLum = sourcePeak > 0 ? request.TargetPeakSignal / sourcePeak : 1;
            curve = new Vector4(
                (float)sourcePeak,
                (float)maxLum,
                (float)Math.Max((1.5 * maxLum) - 0.5, 0),
                request.SourcePeakNits > request.TargetPeakNits ? 1f : 0f);
            output = new Vector4(
                (float)(1 / request.TargetPeakNits),
                (float)(1 / HdrToneMapRequest.SdrGamma),
                (float)request.TargetPeakNits,
                0f);
            transfer = new Vector4(hlg ? 1f : 0f, (float)request.SourcePeakNits, (float)(request.HlgSystemGamma - 1), 1f);
        }

        return new VulkanVideoConstants
        {
            TextureScale = new Vector4((float)(displayWidth / (double)codedWidth), (float)(displayHeight / (double)codedHeight), 0, 0),
            YuvScaleOffset = new Vector4((float)lumaScale, (float)lumaOffset, (float)chromaScale, (float)chromaOffset),
            YuvMatrix = new Vector4(
                (float)(2 * (1 - kr)),
                (float)(2 * kb * (1 - kb) / kg),
                (float)(2 * kr * (1 - kr) / kg),
                (float)(2 * (1 - kb))),
            Curve = curve,
            Output = output,
            // As the Direct3D11 shader: half a chroma texel (one luma texel) inside the picture, so
            // bilinear taps never reach the decoder's padding rows and columns.
            SampleClamp = new Vector4(
                (float)(Math.Min(1, displayWidth - 1) / (double)codedWidth),
                (float)(Math.Min(1, displayHeight - 1) / (double)codedHeight),
                (float)(Math.Max(displayWidth - 1, 1) / (double)codedWidth),
                (float)(Math.Max(displayHeight - 1, 1) / (double)codedHeight)),
            Transfer = transfer,
        };
    }

    /// <summary>
    /// The normalized RGB the shader computes for one pixel's samples (tests compare it with the
    /// GPU and with swscale): SDR conversion only, clamped to [0, 1].
    /// </summary>
    public static (double R, double G, double B) ConvertSdr(in VulkanVideoConstants constants, double lumaSample, double cbSample, double crSample)
    {
        var y = (lumaSample * constants.YuvScaleOffset.X) + constants.YuvScaleOffset.Y;
        var cb = (cbSample * constants.YuvScaleOffset.Z) + constants.YuvScaleOffset.W;
        var cr = (crSample * constants.YuvScaleOffset.Z) + constants.YuvScaleOffset.W;
        return (
            Math.Clamp(y + (constants.YuvMatrix.X * cr), 0, 1),
            Math.Clamp(y - (constants.YuvMatrix.Y * cb) - (constants.YuvMatrix.Z * cr), 0, 1),
            Math.Clamp(y + (constants.YuvMatrix.W * cb), 0, 1));
    }
}
