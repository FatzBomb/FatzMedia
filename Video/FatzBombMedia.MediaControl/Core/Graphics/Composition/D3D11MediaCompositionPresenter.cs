using FatzBombMedia.MediaControl.Core.Cdg;
using FatzBombMedia.MediaControl.Core.Graphics.Composition.D3D11;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using FatzBombMedia.MediaControl.Core.Graphics.Geometry;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace FatzBombMedia.MediaControl.Core.Graphics.Composition;

/// <summary>
/// D3D11 implementation of the single media compositor. It draws an optional
/// background/base layer, optional wash layer, optional CDG foreground overlay, then presents once.
/// </summary>
internal sealed partial class D3D11MediaCompositionPresenter(bool enableLayerDiagnostics = false, bool enableCompositionDiagnostics = false)
{
    private const int CompositionDiagnosticInterval = 120;

    private readonly D3D11CompositionLayerCache _layerCache = new();
    private readonly D3D11CompositionTargetRegistry _targetRegistry = new();
    private readonly List<D3D11PresentationTargetSnapshot> _scratchValidTargets = [];
    private readonly List<D3D11PresentationTargetSnapshot> _scratchPresentationTargets = [];

    private ID3D11PixelShader? _backgroundPixelShader;
    private ID3D11PixelShader? _solidPixelShader;
    private ID3D11PixelShader? _premultipliedSolidPixelShader;

    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Buffer? _constantBuffer;
    private static readonly int CdgShaderConstantsSize = Marshal.SizeOf<CdgShaderConstants>();
    private ID3D11SamplerState? _samplerState;
    private ID3D11BlendState? _blendState;
    private ID3D11BlendState? _premultipliedBlendState;
    private ID3D11RasterizerState? _rasterizerState;
    private long _lastDrawnRevision = -1;
    private string? _lastLayerDiagnosticSummary;
    private bool _pipelineFailed;
    private int _compositionPasses;
    private int _compositionDrawnTargets;
    private int _compositionLayerUploads;
    private int _compositionLayerRenders;
    private int _compositionRetainedReuses;
    private int _compositionTargetResizes;
    private int _compositionSkippedRetainedNoOps;
    private int _compositionCdgOverlayOnlyPasses;
    private int _compositionCdgOverlayOnlyDrawnTargets;
    private double _compositionMs;
    private double _compositionMaxMs;
    private string? _lastCompositionReason;

    public bool IsReady => _layerCache.TryGet(MediaCompositionLayerId.Overlay)?.HasCdgResources == true;

    public void Reset()
    {
        _lastDrawnRevision = -1;
        _lastLayerDiagnosticSummary = null;
        ResetCompositionDiagnostics();
        _pipelineFailed = false;
        _layerCache.Reset();
        _targetRegistry.Reset();
        _scratchValidTargets.Clear();
        _scratchPresentationTargets.Clear();
        ReleaseResources();
    }

    public void SetBackgroundFrame(
        D3D11DeviceResources deviceResources,
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        bool forceZoomToFill)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || bgraPixels.IsEmpty
            || width <= 0
            || height <= 0
            || stride < width * 4)
        {
            ClearBackgroundFrame();
            return;
        }

        MediaCompositionStretchMode stretchMode = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : MediaCompositionStretchMode.UniformFit;
        D3D11CompositionLayerState backgroundLayer = _layerCache.GetOrCreate(MediaCompositionLayerId.Background);
        backgroundLayer.Update(new MediaCompositionLayer
        {
            Id = MediaCompositionLayerId.Background,
            Kind = MediaCompositionLayerKind.Background,
            SourceKind = MediaCompositionLayerSourceKind.BgraPixels,
            Order = 0,
            Revision = -1,
            Width = width,
            Height = height,
            Stride = stride,
            StretchMode = stretchMode,
            Opacity = 1.0d
        });

        if (!backgroundLayer.UploadBgraPixels(deviceResources.Device, deviceResources.Context, bgraPixels, width, height, stride, stretchMode, -1))
            ClearBackgroundFrame();
    }

    public void ClearBackgroundFrame() => _layerCache.GetOrCreate(MediaCompositionLayerId.Background).Clear();

    public bool SetHardwareBackgroundFrame(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        MediaCompositionStretchMode stretchMode,
        long backgroundRevision)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || !deviceResources.HasVideoProcessorSupport
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
            return false;

        try
        {
            D3D11CompositionLayerState backgroundLayer = _layerCache.GetOrCreate(MediaCompositionLayerId.Background);
            MediaCompositionStretchMode normalizedStretchMode = stretchMode == MediaCompositionStretchMode.Auto
                ? MediaCompositionStretchMode.UniformFit
                : stretchMode;
            var hasCurrentRetainedCopy = backgroundRevision >= 0
                && backgroundLayer.UploadedRevision == backgroundRevision
                && !backgroundLayer.NeedsUpload
                && backgroundLayer.HasTexture
                && backgroundLayer.TextureWidth == width
                && backgroundLayer.TextureHeight == height
                && backgroundLayer.TextureStride == width * 4
                && backgroundLayer.StretchMode == normalizedStretchMode;
            if (hasCurrentRetainedCopy)
                return true;

            if (!backgroundLayer.EnsureRenderTargetTexture(deviceResources.Device, width, height, width * 4, normalizedStretchMode)
                || backgroundLayer.Texture == null
                || backgroundLayer.ShaderResourceView == null)
                return false;

            var normalizedFps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 60.0;
            if (!videoProcessorPresenter.PresentHardwareFrame(
                    deviceResources,
                    backgroundLayer.Texture,
                    sourceTexturePointer,
                    sourceArraySlice,
                    width,
                    height,
                    normalizedFps,
                    width,
                    height,
                    MediaCompositionStretchMode.Fill))
            {
                backgroundLayer.Clear();
                return false;
            }

            backgroundLayer.MarkUploaded(backgroundRevision);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Hardware background copy failed: {ex.GetType().Name}: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "Composition", $"Hardware background copy failed: {ex.GetType().Name}: {ex.Message}", innerException: ex);
            ClearBackgroundFrame();
            return false;
        }
    }

    public bool PresentToTargets(
        D3D11DeviceResources deviceResources,
        MediaCompositionFrame frame,
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        D3D11VideoProcessorPresenter? videoProcessorPresenter = null)
        => PresentToTargets(deviceResources, frame, targets, videoProcessorPresenter, out _);

    public bool DrawCdgOverlayToTarget(
        D3D11DeviceResources deviceResources,
        CdgRenderSnapshot snapshot,
        D3D11PresentationTargetSnapshot target)
    {
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || !target.IsValid
            || target.CompositionTargetKind != D3D11CompositionTargetKind.DirectCompositionOverlay
            || snapshot.ColorIndices.Length < CdgConstants.TotalPixels
            || snapshot.PaletteArgb.Length < CdgConstants.ColorCount)
        {
            return false;
        }

        try
        {
            if (!EnsureCdgOverlayLayer(deviceResources, snapshot) || !EnsureDrawPipeline(deviceResources.Device))
                return false;

            using ID3D11RenderTargetView targetRenderView = deviceResources.Device.CreateRenderTargetView(target.Texture);
            return DrawCdgOverlayOnlyToRenderTarget(
                deviceResources.Context,
                snapshot,
                target.Width,
                target.Height,
                targetRenderView);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] DirectComposition overlay target draw failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool PresentToTargets(
        D3D11DeviceResources deviceResources,
        MediaCompositionFrame frame,
        IReadOnlyList<D3D11PresentationTargetSnapshot> targets,
        D3D11VideoProcessorPresenter? videoProcessorPresenter,
        out IReadOnlyList<D3D11PresentationTargetSnapshot> presentedTargets)
    {
        presentedTargets = Array.Empty<D3D11PresentationTargetSnapshot>();
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || targets.Count == 0)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: invalid device/targets count={targets.Count} reason={frame.Reason}.");
            return false;
        }

        try
        {
            var passStart = Stopwatch.GetTimestamp();
            _scratchValidTargets.Clear();
            foreach (D3D11PresentationTargetSnapshot target in targets)
                if (target.IsValid)
                    _scratchValidTargets.Add(target);

            List<D3D11PresentationTargetSnapshot> validTargets = _scratchValidTargets;
            if (validTargets.Count == 0)
            {
                Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: no valid targets reason={frame.Reason}.");
                return false;
            }

            _targetRegistry.Capture(validTargets.Select(static target => target.ToCompositionTargetDescriptor()));
            D3D11CompositionTargetPlan targetPlan = _targetRegistry.CreatePlan();
            _scratchPresentationTargets.Clear();
            foreach (D3D11PresentationTargetSnapshot target in validTargets)
                if (targetPlan.ShouldPresentTarget(target.TargetId))
                    _scratchPresentationTargets.Add(target);

            List<D3D11PresentationTargetSnapshot> presentationTargets = _scratchPresentationTargets;
            if (presentationTargets.Count == 0)
            {
                Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: target plan has no active presentation targets reason={frame.Reason}.");
                return false;
            }

            _layerCache.Capture(frame);
            D3D11CompositionLayerUpdatePlan updatePlan = _layerCache.CreateUpdatePlan();
            LogClearOnlyResourceGuardrailIfNeeded(updatePlan);
            LogLayerLifecycleIfNeeded(frame, updatePlan, targetPlan);

            if (CanSkipRetainedRefresh(frame, updatePlan, targetPlan))
            {
                RecordCompositionDiagnostics(
                    frame,
                    updatePlan,
                    targetPlan,
                    drawnTargets: 0,
                    skippedRetainedNoOp: true,
                    D3D11PresentationDiagnostics.ElapsedMilliseconds(passStart));
                _layerCache.MarkPlanConsumed();
                _targetRegistry.MarkPlanConsumed();
                return true;
            }

            if (!EnsureCompositionBackgroundFrame(deviceResources, frame, videoProcessorPresenter))
            {
                Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: background layer could not be prepared reason={frame.Reason}.");
                return false;
            }

            if (!EnsureCompositionOverlayFrame(deviceResources, frame))
            {
                Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: overlay layer could not be prepared reason={frame.Reason}.");
                return false;
            }

            if (frame.HasShaderVisibleLayer && !EnsureDrawPipeline(deviceResources.Device))
            {
                Debug.WriteLine("[COMPOSITION-D3D11] Visible present skipped: shader pipeline is unavailable.");
                return false;
            }

            var drawnTargets = new List<D3D11PresentationTargetSnapshot>(presentationTargets.Count);
            foreach (D3D11PresentationTargetSnapshot target in presentationTargets)
            {
                using ID3D11RenderTargetView targetRenderView = deviceResources.Device.CreateRenderTargetView(target.Texture);
                if (!DrawToRenderTarget(
                        deviceResources.Context,
                        frame,
                        target.Width,
                        target.Height,
                        targetRenderView,
                        clearTarget: frame.ClearMode != MediaCompositionClearMode.PreservePrevious))
                {
                    Debug.WriteLine($"[COMPOSITION-D3D11] Target present failed target={target.TargetId} {target.Width}x{target.Height} backend={target.BackendName} reason={frame.Reason}.");
                    continue;
                }

                drawnTargets.Add(target);
            }

            D3D11PresentationTargetSnapshot primaryTarget = presentationTargets[0];
            var primaryDrew = drawnTargets.Count > 0 && drawnTargets[0].TargetId == primaryTarget.TargetId;
            if (primaryDrew)
            {
                MarkRenderedLayers(frame);
                presentedTargets = drawnTargets;
                RecordCompositionDiagnostics(
                    frame,
                    updatePlan,
                    targetPlan,
                    drawnTargets.Count,
                    skippedRetainedNoOp: false,
                    D3D11PresentationDiagnostics.ElapsedMilliseconds(passStart));
                _layerCache.MarkPlanConsumed();
                _targetRegistry.MarkPlanConsumed();
            }

            return primaryDrew;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[COMPOSITION-D3D11] Visible target present failed: {ex.GetType().Name}: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Visible target present failed: {ex.GetType().Name}: {ex.Message}", innerException: ex);
            Reset();
            return false;
        }
    }

    private bool EnsureCompositionBackgroundFrame(D3D11DeviceResources deviceResources, MediaCompositionFrame frame, D3D11VideoProcessorPresenter? videoProcessorPresenter)
    {
        D3D11CompositionLayerState? backgroundLayer = _layerCache.TryGet(MediaCompositionLayerId.Background);
        MediaCompositionLayer? descriptor = frame.TryGetBackgroundLayer();

        if (frame.HasD3D11Background)
        {
            return videoProcessorPresenter != null && SetHardwareBackgroundFrame(
                deviceResources,
                videoProcessorPresenter,
                frame.BackgroundD3D11TexturePointer,
                frame.BackgroundD3D11ArraySlice,
                frame.BackgroundD3D11Width,
                frame.BackgroundD3D11Height,
                frame.BackgroundD3D11FramesPerSecond,
                frame.EffectiveBackgroundStretchMode,
                frame.BackgroundRevision);
        }

        if (frame.HasBackground)
        {
            if (backgroundLayer == null || descriptor == null)
                return false;

            var revisionMatches = frame.BackgroundRevision >= 0 && frame.BackgroundRevision == backgroundLayer.UploadedRevision;
            var dimensionsMatch = backgroundLayer.HasTexture
                && backgroundLayer.TextureWidth == frame.BackgroundWidth
                && backgroundLayer.TextureHeight == frame.BackgroundHeight
                && backgroundLayer.TextureStride == frame.BackgroundStride
                && backgroundLayer.StretchMode == frame.EffectiveBackgroundStretchMode;

            return (revisionMatches && dimensionsMatch && !backgroundLayer.NeedsUpload) || backgroundLayer.UploadBgraPixels(deviceResources.Device!, deviceResources.Context!, descriptor);
        }

        if (frame.HasRetainedBackground)
        {
            if (backgroundLayer == null || !backgroundLayer.HasTexture)
                return false;

            if (descriptor != null && descriptor.StretchMode != MediaCompositionStretchMode.Auto)
                backgroundLayer.Update(descriptor);

            return true;
        }

        ClearBackgroundFrame();
        return true;
    }

    private static bool CanSkipRetainedRefresh(
        MediaCompositionFrame frame,
        D3D11CompositionLayerUpdatePlan updatePlan,
        D3D11CompositionTargetPlan targetPlan) =>
        frame.RetainedRefresh
        && !updatePlan.HasWork
        && !targetPlan.HasMetadataWork;

    private static bool IsCdgOverlayOnlyCompositionCandidate(
        MediaCompositionFrame frame,
        D3D11CompositionLayerUpdatePlan updatePlan,
        D3D11CompositionTargetPlan targetPlan) =>
        frame.HasCdgOverlay
        && frame.HasRetainedBackground
        && !frame.HasBackground
        && !frame.HasD3D11Background
        && !updatePlan.IsClearOnly
        && !updatePlan.DeactivatedLayers
        && !targetPlan.HasMetadataWork
        && updatePlan.Updates.Any(static update => update.Id == MediaCompositionLayerId.Overlay && update.RequiresWork)
        && updatePlan.Updates.All(static update => !update.RequiresWork || update.Id == MediaCompositionLayerId.Overlay);

    private void RecordCompositionDiagnostics(
        MediaCompositionFrame frame,
        D3D11CompositionLayerUpdatePlan updatePlan,
        D3D11CompositionTargetPlan targetPlan,
        int drawnTargets,
        bool skippedRetainedNoOp,
        double elapsedMs)
    {
        if (!enableCompositionDiagnostics)
            return;

        _compositionPasses++;
        _compositionDrawnTargets += drawnTargets;
        _compositionLayerUploads += updatePlan.Updates.Count(static update => update.Action.HasFlag(D3D11CompositionLayerUpdateAction.Upload));
        _compositionLayerRenders += updatePlan.Updates.Count(static update => update.Action.HasFlag(D3D11CompositionLayerUpdateAction.Render));
        _compositionRetainedReuses += updatePlan.ReusableLayerCount;
        _compositionTargetResizes += targetPlan.ResizeTargetCount;
        if (skippedRetainedNoOp)
            _compositionSkippedRetainedNoOps++;
        if (IsCdgOverlayOnlyCompositionCandidate(frame, updatePlan, targetPlan))
        {
            _compositionCdgOverlayOnlyPasses++;
            _compositionCdgOverlayOnlyDrawnTargets += drawnTargets;
        }

        _compositionMs += elapsedMs;
        _compositionMaxMs = Math.Max(_compositionMaxMs, elapsedMs);
        _lastCompositionReason = frame.Reason;

        if (_compositionPasses < CompositionDiagnosticInterval)
            return;

        Debug.WriteLine(
            $"[COMPOSITION-D3D11:10X] passes={_compositionPasses}, avgPass={_compositionMs / _compositionPasses:F2}ms, " +
            $"maxPass={_compositionMaxMs:F2}ms, drawnTargets={_compositionDrawnTargets}, layerUploads={_compositionLayerUploads}, " +
            $"layerRenders={_compositionLayerRenders}, retainedReuses={_compositionRetainedReuses}, retainedNoOpSkips={_compositionSkippedRetainedNoOps}, targetResizes={_compositionTargetResizes}, " +
            $"cdgOverlayOnlyPasses={_compositionCdgOverlayOnlyPasses}, cdgOverlayOnlyDrawnTargets={_compositionCdgOverlayOnlyDrawnTargets}, " +
            $"lastReason={_lastCompositionReason}, lastLayerPlan=({updatePlan.Summary}), lastTargetPlan=({targetPlan.Summary})");

        ResetCompositionDiagnostics();
    }

    private void ResetCompositionDiagnostics()
    {
        _compositionPasses = 0;
        _compositionDrawnTargets = 0;
        _compositionLayerUploads = 0;
        _compositionLayerRenders = 0;
        _compositionRetainedReuses = 0;
        _compositionTargetResizes = 0;
        _compositionSkippedRetainedNoOps = 0;
        _compositionCdgOverlayOnlyPasses = 0;
        _compositionCdgOverlayOnlyDrawnTargets = 0;
        _compositionMs = 0;
        _compositionMaxMs = 0;
        _lastCompositionReason = null;
    }

    private bool EnsureCompositionOverlayFrame(D3D11DeviceResources deviceResources, MediaCompositionFrame frame)
    {
        if (!frame.HasCdgOverlay)
            return true;

        D3D11CompositionLayerState? overlayLayer = _layerCache.TryGet(MediaCompositionLayerId.Overlay);
        MediaCompositionLayer? descriptor = frame.TryGetOverlayLayer();
        if (overlayLayer == null || descriptor == null || descriptor.CdgSnapshot == null)
            return false;

        var paletteRevision = descriptor.CdgSnapshot.PaletteRevision;
        var revisionMatches = descriptor.Revision >= 0
            && descriptor.Revision == overlayLayer.UploadedRevision
            && paletteRevision == overlayLayer.CdgPaletteUploadedRevision;

        return (revisionMatches && overlayLayer.HasCdgResources && !overlayLayer.NeedsUpload) || overlayLayer.UploadCdgSnapshot(deviceResources.Device!, deviceResources.Context!, descriptor);
    }

    private bool EnsureCdgOverlayLayer(D3D11DeviceResources deviceResources, CdgRenderSnapshot snapshot)
    {
        if (deviceResources.Device == null || deviceResources.Context == null)
            return false;

        D3D11CompositionLayerState overlayLayer = _layerCache.GetOrCreate(MediaCompositionLayerId.Overlay);
        MediaCompositionLayer descriptor = CreateCdgOverlayLayerDescriptor(snapshot);
        overlayLayer.Update(descriptor);

        var revisionMatches = descriptor.Revision >= 0
            && descriptor.Revision == overlayLayer.UploadedRevision
            && snapshot.PaletteRevision == overlayLayer.CdgPaletteUploadedRevision;

        return (revisionMatches && overlayLayer.HasCdgResources && !overlayLayer.NeedsUpload)
            || overlayLayer.UploadCdgSnapshot(deviceResources.Device, deviceResources.Context, snapshot);
    }

    private static MediaCompositionLayer CreateCdgOverlayLayerDescriptor(CdgRenderSnapshot snapshot) => new()
    {
        Id = MediaCompositionLayerId.Overlay,
        Kind = MediaCompositionLayerKind.Overlay,
        SourceKind = MediaCompositionLayerSourceKind.CdgSnapshot,
        Order = 2,
        Revision = snapshot.Revision,
        Width = snapshot.Width,
        Height = snapshot.Height,
        Stride = snapshot.Width,
        StretchMode = MediaCompositionStretchMode.UniformFit,
        Opacity = 1.0d,
        CdgSnapshot = snapshot
    };

    private bool DrawToRenderTarget(
        ID3D11DeviceContext context,
        MediaCompositionFrame frame,
        int outputWidth,
        int outputHeight,
        ID3D11RenderTargetView renderTargetView,
        bool clearTarget)
    {
        if (frame.HasShaderVisibleLayer && (_constantBuffer == null || _vertexShader == null
            || _samplerState == null || _blendState == null || _rasterizerState == null))
            return false;

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetViewport(new Viewport(0, 0, outputWidth, outputHeight));
        context.OMSetRenderTargets(renderTargetView);

        if (clearTarget)
            context.ClearRenderTargetView(renderTargetView, ResolveClearColor(frame));

        if (frame.HasShaderVisibleLayer)
        {
            context.VSSetShader(_vertexShader);
            context.RSSetState(_rasterizerState);
            context.OMSetBlendState(_blendState);
            context.PSSetSampler(0, _samplerState);
        }

        D3D11CompositionLayerState? backgroundLayer = _layerCache.TryGet(MediaCompositionLayerId.Background);
        if (frame.HasAnyBackground
            && backgroundLayer?.HasTexture == true
            && _backgroundPixelShader != null)
            if (!DrawBackgroundLayer(context, outputWidth, outputHeight, backgroundLayer))
                return false;

        D3D11CompositionLayerState? washLayer = _layerCache.TryGet(MediaCompositionLayerId.Wash);
        if (frame.HasWash)
        {
            if (washLayer?.HasSolidColor != true)
                return false;

            DrawWashLayer(context, outputWidth, outputHeight, washLayer);
        }

        D3D11CompositionLayerState? overlayLayer = _layerCache.TryGet(MediaCompositionLayerId.Overlay);
        CdgRenderSnapshot? snapshot = frame.CdgOverlay;
        if (frame.HasCdgOverlay && snapshot != null && !frame.SuppressCdgOverlayDraw)
        {
            if (overlayLayer == null || !DrawCdgOverlayLayer(context, snapshot, outputWidth, outputHeight, overlayLayer))
                return false;
            _lastDrawnRevision = snapshot.Revision;
        }
        else
            _lastDrawnRevision = -1;

        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetSampler(0, null!);
        context.PSSetConstantBuffer(0, null!);
        context.VSSetConstantBuffer(0, null!);
        context.RSSetState(null);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        context.Flush();
        return true;
    }

    private bool DrawCdgOverlayOnlyToRenderTarget(
        ID3D11DeviceContext context,
        CdgRenderSnapshot snapshot,
        int outputWidth,
        int outputHeight,
        ID3D11RenderTargetView renderTargetView)
    {
        if (_constantBuffer == null
            || _vertexShader == null
            || _samplerState == null
            || _premultipliedBlendState == null
            || _rasterizerState == null)
        {
            return false;
        }

        D3D11CompositionLayerState? overlayLayer = _layerCache.TryGet(MediaCompositionLayerId.Overlay);
        if (overlayLayer == null)
            return false;

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetViewport(new Viewport(0, 0, outputWidth, outputHeight));
        context.OMSetRenderTargets(renderTargetView);
        context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 0f));
        context.VSSetShader(_vertexShader);
        context.RSSetState(_rasterizerState);
        context.OMSetBlendState(_premultipliedBlendState);
        context.PSSetSampler(0, _samplerState);

        if (!DrawCdgOverlayLayer(context, snapshot, outputWidth, outputHeight, overlayLayer))
            return false;

        overlayLayer.MarkRendered(snapshot.Revision);
        _lastDrawnRevision = snapshot.Revision;
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetSampler(0, null!);
        context.PSSetConstantBuffer(0, null!);
        context.VSSetConstantBuffer(0, null!);
        context.RSSetState(null);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        context.Flush();
        return true;
    }

    private static Color4 ResolveClearColor(MediaCompositionFrame frame) => frame.ClearMode switch
    {
        MediaCompositionClearMode.Transparent => new Color4(0, 0, 0, 0),
        MediaCompositionClearMode.ConfiguredColor => ToColor4(frame.ClearArgb),
        MediaCompositionClearMode.PreservePrevious => new Color4(0, 0, 0, 0),
        _ => ToColor4(frame.ClearArgb == 0 ? 0xFF000000 : frame.ClearArgb)
    };

    private void MarkRenderedLayers(MediaCompositionFrame frame)
    {
        if (frame.HasAnyBackground && _layerCache.TryGet(MediaCompositionLayerId.Background) is { } backgroundLayer)
            backgroundLayer.MarkRendered(backgroundLayer.UploadedRevision);

        if (frame.HasWash && _layerCache.TryGet(MediaCompositionLayerId.Wash) is { } washLayer)
            washLayer.MarkRendered(washLayer.UploadedRevision);

        if (frame.HasCdgOverlay && !frame.SuppressCdgOverlayDraw && frame.CdgOverlay is { } snapshot && _layerCache.TryGet(MediaCompositionLayerId.Overlay) is { } overlayLayer)
            overlayLayer.MarkRendered(snapshot.Revision);
    }

    private bool DrawBackgroundLayer(ID3D11DeviceContext context, int outputWidth, int outputHeight, D3D11CompositionLayerState backgroundLayer)
    {
        ID3D11ShaderResourceView? shaderResourceView = backgroundLayer.ShaderResourceView;
        if (shaderResourceView == null || _backgroundPixelShader == null)
            return false;

        RectD backgroundRect = ResolveBackgroundDestinationRect(backgroundLayer, outputWidth, outputHeight);

        SetQuadConstants(context, backgroundRect, outputWidth, outputHeight, new Vector4(0, 0, 0, 0));
        context.PSSetShader(_backgroundPixelShader);
        context.PSSetShaderResource(0, shaderResourceView);
        context.Draw(4, 0);
        context.PSSetShaderResource(0, null!);
        return true;
    }

    private bool DrawCdgOverlayLayer(
        ID3D11DeviceContext context,
        CdgRenderSnapshot snapshot,
        int outputWidth,
        int outputHeight,
        D3D11CompositionLayerState overlayLayer)
    {
        if (!overlayLayer.HasCdgResources || overlayLayer.CdgIndexShaderResourceView == null || overlayLayer.CdgPaletteShaderResourceView == null || _pixelShader == null)
            return false;

        RectD cdgRect = ViewportGeometry.UniformFit(CdgConstants.FrameWidth, CdgConstants.FrameHeight, outputWidth, outputHeight);
        var cdgLeft = (int)Math.Round(cdgRect.Left);
        var cdgTop = (int)Math.Round(cdgRect.Top);
        var cdgRight = cdgLeft + Math.Max(1, (int)Math.Round(cdgRect.Width));
        var cdgBottom = cdgTop + Math.Max(1, (int)Math.Round(cdgRect.Height));
        var cdgDestination = new RectD(cdgLeft, cdgTop, cdgRight - cdgLeft, cdgBottom - cdgTop);

        SetQuadConstants(
            context,
            cdgDestination,
            outputWidth,
            outputHeight,
            new Vector4(CdgConstants.ColorCount, snapshot.BackgroundColorIndex, 0f, 0f));
        DrawCdgForegroundLayer(
            context,
            overlayLayer.CdgIndexShaderResourceView,
            overlayLayer.CdgPaletteShaderResourceView);
        return true;
    }


    private static RectD ResolveBackgroundDestinationRect(D3D11CompositionLayerState backgroundLayer, int outputWidth, int outputHeight) => backgroundLayer.StretchMode switch
    {
        MediaCompositionStretchMode.Original => CenterOriginal(backgroundLayer.TextureWidth, backgroundLayer.TextureHeight, outputWidth, outputHeight),
        MediaCompositionStretchMode.Fill => new RectD(0, 0, outputWidth, outputHeight),
        MediaCompositionStretchMode.UniformToFill => ViewportGeometry.UniformToFill(backgroundLayer.TextureWidth, backgroundLayer.TextureHeight, outputWidth, outputHeight),
        _ => ViewportGeometry.UniformFit(backgroundLayer.TextureWidth, backgroundLayer.TextureHeight, outputWidth, outputHeight)
    };

    private static RectD CenterOriginal(int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var width = Math.Max(1, Math.Min(sourceWidth, outputWidth));
        var height = Math.Max(1, Math.Min(sourceHeight, outputHeight));
        var left = (outputWidth - width) / 2.0;
        var top = (outputHeight - height) / 2.0;
        return new RectD(left, top, width, height);
    }

    private void DrawWashLayer(ID3D11DeviceContext context, int outputWidth, int outputHeight, D3D11CompositionLayerState washLayer)
        => DrawWashArgbLayer(context, outputWidth, outputHeight, washLayer.SolidColorArgb, premultipliedOutput: false);

    private void DrawWashArgbLayer(
        ID3D11DeviceContext context,
        int outputWidth,
        int outputHeight,
        uint washArgb,
        bool premultipliedOutput = false)
    {
        var alpha = ((washArgb >> 24) & 0xFF) / 255.0f;
        ID3D11PixelShader? pixelShader = premultipliedOutput ? _premultipliedSolidPixelShader : _solidPixelShader;
        if (alpha <= 0f || pixelShader == null)
            return;

        var r = ((washArgb >> 16) & 0xFF) / 255.0f;
        var g = ((washArgb >> 8) & 0xFF) / 255.0f;
        var b = (washArgb & 0xFF) / 255.0f;
        SetQuadConstants(context, new RectD(0, 0, outputWidth, outputHeight), outputWidth, outputHeight, new Vector4(r, g, b, alpha));
        context.PSSetShader(pixelShader);
        context.Draw(4, 0);
    }

    private void DrawCdgForegroundLayer(
        ID3D11DeviceContext context,
        ID3D11ShaderResourceView indexShaderResourceView,
        ID3D11ShaderResourceView paletteShaderResourceView)
    {
        ID3D11PixelShader pixelShader = _pixelShader
            ?? throw new InvalidOperationException("The D3D11 composition pixel shader is not initialized.");
        ID3D11Buffer constantBuffer = _constantBuffer
            ?? throw new InvalidOperationException("The D3D11 composition constant buffer is not initialized.");

        context.PSSetShader(pixelShader);
        context.PSSetConstantBuffer(0, constantBuffer);
        context.PSSetShaderResource(0, indexShaderResourceView);
        context.PSSetShaderResource(1, paletteShaderResourceView);
        context.Draw(4, 0);
    }

    private void SetQuadConstants(ID3D11DeviceContext context, RectD destinationRect, int outputWidth, int outputHeight, Vector4 paletteInfo)
    {
        var left = (int)Math.Round(destinationRect.Left);
        var top = (int)Math.Round(destinationRect.Top);
        var right = left + Math.Max(1, (int)Math.Round(destinationRect.Width));
        var bottom = top + Math.Max(1, (int)Math.Round(destinationRect.Height));

        Span<byte> constantBytes = stackalloc byte[CdgShaderConstantsSize];
        var constants = new CdgShaderConstants
        {
            DestinationRect = new Vector4(
                (float)((left / (double)outputWidth * 2.0) - 1.0),
                (float)(1.0 - (top / (double)outputHeight * 2.0)),
                (float)((right / (double)outputWidth * 2.0) - 1.0),
                (float)(1.0 - (bottom / (double)outputHeight * 2.0))),
            TextureRect = new Vector4(0f, 0f, 1f, 1f),
            PaletteInfo = paletteInfo
        };

        ID3D11Buffer constantBuffer = _constantBuffer
            ?? throw new InvalidOperationException("The D3D11 composition constant buffer is not initialized.");

        MemoryMarshal.Write(constantBytes, in constants);
        context.UpdateSubresource(constantBytes, constantBuffer, 0, 0, 0, null);
        context.VSSetConstantBuffer(0, constantBuffer);
        context.PSSetConstantBuffer(0, constantBuffer);
    }

    private bool EnsureDrawPipeline(ID3D11Device device)
    {
        if (_pipelineFailed)
            return false;

        try
        {
            if (_vertexShader == null || _pixelShader == null)
            {
                var vertexShaderBytecode = CompileShaderBytecode(CdgShaderSource, "VSMain", "vs_5_0");
                var pixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSMain", "ps_5_0");
                var backgroundPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSBackground", "ps_5_0");
                var solidPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSSolid", "ps_5_0");
                var premultipliedSolidPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSSolidPremultiplied", "ps_5_0");
                _vertexShader = device.CreateVertexShader(vertexShaderBytecode);
                _pixelShader = device.CreatePixelShader(pixelShaderBytecode);
                _backgroundPixelShader = device.CreatePixelShader(backgroundPixelShaderBytecode);
                _solidPixelShader = device.CreatePixelShader(solidPixelShaderBytecode);
                _premultipliedSolidPixelShader = device.CreatePixelShader(premultipliedSolidPixelShaderBytecode);
            }

            if (_constantBuffer == null)
            {
                var constantBufferDescription = new BufferDescription
                {
                    ByteWidth = (uint)CdgShaderConstantsSize,
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                    StructureByteStride = 0
                };
                _constantBuffer = device.CreateBuffer(constantBufferDescription);
            }

            if (_samplerState == null)
            {
                var samplerDescription = new SamplerDescription
                {
                    Filter = Filter.MinMagMipPoint,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MipLODBias = 0,
                    MaxAnisotropy = 1,
                    ComparisonFunc = ComparisonFunction.Never,
                    BorderColor = new Color4(0, 0, 0, 0),
                    MinLOD = 0,
                    MaxLOD = float.MaxValue
                };
                _samplerState = device.CreateSamplerState(samplerDescription);
            }

            if (_blendState == null)
            {
                var blendDescription = new BlendDescription
                {
                    AlphaToCoverageEnable = false,
                    IndependentBlendEnable = false
                };
                blendDescription.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true,
                    SourceBlend = Blend.SourceAlpha,
                    DestinationBlend = Blend.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.InverseSourceAlpha,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All
                };
                _blendState = device.CreateBlendState(blendDescription);
            }

            if (_premultipliedBlendState == null)
            {
                var premultipliedBlendDescription = new BlendDescription
                {
                    AlphaToCoverageEnable = false,
                    IndependentBlendEnable = false
                };
                premultipliedBlendDescription.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true,
                    SourceBlend = Blend.One,
                    DestinationBlend = Blend.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.InverseSourceAlpha,
                    BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All
                };
                _premultipliedBlendState = device.CreateBlendState(premultipliedBlendDescription);
            }

            if (_rasterizerState == null)
            {
                var rasterizerDescription = new RasterizerDescription
                {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.None,
                    FrontCounterClockwise = false,
                    DepthBias = 0,
                    DepthBiasClamp = 0,
                    SlopeScaledDepthBias = 0,
                    DepthClipEnable = true,
                    ScissorEnable = false,
                    MultisampleEnable = false,
                    AntialiasedLineEnable = false
                };
                _rasterizerState = device.CreateRasterizerState(rasterizerDescription);
            }

            return _vertexShader != null
                && _pixelShader != null
                && _backgroundPixelShader != null
                && _solidPixelShader != null
                && _premultipliedSolidPixelShader != null
                && _constantBuffer != null
                && _samplerState != null
                && _blendState != null
                && _premultipliedBlendState != null
                && _rasterizerState != null;
        }
        catch (Exception ex)
        {
            _pipelineFailed = true;
            Debug.WriteLine($"[CDG-D3D11] Draw pipeline initialization failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }




    private void LogClearOnlyResourceGuardrailIfNeeded(D3D11CompositionLayerUpdatePlan updatePlan)
    {
        if (!updatePlan.IsClearOnly || !_layerCache.HasRetainedResources)
            return;

        Debug.WriteLine(
            $"[COMPOSITION-D3D11] Clear-only capture still has retained layer resources: {_layerCache.RetainedResourceSummary}");
    }

    private void LogLayerLifecycleIfNeeded(
        MediaCompositionFrame frame,
        D3D11CompositionLayerUpdatePlan updatePlan,
        D3D11CompositionTargetPlan targetPlan)
    {
        if (!enableLayerDiagnostics || frame.RetainedRefresh)
            return;

        var lifecycleSummary = _layerCache.CaptureLifecycleSummary;
        var updateSummary = updatePlan.Summary;
        var targetSummary = targetPlan.Summary;
        var targetLifecycleSummary = _targetRegistry.LifecycleSummary;
        var combinedSummary = $"targets={targetSummary}; targetLifecycle={targetLifecycleSummary}; lifecycle={lifecycleSummary}; plan={updateSummary}";

        if (string.IsNullOrWhiteSpace(lifecycleSummary) || string.Equals(combinedSummary, _lastLayerDiagnosticSummary, StringComparison.Ordinal))
            return;

        _lastLayerDiagnosticSummary = combinedSummary;
        var clearMode = frame.ClearMode.ToString();
        var layerMode = updatePlan.IsClearOnly ? "clear-only" : "visible";
        Debug.WriteLine($"[COMPOSITION-D3D11] Layer plan mode={layerMode} clear={clearMode} reason={frame.Reason} {combinedSummary}");
    }



    private static Color4 ToColor4(uint argb)
    {
        var a = ((argb >> 24) & 0xFF) / 255.0f;
        var r = ((argb >> 16) & 0xFF) / 255.0f;
        var g = ((argb >> 8) & 0xFF) / 255.0f;
        var b = (argb & 0xFF) / 255.0f;
        return new Color4(r, g, b, a);
    }

    private void ReleaseResources()
    {
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _backgroundPixelShader?.Dispose();
        _backgroundPixelShader = null;
        _solidPixelShader?.Dispose();
        _solidPixelShader = null;
        _premultipliedSolidPixelShader?.Dispose();
        _premultipliedSolidPixelShader = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _samplerState?.Dispose();
        _samplerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _premultipliedBlendState?.Dispose();
        _premultipliedBlendState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
    }

    private static byte[] CompileShaderBytecode(string shaderSource, string entryPoint, string targetProfile)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(shaderSource);
        var hr = D3DCompile(
            sourceBytes,
            (nuint)sourceBytes.Length,
            null,
            IntPtr.Zero,
            IntPtr.Zero,
            entryPoint,
            targetProfile,
            0,
            0,
            out var codePtr,
            out var errorPtr);

        try
        {
            if (hr < 0)
            {
                var errorMessage = errorPtr == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringAnsi(GetBufferPointer(errorPtr), checked((int)GetBufferSize(errorPtr)));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                    ? $"D3DCompile failed for {entryPoint}/{targetProfile}: 0x{hr:X8}"
                    : $"D3DCompile failed for {entryPoint}/{targetProfile}: {errorMessage}");
            }

            if (codePtr == IntPtr.Zero)
                throw new InvalidOperationException($"D3DCompile returned no bytecode for {entryPoint}/{targetProfile}.");

            var size = checked((int)GetBufferSize(codePtr));
            var bytecode = new byte[size];
            Marshal.Copy(GetBufferPointer(codePtr), bytecode, 0, size);
            return bytecode;
        }
        finally
        {
            if (codePtr != IntPtr.Zero)
                _ = Marshal.Release(codePtr);

            if (errorPtr != IntPtr.Zero)
                _ = Marshal.Release(errorPtr);
        }
    }

    private static IntPtr GetBufferPointer(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fp = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(fp)(blob);
    }

    private static nuint GetBufferSize(IntPtr blob)
    {
        var vtbl = Marshal.ReadIntPtr(blob);
        var fp = Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(fp)(blob);
    }

    private delegate IntPtr GetBufferPointerDelegate(IntPtr pThis);
    private delegate nuint GetBufferSizeDelegate(IntPtr pThis);

    [LibraryImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(System.Runtime.InteropServices.Marshalling.AnsiStringMarshaller))]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static partial int D3DCompile(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] sourceData,
        nuint sourceDataSize,
        string? sourceName,
        IntPtr defines,
        IntPtr include,
        string entryPoint,
        string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    [StructLayout(LayoutKind.Sequential)]
    private struct CdgShaderConstants
    {
        public Vector4 DestinationRect;
        public Vector4 TextureRect;
        public Vector4 PaletteInfo;
    }

    private const string CdgShaderSource = @"
cbuffer CdgConstants : register(b0)
{
    float4 DestinationRect;
    float4 TextureRect;
    float4 PaletteInfo;
};

Texture2D IndexTexture : register(t0);
Texture2D PaletteTexture : register(t1);
SamplerState PointSampler : register(s0);

struct VertexOut
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VertexOut VSMain(uint vertexId : SV_VertexID)
{
    float2 positions[4] =
    {
        float2(DestinationRect.x, DestinationRect.w),
        float2(DestinationRect.x, DestinationRect.y),
        float2(DestinationRect.z, DestinationRect.w),
        float2(DestinationRect.z, DestinationRect.y)
    };

    float2 texCoords[4] =
    {
        float2(TextureRect.x, TextureRect.w),
        float2(TextureRect.x, TextureRect.y),
        float2(TextureRect.z, TextureRect.w),
        float2(TextureRect.z, TextureRect.y)
    };

    VertexOut output;
    output.Position = float4(positions[vertexId], 0.0f, 1.0f);
    output.TexCoord = texCoords[vertexId];
    return output;
}

float4 PSBackground(VertexOut input) : SV_TARGET
{
    return IndexTexture.Sample(PointSampler, input.TexCoord);
}

float4 PSSolid(VertexOut input) : SV_TARGET
{
    return PaletteInfo;
}

float4 PSSolidPremultiplied(VertexOut input) : SV_TARGET
{
    return float4(PaletteInfo.rgb * PaletteInfo.a, PaletteInfo.a);
}

float4 PSMain(VertexOut input) : SV_TARGET
{
    float normalizedIndex = IndexTexture.Sample(PointSampler, input.TexCoord).r;
    float paletteIndex = clamp(floor((normalizedIndex * 255.0f) + 0.5f), 0.0f, PaletteInfo.x - 1.0f);

    // Until a CDG memory-preset establishes the background color (PaletteInfo.y < 0), the entire
    // overlay is transparent so any underlying background shows through instead of an opaque black
    // plane built from the default palette.
    if (PaletteInfo.y < 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    // The active CDG path clears the target to the background/wash color first.
    // Only the current CDG background index is transparent in the foreground pass.
    // Do not automatically hide index 0; CDG files may legitimately use color 0 as
    // a lyric/tile color when a different background index is active.
    if (abs(paletteIndex - PaletteInfo.y) < 0.5f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float paletteU = (paletteIndex + 0.5f) / PaletteInfo.x;
    float4 color = PaletteTexture.Sample(PointSampler, float2(paletteU, 0.5f));
    return float4(color.rgb, 1.0f);
}
";

    public void Dispose() => Reset();
}
