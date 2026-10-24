using FatzBomb.MediaControl.Core.Cdg;
using FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;
using FatzBomb.MediaControl.Core.Graphics.Composition.Planning;
using FatzBomb.MediaControl.Core.Graphics.D3D.Device;
using FatzBomb.MediaControl.Core.Graphics.D3D.Diagnostics;
using FatzBomb.MediaControl.Core.Graphics.D3D.Presentation;
using FatzBomb.MediaControl.Core.Graphics.D3D.Video;
using FatzBomb.MediaControl.Core.Graphics.Geometry;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FatzBomb.MediaControl.Core.Graphics.Composition;

/// <summary>
/// D3D11 implementation of the single media compositor. It draws an optional
/// background/base layer, optional wash layer, optional CDG foreground overlay, then presents once.
/// </summary>
internal sealed partial class D3D11MediaCompositionPresenter(bool enableLayerDiagnostics = false, bool enableCompositionDiagnostics = false)
{
    private const int CompositionDiagnosticInterval = 120;

    /// <summary>Why the last <see cref="PresentToTargets(D3D11DeviceResources, MediaCompositionFrame, IReadOnlyList{D3D11PresentationTargetSnapshot}, D3D11VideoProcessorPresenter?, out IReadOnlyList{D3D11PresentationTargetSnapshot})"/> returned false, or null.</summary>
    public string? LastFailureReason { get; private set; }

    private void Fail(string reason, MediaCompositionFrame frame)
    {
        LastFailureReason = reason;
        Debug.WriteLine($"[COMPOSITION-D3D11] Visible present skipped: {reason} reason={frame.Reason}.");
    }

    private readonly D3D11CompositionLayerCache _layerCache = new();
    private readonly CompositionTargetRegistry _targetRegistry = new();
    private readonly List<D3D11PresentationTargetSnapshot> _scratchValidTargets = [];
    private readonly List<D3D11PresentationTargetSnapshot> _scratchPresentationTargets = [];

    private ID3D11PixelShader? _backgroundPixelShader;
    private ID3D11PixelShader? _baseVisualPixelShader;
    private ID3D11PixelShader? _baseVisualPostPixelShader;
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
    private readonly Dictionary<string, PostProcessTarget> _postProcessTargets = new(StringComparer.OrdinalIgnoreCase);
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

    public void ClearBackgroundFrame() => _layerCache.GetOrCreate(MediaCompositionLayerId.Background).Clear();

    public bool SetHardwareBackgroundFrame(
        D3D11DeviceResources deviceResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond,
        VideoColorSpace colorSpace,
        HdrToneMapRequest? toneMap,
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
                    colorSpace,
                    toneMap,
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
            || target.CompositionTargetKind != CompositionTargetKind.DirectCompositionOverlay
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
        presentedTargets = [];
        LastFailureReason = null;
        if (deviceResources.Device == null
            || deviceResources.Context == null
            || targets.Count == 0)
        {
            Fail($"no D3D11 device or presentation target (targets={targets.Count})", frame);
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
                Fail("no valid presentation target", frame);
                return false;
            }

            _targetRegistry.Capture(validTargets.Select(static target => target.ToCompositionTargetDescriptor()));
            CompositionTargetPlan targetPlan = _targetRegistry.CreatePlan();
            _scratchPresentationTargets.Clear();
            foreach (D3D11PresentationTargetSnapshot target in validTargets)
                if (targetPlan.ShouldPresentTarget(target.TargetId) && IsForTarget(frame, target.TargetId))
                    _scratchPresentationTargets.Add(target);

            List<D3D11PresentationTargetSnapshot> presentationTargets = _scratchPresentationTargets;
            if (presentationTargets.Count == 0)
            {
                // A composition for some targets only (display-locked video on several monitors)
                // whose surfaces just detached: nothing to present, not a failure.
                if (frame.Targets is not null)
                    return true;

                Fail("target plan has no active presentation target", frame);
                return false;
            }

            _layerCache.Capture(frame);
            CompositionLayerUpdatePlan updatePlan = _layerCache.CreateUpdatePlan();
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
                Fail("background layer could not be prepared", frame);
                return false;
            }

            if (!EnsureCompositionOverlayFrame(deviceResources, frame))
            {
                Fail("overlay layer could not be prepared", frame);
                return false;
            }

            if (!EnsureCompositionSubtitleFrame(deviceResources, frame))
            {
                Fail("subtitle layer could not be prepared", frame);
                return false;
            }

            if (frame.HasShaderVisibleLayer && !EnsureDrawPipeline(deviceResources.Device))
            {
                Fail("shader pipeline is unavailable", frame);
                return false;
            }

            var drawnTargets = new List<D3D11PresentationTargetSnapshot>(presentationTargets.Count);
            foreach (D3D11PresentationTargetSnapshot target in presentationTargets)
            {
                using ID3D11RenderTargetView targetRenderView = deviceResources.Device.CreateRenderTargetView(target.Texture);
                if (!DrawToRenderTarget(
                        deviceResources.Device,
                        deviceResources.Context,
                        frame,
                        target.TargetId,
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

            D3D11PresentationTargetSnapshot primaryTarget = presentationTargets.FirstOrDefault(
                static target => target.TargetId == CompositionTargetDescriptor.PrimaryFinalTargetId);
            var requiredTargetDrew = primaryTarget.IsValid
                ? drawnTargets.Any(target => target.TargetId == primaryTarget.TargetId)
                : drawnTargets.Count > 0;
            if (requiredTargetDrew)
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

            if (!requiredTargetDrew)
                LastFailureReason = "drawing to the primary target failed";

            return requiredTargetDrew;
        }
        catch (Exception ex)
        {
            LastFailureReason = $"{ex.GetType().Name}: {ex.Message}";
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "Composition", $"Visible target present failed: {ex.GetType().Name}: {ex.Message}", innerException: ex);
            Reset();
            return false;
        }
    }

    private bool EnsureCompositionBackgroundFrame(D3D11DeviceResources deviceResources, MediaCompositionFrame frame, D3D11VideoProcessorPresenter? videoProcessorPresenter)
    {
        D3D11CompositionLayerState? backgroundLayer = _layerCache.TryGet(MediaCompositionLayerId.Background);
        MediaCompositionLayer? descriptor = frame.TryGetBackgroundLayer();

        if (frame.HasNativeBackground)
        {
            NativeVideoFrameResource? resource = frame.BackgroundNativeResource;
            return videoProcessorPresenter != null && SetHardwareBackgroundFrame(
                deviceResources,
                videoProcessorPresenter,
                resource!.ResourceHandle,
                resource.SubresourceIndex,
                frame.BackgroundNativeWidth,
                frame.BackgroundNativeHeight,
                frame.BackgroundNativeFramesPerSecond,
                resource.ColorSpace,
                resource.HdrToneMap,
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
        CompositionLayerUpdatePlan updatePlan,
        CompositionTargetPlan targetPlan) =>
        frame.RetainedRefresh
        && !updatePlan.HasWork
        && !targetPlan.HasMetadataWork;

    /// <summary>
    /// Whether a composition is for a target (Stage 017A slice 5): the primary final target by
    /// role, a secondary window by the surface id inside its target id.
    /// </summary>
    private static bool IsForTarget(MediaCompositionFrame frame, string targetId) =>
        frame.Targets is null
        || (targetId == CompositionTargetDescriptor.PrimaryFinalTargetId
            ? frame.Targets.IncludesPrimary
            : frame.Targets.Includes(isPrimary: false, D3D11PresentationBackendSet.ToSecondarySurfaceId(targetId)));

    private static bool IsCdgOverlayOnlyCompositionCandidate(
        MediaCompositionFrame frame,
        CompositionLayerUpdatePlan updatePlan,
        CompositionTargetPlan targetPlan) =>
        frame.HasCdgOverlay
        && frame.HasRetainedBackground
        && !frame.HasBackground
        && !frame.HasNativeBackground
        && !updatePlan.IsClearOnly
        && !updatePlan.DeactivatedLayers
        && !targetPlan.HasMetadataWork
        && updatePlan.Updates.Any(static update => update.Id == MediaCompositionLayerId.Overlay && update.RequiresWork)
        && updatePlan.Updates.All(static update => !update.RequiresWork || update.Id == MediaCompositionLayerId.Overlay);

    private void RecordCompositionDiagnostics(
        MediaCompositionFrame frame,
        CompositionLayerUpdatePlan updatePlan,
        CompositionTargetPlan targetPlan,
        int drawnTargets,
        bool skippedRetainedNoOp,
        double elapsedMs)
    {
        if (!enableCompositionDiagnostics)
            return;

        _compositionPasses++;
        _compositionDrawnTargets += drawnTargets;
        _compositionLayerUploads += updatePlan.Updates.Count(static update => update.Action.HasFlag(CompositionLayerUpdateAction.Upload));
        _compositionLayerRenders += updatePlan.Updates.Count(static update => update.Action.HasFlag(CompositionLayerUpdateAction.Render));
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

    private bool EnsureCompositionSubtitleFrame(D3D11DeviceResources deviceResources, MediaCompositionFrame frame)
    {
        if (!frame.HasSubtitleOverlay)
            return true;

        D3D11CompositionLayerState? subtitleLayer = _layerCache.TryGet(MediaCompositionLayerId.Subtitle);
        MediaCompositionLayer? descriptor = frame.TryGetSubtitleLayer();
        if (subtitleLayer == null || descriptor == null)
            return false;

        var revisionMatches = descriptor.Revision >= 0
            && descriptor.Revision == subtitleLayer.UploadedRevision;
        var dimensionsMatch = subtitleLayer.HasTexture
            && subtitleLayer.TextureWidth == descriptor.Width
            && subtitleLayer.TextureHeight == descriptor.Height
            && subtitleLayer.TextureStride == descriptor.Stride;

        return (revisionMatches && dimensionsMatch && !subtitleLayer.NeedsUpload)
            || subtitleLayer.UploadBgraPixels(deviceResources.Device!, deviceResources.Context!, descriptor);
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
        ID3D11Device device,
        ID3D11DeviceContext context,
        MediaCompositionFrame frame,
        string targetId,
        int outputWidth,
        int outputHeight,
        ID3D11RenderTargetView renderTargetView,
        bool clearTarget)
    {
        var transformWholeComposition = frame.BaseVisualTransform is { IsIdentity: false, IncludeOverlays: true };
        PostProcessTarget? postProcessTarget = null;
        if (transformWholeComposition && !TryGetPostProcessTarget(device, targetId, outputWidth, outputHeight, out postProcessTarget))
            return false;

        if (frame.HasShaderVisibleLayer && (_constantBuffer == null || _vertexShader == null
            || _samplerState == null || _blendState == null || _rasterizerState == null))
            return false;

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.IASetInputLayout(null);
        context.RSSetViewport(new Viewport(0, 0, outputWidth, outputHeight));
        ID3D11RenderTargetView compositionRenderTargetView = transformWholeComposition
            ? postProcessTarget!.RenderTargetView
            : renderTargetView;
        context.OMSetRenderTargets(compositionRenderTargetView);

        try
        {
            if (clearTarget || transformWholeComposition)
                context.ClearRenderTargetView(
                    compositionRenderTargetView,
                    clearTarget ? ResolveClearColor(frame) : new Color4(0f, 0f, 0f, 0f));

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
                if (!DrawBackgroundLayer(context, outputWidth, outputHeight, backgroundLayer, applyTransform: !transformWholeComposition))
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

            D3D11CompositionLayerState? subtitleLayer = _layerCache.TryGet(MediaCompositionLayerId.Subtitle);
            if (frame.HasSubtitleOverlay)
            {
                if (subtitleLayer == null || !DrawSubtitleLayer(context, outputWidth, outputHeight, subtitleLayer))
                    return false;
            }

            if (transformWholeComposition
                && !DrawPostProcessToRenderTarget(
                    context,
                    frame.BaseVisualTransform,
                    outputWidth,
                    outputHeight,
                    renderTargetView,
                    postProcessTarget!.ShaderResourceView))
            {
                return false;
            }

            return true;
        }
        finally
        {
            // Success or not, the context must not keep the swap-chain buffer bound:
            // ResizeBuffers and swap-chain release fail while it is.
            UnbindPipeline(context);
        }
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

        try
        {
            context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 0f));
            context.VSSetShader(_vertexShader);
            context.RSSetState(_rasterizerState);
            context.OMSetBlendState(_premultipliedBlendState);
            context.PSSetSampler(0, _samplerState);

            if (!DrawCdgOverlayLayer(context, snapshot, outputWidth, outputHeight, overlayLayer))
                return false;

            overlayLayer.MarkRendered(snapshot.Revision);
            _lastDrawnRevision = snapshot.Revision;
            return true;
        }
        finally
        {
            // Success or not, the context must not keep the swap-chain buffer bound:
            // ResizeBuffers and swap-chain release fail while it is.
            UnbindPipeline(context);
        }
    }

    private static void UnbindPipeline(ID3D11DeviceContext context)
    {
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetSampler(0, null!);
        context.PSSetConstantBuffer(0, null!);
        context.VSSetConstantBuffer(0, null!);
        context.RSSetState(null);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null!);
        context.Flush();
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

        if (frame.HasSubtitleOverlay && _layerCache.TryGet(MediaCompositionLayerId.Subtitle) is { } subtitleLayer)
            subtitleLayer.MarkRendered(subtitleLayer.UploadedRevision);
    }

    private bool DrawBackgroundLayer(
        ID3D11DeviceContext context,
        int outputWidth,
        int outputHeight,
        D3D11CompositionLayerState backgroundLayer,
        bool applyTransform = true)
    {
        ID3D11ShaderResourceView? shaderResourceView = backgroundLayer.ShaderResourceView;
        if (shaderResourceView == null || _backgroundPixelShader == null || _baseVisualPixelShader == null)
            return false;

        RectD backgroundRect = ResolveBackgroundDestinationRect(backgroundLayer, outputWidth, outputHeight);
        BaseVisualTransform transform = applyTransform
            ? backgroundLayer.Descriptor?.BaseVisualTransform ?? BaseVisualTransform.Identity
            : BaseVisualTransform.Identity;

        SetQuadConstants(context, backgroundRect, outputWidth, outputHeight, new Vector4(0, 0, 0, 0), transform);
        context.PSSetShader(transform.IsIdentity ? _backgroundPixelShader : _baseVisualPixelShader);
        context.PSSetShaderResource(0, shaderResourceView);
        context.Draw(4, 0);
        context.PSSetShaderResource(0, null!);
        return true;
    }

    private bool DrawPostProcessToRenderTarget(
        ID3D11DeviceContext context,
        BaseVisualTransform transform,
        int outputWidth,
        int outputHeight,
        ID3D11RenderTargetView renderTargetView,
        ID3D11ShaderResourceView sourceShaderResourceView)
    {
        if (_baseVisualPostPixelShader == null)
            return false;

        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets(renderTargetView);
        context.RSSetViewport(new Viewport(0, 0, outputWidth, outputHeight));
        context.OMSetBlendState(null);
        SetQuadConstants(
            context,
            new RectD(0, 0, outputWidth, outputHeight),
            outputWidth,
            outputHeight,
            new Vector4(0f, 0f, 0f, 0f),
            transform);
        context.PSSetShader(_baseVisualPostPixelShader);
        context.PSSetShaderResource(0, sourceShaderResourceView);
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

    private bool DrawSubtitleLayer(ID3D11DeviceContext context, int outputWidth, int outputHeight, D3D11CompositionLayerState subtitleLayer)
    {
        if (subtitleLayer.ShaderResourceView == null || _backgroundPixelShader == null || _premultipliedBlendState == null)
            return false;

        MediaCompositionLayer? descriptor = subtitleLayer.Descriptor;
        var hasCanvasPlacement = descriptor is
        {
            CanvasWidth: > 0,
            CanvasHeight: > 0,
            Width: > 0,
            Height: > 0
        };
        RectD subtitleRect = hasCanvasPlacement
            ? ViewportGeometry.MapCanvasRegionToViewport(
                new RectD(descriptor!.CanvasLeft, descriptor.CanvasTop, descriptor.Width, descriptor.Height),
                descriptor.CanvasWidth,
                descriptor.CanvasHeight,
                outputWidth,
                outputHeight)
            : ViewportGeometry.DestinationRect(
                subtitleLayer.TextureWidth,
                subtitleLayer.TextureHeight,
                outputWidth,
                outputHeight,
                subtitleLayer.StretchMode);
        SetQuadConstants(context, subtitleRect, outputWidth, outputHeight, new Vector4(0, 0, 0, 0));
        context.OMSetBlendState(_premultipliedBlendState);
        context.PSSetShader(_backgroundPixelShader);
        context.PSSetShaderResource(0, subtitleLayer.ShaderResourceView);
        context.Draw(4, 0);
        context.PSSetShaderResource(0, null!);
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

    private void SetQuadConstants(
        ID3D11DeviceContext context,
        RectD destinationRect,
        int outputWidth,
        int outputHeight,
        Vector4 paletteInfo,
        BaseVisualTransform transform = default)
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
            PaletteInfo = paletteInfo,
            TransformAdjustments = new Vector4(
                transform.HueDegrees * (MathF.PI / 180f),
                transform.Saturation,
                transform.Brightness,
                0f),
            TransformBlend = new Vector4(
                transform.Contrast,
                0f,
                0f,
                0f),
            TransformRgbGains = new Vector4(
                transform.RedGain,
                transform.GreenGain,
                transform.BlueGain,
                0f)
        };

        ID3D11Buffer constantBuffer = _constantBuffer
            ?? throw new InvalidOperationException("The D3D11 composition constant buffer is not initialized.");

        MemoryMarshal.Write(constantBytes, in constants);
        context.UpdateSubresource(constantBytes, constantBuffer, 0, 0, 0, null);
        context.VSSetConstantBuffer(0, constantBuffer);
        context.PSSetConstantBuffer(0, constantBuffer);
    }

    private bool TryGetPostProcessTarget(
        ID3D11Device device,
        string targetId,
        int width,
        int height,
        out PostProcessTarget? target)
    {
        if (_postProcessTargets.TryGetValue(targetId, out target))
        {
            if (target.Width == width && target.Height == height)
                return true;

            target.Dispose();
            _postProcessTargets.Remove(targetId);
            target = null;
        }

        ID3D11Texture2D? texture = null;
        ID3D11RenderTargetView? renderTargetView = null;
        ID3D11ShaderResourceView? shaderResourceView = null;
        try
        {
            var description = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                BindFlags.ShaderResource | BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                sampleCount: 1,
                sampleQuality: 0,
                ResourceOptionFlags.None);
            texture = device.CreateTexture2D(in description);
            renderTargetView = device.CreateRenderTargetView(texture);
            shaderResourceView = device.CreateShaderResourceView(texture);
            target = new PostProcessTarget(texture, renderTargetView, shaderResourceView, width, height);
            _postProcessTargets.Add(targetId, target);
            return true;
        }
        catch
        {
            shaderResourceView?.Dispose();
            renderTargetView?.Dispose();
            texture?.Dispose();
            target = null;
            return false;
        }
    }

    private void ReleasePostProcessTargets()
    {
        foreach (PostProcessTarget target in _postProcessTargets.Values)
            target.Dispose();

        _postProcessTargets.Clear();
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
                var baseVisualPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSBaseVisual", "ps_5_0");
                var baseVisualPostPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSBaseVisualPremultiplied", "ps_5_0");
                var solidPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSSolid", "ps_5_0");
                var premultipliedSolidPixelShaderBytecode = CompileShaderBytecode(CdgShaderSource, "PSSolidPremultiplied", "ps_5_0");
                _vertexShader = device.CreateVertexShader(vertexShaderBytecode);
                _pixelShader = device.CreatePixelShader(pixelShaderBytecode);
                _backgroundPixelShader = device.CreatePixelShader(backgroundPixelShaderBytecode);
                _baseVisualPixelShader = device.CreatePixelShader(baseVisualPixelShaderBytecode);
                _baseVisualPostPixelShader = device.CreatePixelShader(baseVisualPostPixelShaderBytecode);
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
                && _baseVisualPixelShader != null
                && _baseVisualPostPixelShader != null
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




    private void LogClearOnlyResourceGuardrailIfNeeded(CompositionLayerUpdatePlan updatePlan)
    {
        if (!updatePlan.IsClearOnly || !_layerCache.HasRetainedResources)
            return;

        Debug.WriteLine(
            $"[COMPOSITION-D3D11] Clear-only capture still has retained layer resources: {_layerCache.RetainedResourceSummary}");
    }

    private void LogLayerLifecycleIfNeeded(
        MediaCompositionFrame frame,
        CompositionLayerUpdatePlan updatePlan,
        CompositionTargetPlan targetPlan)
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
        _baseVisualPixelShader?.Dispose();
        _baseVisualPixelShader = null;
        _baseVisualPostPixelShader?.Dispose();
        _baseVisualPostPixelShader = null;
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
        ReleasePostProcessTargets();
    }

    internal static byte[] CompileShaderBytecode(string shaderSource, string entryPoint, string targetProfile)
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
        public Vector4 TransformAdjustments;
        public Vector4 TransformBlend;
        public Vector4 TransformRgbGains;
    }

    private sealed class PostProcessTarget(
        ID3D11Texture2D texture,
        ID3D11RenderTargetView renderTargetView,
        ID3D11ShaderResourceView shaderResourceView,
        int width,
        int height) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public ID3D11RenderTargetView RenderTargetView { get; } = renderTargetView;
        public ID3D11ShaderResourceView ShaderResourceView { get; } = shaderResourceView;
        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Dispose()
        {
            ShaderResourceView.Dispose();
            RenderTargetView.Dispose();
            Texture.Dispose();
        }
    }

    internal const string CdgShaderSource = @"
cbuffer CdgConstants : register(b0)
{
    float4 DestinationRect;
    float4 TextureRect;
    float4 PaletteInfo;
    float4 TransformAdjustments;
    float4 TransformBlend;
    float4 TransformRgbGains;
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

float3 ApplyVisualTransform(float3 color)
{
    if (TransformAdjustments.x != 0.0f)
    {
        float y = dot(color, float3(0.299f, 0.587f, 0.114f));
        float i = dot(color, float3(0.596f, -0.274f, -0.322f));
        float q = dot(color, float3(0.211f, -0.523f, 0.312f));
        float cosine = cos(TransformAdjustments.x);
        float sine = sin(TransformAdjustments.x);
        float rotatedI = (i * cosine) - (q * sine);
        float rotatedQ = (i * sine) + (q * cosine);
        color = saturate(float3(
            y + (0.956f * rotatedI) + (0.621f * rotatedQ),
            y - (0.272f * rotatedI) - (0.647f * rotatedQ),
            y - (1.106f * rotatedI) + (1.703f * rotatedQ)));
    }

    if (TransformAdjustments.y != 1.0f)
    {
        float luma = dot(color, float3(0.2126f, 0.7152f, 0.0722f));
        color = saturate(luma + ((color - luma) * TransformAdjustments.y));
    }

    if (TransformBlend.x != 1.0f)
    {
        color = saturate(color * TransformBlend.x);
    }

    if (TransformAdjustments.z != 0.0f)
    {
        color = saturate(color + TransformAdjustments.z);
    }

    if (any(TransformRgbGains.rgb != float3(1.0f, 1.0f, 1.0f)))
    {
        color = saturate(color * TransformRgbGains.rgb);
    }

    return color;
}

float4 PSBaseVisual(VertexOut input) : SV_TARGET
{
    float4 sampled = IndexTexture.Sample(PointSampler, input.TexCoord);
    // A B8G8R8A8_UNORM SRV is exposed to HLSL as logical RGBA channels;
    // the format describes memory layout and does not require a manual swizzle.
    return float4(ApplyVisualTransform(sampled.rgb), sampled.a);
}

float4 PSBaseVisualPremultiplied(VertexOut input) : SV_TARGET
{
    float4 sampled = IndexTexture.Sample(PointSampler, input.TexCoord);
    if (sampled.a <= 0.0f)
    {
        return float4(0.0f, 0.0f, 0.0f, 0.0f);
    }

    float3 color = ApplyVisualTransform(sampled.rgb / sampled.a);
    return float4(color * sampled.a, sampled.a);
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
