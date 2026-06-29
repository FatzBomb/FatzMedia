using FatzBombMedia.MediaControl.Controllers;
using FatzBombMedia.MediaControl.Core.Abstractions;
using FatzBombMedia.MediaControl.Core.Configuration;
using FatzBombMedia.MediaControl.Core.DxCdg;
using FatzBombMedia.MediaControl.Core.Graphics.Composition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D;
using FatzBombMedia.MediaControl.Pipeline;
using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Graphics;

internal sealed class NativeMediaPresentationCoordinator : IMediaRenderCoordinator, IDisposable
{
    private const int PresentationLoopDelayMs = 10;

    private readonly IMediaEngine _mediaEngine;
    private readonly MediaPlayerConfiguration _configuration;
    private readonly RenderPipeline _renderPipeline;
    private readonly NativeD3D11PresentationBridge _presenter;
    private readonly PlaybackScheduler _presentationScheduler = new("CoreNativePresentation");
    private readonly Dictionary<string, MediaPresentationSurfaceDescriptor> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private IVideoEngine? _attachedVideo;
    private ICdgEngine? _attachedCdg;
    private bool _isRenderingActive;
    private bool _disposed;
    private ReadOnlyMemory<byte> _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
    private byte[]? _compositionBackgroundCopyBuffer;
    private int _compositionBackgroundWidth;
    private int _compositionBackgroundHeight;
    private int _compositionBackgroundStride;
    private MediaCompositionStretchMode _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
    private bool _compositionBackgroundIsRetainedGpu;
    private long _compositionBackgroundRevision;
    private MediaCompositionStretchMode _primaryStretchMode = MediaCompositionStretchMode.UniformFit;

    public NativeMediaPresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
    {
        _mediaEngine = mediaEngine ?? throw new ArgumentNullException(nameof(mediaEngine));
        _configuration = configuration;
        _renderPipeline = new RenderPipeline(_mediaEngine);
        _presenter = new NativeD3D11PresentationBridge(configuration);
        _presenter.FramePresented += OnFramePresented;

        _mediaEngine.ImageFrameReady += OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady += OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared += OnCoreBackgroundCleared;

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
    }

    public event EventHandler? FramePresented;

    public string PresentationBackend => _presenter.PresentationBackend;

    public VideoDisplayMode VideoDisplayMode
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _primaryStretchMode = ToCompositionStretchMode(value);
            _presenter.SetNativePresentationStretch(_primaryStretchMode);
            PresentCurrentComposition("display mode changed");
        }
    } = VideoDisplayMode.Zoom;

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface)
    {
        if (_disposed || surface is null || !surface.IsValid || surface.Kind != MediaPresentationSurfaceKind.NativeHwnd)
        {
            return false;
        }

        bool attached;
        lock (_lock)
        {
            if (!EnsureD3D11())
            {
                return false;
            }

            attached = AttachSurfaceNoLock(surface);
            if (attached)
            {
                _surfaces[surface.Id] = surface;
            }
        }

        if (attached)
        {
            PresentCurrentComposition("presentation surface attached");
        }

        return attached;
    }

    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId) || pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }

        bool resized;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
            {
                return false;
            }

            surface = surface.WithSize(pixelWidth, pixelHeight);
            _surfaces[surface.Id] = surface;

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                _presenter.QueueNativeHwndPresentationResize(pixelWidth, pixelHeight);
                resized = true;
            }
            else if (!AttachSurfaceNoLock(surface))
            {
                return false;
            }
            else
            {
                resized = true;
            }
        }

        if (resized)
        {
            PresentCurrentComposition("presentation surface resized");
        }

        return resized;
    }

    public bool DetachPresentationSurface(string surfaceId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(surfaceId))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_surfaces.Remove(surfaceId, out MediaPresentationSurfaceDescriptor? surface))
            {
                return false;
            }

            if (surface.Role == MediaPresentationSurfaceRole.Primary)
            {
                _presenter.CleanupResources();
                _ = EnsureD3D11();
                RecreateAttachedSurfacesNoLock();
            }
            else
            {
                _ = _presenter.RemoveSecondaryPresentationTarget(surface.Id);
            }

            return true;
        }
    }

    public void RefreshCurrentPosition()
    {
        _renderPipeline.RenderCdgAtCurrentPosition(force: true);
        _renderPipeline.RenderVideoAtCurrentPosition(allowWhilePlaying: true);
        PresentCurrentComposition("current position refreshed");
    }

    void IMediaRenderCoordinator.RunOnRenderThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    void IMediaRenderCoordinator.StartRendering() => StartRendering();

    void IMediaRenderCoordinator.StopRendering() => StopRendering();

    void IMediaRenderCoordinator.RefreshAttachments() => RefreshAttachments();

    public void StartRendering()
    {
        if (_disposed || _isRenderingActive)
        {
            return;
        }

        _isRenderingActive = true;
        _attachedVideo?.SetHostRenderEventDrivenPresentation(false);

        if (_attachedCdg != null)
        {
            _presentationScheduler.Start(RenderLoop);
        }
    }

    public void StopRendering()
    {
        _isRenderingActive = false;
        _presentationScheduler.Stop();
    }

    internal void RefreshAttachments()
    {
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine("[NativeMediaPresentationCoordinator] Refreshing Core presentation attachments");
        }

        var wasRenderingActive = _isRenderingActive;
        StopRendering();
        ClearAllContent();
        DetachAllEngines();

        AttachVideoIfAvailable();
        AttachCdgIfAvailable();
        AttachImageIfAvailable();
        _renderPipeline.Attach(_attachedVideo, _attachedCdg);
        PresentCurrentComposition("attachments refreshed");

        if (wasRenderingActive || _mediaEngine.State == MediaPlayerState.Playing)
        {
            StartRendering();
        }
    }

    private void RenderLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _renderPipeline.RenderCdgAtCurrentPosition();
            _ = cancellationToken.WaitHandle.WaitOne(PresentationLoopDelayMs);
        }
    }

    private bool AttachSurfaceNoLock(MediaPresentationSurfaceDescriptor surface)
    {
        return surface.Role == MediaPresentationSurfaceRole.Primary
            ? _presenter.CreateNativeHwndSurface(surface.NativeHandle, surface.PixelWidth, surface.PixelHeight)
            : _presenter.CreateSecondaryNativeHwndSurface(surface.Id, surface.NativeHandle, surface.PixelWidth, surface.PixelHeight);
    }

    private bool EnsureD3D11()
    {
        if (_presenter.NativeDevicePointer != IntPtr.Zero)
        {
            return true;
        }

        bool initialized = _presenter.Initialize();
        if (initialized)
        {
            RecreateAttachedSurfacesNoLock();
        }

        return initialized;
    }

    private bool EnsureD3D11ForHardwareFrame(VideoFrameEventArgs frame)
    {
        if (!frame.IsD3D11HardwareFrame)
        {
            return false;
        }

        if (_presenter.NativeDevicePointer == frame.D3D11DevicePointer)
        {
            return true;
        }

        bool initialized = _presenter.InitializeFromNativeDevice(frame.D3D11DevicePointer, frame.D3D11DeviceContextPointer);
        if (initialized)
        {
            RecreateAttachedSurfacesNoLock();
        }

        return initialized;
    }

    private void RecreateAttachedSurfacesNoLock()
    {
        foreach (MediaPresentationSurfaceDescriptor surface in _surfaces.Values)
        {
            _ = AttachSurfaceNoLock(surface);
        }
    }

    private void AttachVideoIfAvailable()
    {
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetHostRenderEventDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
            }
            catch
            {
            }

            _attachedVideo = null;
        }

        if (_mediaEngine.Video == null)
        {
            return;
        }

        _attachedVideo = _mediaEngine.Video;
        _attachedVideo.SetHostRenderEventDrivenPresentation(false);
        _attachedVideo.FrameReady += OnVideoFrameReady;
    }

    private void AttachCdgIfAvailable()
    {
        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch
            {
            }

            _attachedCdg = null;
        }

        if (_mediaEngine.Cdg == null)
        {
            return;
        }

        _attachedCdg = _mediaEngine.Cdg;
        _attachedCdg.FrameInvalidated += OnCdgFrameInvalidated;
        _attachedCdg.BackgroundWashChanged += OnCdgBackgroundWashChanged;
        _renderPipeline.Attach(_attachedVideo, _attachedCdg);
        _renderPipeline.RenderCdgAtCurrentPosition(force: true);
        PresentRetainedDxCdgFrame();
    }

    private void AttachImageIfAvailable()
    {
        if (!_mediaEngine.Content.HasFlag(MediaContent.Image))
        {
            return;
        }

        if (_mediaEngine.TryGetCurrentImageFrame(out VideoFrameEventArgs? frame) && frame != null)
        {
            PresentCoreImageFrame(frame);
        }
    }

    private void OnCoreImageFrameReady(object? sender, VideoFrameEventArgs e) => PresentCoreImageFrame(e);

    private void PresentCoreImageFrame(VideoFrameEventArgs e)
    {
        if (!IsValidBgraFrame(e))
        {
            return;
        }

        SetCompositionLayer0Frame(e, MediaCompositionStretchMode.UniformFit);
        PresentMediaComposition(BuildCompositionFrame(null, 0, retainedRefresh: false, "core image frame"));
    }

    private void OnCoreBackgroundFrameReady(object? sender, VideoFrameEventArgs e)
    {
        if (!IsValidBgraFrame(e))
        {
            return;
        }

        SetCompositionLayer0Frame(e, MediaCompositionStretchMode.UniformToFill);
        if (HasDxCdgContent)
        {
            PresentRetainedDxCdgFrame();
            return;
        }

        PresentMediaComposition(BuildCompositionFrame(null, 0, retainedRefresh: false, "core background frame"));
    }

    private void OnCoreBackgroundCleared(object? sender, EventArgs e)
    {
        ClearCompositionLayer0Frame();
        if (HasDxCdgContent)
        {
            PresentRetainedDxCdgFrame();
            return;
        }

        PresentMediaComposition(MediaCompositionFrame.Empty(reason: "core background cleared"));
    }

    private void OnVideoFrameReady(object? sender, VideoFrameEventArgs e)
    {
        try
        {
            if (HasDxCdgContent)
            {
                PresentVideoFrameAsCdgBackground(e);
                return;
            }

            PresentPrimaryVideoFrame(e);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NativeMediaPresentationCoordinator] Video frame present failed: {ex.Message}");
        }
    }

    private void PresentPrimaryVideoFrame(VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, _primaryStretchMode);
            PresentMediaComposition(BuildCompositionFrame(null, 0, retainedRefresh: false, "primary video frame"));
            return;
        }

        if (!e.IsD3D11HardwareFrame)
        {
            return;
        }

        lock (_lock)
        {
            if (!EnsureD3D11ForHardwareFrame(e))
            {
                return;
            }

            _ = _presenter.PresentHardwareFrame(e);
        }
    }

    private void PresentVideoFrameAsCdgBackground(VideoFrameEventArgs e)
    {
        if (IsValidBgraFrame(e))
        {
            SetCompositionLayer0Frame(e, MediaCompositionStretchMode.UniformToFill);
            PresentRetainedDxCdgFrame();
            return;
        }

        if (e.IsD3D11HardwareFrame)
        {
            RenderHardwareVideoCompositionFrame(e, forceZoomToFill: true, "hardware video + dxcdg");
        }
    }

    private void OnCdgBackgroundWashChanged(object? sender, EventArgs e) => PresentRetainedDxCdgFrame();

    private void OnCdgFrameInvalidated(object? sender, EventArgs e) =>
        RenderCurrentDxCdgFrame(consumeDirtyRegions: true, retainedRefresh: false, "dxcdg invalidated");

    private void PresentRetainedDxCdgFrame() =>
        RenderCurrentDxCdgFrame(consumeDirtyRegions: false, retainedRefresh: true, "retained dxcdg refresh");

    private void RenderCurrentDxCdgFrame(bool consumeDirtyRegions, bool retainedRefresh, string reason)
    {
        if (_attachedCdg is not IDxCdgSnapshotSource snapshotSource
            || !_mediaEngine.Content.HasFlag(MediaContent.Cdg)
            || !snapshotSource.TryCreateDxCdgSnapshot(consumeDirtyRegions, out DxCdgRenderSnapshot? snapshot))
        {
            return;
        }

        PresentMediaComposition(BuildCompositionFrame(snapshot, snapshot.BackgroundWashArgb, retainedRefresh, reason));
    }

    private void RenderHardwareVideoCompositionFrame(VideoFrameEventArgs frame, bool forceZoomToFill, string reason)
    {
        DxCdgRenderSnapshot? snapshot = null;
        uint washArgb = 0;
        if (_attachedCdg is IDxCdgSnapshotSource snapshotSource
            && snapshotSource.TryCreateDxCdgSnapshot(consumeDirtyRegions: false, out DxCdgRenderSnapshot? cdgSnapshot))
        {
            snapshot = cdgSnapshot;
            washArgb = cdgSnapshot.BackgroundWashArgb;
        }

        MediaCompositionFrame compositionFrame;
        lock (_lock)
        {
            if (!EnsureD3D11ForHardwareFrame(frame))
            {
                return;
            }

            MediaCompositionStretchMode stretch = forceZoomToFill ? MediaCompositionStretchMode.UniformToFill : _primaryStretchMode;
            _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
            _compositionBackgroundWidth = frame.Width;
            _compositionBackgroundHeight = frame.Height;
            _compositionBackgroundStride = frame.Width * 4;
            _compositionBackgroundStretchMode = stretch;
            _compositionBackgroundIsRetainedGpu = true;
            long backgroundRevision = ++_compositionBackgroundRevision;

            compositionFrame = new MediaCompositionFrame
            {
                ClearMode = MediaCompositionClearMode.Black,
                ClearArgb = 0xFF000000,
                BackgroundD3D11TexturePointer = frame.D3D11TexturePointer,
                BackgroundD3D11ArraySlice = frame.D3D11ArraySlice,
                BackgroundD3D11Width = frame.Width,
                BackgroundD3D11Height = frame.Height,
                BackgroundD3D11FramesPerSecond = frame.FramesPerSecond,
                BackgroundWidth = frame.Width,
                BackgroundHeight = frame.Height,
                BackgroundStride = frame.Width * 4,
                BackgroundStretchMode = stretch,
                BackgroundUniformToFill = stretch == MediaCompositionStretchMode.UniformToFill,
                BackgroundRevision = backgroundRevision,
                WashArgb = washArgb,
                CdgOverlay = snapshot,
                Reason = reason
            };
        }

        PresentMediaComposition(compositionFrame);
    }

    private void PresentCurrentComposition(string reason)
    {
        if (HasDxCdgContent)
        {
            PresentRetainedDxCdgFrame();
            return;
        }

        if (_mediaEngine.Content.HasFlag(MediaContent.Image)
            && _mediaEngine.TryGetCurrentImageFrame(out VideoFrameEventArgs? frame)
            && frame != null)
        {
            PresentCoreImageFrame(frame);
            return;
        }

        PresentMediaComposition(BuildCompositionFrame(null, 0, retainedRefresh: false, reason));
    }

    private void PresentMediaComposition(MediaCompositionFrame frame)
    {
        lock (_lock)
        {
            if (!EnsureD3D11())
            {
                return;
            }

            if (!_presenter.PresentMediaCompositionVisible(frame) && _configuration.EnableLogging)
            {
                Debug.WriteLine($"[NativeMediaPresentationCoordinator] Composition present failed reason={frame.Reason}");
            }
        }
    }

    private MediaCompositionFrame BuildCompositionFrame(
        DxCdgRenderSnapshot? cdgOverlay,
        uint washArgb,
        bool retainedRefresh,
        string reason)
    {
        ReadOnlyMemory<byte> backgroundBgra;
        int backgroundWidth;
        int backgroundHeight;
        int backgroundStride;
        MediaCompositionStretchMode backgroundStretchMode;
        bool backgroundIsRetainedGpu;
        long backgroundRevision;

        lock (_lock)
        {
            backgroundBgra = _compositionBackgroundBgra;
            backgroundWidth = _compositionBackgroundWidth;
            backgroundHeight = _compositionBackgroundHeight;
            backgroundStride = _compositionBackgroundStride;
            backgroundStretchMode = _compositionBackgroundStretchMode;
            backgroundIsRetainedGpu = _compositionBackgroundIsRetainedGpu;
            backgroundRevision = _compositionBackgroundRevision;
        }

        var hasBackground = !backgroundBgra.IsEmpty
            && backgroundWidth > 0
            && backgroundHeight > 0
            && backgroundStride >= backgroundWidth * 4
            && backgroundBgra.Length >= checked(backgroundStride * backgroundHeight);
        var hasRetainedGpuBackground = !hasBackground
            && backgroundIsRetainedGpu
            && backgroundWidth > 0
            && backgroundHeight > 0;

        return new MediaCompositionFrame
        {
            ClearMode = MediaCompositionClearMode.Black,
            ClearArgb = 0xFF000000,
            BackgroundBgra = hasBackground ? backgroundBgra : ReadOnlyMemory<byte>.Empty,
            BackgroundWidth = hasBackground || hasRetainedGpuBackground ? backgroundWidth : 0,
            BackgroundHeight = hasBackground || hasRetainedGpuBackground ? backgroundHeight : 0,
            BackgroundStride = hasBackground ? backgroundStride : 0,
            BackgroundStretchMode = hasBackground || hasRetainedGpuBackground ? backgroundStretchMode : MediaCompositionStretchMode.UniformFit,
            BackgroundUniformToFill = (hasBackground || hasRetainedGpuBackground) && backgroundStretchMode == MediaCompositionStretchMode.UniformToFill,
            BackgroundRevision = hasBackground || hasRetainedGpuBackground ? backgroundRevision : -1,
            UseRetainedBackground = hasRetainedGpuBackground,
            WashArgb = washArgb,
            CdgOverlay = cdgOverlay,
            RetainedRefresh = retainedRefresh,
            Reason = reason
        };
    }

    private void SetCompositionLayer0Frame(VideoFrameEventArgs frame, MediaCompositionStretchMode stretchMode)
    {
        if (_disposed || !IsValidBgraFrame(frame))
        {
            ClearCompositionLayer0Frame();
            return;
        }

        var required = checked(frame.Stride * frame.Height);
        if (frame.PixelData.Length < required)
        {
            ClearCompositionLayer0Frame();
            return;
        }

        lock (_lock)
        {
            if (frame.PixelDataCanBeRetained)
            {
                _compositionBackgroundBgra = frame.PixelData.Slice(0, required);
            }
            else
            {
                if (_compositionBackgroundCopyBuffer == null || _compositionBackgroundCopyBuffer.Length != required)
                {
                    _compositionBackgroundCopyBuffer = new byte[required];
                }

                frame.PixelData.Span[..required].CopyTo(_compositionBackgroundCopyBuffer);
                _compositionBackgroundBgra = new ReadOnlyMemory<byte>(_compositionBackgroundCopyBuffer, 0, required);
            }

            _compositionBackgroundWidth = frame.Width;
            _compositionBackgroundHeight = frame.Height;
            _compositionBackgroundStride = frame.Stride;
            _compositionBackgroundStretchMode = stretchMode;
            _compositionBackgroundIsRetainedGpu = false;
            _compositionBackgroundRevision++;
        }
    }

    private void ClearCompositionLayer0Frame()
    {
        lock (_lock)
        {
            _compositionBackgroundBgra = ReadOnlyMemory<byte>.Empty;
            _compositionBackgroundCopyBuffer = null;
            _compositionBackgroundWidth = 0;
            _compositionBackgroundHeight = 0;
            _compositionBackgroundStride = 0;
            _compositionBackgroundStretchMode = MediaCompositionStretchMode.UniformFit;
            _compositionBackgroundIsRetainedGpu = false;
            _compositionBackgroundRevision++;
        }

        _presenter.ClearCompositionBackgroundFrame();
    }

    private void ClearAllContent()
    {
        ClearCompositionLayer0Frame();
        _presenter.ClearPresentedFrame();
    }

    private void DetachAllEngines()
    {
        if (_attachedVideo != null)
        {
            try
            {
                _attachedVideo.SetHostRenderEventDrivenPresentation(false);
                _attachedVideo.FrameReady -= OnVideoFrameReady;
            }
            catch
            {
            }

            _attachedVideo = null;
        }

        if (_attachedCdg != null)
        {
            try
            {
                _attachedCdg.FrameInvalidated -= OnCdgFrameInvalidated;
                _attachedCdg.BackgroundWashChanged -= OnCdgBackgroundWashChanged;
            }
            catch
            {
            }

            _attachedCdg = null;
        }

        _renderPipeline.Clear();
    }

    private bool HasDxCdgContent => _mediaEngine.Content.HasFlag(MediaContent.Cdg);

    private static bool IsValidBgraFrame(VideoFrameEventArgs e) =>
        !e.PixelData.IsEmpty && e.Width > 0 && e.Height > 0 && e.Stride >= e.Width * 4;

    private static MediaCompositionStretchMode ToCompositionStretchMode(VideoDisplayMode mode) => mode switch
    {
        VideoDisplayMode.Original => MediaCompositionStretchMode.Original,
        VideoDisplayMode.Stretch => MediaCompositionStretchMode.Fill,
        VideoDisplayMode.ZoomToFill => MediaCompositionStretchMode.UniformToFill,
        _ => MediaCompositionStretchMode.UniformFit
    };

    private void OnFramePresented(object? sender, EventArgs e) => FramePresented?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopRendering();
        DetachAllEngines();
        _mediaEngine.ImageFrameReady -= OnCoreImageFrameReady;
        _mediaEngine.BackgroundFrameReady -= OnCoreBackgroundFrameReady;
        _mediaEngine.BackgroundCleared -= OnCoreBackgroundCleared;
        _presenter.FramePresented -= OnFramePresented;
        _presenter.Dispose();
        _presentationScheduler.Dispose();
        _disposed = true;
    }
}
