using FatzBombMedia.MediaControl.Core.DxCdg;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

public readonly partial record struct MediaCompositionFrame
{
    /// <summary>
    /// Enumerates the retained-layer intents represented by this frame in final draw order.
    /// Stage 10A keeps existing compatibility properties intact; this gives the D3D compositor
    /// a stable descriptor stream to start caching against without changing Stage 9 behavior.
    /// </summary>
    public IEnumerable<MediaCompositionLayer> EnumerateLayers()
    {
        if (HasBackground)
        {
            yield return new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Background,
                Kind = MediaCompositionLayerKind.Background,
                SourceKind = MediaCompositionLayerSourceKind.BgraPixels,
                Order = 0,
                Revision = BackgroundRevision,
                Width = BackgroundWidth,
                Height = BackgroundHeight,
                Stride = BackgroundStride,
                StretchMode = EffectiveBackgroundStretchMode,
                Opacity = 1.0d,
                BgraPixels = BackgroundBgra
            };
        }
        else if (HasD3D11Background)
        {
            yield return new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Background,
                Kind = MediaCompositionLayerKind.Background,
                SourceKind = MediaCompositionLayerSourceKind.D3D11Texture,
                Order = 0,
                Revision = BackgroundRevision,
                Width = BackgroundD3D11Width,
                Height = BackgroundD3D11Height,
                Stride = BackgroundD3D11Width > 0 ? BackgroundD3D11Width * 4 : 0,
                StretchMode = EffectiveBackgroundStretchMode,
                Opacity = 1.0d,
                D3D11TexturePointer = BackgroundD3D11TexturePointer,
                D3D11ArraySlice = BackgroundD3D11ArraySlice,
                FramesPerSecond = BackgroundD3D11FramesPerSecond
            };
        }
        else if (HasRetainedBackground)
        {
            yield return new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Background,
                Kind = MediaCompositionLayerKind.Background,
                SourceKind = MediaCompositionLayerSourceKind.RetainedD3D11Texture,
                Order = 0,
                Revision = BackgroundRevision,
                Width = BackgroundWidth,
                Height = BackgroundHeight,
                Stride = BackgroundStride,
                StretchMode = EffectiveBackgroundStretchMode,
                Opacity = 1.0d
            };
        }

        if (HasWash)
        {
            yield return new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Wash,
                Kind = MediaCompositionLayerKind.Wash,
                SourceKind = MediaCompositionLayerSourceKind.SolidColor,
                Order = 1,
                Revision = StableColorRevision(WashArgb),
                Width = 0,
                Height = 0,
                Stride = 0,
                StretchMode = MediaCompositionStretchMode.Fill,
                Opacity = ((WashArgb >> 24) & 0xFF) / 255.0d,
                ColorArgb = WashArgb
            };
        }

        DxCdgRenderSnapshot? overlay = CdgOverlay;
        if (HasCdgOverlay && overlay != null)
        {
            yield return new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Overlay,
                Kind = MediaCompositionLayerKind.Overlay,
                SourceKind = MediaCompositionLayerSourceKind.DxCdgSnapshot,
                Order = 2,
                Revision = overlay.Revision,
                Width = overlay.Width,
                Height = overlay.Height,
                Stride = overlay.Width,
                StretchMode = MediaCompositionStretchMode.UniformFit,
                Opacity = 1.0d,
                CdgSnapshot = overlay
            };
        }
    }

    public MediaCompositionLayer[] CreateLayerDescriptors()
        => [.. EnumerateLayers()];

    public string LayerSummary
    {
        get
        {
            MediaCompositionLayer[] layers = CreateLayerDescriptors();
            return layers.Length == 0 ? "none" : string.Join(",", layers.Select(static layer => layer.ToString()));
        }
    }

    private static long StableColorRevision(uint argb)
        => unchecked((long)(ulong)argb);
}
