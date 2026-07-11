using System.Numerics;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;

namespace FatzBombMedia.MediaControl.Core.Subtitle;

internal sealed class SubtitleTextRenderer : IDisposable
{
    // Presentation may request a fresh cue from multiple scheduler paths; the coordinator serializes
    // calls, and the factory is multi-threaded to keep that usage valid if a future preview path is added.
    private readonly ID2D1Factory1 _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(Vortice.Direct2D1.FactoryType.MultiThreaded);
    private readonly IDWriteFactory _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    private readonly IWICImagingFactory _wicFactory = new();
    private readonly SubtitleTextStyle _style;

    public SubtitleTextRenderer(SubtitleTextStyle? style = null) => _style = (style ?? new SubtitleTextStyle()).Normalize();

    public byte[] Render(string text, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        using var bitmap = _wicFactory.CreateBitmap((uint)width, (uint)height, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var target = _d2dFactory.CreateWicBitmapRenderTarget(
            bitmap,
            new RenderTargetProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        using var format = _writeFactory.CreateTextFormat(_style.FontFamily, null, FontWeight.Bold, FontStyle.Normal, FontStretch.Normal, _style.FontSize, "");
        using var layout = _writeFactory.CreateTextLayout(text, format, width * 0.85f, height * 0.3f);
        layout.TextAlignment = TextAlignment.Center;
        using var textBrush = target.CreateSolidColorBrush(ToColor(_style.TextArgb));
        using var outlineBrush = target.CreateSolidColorBrush(ToColor(_style.OutlineArgb));
        using var shadowBrush = target.CreateSolidColorBrush(ToColor(_style.ShadowArgb));

        var metrics = layout.Metrics;
        var origin = new Vector2(
            width * 0.075f,
            Math.Max(0, height - metrics.Height - (height * 0.08f)));

        target.BeginDraw();
        target.Clear(new Color4(0f, 0f, 0f, 0f));
        if ((_style.ShadowArgb >> 24) != 0)
            target.DrawTextLayout(origin + new Vector2(_style.ShadowOffsetX, _style.ShadowOffsetY), layout, shadowBrush);

        if (_style.OutlineWidth > 0 && (_style.OutlineArgb >> 24) != 0)
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        target.DrawTextLayout(origin + new Vector2(x * _style.OutlineWidth, y * _style.OutlineWidth), layout, outlineBrush);
                }
            }
        }

        target.DrawTextLayout(origin, layout, textBrush);
        target.EndDraw();

        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels((uint)(width * 4), pixels);
        return pixels;
    }

    private static Color4 ToColor(uint argb) => new(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f);

    public void Dispose()
    {
        _wicFactory.Dispose();
        _writeFactory.Dispose();
        _d2dFactory.Dispose();
    }
}
