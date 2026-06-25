using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Numerics;
using System.Text;
using Vortice.Direct3D;
using Vortice.DXGI;
using SharpGen.Runtime;
using Vortice.Mathematics;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D;

/// <summary>
/// DirectX 11 surface manager that presents through WPF D3DImage when available.
/// </summary>
internal sealed class VorticeD3D11Renderer : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11Multithread? _multithreadProtection;
    private ID3D11VideoProcessorEnumerator? _videoProcessorEnumerator;
    private ID3D11VideoProcessor? _videoProcessor;
    private ID3D11VideoProcessorOutputView? _videoOutputView;
    private ID3D11Texture2D? _presentationTexture;
    private ID3D11Texture2D? _cpuUploadTexture;
    private int _cpuUploadWidth, _cpuUploadHeight;
    private IDXGIResource? _sharedResource;
    private D3D9InteropSurface? _interopSurface;
    private D3DImage? _d3dImage;
    private IDXGISwapChain? _swapChain;
    private IDXGISwapChain1? _compositionOverlaySwapChain;
    private ID3D11Texture2D? _compositionOverlayTexture;
    private ID3D11Texture2D? _cdgOverlayTexture;
    private ID3D11ShaderResourceView? _cdgOverlayShaderResourceView;
    private ID3D11RenderTargetView? _presentationRenderTargetView;
    private ID3D11VertexShader? _cdgOverlayVertexShader;
    private ID3D11PixelShader? _cdgOverlayPixelShader;
    private ID3D11Buffer? _cdgOverlayConstantBuffer;
    private ID3D11SamplerState? _cdgOverlaySamplerState;
    private ID3D11BlendState? _cdgOverlayBlendState;
    private bool _cdgOverlayPipelineFailed;
    private int _cdgOverlayWidth;
    private int _cdgOverlayHeight;
    private int _cdgOverlayStride;
    private IDCompositionDevice? _compositionDevice;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _compositionRootVisual;
    private IDCompositionVisual? _compositionBaseVisual;
    private IDCompositionVisual? _compositionOverlayVisual;
    private int _compositionOutputWidth;
    private int _compositionOutputHeight;
    private bool _directCompositionActive;
    private IntPtr _nativeHwnd;
    private bool _nativeSwapChain;
    private int _nativeLiveResizeInProgress;
    private Stretch _nativePresentationStretch = Stretch.Uniform;
    private bool _disposed;
    private int _currentWidth, _currentHeight;
    private int _videoProcessorWidth, _videoProcessorHeight;
    private int _videoProcessorOutputWidth, _videoProcessorOutputHeight;
    private double _videoProcessorFps;
    private IntPtr _nativeDevicePointer;
    private readonly Lock _lock = new();
    private int _surfaceGeneration;
    private readonly MediaPlayerConfiguration _configuration;
    private const int UploadDiagnosticsFrameInterval = 120;
    private const int DirectCompositionBufferCount = 3;
    private int _uploadDiagnosticFrames;
    private double _uploadDiagnosticMs;
    private double _uploadDiagnosticMaxMs;
    private const int NativePresentDiagnosticsFrameInterval = 120;
    private int _nativePresentDiagnosticFrames;
    private double _nativePresentDiagnosticMs;
    private double _nativePresentDiagnosticMaxMs;
    private int _pendingNativeHwndWidth;
    private int _pendingNativeHwndHeight;
    private bool _nativeHwndResizePending;

    public VorticeD3D11Renderer()
        : this(MediaPlayerConfiguration.Auto)
    {
    }

    public VorticeD3D11Renderer(MediaPlayerConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Raised on the UI dispatcher thread immediately after a frame's dirty rect has been
    /// applied to the D3DImage. WPF's CompositionTarget.Rendering scheduler throttles down
    /// when it doesn't detect tracked visual-tree changes; D3DImage pixel updates alone are
    /// invisible to that scheduler, so subscribers should use this to invalidate the hosting
    /// element and keep the render tick running at the true display refresh rate.
    /// </summary>
    public event EventHandler? FramePresented;

    /// <summary>
    /// Gets the WPF ImageSource backed by a D3DImage shared surface.
    /// </summary>
    public ImageSource? ImageSource => _d3dImage;

    /// <summary>
    /// Gets whether the D3D11 device and active presentation target are ready.
    /// </summary>
    public bool IsReady => _device != null
        && _context != null
        && _presentationTexture != null
        && (_nativeSwapChain ? _swapChain != null : _d3dImage != null);

    public IntPtr NativeDevicePointer => _nativeDevicePointer;

    public bool IsNativeSwapChainActive => _nativeSwapChain && _swapChain != null;

    public bool HasCdgOverlayTexture => _cdgOverlayTexture != null && _cdgOverlayShaderResourceView != null;

    public void SetNativeLiveResizeInProgress(bool isResizing)
    {
        Volatile.Write(ref _nativeLiveResizeInProgress, isResizing ? 1 : 0);
    }

    public bool IsDirectCompositionActive => _directCompositionActive && _compositionDevice != null && _compositionOverlaySwapChain != null;

    public string PresentationBackend => IsDirectCompositionActive
        ? "DirectCompositionNativeLayers"
        : IsNativeSwapChainActive
            ? "NativeHwndSwapChain"
        : _d3dImage != null
            ? "D3DImage"
            : "None";


    public void SetNativePresentationStretch(Stretch stretch)
    {
        lock (_lock)
        {
            if (_nativePresentationStretch == stretch)
            {
                return;
            }

            _nativePresentationStretch = stretch;
            CleanupVideoProcessor();
        }
    }

    /// <summary>
    /// Initialize D3D11 device for GPU-accelerated pixel operations.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    public bool Initialize()
    {
        if (_disposed) return false;

        lock (_lock)
        {
            try
            {
                var creationFlagOptions = new[]
                {
                    DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    DeviceCreationFlags.BgraSupport
                };

                SharpGen.Runtime.Result result = default;
                Vortice.Direct3D.FeatureLevel actualFeatureLevel = default;
                foreach (var creationFlags in creationFlagOptions)
                {
                    result = D3D11.D3D11CreateDevice(
                        adapter: null,
                        driverType: Vortice.Direct3D.DriverType.Hardware,
                        flags: creationFlags,
                        featureLevels: [Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1],
                        device: out _device,
                        featureLevel: out actualFeatureLevel,
                        immediateContext: out _context);

                    if (!result.Failure && _device != null && _context != null)
                    {
                        break;
                    }

                    _context?.Dispose();
                    _context = null;
                    _device?.Dispose();
                    _device = null;
                }

                if (result.Failure || _device == null || _context == null)
                {
                    Debug.WriteLine($"[VorticeD3D11] Failed to create D3D11 device: {result}");
                    return false;
                }

                InitializeVideoInterfaces();
                _nativeDevicePointer = _device.NativePointer;
                if (_configuration.EnableLogging) Debug.WriteLine($"[VorticeD3D11] D3D11 device created successfully with feature level: {actualFeatureLevel}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to initialize: {ex.Message}");
                CleanupResources();
                return false;
            }
        }
    }

    public bool InitializeFromNativeDevice(IntPtr devicePointer, IntPtr deviceContextPointer)
    {
        if (_disposed || devicePointer == IntPtr.Zero) return false;

        lock (_lock)
        {
            try
            {
                CleanupResources();
                _device = AddRefAndWrap(devicePointer, static pointer => new ID3D11Device(pointer));
                _context = deviceContextPointer != IntPtr.Zero
                    ? AddRefAndWrap(deviceContextPointer, static pointer => new ID3D11DeviceContext(pointer))
                    : _device.ImmediateContext;

                InitializeVideoInterfaces();
                _nativeDevicePointer = devicePointer;
                if (_configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Attached to FFmpeg D3D11 hardware device for direct texture presentation");
                return _videoDevice != null && _videoContext != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to attach native D3D11 device: {ex.Message}");
                CleanupResources();
                return false;
            }
        }
    }

    /// <summary>
    /// Creates or recreates surfaces for the given dimensions.
    /// </summary>
    /// <param name="width">Surface width in pixels</param>
    /// <param name="height">Surface height in pixels</param>
    /// <returns>True if surfaces were created successfully.</returns>
    public bool CreateSurfaces(int width, int height)
    {
        if (_disposed || _device == null || _context == null || width <= 0 || height <= 0) return false;

        lock (_lock)
        {
            if (!_nativeSwapChain && width == _currentWidth && height == _currentHeight)
            {
                return true;
            }

            CleanupSurfaces();
            _currentWidth = 0;
            _currentHeight = 0;
            _nativeSwapChain = false;
            _nativeHwnd = IntPtr.Zero;

            try
            {
                var textureDescription = new Texture2DDescription(
                    Format.B8G8R8A8_UNorm,
                    (uint)width,
                    (uint)height,
                    arraySize: 1,
                    mipLevels: 1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    sampleCount: 1,
                    sampleQuality: 0,
                    ResourceOptionFlags.Shared);

                _presentationTexture = _device.CreateTexture2D(in textureDescription);
                _sharedResource = _presentationTexture.QueryInterface<IDXGIResource>();
                var sharedHandle = _sharedResource.SharedHandle;
                if (sharedHandle == IntPtr.Zero)
                {
                    Debug.WriteLine("[VorticeD3D11] Failed to get shared texture handle");
                    CleanupSurfaces();
                    return false;
                }

                _interopSurface = D3D9InteropSurface.Create(sharedHandle, width, height);
                if (_interopSurface is null || _interopSurface.SurfacePointer == IntPtr.Zero)
                {
                    CleanupSurfaces();
                    return false;
                }

                var image = new D3DImage();
                image.Lock();
                try
                {
                    image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _interopSurface.SurfacePointer);
                }
                finally
                {
                    image.Unlock();
                }

                _d3dImage = image;
                _currentWidth = width;
                _currentHeight = height;
                _compositionOutputWidth = width;
                _compositionOutputHeight = height;

                ClearPresentationTextureToBlack();
                NotifyDirty(_currentWidth, _currentHeight);

                if (_configuration.EnableLogging)
                {
                    Debug.WriteLine($"[VorticeD3D11] Created D3DImage shared presentation surface for {width}x{height}");
                    Debug.WriteLine("[VorticeD3D11] Presentation backend: D3DImage");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to create D3DImage surfaces: {ex.Message}");
                CleanupSurfaces();
                return false;
            }
        }
    }

    public bool CreateNativeHwndSurfaces(IntPtr hwnd, int width, int height)
    {
        if (_disposed || _device == null || _context == null || hwnd == IntPtr.Zero || width <= 0 || height <= 0) return false;

        lock (_lock)
        {
            if (_nativeSwapChain && hwnd == _nativeHwnd && width == _currentWidth && height == _currentHeight)
            {
                return true;
            }

            CleanupSurfaces();
            _currentWidth = 0;
            _currentHeight = 0;
            _nativeSwapChain = false;
            _nativeHwnd = IntPtr.Zero;

            try
            {
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory>();

                _swapChain = CreateBestNativeSwapChain(factory, hwnd, width, height);
                if (_swapChain == null)
                {
                    CleanupSurfaces();
                    return false;
                }

                _presentationTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                _nativeSwapChain = true;
                _nativeHwnd = hwnd;
                _currentWidth = width;
                _currentHeight = height;

                ClearPresentationTextureToBlack();
                PresentSwapChain(syncImmediate: true);

                if (_configuration.EnableLogging)
                {
                    Debug.WriteLine($"[VorticeD3D11] Created native HWND swap-chain presentation surface for {width}x{height}");
                    Debug.WriteLine("[VorticeD3D11] Presentation backend: NativeHwndSwapChain");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to create native HWND swap-chain surface: {ex.Message}");
                CleanupSurfaces();
                return false;
            }
        }
    }


    /// <summary>
    /// Creates a DirectComposition-backed native target with two internal layers:
    /// a base composition swap-chain for video/image frames and a premultiplied-alpha
    /// overlay swap-chain for CDG.  This keeps CDG updates out of the hot background-video
    /// path while still using a single HWND from the host framework.
    /// </summary>
    public bool CreateDirectCompositionHwndSurfaces(IntPtr hwnd, int width, int height)
    {
        if (_disposed || _device == null || _context == null || hwnd == IntPtr.Zero || width <= 0 || height <= 0) return false;

        lock (_lock)
        {
            if (_directCompositionActive && hwnd == _nativeHwnd && width == _currentWidth && height == _currentHeight)
            {
                return true;
            }

            CleanupSurfaces();
            _currentWidth = 0;
            _currentHeight = 0;
            _nativeSwapChain = false;
            _nativeHwnd = IntPtr.Zero;
            _directCompositionActive = false;

            try
            {
                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();

                var baseDescription = CreateCompositionSwapChainDescription(width, height, AlphaMode.Ignore);
                _swapChain = factory.CreateSwapChainForComposition(_device, baseDescription);
                if (_swapChain == null)
                {
                    CleanupSurfaces();
                    return false;
                }

                var overlayDescription = CreateCompositionSwapChainDescription(width, height, AlphaMode.Premultiplied);
                _compositionOverlaySwapChain = factory.CreateSwapChainForComposition(_device, overlayDescription);
                if (_compositionOverlaySwapChain == null)
                {
                    CleanupSurfaces();
                    return false;
                }

                _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
                _compositionDevice.CreateTargetForHwnd(hwnd, true, out _compositionTarget);
                _compositionRootVisual = _compositionDevice.CreateVisual();
                _compositionBaseVisual = _compositionDevice.CreateVisual();
                _compositionOverlayVisual = _compositionDevice.CreateVisual();

                _compositionBaseVisual.SetContent(_swapChain);
                _compositionOverlayVisual.SetContent(_compositionOverlaySwapChain);
                _compositionRootVisual.AddVisual(_compositionBaseVisual, false, null);
                _compositionRootVisual.AddVisual(_compositionOverlayVisual, true, _compositionBaseVisual);
                _compositionTarget.SetRoot(_compositionRootVisual);
                _compositionOutputWidth = width;
                _compositionOutputHeight = height;
                ApplyDirectCompositionTransforms();
                _compositionDevice.Commit();

                _presentationTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
                _compositionOverlayTexture = _compositionOverlaySwapChain.GetBuffer<ID3D11Texture2D>(0);
                _nativeSwapChain = true;
                _directCompositionActive = true;
                _nativeHwnd = hwnd;
                _currentWidth = width;
                _currentHeight = height;

                ClearPresentationTextureToBlack();
                ClearDirectCompositionOverlayToTransparent();
                PresentSwapChain(syncImmediate: true);
                _compositionOverlaySwapChain.Present(1, (int)PresentFlags.None);
                _compositionDevice.Commit();

                if (_configuration.EnableLogging)
                {
                    Debug.WriteLine($"[VorticeD3D11] Created DirectComposition native layered presentation surface for {width}x{height}");
                    Debug.WriteLine("[VorticeD3D11] Presentation backend: DirectCompositionNativeLayers");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to create DirectComposition native layered surface: {ex.Message}");
                CleanupSurfaces();
                return false;
            }
        }
    }

    private static SwapChainDescription1 CreateCompositionSwapChainDescription(int width, int height, AlphaMode alphaMode)
    {
        return new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = DirectCompositionBufferCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = alphaMode,
            Flags = SwapChainFlags.None
        };
    }

    private IDXGISwapChain? CreateBestNativeSwapChain(IDXGIFactory factory, IntPtr hwnd, int width, int height)
    {
        var device = _device ?? throw new InvalidOperationException("D3D11 device is not initialized.");

        // Prefer a double-buffered flip model. That is closer to modern video renderers and usually
        // gives DXGI/DWM a cleaner cadence than the legacy one-buffer discard chain. Some wrapper or
        // driver combinations reject flip model through this older CreateSwapChain path, so fall back
        // to the legacy chain instead of disabling native mode entirely.
        var flipDescription = CreateNativeSwapChainDescription(hwnd, width, height, bufferCount: 2, SwapEffect.FlipSequential);
        try
        {
            var swapChain = factory.CreateSwapChain(device, flipDescription);
            if (_configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Native HWND swap-chain mode: FlipSequential, buffers=2");
            return swapChain;
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging) Debug.WriteLine($"[VorticeD3D11] FlipSequential swap-chain unavailable; falling back to legacy Discard. Reason: {ex.Message}");
        }

        var legacyDescription = CreateNativeSwapChainDescription(hwnd, width, height, bufferCount: 1, SwapEffect.Discard);
        var legacySwapChain = factory.CreateSwapChain(device, legacyDescription);
        if (_configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Native HWND swap-chain mode: Discard, buffers=1");
        return legacySwapChain;
    }

    private static SwapChainDescription CreateNativeSwapChainDescription(IntPtr hwnd, int width, int height, int bufferCount, SwapEffect swapEffect)
    {
        return new SwapChainDescription
        {
            BufferCount = (uint)bufferCount,
            BufferDescription = new ModeDescription(
                (uint)width,
                (uint)height,
                new Rational(60, 1),
                Format.B8G8R8A8_UNorm),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = hwnd,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = swapEffect
        };
    }


    /// <summary>
    /// Updates the DirectComposition visual output size without recreating the composition swap-chains.
    /// The base and CDG overlay swap-chains stay at their media/source size while DirectComposition
    /// scales the visuals to the current child-HWND rectangle. This avoids live-resize flicker,
    /// stale back-buffer garbage, and delayed CDG resizing caused by repeatedly tearing down layered
    /// swap-chains while the host window is being resized.
    /// </summary>
    public void SetDirectCompositionOutputSize(int width, int height)
    {
        if (_disposed || !_directCompositionActive || _compositionDevice == null || _currentWidth <= 0 || _currentHeight <= 0)
        {
            return;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        lock (_lock)
        {
            if (!_directCompositionActive || _compositionDevice == null)
            {
                return;
            }

            if (_compositionOutputWidth == width && _compositionOutputHeight == height)
            {
                return;
            }

            var liveResize = Volatile.Read(ref _nativeLiveResizeInProgress) != 0;
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] SetDirectCompositionOutputSize: {_compositionOutputWidth}x{_compositionOutputHeight} -> {width}x{height}, content={_currentWidth}x{_currentHeight}, resize={liveResize}");
            }

            _compositionOutputWidth = width;
            _compositionOutputHeight = height;
            ApplyDirectCompositionTransforms();
            _compositionDevice.Commit();

            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] DirectComposition visual output resized by transform: content={_currentWidth}x{_currentHeight}, output={width}x{height}");
            }
        }
    }

    private void ApplyDirectCompositionTransforms()
    {
        if (!_directCompositionActive || _compositionRootVisual == null || _currentWidth <= 0 || _currentHeight <= 0)
        {
            return;
        }

        var outputWidth = _compositionOutputWidth > 0 ? _compositionOutputWidth : _currentWidth;
        var outputHeight = _compositionOutputHeight > 0 ? _compositionOutputHeight : _currentHeight;
        var scaleX = (float)outputWidth / _currentWidth;
        var scaleY = (float)outputHeight / _currentHeight;

        // Vortice.DirectComposition 3.8.x exposes IDCompositionVisual.SetTransform(Matrix3x2).
        // Use the strongly-typed overload. The previous dynamic fallback threw repeated
        // RuntimeBinderException first-chance exceptions during playback/resize and caused the
        // dispatcher/render loop to fall behind. DirectComposition visuals are fully opaque by
        // default, so do not dynamically call SetOpacity either.
        var matrix = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY);
        var result = _compositionRootVisual.SetTransform(matrix);
        if (result.Failure && _configuration.EnableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition root transform update failed: {result}");
        }
    }

    /// <summary>
    /// Updates the DirectComposition CDG overlay swap-chain.  Pixel data must be BGRA with
    /// premultiplied alpha at the current presentation size.  This method presents only the
    /// overlay swap-chain; the base video swap-chain keeps its own native cadence.
    /// </summary>
    public bool UploadDirectCompositionOverlayFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed
            || !_directCompositionActive
            || _context == null
            || _compositionOverlaySwapChain == null
            || width <= 0
            || height <= 0
            || stride < width * 4
            || pixelData.IsEmpty)
        {
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (early: disposed={_disposed}, active={_directCompositionActive}, ctx={_context != null}, swapChain={_compositionOverlaySwapChain != null}, {width}x{height} stride={stride}, data={!pixelData.IsEmpty})");
            }
            return false;
        }

        var liveResize = Volatile.Read(ref _nativeLiveResizeInProgress) != 0;
        if (_configuration.EnableVerboseLogging)
        {
            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame entering: {width}x{height} into {_currentWidth}x{_currentHeight}, resize={liveResize}");
        }

        lock (_lock)
        {
            try
            {
                var uploadStart = Stopwatch.GetTimestamp();

                // The overlay swap-chain is updated only when CDG changes, unlike the base
                // video swap-chain which receives a new frame every video tick. With a flip-model
                // composition swap-chain, stale back buffers can reappear as intermittent
                // transparency or ghosted lyrics if only one buffer is updated. Prime every
                // overlay buffer with the same CDG frame, using immediate presents for the
                // non-final buffers so this does not add a visible multi-vblank stall.
                for (var bufferIndex = 0; bufferIndex < DirectCompositionBufferCount; bufferIndex++)
                {
                    ReacquireDirectCompositionOverlayBackBuffer();
                    if (_compositionOverlayTexture == null)
                    {
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (null overlay texture at bufferIndex={bufferIndex}, resize={liveResize})");
                        }
                        return false;
                    }

                    if (width == _currentWidth && height == _currentHeight)
                    {
                        _context.UpdateSubresource(pixelData, _compositionOverlayTexture, 0, (uint)stride, 0, null);
                        _context.Flush();
                    }
                    else
                    {
                        if (!EnsureCpuUploadTexture(width, height) || _cpuUploadTexture == null)
                        {
                            if (_configuration.EnableVerboseLogging)
                            {
                                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (blit path failed, bufferIndex={bufferIndex}, resize={liveResize})");
                            }
                            return false;
                        }

                        _context.UpdateSubresource(pixelData, _cpuUploadTexture, 0, (uint)stride, 0, null);
                        if (!BlitTextureToDirectCompositionOverlay(_cpuUploadTexture, width, height, 60.0))
                        {
                            if (_configuration.EnableVerboseLogging)
                            {
                                Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (blit failed, bufferIndex={bufferIndex}, resize={liveResize})");
                            }
                            return false;
                        }
                    }

                    var syncInterval = (bufferIndex == DirectCompositionBufferCount - 1 && !liveResize) ? 1u : 0u;
                    var result = _compositionOverlaySwapChain.Present(syncInterval, (int)PresentFlags.None);
                    if (result.Failure)
                    {
                        Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay Present failed: {result}");
                        if (_configuration.EnableVerboseLogging)
                        {
                            Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> false (Present failed at bufferIndex={bufferIndex}, resize={liveResize})");
                        }
                        return false;
                    }
                }

                ReacquireDirectCompositionOverlayBackBuffer();
                _compositionDevice?.Commit();
                RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                if (_configuration.EnableVerboseLogging)
                {
                    Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> true (commit done, {width}x{height}, resize={liveResize})");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay upload failed: {ex.Message}");
                if (_configuration.EnableVerboseLogging)
                {
                    Debug.WriteLine($"[CDG-DC-TRACE] UploadDirectCompositionOverlayFrame -> exception: {ex.Message}, resize={liveResize}");
                }
                return false;
            }
        }
    }

    private void ReacquireDirectCompositionOverlayBackBuffer()
    {
        if (_compositionOverlaySwapChain == null)
        {
            return;
        }

        _compositionOverlayTexture?.Dispose();
        _compositionOverlayTexture = _compositionOverlaySwapChain.GetBuffer<ID3D11Texture2D>(0);
    }


    /// <summary>
    /// Uploads a retained CDG overlay texture for the native single-swap-chain compositor.
    /// The texture is source-sized to the current video frame before it gets here, so the final
    /// video-processor pass can sample video + CDG in one coordinate system and present once.
    /// Pixel data is straight-alpha BGRA.
    /// </summary>
    public bool UploadCdgOverlayTexture(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || _device == null || _context == null) return false;
        if (pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4) return false;

        lock (_lock)
        {
            try
            {
                if (_cdgOverlayTexture == null
                    || _cdgOverlayShaderResourceView == null
                    || _cdgOverlayWidth != width
                    || _cdgOverlayHeight != height
                    || _cdgOverlayStride != stride)
                {
                    _cdgOverlayShaderResourceView?.Dispose();
                    _cdgOverlayShaderResourceView = null;
                    _cdgOverlayTexture?.Dispose();
                    _cdgOverlayTexture = null;

                    var textureDescription = new Texture2DDescription(
                        Format.B8G8R8A8_UNorm,
                        (uint)width,
                        (uint)height,
                        arraySize: 1,
                        mipLevels: 1,
                        BindFlags.ShaderResource,
                        ResourceUsage.Default,
                        CpuAccessFlags.None,
                        sampleCount: 1,
                        sampleQuality: 0,
                        ResourceOptionFlags.None);

                    _cdgOverlayTexture = _device.CreateTexture2D(in textureDescription);
                    _cdgOverlayShaderResourceView = _device.CreateShaderResourceView(_cdgOverlayTexture);
                    _cdgOverlayWidth = width;
                    _cdgOverlayHeight = height;
                    _cdgOverlayStride = stride;
                }

                _context.UpdateSubresource(pixelData, _cdgOverlayTexture, 0, (uint)stride, 0, null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] CDG cached overlay texture upload failed: {ex.Message}");
                _cdgOverlayShaderResourceView?.Dispose();
                _cdgOverlayShaderResourceView = null;
                _cdgOverlayTexture?.Dispose();
                _cdgOverlayTexture = null;
                _cdgOverlayWidth = 0;
                _cdgOverlayHeight = 0;
                _cdgOverlayStride = 0;
                return false;
            }
        }
    }

    public void ClearCdgOverlayTexture()
    {
        lock (_lock)
        {
            _cdgOverlayShaderResourceView?.Dispose();
            _cdgOverlayShaderResourceView = null;
            _cdgOverlayTexture?.Dispose();
            _cdgOverlayTexture = null;
            _cdgOverlayWidth = 0;
            _cdgOverlayHeight = 0;
            _cdgOverlayStride = 0;
        }
    }

    public void RefreshNativePresentation()
    {
        if (_disposed || !_nativeSwapChain || _swapChain == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed || !_nativeSwapChain || _swapChain == null)
            {
                return;
            }

            if (_nativeHwndResizePending)
            {
                // Do not apply or present a resize refresh here. The next real frame will resize
                // and present content; presenting the old buffer after a new size was queued causes
                // another visible size jump during manual resize.
                return;
            }

            // Re-present the current completed backbuffer when no resize is pending. This is useful
            // for restore/show transitions, but it deliberately avoids black clears or ResizeBuffers.
            PresentSwapChain(syncImmediate: true);
        }
    }

    /// <summary>
    /// Records the latest native HWND client size. The actual DXGI ResizeBuffers call is
    /// deferred until the next real content blit, so live WPF resize does not present a
    /// black-only frame for every intermediate drag size.
    /// </summary>
    public void QueueNativeHwndPresentationResize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_disposed || !_nativeSwapChain || _swapChain == null)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed || !_nativeSwapChain || _swapChain == null)
            {
                return;
            }

            _pendingNativeHwndWidth = width;
            _pendingNativeHwndHeight = height;
            _nativeHwndResizePending = width != _currentWidth || height != _currentHeight;
        }
    }

    /// <summary>
    /// Resizes the stable native HWND swap-chain to the current host/client size without
    /// recreating the renderer. The video source texture remains media-sized; the video
    /// processor then draws it into an aspect-correct rectangle inside this host-sized
    /// backbuffer.
    /// </summary>
    public bool ResizeNativeHwndPresentation(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (_disposed || !_nativeSwapChain || _swapChain == null)
        {
            return false;
        }

        lock (_lock)
        {
            return ResizeNativeHwndPresentationCore(width, height);
        }
    }

    private bool ApplyPendingNativeHwndResizeIfNeeded()
    {
        if (!_nativeSwapChain || _swapChain == null || !_nativeHwndResizePending)
        {
            return true;
        }

        var width = Math.Max(1, _pendingNativeHwndWidth);
        var height = Math.Max(1, _pendingNativeHwndHeight);
        if (width == _currentWidth && height == _currentHeight)
        {
            _nativeHwndResizePending = false;
            return true;
        }

        return ResizeNativeHwndPresentationCore(width, height);
    }

    private bool ResizeNativeHwndPresentationCore(int width, int height)
    {
        if (_disposed || !_nativeSwapChain || _swapChain == null)
        {
            return false;
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _currentWidth && height == _currentHeight)
        {
            _nativeHwndResizePending = false;
            return true;
        }

        try
        {
            // DXGI ResizeBuffers requires all direct and indirect references to the current
            // backbuffer to be released first. The video processor output view references the
            // backbuffer, so release processor state before resizing. Upload/overlay textures
            // are independent source-sized textures and can be retained.
            CleanupVideoProcessor();
            _presentationRenderTargetView?.Dispose();
            _presentationRenderTargetView = null;
            _presentationTexture?.Dispose();
            _presentationTexture = null;

            _swapChain.ResizeBuffers(
                0,
                (uint)width,
                (uint)height,
                Format.Unknown,
                SwapChainFlags.None);

            _presentationTexture = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _currentWidth = width;
            _currentHeight = height;
            _nativeHwndResizePending = false;

            // Do not clear/present here. Presenting black during live resize is the flicker.
            // The caller will immediately draw real video/CDG/image content into the new
            // backbuffer and present that complete frame once.
            if (_configuration.EnableVerboseLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain resized at frame boundary to {width}x{height}");
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain resize failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Presents a BGRA video frame through the normal video fast path, then draws the retained
    /// CDG alpha texture as one lightweight GPU quad over the same aspect-correct output rect.
    /// This keeps CDG out of the video upload/compositor path, especially during WPF live resize.
    /// </summary>
    public bool UploadFrameWithCachedCdgOverlay(ReadOnlySpan<byte> pixelData, int width, int height, int stride, double framesPerSecond = 60.0)
    {
        if (_disposed || _context == null || _presentationTexture == null) return false;
        if (pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4) return false;

        lock (_lock)
        {
            try
            {
                var uploadStart = Stopwatch.GetTimestamp();

                if (!ApplyPendingNativeHwndResizeIfNeeded())
                {
                    return false;
                }

                if (width == _currentWidth && height == _currentHeight)
                {
                    _context.UpdateSubresource(pixelData, _presentationTexture, 0, (uint)stride, 0, null);
                    _context.Flush();
                }
                else
                {
                    if (!EnsureCpuUploadTexture(width, height) || _cpuUploadTexture == null)
                    {
                        return false;
                    }

                    _context.UpdateSubresource(pixelData, _cpuUploadTexture, 0, (uint)stride, 0, null);
                    if (!BlitTextureToPresentation(_cpuUploadTexture, 0, width, height, framesPerSecond))
                    {
                        return false;
                    }
                }

                // CDG is drawn as a retained alpha texture.  If the overlay texture exists but
                // the shader draw path fails, report failure so the caller can fall back to the
                // proven CDG presentation path during normal playback instead of silently dropping
                // the lyrics.
                var overlayExpected = HasCdgOverlayTexture;
                if (overlayExpected && !DrawCachedCdgOverlayQuad(width, height))
                {
                    // Important: do NOT present the video-only back buffer here. The caller may
                    // fall back to the proven CDG compositor for this same video frame. Presenting
                    // here first creates an alternating video-only / video+CDG sequence, which
                    // appears as high-speed CDG flicker and also doubles the present cost.
                    RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                    return false;
                }

                RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                NotifyPresented(width, height);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Cached CDG overlay frame failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Presents a BGRA video frame through the proven native HWND swap-chain while blending the
    /// retained CDG texture in the same D3D11 video-processor pass. This deliberately avoids
    /// DirectComposition and avoids CPU full-frame blending.
    /// </summary>
    public bool UploadFrameWithCdgOverlay(ReadOnlySpan<byte> pixelData, int width, int height, int stride, double framesPerSecond = 60.0)
    {
        if (_disposed || _context == null || _presentationTexture == null) return false;
        if (pixelData.IsEmpty || width <= 0 || height <= 0 || stride < width * 4) return false;

        lock (_lock)
        {
            try
            {
                var uploadStart = Stopwatch.GetTimestamp();

                if (!ApplyPendingNativeHwndResizeIfNeeded())
                {
                    return false;
                }

                if (_cdgOverlayTexture == null || _cdgOverlayWidth != width || _cdgOverlayHeight != height)
                {
                    if (width == _currentWidth && height == _currentHeight)
                    {
                        _context.UpdateSubresource(pixelData, _presentationTexture, 0, (uint)stride, 0, null);
                        _context.Flush();
                    }
                    else
                    {
                        if (!EnsureCpuUploadTexture(width, height) || _cpuUploadTexture == null)
                        {
                            return false;
                        }

                        _context.UpdateSubresource(pixelData, _cpuUploadTexture, 0, (uint)stride, 0, null);
                        if (!BlitTextureToPresentation(_cpuUploadTexture, 0, width, height, framesPerSecond))
                        {
                            return false;
                        }
                    }

                    RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                    NotifyPresented(width, height);
                    return true;
                }

                if (!EnsureCpuUploadTexture(width, height) || _cpuUploadTexture == null)
                {
                    return false;
                }

                _context.UpdateSubresource(pixelData, _cpuUploadTexture, 0, (uint)stride, 0, null);
                if (!BlitTextureToPresentationWithOverlay(_cpuUploadTexture, _cdgOverlayTexture, width, height, framesPerSecond))
                {
                    return false;
                }

                RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                NotifyPresented(width, height);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Native GPU compositor frame failed: {ex.Message}");
                return false;
            }
        }
    }

    public void UploadFrame(ReadOnlySpan<byte> pixelData, int width, int height, int stride)
    {
        if (_disposed || _context == null || _presentationTexture == null) return;
        if (pixelData.IsEmpty || width <= 0 || height <= 0) return;

        lock (_lock)
        {
            try
            {
                var uploadStart = Stopwatch.GetTimestamp();

                if (!ApplyPendingNativeHwndResizeIfNeeded())
                {
                    return;
                }

                if (width == _currentWidth && height == _currentHeight)
                {
                    _context.UpdateSubresource(pixelData, _presentationTexture, 0, (uint)stride, 0, null);
                    _context.Flush();
                }
                else
                {
                    if (!EnsureCpuUploadTexture(width, height) || _cpuUploadTexture == null)
                    {
                        return;
                    }

                    _context.UpdateSubresource(pixelData, _cpuUploadTexture, 0, (uint)stride, 0, null);
                    if (!BlitTextureToPresentation(_cpuUploadTexture, 0, width, height, 60.0))
                    {
                        return;
                    }
                }

                RecordUploadDiagnostics(GetElapsedMilliseconds(uploadStart), width, height);
                NotifyPresented(width, height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Frame upload failed: {ex.Message}");
            }
        }
    }

    public bool PresentHardwareFrame(
        IntPtr sourceTexturePointer,
        int sourceArraySlice,
        int width,
        int height,
        double framesPerSecond)
    {
        if (_disposed
            || _context == null
            || _videoDevice == null
            || _videoContext == null
            || _presentationTexture == null
            || sourceTexturePointer == IntPtr.Zero
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            try
            {
                var presentStart = Stopwatch.GetTimestamp();
                if (!ApplyPendingNativeHwndResizeIfNeeded())
                {
                    return false;
                }

                using var sourceTexture = AddRefAndWrap(sourceTexturePointer, static pointer => new ID3D11Texture2D(pointer));
                if (!BlitTextureToPresentation(sourceTexture, sourceArraySlice, width, height, framesPerSecond))
                {
                    return false;
                }

                RecordUploadDiagnostics(GetElapsedMilliseconds(presentStart), width, height);
                NotifyPresented(width, height);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] Hardware frame presentation failed: {ex.Message}");
                return false;
            }
        }
    }

    private bool EnsureCpuUploadTexture(int width, int height)
    {
        if (_device == null)
        {
            return false;
        }

        if (_cpuUploadTexture != null && _cpuUploadWidth == width && _cpuUploadHeight == height)
        {
            return true;
        }

        _cpuUploadTexture?.Dispose();
        _cpuUploadTexture = null;
        _cpuUploadWidth = 0;
        _cpuUploadHeight = 0;

        var textureDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            sampleCount: 1,
            sampleQuality: 0,
            ResourceOptionFlags.None);

        _cpuUploadTexture = _device.CreateTexture2D(in textureDescription);
        _cpuUploadWidth = width;
        _cpuUploadHeight = height;
        return _cpuUploadTexture != null;
    }

    private bool BlitTextureToPresentation(ID3D11Texture2D sourceTexture, int sourceArraySlice, int width, int height, double framesPerSecond)
    {
        if (_videoDevice == null || _videoContext == null || _context == null || _presentationTexture == null)
        {
            return false;
        }

        if (!ApplyPendingNativeHwndResizeIfNeeded())
        {
            return false;
        }

        if (!EnsureVideoProcessor(width, height, framesPerSecond))
        {
            return false;
        }

        using var inputView = _videoDevice.CreateVideoProcessorInputView(
            sourceTexture,
            _videoProcessorEnumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = (uint)Math.Max(0, sourceArraySlice)
                }
            });

        _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor!, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor!, 0, false);
        var outputRect = CalculateVideoOutputRect(width, height);
        ApplyVideoProcessorOutputRect(streamIndex: 0, outputRect);
        ClearPresentationTextureToBlack(flush: false);

        var streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = inputView
            }
        };

        var result = _videoContext.VideoProcessorBlt(_videoProcessor!, _videoOutputView!, 0, streams);
        if (result.Failure)
        {
            Debug.WriteLine($"[VorticeD3D11] Video processor blit failed: {result}");
            return false;
        }

        _context.Flush();
        return true;
    }


    private bool BlitTextureToPresentationWithOverlay(
        ID3D11Texture2D baseTexture,
        ID3D11Texture2D overlayTexture,
        int width,
        int height,
        double framesPerSecond)
    {
        if (_videoDevice == null || _videoContext == null || _context == null || _presentationTexture == null)
        {
            return false;
        }

        if (!ApplyPendingNativeHwndResizeIfNeeded())
        {
            return false;
        }

        if (!EnsureVideoProcessor(width, height, framesPerSecond))
        {
            return false;
        }

        using var baseInputView = _videoDevice.CreateVideoProcessorInputView(
            baseTexture,
            _videoProcessorEnumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = 0
                }
            });

        using var overlayInputView = _videoDevice.CreateVideoProcessorInputView(
            overlayTexture,
            _videoProcessorEnumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView
                {
                    MipSlice = 0,
                    ArraySlice = 0
                }
            });

        _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor!, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor!, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor!, 1, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_videoProcessor!, 1, false);
        _videoContext.VideoProcessorSetStreamAlpha(_videoProcessor!, 1, true, 1.0f);
        var outputRect = CalculateVideoOutputRect(width, height);
        ApplyVideoProcessorOutputRect(streamIndex: 0, outputRect);
        ApplyVideoProcessorOutputRect(streamIndex: 1, outputRect);
        ClearPresentationTextureToBlack(flush: false);

        var streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = baseInputView
            },
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = overlayInputView
            }
        };

        var result = _videoContext.VideoProcessorBlt(_videoProcessor!, _videoOutputView!, 0, streams);
        if (result.Failure)
        {
            Debug.WriteLine($"[VorticeD3D11] Video processor compositor blit failed: {result}");
            return false;
        }

        _context.Flush();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CdgOverlayShaderConstants
    {
        public Vector4 DestinationRect;
        public Vector4 TextureRect;
    }

    private const string CdgOverlayShaderSource = @"
cbuffer OverlayConstants : register(b0)
{
    float4 DestinationRect;
    float4 TextureRect;
};

Texture2D OverlayTexture : register(t0);
SamplerState OverlaySampler : register(s0);

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

float4 PSMain(VertexOut input) : SV_TARGET
{
    return OverlayTexture.Sample(OverlaySampler, input.TexCoord);
}
";

    private bool DrawCachedCdgOverlayQuad(int videoWidth, int videoHeight)
    {
        if (_device == null
            || _context == null
            || _presentationTexture == null
            || _cdgOverlayShaderResourceView == null
            || _cdgOverlayWidth <= 0
            || _cdgOverlayHeight <= 0)
        {
            return false;
        }

        try
        {
            if (!EnsureCdgOverlayPipeline())
            {
                return false;
            }

            var outputRect = CalculateVideoOutputRect(videoWidth, videoHeight);
            var surfaceWidth = Math.Max(1, _currentWidth);
            var surfaceHeight = Math.Max(1, _currentHeight);

            var constants = new CdgOverlayShaderConstants
            {
                DestinationRect = new Vector4(
                    (float)((outputRect.Left / (double)surfaceWidth) * 2.0 - 1.0),
                    (float)(1.0 - (outputRect.Top / (double)surfaceHeight) * 2.0),
                    (float)((outputRect.Right / (double)surfaceWidth) * 2.0 - 1.0),
                    (float)(1.0 - (outputRect.Bottom / (double)surfaceHeight) * 2.0)),
                TextureRect = new Vector4(0f, 0f, 1f, 1f)
            };

            Span<byte> constantBytes = stackalloc byte[Marshal.SizeOf<CdgOverlayShaderConstants>()];
            MemoryMarshal.Write(constantBytes, ref constants);
            _context.UpdateSubresource(constantBytes, _cdgOverlayConstantBuffer, 0, 0, 0, null);

            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _context.IASetInputLayout(null);
            _context.VSSetShader(_cdgOverlayVertexShader);
            _context.VSSetConstantBuffer(0, _cdgOverlayConstantBuffer);
            _context.PSSetShader(_cdgOverlayPixelShader);
            _context.PSSetShaderResource(0, _cdgOverlayShaderResourceView);
            _context.PSSetSampler(0, _cdgOverlaySamplerState);
            _context.RSSetViewport(new Viewport(0, 0, surfaceWidth, surfaceHeight));
            _context.OMSetRenderTargets(_presentationRenderTargetView);
            _context.OMSetBlendState(_cdgOverlayBlendState);
            _context.Draw(4, 0);

            // Prevent D3D11 warning/resource hazard when the overlay texture is later updated.
            _context.PSSetShaderResource(0, null);
            _context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
            return true;
        }
        catch (Exception ex)
        {
            if (_configuration.EnableLogging)
            {
                Debug.WriteLine($"[VorticeD3D11] Cached CDG overlay draw failed: {ex.Message}");
            }
            return false;
        }
    }

    private bool EnsureCdgOverlayPipeline()
    {
        if (_device == null || _presentationTexture == null || _cdgOverlayPipelineFailed)
        {
            return false;
        }

        try
        {
            _presentationRenderTargetView ??= _device.CreateRenderTargetView(_presentationTexture);

            if (_cdgOverlayVertexShader == null || _cdgOverlayPixelShader == null)
            {
                var vertexShaderBytecode = CompileShaderBytecode(CdgOverlayShaderSource, "VSMain", "vs_5_0");
                var pixelShaderBytecode = CompileShaderBytecode(CdgOverlayShaderSource, "PSMain", "ps_5_0");
                _cdgOverlayVertexShader = _device.CreateVertexShader(vertexShaderBytecode);
                _cdgOverlayPixelShader = _device.CreatePixelShader(pixelShaderBytecode);
            }

            if (_cdgOverlayConstantBuffer == null)
            {
                var constantBufferDescription = new BufferDescription
                {
                    ByteWidth = (uint)Marshal.SizeOf<CdgOverlayShaderConstants>(),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ConstantBuffer,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                    StructureByteStride = 0
                };
                _cdgOverlayConstantBuffer = _device.CreateBuffer(constantBufferDescription);
            }

            if (_cdgOverlaySamplerState == null)
            {
                var samplerDescription = new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
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
                _cdgOverlaySamplerState = _device.CreateSamplerState(samplerDescription);
            }

            if (_cdgOverlayBlendState == null)
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
                _cdgOverlayBlendState = _device.CreateBlendState(blendDescription);
            }

            return _presentationRenderTargetView != null
                && _cdgOverlayVertexShader != null
                && _cdgOverlayPixelShader != null
                && _cdgOverlayConstantBuffer != null
                && _cdgOverlaySamplerState != null
                && _cdgOverlayBlendState != null;
        }
        catch (Exception ex)
        {
            _cdgOverlayPipelineFailed = true;
            Debug.WriteLine($"[VorticeD3D11] Cached CDG overlay pipeline unavailable: {ex.Message}");
            return false;
        }
    }


    private static byte[] CompileShaderBytecode(string shaderSource, string entryPoint, string targetProfile)
    {
        var sourceBytes = Encoding.ASCII.GetBytes(shaderSource);
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
            out var codeBlobPointer,
            out var errorBlobPointer);

        try
        {
            if (hr < 0)
            {
                var errorMessage = ReadD3DBlobAnsi(errorBlobPointer);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                    ? $"D3DCompile failed for {entryPoint}/{targetProfile}: 0x{hr:X8}"
                    : $"D3DCompile failed for {entryPoint}/{targetProfile}: {errorMessage}");
            }

            if (codeBlobPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"D3DCompile returned no bytecode for {entryPoint}/{targetProfile}.");
            }

            var codeBlob = (ID3DBlob)Marshal.GetObjectForIUnknown(codeBlobPointer);
            var byteLength = checked((int)codeBlob.GetBufferSize());
            var bytecode = new byte[byteLength];
            Marshal.Copy(codeBlob.GetBufferPointer(), bytecode, 0, byteLength);
            return bytecode;
        }
        finally
        {
            if (codeBlobPointer != IntPtr.Zero)
            {
                Marshal.Release(codeBlobPointer);
            }

            if (errorBlobPointer != IntPtr.Zero)
            {
                Marshal.Release(errorBlobPointer);
            }
        }
    }

    private static string ReadD3DBlobAnsi(IntPtr blobPointer)
    {
        if (blobPointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(blobPointer);
        try
        {
            var byteLength = checked((int)blob.GetBufferSize());
            if (byteLength <= 0 || blob.GetBufferPointer() == IntPtr.Zero)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringAnsi(blob.GetBufferPointer(), byteLength)?.TrimEnd('\0', '\r', '\n') ?? string.Empty;
        }
        finally
        {
            Marshal.ReleaseComObject(blob);
        }
    }

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private static extern int D3DCompile(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] sourceData,
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

    [ComImport]
    [Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        IntPtr GetBufferPointer();
        nuint GetBufferSize();
    }

    private Vortice.RawRect CalculateVideoOutputRect(int sourceWidth, int sourceHeight)
    {
        var outputWidth = Math.Max(1, _currentWidth);
        var outputHeight = Math.Max(1, _currentHeight);
        var inputWidth = Math.Max(1, sourceWidth);
        var inputHeight = Math.Max(1, sourceHeight);

        if (_nativePresentationStretch == Stretch.Fill)
        {
            return new Vortice.RawRect(0, 0, outputWidth, outputHeight);
        }

        double scaleX = outputWidth / (double)inputWidth;
        double scaleY = outputHeight / (double)inputHeight;
        double scale = _nativePresentationStretch switch
        {
            Stretch.None => 1.0,
            Stretch.UniformToFill => Math.Max(scaleX, scaleY),
            Stretch.Uniform => Math.Min(scaleX, scaleY),
            _ => Math.Min(scaleX, scaleY)
        };

        var displayWidth = Math.Max(1, (int)Math.Round(inputWidth * scale));
        var displayHeight = Math.Max(1, (int)Math.Round(inputHeight * scale));
        var left = (outputWidth - displayWidth) / 2;
        var top = (outputHeight - displayHeight) / 2;
        var right = left + displayWidth;
        var bottom = top + displayHeight;

        return new Vortice.RawRect(left, top, right, bottom);
    }

    private void ApplyVideoProcessorOutputRect(int streamIndex, Vortice.RawRect outputRect)
    {
        if (_videoContext == null || _videoProcessor == null)
        {
            return;
        }

        _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, (uint)streamIndex, true, outputRect);
    }

    private bool EnsureVideoProcessor(int width, int height, double framesPerSecond)
    {
        if (_videoDevice == null || _videoContext == null || _presentationTexture == null)
        {
            return false;
        }

        var normalizedFps = double.IsFinite(framesPerSecond) && framesPerSecond > 0 ? framesPerSecond : 60.0;
        if (_videoProcessor != null
            && _videoOutputView != null
            && _videoProcessorEnumerator != null
            && _videoProcessorWidth == width
            && _videoProcessorHeight == height
            && _videoProcessorOutputWidth == _currentWidth
            && _videoProcessorOutputHeight == _currentHeight
            && Math.Abs(_videoProcessorFps - normalizedFps) < 0.01)
        {
            return true;
        }

        CleanupVideoProcessor();

        var frameRate = ToRational(normalizedFps);
        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = frameRate,
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = frameRate,
            OutputWidth = (uint)_currentWidth,
            OutputHeight = (uint)_currentHeight,
            Usage = VideoUsage.OptimalSpeed
        };

        _videoProcessorEnumerator = _videoDevice.CreateVideoProcessorEnumerator(contentDescription);
        _videoProcessor = _videoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0);
        _videoOutputView = _videoDevice.CreateVideoProcessorOutputView(
            _presentationTexture,
            _videoProcessorEnumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView
                {
                    MipSlice = 0
                }
            });

        _videoProcessorWidth = width;
        _videoProcessorHeight = height;
        _videoProcessorOutputWidth = _currentWidth;
        _videoProcessorOutputHeight = _currentHeight;
        _videoProcessorFps = normalizedFps;
        if (_configuration.EnableLogging)
        {
            Debug.WriteLine($"[VorticeD3D11] Created video processor source={width}x{height}, output={_currentWidth}x{_currentHeight} @ {normalizedFps:F2}fps");
        }
        return true;
    }

    private static Rational ToRational(double framesPerSecond)
    {
        var numerator = (uint)Math.Clamp(Math.Round(framesPerSecond * 1000.0), 1, uint.MaxValue);
        return new Rational(numerator, 1000);
    }

    private void RecordUploadDiagnostics(double uploadMs, int width, int height)
    {
        _uploadDiagnosticFrames++;
        _uploadDiagnosticMs += uploadMs;
        _uploadDiagnosticMaxMs = Math.Max(_uploadDiagnosticMaxMs, uploadMs);

        if (_uploadDiagnosticFrames < UploadDiagnosticsFrameInterval)
        {
            return;
        }

        if (_configuration.EnablePerformanceLogging)
        {
            Debug.WriteLine(
                $"[VorticeD3D11:Perf] frames={_uploadDiagnosticFrames}, avgUploadFlush={_uploadDiagnosticMs / _uploadDiagnosticFrames:F2}ms, " +
                $"maxUploadFlush={_uploadDiagnosticMaxMs:F2}ms, source={width}x{height}, surface={_currentWidth}x{_currentHeight}");
        }

        _uploadDiagnosticFrames = 0;
        _uploadDiagnosticMs = 0;
        _uploadDiagnosticMaxMs = 0;
    }

    private static double GetElapsedMilliseconds(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }

    private static void MarkSurfaceDirty(D3DImage d3dImage, int width, int height)
    {
        if (!d3dImage.IsFrontBufferAvailable) return;

        try
        {
            d3dImage.Lock();
            try
            {
                d3dImage.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                d3dImage.Unlock();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] D3DImage update failed: {ex.Message}");
        }
    }

    private void NotifyPresented(int width, int height)
    {
        if (_nativeSwapChain)
        {
            PresentSwapChain();
            FramePresented?.Invoke(this, EventArgs.Empty);
            return;
        }

        NotifyDirty(_currentWidth, _currentHeight);
    }


    private void ClearDirectCompositionOverlayToTransparent()
    {
        if (_device == null || _context == null || _compositionOverlaySwapChain == null)
        {
            return;
        }

        try
        {
            // Clear every flip-model back buffer. The CDG overlay is not presented every video
            // frame, so an uncleared stale back buffer can otherwise reappear as temporary
            // transparency/ghosting when DWM samples the overlay chain.
            for (var bufferIndex = 0; bufferIndex < DirectCompositionBufferCount; bufferIndex++)
            {
                ReacquireDirectCompositionOverlayBackBuffer();
                if (_compositionOverlayTexture == null)
                {
                    return;
                }

                using var renderTargetView = _device.CreateRenderTargetView(_compositionOverlayTexture);
                _context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 0f));
                _context.Flush();
                _compositionOverlaySwapChain.Present(0, (int)PresentFlags.None);
            }

            ReacquireDirectCompositionOverlayBackBuffer();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] DirectComposition overlay clear failed: {ex.Message}");
        }
    }

    private bool BlitTextureToDirectCompositionOverlay(ID3D11Texture2D sourceTexture, int width, int height, double framesPerSecond)
    {
        if (_compositionOverlayTexture == null || _videoDevice == null || _videoContext == null || _context == null)
        {
            return false;
        }

        var savedPresentationTexture = _presentationTexture;
        try
        {
            _presentationTexture = _compositionOverlayTexture;
            return BlitTextureToPresentation(sourceTexture, 0, width, height, framesPerSecond);
        }
        finally
        {
            _presentationTexture = savedPresentationTexture;
        }
    }

    private void ClearPresentationTextureToBlack(bool flush = true)
    {
        if (_device == null || _context == null || _presentationTexture == null)
        {
            return;
        }

        try
        {
            using var renderTargetView = _device.CreateRenderTargetView(_presentationTexture);
            _context.ClearRenderTargetView(renderTargetView, new Color4(0f, 0f, 0f, 1f));
            if (flush)
            {
                _context.Flush();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Presentation texture black clear failed: {ex.Message}");
        }
    }

    private void PresentSwapChain(bool syncImmediate = false)
    {
        var swapChain = _swapChain;
        if (swapChain == null) return;

        try
        {
            // During WPF live resize, a vsync-blocking Present(1) can make the top-level
            // resize loop feel like it is dragging through mud.  Keep video frames live,
            // but present them in immediate mode while the user is actively resizing.
            var liveResize = Volatile.Read(ref _nativeLiveResizeInProgress) != 0;
            uint syncInterval = syncImmediate || liveResize || _configuration.NativeSwapChainPresentMode == NativeSwapChainPresentMode.Immediate ? 0u : 1u;
            var presentStart = Stopwatch.GetTimestamp();
            var result = swapChain.Present(syncInterval, (int)PresentFlags.None);
            // During DirectComposition mode, do not commit the device from the video-frame
            // present path. This intermediate commit can force DWM to sample the overlay
            // swap-chain between CDG frame uploads, which makes the CDG lyrics disappear
            // during live resize.  The overlay upload path (UploadDirectCompositionOverlayFrame)
            // and the resize path (SetDirectCompositionOutputSize) own their own commits.
            // For CreateSwapChainForComposition swap-chains, Present() alone makes the
            // updated surface available to DWM without an explicit device Commit().
            RecordNativePresentDiagnostics(GetElapsedMilliseconds(presentStart), (int)syncInterval);
            if (result.Failure)
            {
                Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain Present failed: {result}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Native HWND swap-chain Present failed: {ex.Message}");
        }
    }

    private void RecordNativePresentDiagnostics(double presentMs, int syncInterval)
    {
        _nativePresentDiagnosticFrames++;
        _nativePresentDiagnosticMs += presentMs;
        _nativePresentDiagnosticMaxMs = Math.Max(_nativePresentDiagnosticMaxMs, presentMs);

        if (_nativePresentDiagnosticFrames < NativePresentDiagnosticsFrameInterval)
        {
            return;
        }

        if (_configuration.EnablePerformanceLogging)
        {
            var mode = syncInterval == 0 ? "Immediate" : "VSync";
            Debug.WriteLine(
                $"[VorticeD3D11:PresentPerf] frames={_nativePresentDiagnosticFrames}, mode={mode}, " +
                $"avgPresent={_nativePresentDiagnosticMs / _nativePresentDiagnosticFrames:F2}ms, " +
                $"maxPresent={_nativePresentDiagnosticMaxMs:F2}ms");
        }

        _nativePresentDiagnosticFrames = 0;
        _nativePresentDiagnosticMs = 0;
        _nativePresentDiagnosticMaxMs = 0;
    }

    private void NotifyDirty(int width, int height)
    {
        var d3dImage = _d3dImage;
        if (d3dImage == null) return;

        var generation = Volatile.Read(ref _surfaceGeneration);

        if (d3dImage.Dispatcher.CheckAccess())
        {
            if (!_disposed && generation == Volatile.Read(ref _surfaceGeneration) && ReferenceEquals(_d3dImage, d3dImage))
            {
                MarkSurfaceDirty(d3dImage, width, height);
                FramePresented?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        d3dImage.Dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed || generation != Volatile.Read(ref _surfaceGeneration) || !ReferenceEquals(_d3dImage, d3dImage))
                {
                    return;
                }

                MarkSurfaceDirty(d3dImage, width, height);
                FramePresented?.Invoke(this, EventArgs.Empty);
            },
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RetireSurfaces()
    {
        Interlocked.Increment(ref _surfaceGeneration);
    }

    private void InitializeVideoInterfaces()
    {
        _multithreadProtection?.Dispose();
        _multithreadProtection = null;
        _videoDevice?.Dispose();
        _videoContext?.Dispose();
        _videoDevice = null;
        _videoContext = null;

        if (_device == null || _context == null)
        {
            return;
        }

        _videoDevice = _device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterfaceOrNull<ID3D11VideoContext>();
        _multithreadProtection = _device.QueryInterfaceOrNull<ID3D11Multithread>();
        if (_multithreadProtection != null)
        {
            _multithreadProtection.SetMultithreadProtected(true);
            if (_configuration.EnableVerboseLogging) Debug.WriteLine("[VorticeD3D11] Enabled D3D11 multithread protection for shared decode/presentation device");
        }
    }

    private static T AddRefAndWrap<T>(IntPtr pointer, Func<IntPtr, T> factory)
        where T : IDisposable
    {
        Marshal.AddRef(pointer);
        return factory(pointer);
    }

    private void CleanupSurfaces()
    {
        RetireSurfaces();
        CleanupVideoProcessor();

        if (_d3dImage != null)
        {
            try
            {
                _d3dImage.Lock();
                try
                {
                    _d3dImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                }
                finally
                {
                    _d3dImage.Unlock();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] D3DImage cleanup failed: {ex.Message}");
            }
        }

        _compositionOverlayTexture?.Dispose();
        _compositionOverlayTexture = null;
        _presentationRenderTargetView?.Dispose();
        _presentationRenderTargetView = null;
        _cdgOverlayShaderResourceView?.Dispose();
        _cdgOverlayShaderResourceView = null;
        _cdgOverlayTexture?.Dispose();
        _cdgOverlayTexture = null;
        _cdgOverlayVertexShader?.Dispose();
        _cdgOverlayVertexShader = null;
        _cdgOverlayPixelShader?.Dispose();
        _cdgOverlayPixelShader = null;
        _cdgOverlayConstantBuffer?.Dispose();
        _cdgOverlayConstantBuffer = null;
        _cdgOverlaySamplerState?.Dispose();
        _cdgOverlaySamplerState = null;
        _cdgOverlayBlendState?.Dispose();
        _cdgOverlayBlendState = null;
        _cdgOverlayPipelineFailed = false;
        _cdgOverlayWidth = 0;
        _cdgOverlayHeight = 0;
        _cdgOverlayStride = 0;
        _compositionOverlaySwapChain?.Dispose();
        _compositionOverlaySwapChain = null;
        _compositionOverlayVisual?.Dispose();
        _compositionOverlayVisual = null;
        _compositionBaseVisual?.Dispose();
        _compositionBaseVisual = null;
        _compositionRootVisual?.Dispose();
        _compositionRootVisual = null;
        _compositionTarget?.Dispose();
        _compositionTarget = null;
        _compositionDevice?.Dispose();
        _compositionDevice = null;
        _compositionOutputWidth = 0;
        _compositionOutputHeight = 0;
        _directCompositionActive = false;

        _d3dImage = null;
        _interopSurface?.Dispose();
        _interopSurface = null;
        _cpuUploadTexture?.Dispose();
        _cpuUploadTexture = null;
        _cpuUploadWidth = 0;
        _cpuUploadHeight = 0;
        _sharedResource?.Dispose();
        _sharedResource = null;
        _presentationTexture?.Dispose();
        _presentationTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _nativeSwapChain = false;
        _nativeHwnd = IntPtr.Zero;
        _currentWidth = _currentHeight = 0;
    }

    private void CleanupResources()
    {
        CleanupSurfaces();

        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _multithreadProtection?.Dispose();
        _multithreadProtection = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _nativeDevicePointer = IntPtr.Zero;
    }

    private void CleanupVideoProcessor()
    {
        _videoOutputView?.Dispose();
        _videoOutputView = null;
        _videoProcessor?.Dispose();
        _videoProcessor = null;
        _videoProcessorEnumerator?.Dispose();
        _videoProcessorEnumerator = null;
        _videoProcessorWidth = 0;
        _videoProcessorHeight = 0;
        _videoProcessorOutputWidth = 0;
        _videoProcessorOutputHeight = 0;
        _videoProcessorFps = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            CleanupResources();
            _disposed = true;
        }

        if (_configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Vortice D3D11 renderer disposed");
    }
}
