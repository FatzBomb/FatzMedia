namespace FatzMedia.Video.Core.Graphics.Composition;

public readonly partial record struct MediaCompositionFrame
{
    /// <summary>
    /// Enumerates the retained-layer intents represented by this frame in final draw order.
    /// Stage 10A keeps existing compatibility properties intact; this gives the D3D compositor
    /// a stable descriptor stream to start caching against without changing Stage 9 behavior.
    /// </summary>
    public IEnumerable<MediaCompositionLayer> EnumerateLayers()
    {
        if (TryGetBackgroundLayer() is { } background)
            yield return background;

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

        if (TryGetOverlayLayer() is { } overlay)
            yield return overlay;

        if (TryGetSubtitleLayer() is { } subtitle)
            yield return subtitle;
    }

    static long StableColorRevision(uint argb)
        => unchecked((long)(ulong)argb);

    public MediaCompositionLayer? TryGetBackgroundLayer() =>
        HasBackground
            ? new MediaCompositionLayer
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
                BaseVisualTransform = BaseVisualTransform,
                BgraPixels = BackgroundBgra
            }
            : HasNativeBackground
                ? new MediaCompositionLayer
                {
                    Id = MediaCompositionLayerId.Background,
                    Kind = MediaCompositionLayerKind.Background,
                    SourceKind = MediaCompositionLayerSourceKind.NativeTexture,
                    Order = 0,
                    Revision = BackgroundRevision,
                    Width = BackgroundNativeWidth,
                    Height = BackgroundNativeHeight,
                    Stride = BackgroundNativeWidth > 0 ? BackgroundNativeWidth * 4 : 0,
                    StretchMode = EffectiveBackgroundStretchMode,
                    Opacity = 1.0d,
                    BaseVisualTransform = BaseVisualTransform,
                    NativeResource = BackgroundNativeResource,
                    FramesPerSecond = BackgroundNativeFramesPerSecond
                }
                : HasRetainedBackground
                    ? new MediaCompositionLayer
                    {
                        Id = MediaCompositionLayerId.Background,
                        Kind = MediaCompositionLayerKind.Background,
                        SourceKind = MediaCompositionLayerSourceKind.RetainedNativeTexture,
                        Order = 0,
                        Revision = BackgroundRevision,
                        Width = BackgroundWidth,
                        Height = BackgroundHeight,
                        Stride = BackgroundStride,
                        StretchMode = EffectiveBackgroundStretchMode,
                        Opacity = 1.0d,
                        BaseVisualTransform = BaseVisualTransform
                    }
                    : null;

    public MediaCompositionLayer? TryGetOverlayLayer()
    {
        var overlay = CdgOverlay;
        return HasCdgOverlay && overlay != null
            ? new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Overlay,
                Kind = MediaCompositionLayerKind.Overlay,
                SourceKind = MediaCompositionLayerSourceKind.CdgSnapshot,
                Order = 2,
                Revision = overlay.Revision,
                Width = overlay.Width,
                Height = overlay.Height,
                Stride = overlay.Width,
                StretchMode = MediaCompositionStretchMode.UniformFit,
                Opacity = 1.0d,
                CdgSnapshot = overlay
            }
            : null;
    }

    public MediaCompositionLayer? TryGetSubtitleLayer() =>
        HasSubtitleOverlay
            ? new MediaCompositionLayer
            {
                Id = MediaCompositionLayerId.Subtitle,
                Kind = MediaCompositionLayerKind.Overlay,
                SourceKind = MediaCompositionLayerSourceKind.BgraPixels,
                Order = 3,
                Revision = SubtitleRevision,
                Width = SubtitleWidth,
                Height = SubtitleHeight,
                Stride = SubtitleStride,
                CanvasLeft = SubtitleCanvasLeft,
                CanvasTop = SubtitleCanvasTop,
                CanvasWidth = SubtitleCanvasWidth,
                CanvasHeight = SubtitleCanvasHeight,
                StretchMode = MediaCompositionStretchMode.UniformFit,
                Opacity = 1.0d,
                BgraPixels = SubtitleBgra
            }
            : null;
}
