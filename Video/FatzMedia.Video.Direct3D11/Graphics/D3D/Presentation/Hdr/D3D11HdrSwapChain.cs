using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Video.Hdr;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;

/// <summary>
/// Switches a flip-model swap chain between SDR (<c>B8G8R8A8_UNORM</c>, sRGB) and scRGB
/// (<c>R16G16B16A16_FLOAT</c>, <c>RGB_FULL_G10_NONE_P709</c>) output and sets its HDR10 metadata
/// (Stage 042A slice 2). The caller releases every back-buffer reference first.
/// </summary>
internal static class D3D11HdrSwapChain
{
    public const Format ScRgbFormat = Format.R16G16B16A16_Float;
    public const Format SdrFormat = Format.B8G8R8A8_UNorm;
    public const ColorSpaceType ScRgbColorSpace = ColorSpaceType.RgbFullG10NoneP709;
    public const ColorSpaceType SdrColorSpace = ColorSpaceType.RgbFullG22NoneP709;

    /// <summary>The output a target texture holds: scRGB when it is FP16.</summary>
    public static HdrOutputMode ModeOf(ID3D11Texture2D? texture) =>
        texture != null && texture.Description.Format == ScRgbFormat ? HdrOutputMode.ScRgb : HdrOutputMode.Sdr;

    /// <summary>
    /// Resizes the buffers of <paramref name="swapChain"/> into <paramref name="mode"/>'s format at
    /// <paramref name="width"/> x <paramref name="height"/>, then sets its color space and, for
    /// scRGB, <paramref name="metadata"/>. On a failure after the format changed it goes back to
    /// SDR; <paramref name="failure"/> says what failed.
    /// </summary>
    public static bool TryApply(IDXGISwapChain swapChain, int width, int height, HdrOutputMode mode, Hdr10MetadataValues? metadata, out string? failure)
    {
        try
        {
            using IDXGISwapChain3? swapChain3 = swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
            if (swapChain3 == null)
            {
                failure = "the swap chain has no color-space control (IDXGISwapChain3; Windows 10 or later)";
                return false;
            }

            // GetDesc1: GetDesc is the legacy call and is not meant for composition swap chains.
            if (mode == HdrOutputMode.ScRgb && !IsFlipModel(swapChain3.Description1.SwapEffect))
            {
                failure = "the legacy (non-flip-model) swap chain cannot present HDR";
                return false;
            }

            Format format = mode == HdrOutputMode.ScRgb ? ScRgbFormat : SdrFormat;
            Result resize = swapChain.ResizeBuffers(0, (uint)Math.Max(1, width), (uint)Math.Max(1, height), format, SwapChainFlags.None);
            if (resize.Failure)
            {
                failure = $"ResizeBuffers to {format} failed: {resize}";
                return false;
            }

            ColorSpaceType colorSpace = mode == HdrOutputMode.ScRgb ? ScRgbColorSpace : SdrColorSpace;
            if (!SupportsPresent(swapChain3, colorSpace))
            {
                failure = $"the swap chain cannot present {colorSpace} with {format}";
                if (mode == HdrOutputMode.ScRgb)
                    RevertToSdr(swapChain, swapChain3, width, height);
                return false;
            }

            swapChain3.SetColorSpace1(colorSpace);
            SetMetadata(swapChain, mode == HdrOutputMode.ScRgb ? metadata : null);
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"{exception.GetType().Name}: {exception.Message}";
            if (mode == HdrOutputMode.ScRgb)
            {
                try
                {
                    using IDXGISwapChain3? swapChain3 = swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
                    if (swapChain3 != null)
                        RevertToSdr(swapChain, swapChain3, width, height);
                }
                catch (Exception)
                {
                    // The caller reads the buffer's actual format; it presents whatever is left.
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Sets the scRGB color space again after a resize kept the FP16 format (best effort; DXGI
    /// keeps it, this only makes sure).
    /// </summary>
    public static void ReapplyScRgbColorSpace(IDXGISwapChain swapChain)
    {
        try
        {
            using IDXGISwapChain3? swapChain3 = swapChain.QueryInterfaceOrNull<IDXGISwapChain3>();
            swapChain3?.SetColorSpace1(ScRgbColorSpace);
        }
        catch (Exception)
        {
            // The color space set when the format changed stays.
        }
    }

    /// <summary>
    /// Sets HDR10 metadata (null clears it). Best effort: the metadata is a hint DWM and the
    /// display may use; an older DXGI without <c>IDXGISwapChain4</c> or a failure changes nothing
    /// else.
    /// </summary>
    public static unsafe void SetMetadata(IDXGISwapChain swapChain, Hdr10MetadataValues? metadata)
    {
        try
        {
            using IDXGISwapChain4? swapChain4 = swapChain.QueryInterfaceOrNull<IDXGISwapChain4>();
            if (swapChain4 == null)
                return;

            if (metadata is not { } values)
            {
                swapChain4.SetHDRMetaData(HdrMetadataType.None, 0, IntPtr.Zero);
                return;
            }

            DxgiHdrMetadataHdr10 native = ToNative(values);
            swapChain4.SetHDRMetaData(HdrMetadataType.Hdr10, (uint)sizeof(DxgiHdrMetadataHdr10), (IntPtr)(&native));
        }
        catch (Exception)
        {
            // Metadata is optional for scRGB output.
        }
    }

    internal static DxgiHdrMetadataHdr10 ToNative(Hdr10MetadataValues values) => new()
    {
        RedX = values.RedX,
        RedY = values.RedY,
        GreenX = values.GreenX,
        GreenY = values.GreenY,
        BlueX = values.BlueX,
        BlueY = values.BlueY,
        WhiteX = values.WhiteX,
        WhiteY = values.WhiteY,
        MaxMasteringLuminance = values.MaxMasteringLuminance,
        MinMasteringLuminance = values.MinMasteringLuminance,
        MaxContentLightLevel = values.MaxContentLightLevel,
        MaxFrameAverageLightLevel = values.MaxFrameAverageLightLevel,
    };

    private static bool IsFlipModel(SwapEffect swapEffect) =>
        swapEffect is SwapEffect.FlipSequential or SwapEffect.FlipDiscard;

    private static bool SupportsPresent(IDXGISwapChain3 swapChain3, ColorSpaceType colorSpace) =>
        (swapChain3.CheckColorSpaceSupport(colorSpace) & SwapChainColorSpaceSupportFlags.Present) != 0;

    private static void RevertToSdr(IDXGISwapChain swapChain, IDXGISwapChain3 swapChain3, int width, int height)
    {
        if (swapChain.ResizeBuffers(0, (uint)Math.Max(1, width), (uint)Math.Max(1, height), SdrFormat, SwapChainFlags.None).Success)
        {
            swapChain3.SetColorSpace1(SdrColorSpace);
            SetMetadata(swapChain, null);
        }
    }

    /// <summary><c>DXGI_HDR_METADATA_HDR10</c>: 28 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DxgiHdrMetadataHdr10
    {
        public ushort RedX;
        public ushort RedY;
        public ushort GreenX;
        public ushort GreenY;
        public ushort BlueX;
        public ushort BlueY;
        public ushort WhiteX;
        public ushort WhiteY;
        public uint MaxMasteringLuminance;
        public uint MinMasteringLuminance;
        public ushort MaxContentLightLevel;
        public ushort MaxFrameAverageLightLevel;
    }
}
