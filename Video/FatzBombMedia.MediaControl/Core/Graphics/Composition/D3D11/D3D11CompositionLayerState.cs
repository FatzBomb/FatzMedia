using System.Diagnostics;
using FatzBombMedia.MediaControl.Core.DxCdg;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Retained per-layer state. Stage 10B attached GPU-side resources to Layer 0;
/// Stage 10C also retains DXCDG Layer 2 index/palette textures by layer identity;
/// Stage 10D retains Layer 1 wash/color state by layer identity instead of drawing
/// directly from the frame compatibility field; Stage 10E records per-layer
/// invalidation metadata for future scheduling without changing draw behavior;
/// Stage 10G hardens layer lifecycle/deactivation reporting; Stage 10H/10I adds
/// transition guardrails and update-planner metadata; Stage 10J/10K adds
/// future offscreen-target ownership and scheduling metadata without changing
/// current presentation behavior.
/// </summary>
internal sealed class D3D11CompositionLayerState : IDisposable
{
    public D3D11CompositionLayerState(MediaCompositionLayerId id)
    {
        Id = id;
    }

    public MediaCompositionLayerId Id { get; }
    public MediaCompositionLayer? Descriptor { get; private set; }
    public MediaCompositionLayerDirtyFlags DirtyFlags { get; private set; } = MediaCompositionLayerDirtyFlags.None;
    public long LastCapturedSequence { get; private set; } = -1;
    public long UploadedRevision { get; private set; } = -1;
    public long RenderedRevision { get; set; } = -1;
    public bool IsActive => Descriptor != null;
    public bool HasRetainedResources => HasTexture || HasDxCdgResources || SolidColorOpacity > 0d || HasOffscreenRenderTarget;
    public bool HadRetainedResourcesWhenDeactivated { get; private set; }
    public bool WasDeactivated => DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.Deactivated);
    public bool RequiresResourceAllocation => DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.ResourceAllocationChanged);
    public bool RequiresResourceRelease => DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.Deactivated) && HadRetainedResourcesWhenDeactivated;
    public bool NeedsUpload => Descriptor != null
        && (DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.UploadRequired)
            || (Descriptor.Revision >= 0 && Descriptor.Revision != UploadedRevision));

    public bool NeedsRender => Descriptor != null
        && (DirtyFlags.HasFlag(MediaCompositionLayerDirtyFlags.RenderRequired)
            || (Descriptor.Revision >= 0 && Descriptor.Revision != RenderedRevision));

    public D3D11CompositionLayerRenderTarget OffscreenRenderTarget { get; } = new();
    public D3D11CompositionLayerSchedule Schedule { get; private set; } = D3D11CompositionLayerSchedule.None;
    public bool HasOffscreenRenderTarget => OffscreenRenderTarget.IsAllocated;

    public D3D11CompositionLayerUpdate CreateUpdatePlanEntry()
    {
        var descriptor = Descriptor;
        var action = D3D11CompositionLayerUpdateAction.None;

        if (WasDeactivated)
        {
            action |= D3D11CompositionLayerUpdateAction.ReleaseResources;
        }
        else if (descriptor != null)
        {
            if (RequiresResourceAllocation)
            {
                action |= D3D11CompositionLayerUpdateAction.AllocateResources;
            }

            if (NeedsUpload)
            {
                action |= D3D11CompositionLayerUpdateAction.Upload;
            }

            if (NeedsRender)
            {
                action |= D3D11CompositionLayerUpdateAction.Render;
            }
        }

        return new D3D11CompositionLayerUpdate(
            Id,
            descriptor?.SourceKind ?? MediaCompositionLayerSourceKind.None,
            descriptor?.Order ?? int.MaxValue,
            descriptor?.Revision ?? -1,
            DirtyFlags,
            action,
            Schedule,
            WasDeactivated ? HadRetainedResourcesWhenDeactivated : HasRetainedResources,
            HasOffscreenRenderTarget,
            LastCapturedSequence);
    }

    public ID3D11Texture2D? Texture { get; private set; }
    public ID3D11ShaderResourceView? ShaderResourceView { get; private set; }
    public int TextureWidth { get; private set; }
    public int TextureHeight { get; private set; }
    public int TextureStride { get; private set; }
    public MediaCompositionStretchMode StretchMode { get; private set; } = MediaCompositionStretchMode.UniformFit;

    public ID3D11Texture2D? CdgIndexTexture { get; private set; }
    public ID3D11ShaderResourceView? CdgIndexShaderResourceView { get; private set; }
    public ID3D11Texture2D? CdgPaletteTexture { get; private set; }
    public ID3D11ShaderResourceView? CdgPaletteShaderResourceView { get; private set; }
    public long CdgPaletteUploadedRevision { get; private set; } = -1;

    public uint SolidColorArgb { get; private set; }
    public double SolidColorOpacity { get; private set; }

    public bool HasTexture => Texture != null
        && ShaderResourceView != null
        && TextureWidth > 0
        && TextureHeight > 0;

    public bool HasDxCdgResources => CdgIndexTexture != null
        && CdgIndexShaderResourceView != null
        && CdgPaletteTexture != null
        && CdgPaletteShaderResourceView != null;

    public bool HasSolidColor => Descriptor?.SourceKind == MediaCompositionLayerSourceKind.SolidColor
        && SolidColorOpacity > 0d
        && ((SolidColorArgb >> 24) & 0xFF) != 0;

    public void Update(MediaCompositionLayer descriptor, long captureSequence = -1)
    {
        HadRetainedResourcesWhenDeactivated = false;
        DirtyFlags = ComputeDirtyFlags(Descriptor, descriptor);
        if (captureSequence >= 0)
        {
            LastCapturedSequence = captureSequence;
        }

        Schedule = D3D11CompositionLayerSchedule.From(descriptor);

        // Keep the retained descriptor metadata, but do not retain transient frame payloads.
        Descriptor = descriptor.WithoutTransientPayload();
        if (descriptor.StretchMode != MediaCompositionStretchMode.Auto)
        {
            StretchMode = descriptor.StretchMode;
        }

        if (descriptor.SourceKind == MediaCompositionLayerSourceKind.SolidColor)
        {
            SolidColorArgb = descriptor.ColorArgb;
            SolidColorOpacity = descriptor.Opacity;
            MarkUploaded(descriptor.Revision);
        }
        else
        {
            SolidColorArgb = 0;
            SolidColorOpacity = 0d;
        }
    }

    public bool UploadBgraPixels(ID3D11Device device, ID3D11DeviceContext context, MediaCompositionLayer layer)
    {
        if (layer.SourceKind != MediaCompositionLayerSourceKind.BgraPixels || layer.BgraPixels.IsEmpty)
        {
            return false;
        }

        return UploadBgraPixels(
            device,
            context,
            layer.BgraPixels.Span,
            layer.Width,
            layer.Height,
            layer.Stride,
            layer.StretchMode,
            layer.Revision);
    }

    public bool UploadBgraPixels(
        ID3D11Device device,
        ID3D11DeviceContext context,
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        MediaCompositionStretchMode stretchMode,
        long revision)
    {
        if (bgraPixels.IsEmpty || width <= 0 || height <= 0 || stride < width * 4)
        {
            return false;
        }

        var required = checked(stride * height);
        if (bgraPixels.Length < required)
        {
            return false;
        }

        try
        {
            EnsureTexture(device, width, height, stride, BindFlags.ShaderResource | BindFlags.RenderTarget);
            if (Texture == null)
            {
                return false;
            }

            context.UpdateSubresource(bgraPixels[..required], Texture, 0, (uint)stride, 0, null);
            MarkUploaded(revision);
            TextureWidth = width;
            TextureHeight = height;
            TextureStride = stride;
            StretchMode = stretchMode == MediaCompositionStretchMode.Auto ? MediaCompositionStretchMode.UniformFit : stretchMode;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Layer {Id} BGRA upload failed: {ex.GetType().Name}: {ex.Message}");
            ReleaseResources();
            return false;
        }
    }

    public bool EnsureRenderTargetTexture(ID3D11Device device, int width, int height, int stride, MediaCompositionStretchMode stretchMode)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return false;
        }

        try
        {
            EnsureTexture(device, width, height, stride, BindFlags.ShaderResource | BindFlags.RenderTarget);
            StretchMode = stretchMode == MediaCompositionStretchMode.Auto ? MediaCompositionStretchMode.UniformFit : stretchMode;
            return HasTexture;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Layer {Id} render-target allocation failed: {ex.GetType().Name}: {ex.Message}");
            ReleaseResources();
            return false;
        }
    }

    public bool UploadDxCdgSnapshot(ID3D11Device device, ID3D11DeviceContext context, MediaCompositionLayer layer)
    {
        if (layer.SourceKind != MediaCompositionLayerSourceKind.DxCdgSnapshot || layer.CdgSnapshot == null)
        {
            return false;
        }

        return UploadDxCdgSnapshot(device, context, layer.CdgSnapshot);
    }

    public bool UploadDxCdgSnapshot(ID3D11Device device, ID3D11DeviceContext context, DxCdgRenderSnapshot snapshot)
    {
        if (snapshot.ColorIndices.Length < DxCdgConstants.TotalPixels
            || snapshot.PaletteArgb.Length < DxCdgConstants.ColorCount)
        {
            return false;
        }

        try
        {
            EnsureDxCdgTextures(device);
            if (!HasDxCdgResources || CdgIndexTexture == null || CdgPaletteTexture == null)
            {
                return false;
            }

            if (UploadedRevision != snapshot.Revision)
            {
                ReadOnlySpan<byte> indices = snapshot.ColorIndices.Span;
                context.UpdateSubresource(indices[..DxCdgConstants.TotalPixels], CdgIndexTexture, 0, DxCdgConstants.FrameWidth, 0, null);
                MarkUploaded(snapshot.Revision);
            }

            if (CdgPaletteUploadedRevision != snapshot.Revision)
            {
                Span<byte> paletteBgra = stackalloc byte[DxCdgConstants.ColorCount * 4];
                ReadOnlySpan<int> palette = snapshot.PaletteArgb.Span;
                for (var i = 0; i < DxCdgConstants.ColorCount; i++)
                {
                    var argb = unchecked((uint)palette[i]);
                    var offset = i * 4;
                    paletteBgra[offset + 0] = (byte)(argb & 0xFF);
                    paletteBgra[offset + 1] = (byte)((argb >> 8) & 0xFF);
                    paletteBgra[offset + 2] = (byte)((argb >> 16) & 0xFF);
                    paletteBgra[offset + 3] = (byte)((argb >> 24) & 0xFF);
                }

                context.UpdateSubresource(paletteBgra, CdgPaletteTexture, 0, DxCdgConstants.ColorCount * 4, 0, null);
                CdgPaletteUploadedRevision = snapshot.Revision;
            }

            TextureWidth = DxCdgConstants.FrameWidth;
            TextureHeight = DxCdgConstants.FrameHeight;
            TextureStride = DxCdgConstants.FrameWidth;
            StretchMode = MediaCompositionStretchMode.UniformFit;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Layer {Id} DXCDG upload failed: {ex.GetType().Name}: {ex.Message}");
            ReleaseCdgResources();
            return false;
        }
    }


    public void MarkUploaded(long revision)
    {
        UploadedRevision = revision;
        DirtyFlags &= ~MediaCompositionLayerDirtyFlags.UploadRequired;
    }

    public void MarkRendered(long revision)
    {
        RenderedRevision = revision;
        DirtyFlags &= ~MediaCompositionLayerDirtyFlags.RenderRequired;
    }


    public void MarkPlanConsumed()
    {
        if (WasDeactivated || Descriptor == null)
        {
            DirtyFlags = MediaCompositionLayerDirtyFlags.None;
            HadRetainedResourcesWhenDeactivated = false;
            return;
        }

        var needsUpload = NeedsUpload;
        var needsRender = NeedsRender;
        var remainingFlags = MediaCompositionLayerDirtyFlags.None;
        if (needsUpload)
        {
            remainingFlags |= MediaCompositionLayerDirtyFlags.UploadRequired;
        }

        if (needsRender)
        {
            remainingFlags |= MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        DirtyFlags = remainingFlags;
    }

    public void Clear(long captureSequence = -1)
    {
        HadRetainedResourcesWhenDeactivated = HasRetainedResources;
        var wasActive = Descriptor != null || HadRetainedResourcesWhenDeactivated;
        Descriptor = null;
        DirtyFlags = wasActive ? MediaCompositionLayerDirtyFlags.Deactivated : MediaCompositionLayerDirtyFlags.None;
        if (captureSequence >= 0)
        {
            LastCapturedSequence = captureSequence;
        }

        UploadedRevision = -1;
        RenderedRevision = -1;
        CdgPaletteUploadedRevision = -1;
        SolidColorArgb = 0;
        SolidColorOpacity = 0d;
        Schedule = D3D11CompositionLayerSchedule.None;
        ReleaseResources();
        ReleaseCdgResources();
        OffscreenRenderTarget.Release();
    }

    public string LifecycleSummary
    {
        get
        {
            if (IsActive && Descriptor != null)
            {
                return $"{Descriptor}; dirty={DirtyFlags}; uploaded={UploadedRevision}; rendered={RenderedRevision}; schedule={Schedule}; {OffscreenRenderTarget}; capture={LastCapturedSequence}";
            }

            return WasDeactivated
                ? $"{Id}; dirty={DirtyFlags}; capture={LastCapturedSequence}"
                : $"{Id}; inactive";
        }
    }

    public void Dispose() => Clear();

    private static MediaCompositionLayerDirtyFlags ComputeDirtyFlags(MediaCompositionLayer? previous, MediaCompositionLayer current)
    {
        if (previous == null)
        {
            return MediaCompositionLayerDirtyFlags.Activated
                | MediaCompositionLayerDirtyFlags.ContentChanged
                | MediaCompositionLayerDirtyFlags.SizeChanged
                | MediaCompositionLayerDirtyFlags.ResourceAllocationChanged
                | MediaCompositionLayerDirtyFlags.UploadRequired
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        var flags = MediaCompositionLayerDirtyFlags.None;

        if (previous.SourceKind != current.SourceKind)
        {
            flags |= MediaCompositionLayerDirtyFlags.SourceKindChanged
                | MediaCompositionLayerDirtyFlags.ResourceAllocationChanged
                | MediaCompositionLayerDirtyFlags.UploadRequired
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (previous.Revision != current.Revision
            || previous.D3D11TexturePointer != current.D3D11TexturePointer
            || previous.D3D11ArraySlice != current.D3D11ArraySlice)
        {
            flags |= MediaCompositionLayerDirtyFlags.ContentChanged
                | MediaCompositionLayerDirtyFlags.UploadRequired
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (previous.Width != current.Width || previous.Height != current.Height)
        {
            flags |= MediaCompositionLayerDirtyFlags.SizeChanged
                | MediaCompositionLayerDirtyFlags.ResourceAllocationChanged
                | MediaCompositionLayerDirtyFlags.UploadRequired
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (previous.Stride != current.Stride)
        {
            flags |= MediaCompositionLayerDirtyFlags.StrideChanged
                | MediaCompositionLayerDirtyFlags.UploadRequired
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (previous.StretchMode != current.StretchMode)
        {
            flags |= MediaCompositionLayerDirtyFlags.StretchChanged
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (!NearlyEquals(previous.FramesPerSecond, current.FramesPerSecond))
        {
            flags |= MediaCompositionLayerDirtyFlags.FrameRateChanged
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (!NearlyEquals(previous.Opacity, current.Opacity))
        {
            flags |= MediaCompositionLayerDirtyFlags.OpacityChanged
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        if (previous.ColorArgb != current.ColorArgb)
        {
            flags |= MediaCompositionLayerDirtyFlags.ColorChanged
                | MediaCompositionLayerDirtyFlags.RenderRequired;
        }

        return flags;
    }

    private static bool NearlyEquals(double left, double right)
        => Math.Abs(left - right) < 0.000001d;

    private void EnsureTexture(ID3D11Device device, int width, int height, int stride, BindFlags bindFlags)
    {
        if (Texture != null
            && ShaderResourceView != null
            && TextureWidth == width
            && TextureHeight == height)
        {
            TextureStride = stride;
            return;
        }

        ReleaseResources();

        var description = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        Texture = device.CreateTexture2D(in description);
        ShaderResourceView = device.CreateShaderResourceView(Texture);
        TextureWidth = width;
        TextureHeight = height;
        TextureStride = stride;
    }

    private void EnsureDxCdgTextures(ID3D11Device device)
    {
        if (HasDxCdgResources)
        {
            return;
        }

        ReleaseCdgResources();

        var indexDescription = new Texture2DDescription(
            Format.R8_UNorm,
            DxCdgConstants.FrameWidth,
            DxCdgConstants.FrameHeight,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        CdgIndexTexture = device.CreateTexture2D(in indexDescription);
        CdgIndexShaderResourceView = device.CreateShaderResourceView(CdgIndexTexture);

        var paletteDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            DxCdgConstants.ColorCount,
            1,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        CdgPaletteTexture = device.CreateTexture2D(in paletteDescription);
        CdgPaletteShaderResourceView = device.CreateShaderResourceView(CdgPaletteTexture);
    }

    private void ReleaseResources()
    {
        ShaderResourceView?.Dispose();
        ShaderResourceView = null;
        Texture?.Dispose();
        Texture = null;
        TextureWidth = 0;
        TextureHeight = 0;
        TextureStride = 0;
        StretchMode = MediaCompositionStretchMode.UniformFit;
    }

    private void ReleaseCdgResources()
    {
        CdgIndexShaderResourceView?.Dispose();
        CdgIndexShaderResourceView = null;
        CdgIndexTexture?.Dispose();
        CdgIndexTexture = null;
        CdgPaletteShaderResourceView?.Dispose();
        CdgPaletteShaderResourceView = null;
        CdgPaletteTexture?.Dispose();
        CdgPaletteTexture = null;
        CdgPaletteUploadedRevision = -1;
    }
}
