namespace FatzBombMedia.MediaControl.Core.Configuration;


/// <summary>
/// Video decoding methods available for MediaPlayer instances.
/// Auto is recommended - it automatically selects the best available method.
/// </summary>
public enum VideoDecodingMethod
{
    /// <summary>
    /// Automatically detect and use the best available method (GPU > CPU).
    /// This is the recommended setting for best quality and performance.
    /// </summary>
    Auto,

    /// <summary>
    /// Force hardware-accelerated video decoding (D3D11VA/D3D12VA/DXVA2 where available).
    /// Falls back to software if hardware acceleration fails.
    /// </summary>
    Hardware,

    /// <summary>
    /// Force CPU-based software video decoding.
    /// Always available but uses more CPU resources.
    /// </summary>
    Software
}

/// <summary>
/// Rendering methods available for MediaPlayer instances.
/// Auto is recommended - it automatically selects the best available method.
/// </summary>
public enum RenderingMethod
{
    /// <summary>
    /// Automatically detect and use the best available method (D3D11 > WriteableBitmap).
    /// This is the recommended setting for best quality and performance.
    /// </summary>
    Auto,

    /// <summary>
    /// Force DirectX 11 hardware-accelerated rendering.
    /// Falls back to software if D3D11 is not available.
    /// </summary>
    DirectX11,

    /// <summary>
    /// Force optimized CPU-based WriteableBitmap rendering.
    /// Always available and uses SIMD optimizations when possible.
    /// </summary>
    Software
}

/// <summary>
/// Native HWND swap-chain present behavior. VSync is the normal smooth-video mode; Immediate is
/// useful as a diagnostic when testing whether DXGI blocking is fighting the player scheduler.
/// </summary>
public enum NativeSwapChainPresentMode
{
    /// <summary>
    /// Present with sync interval 1. This lets DXGI/DWM pace frames to the monitor refresh.
    /// </summary>
    VSync,

    /// <summary>
    /// Present with sync interval 0. This avoids blocking on vblank and is mainly diagnostic.
    /// </summary>
    Immediate
}


/// <summary>
/// WPF D3DImage presentation scheduling style. CompositionTargetRendering follows the
/// WPFDXInterop pattern: WPF's render pass requests the next DirectX surface update,
/// which keeps the airspace-safe ImageSource path synchronized with WPF composition.
/// IndependentPresentationLoop keeps the older worker-thread presentation scheduler.
/// </summary>
public enum WpfD3DImagePresentationMode
{
    /// <summary>
    /// Present D3DImage frames from the video pipeline's independent presentation loop.
    /// This is the stable default because the WPF render-tick experiment produced resize
    /// flicker/stutter in real playback tests.
    /// </summary>
    IndependentPresentationLoop,

    /// <summary>
    /// Experimental mode: present D3DImage frames when WPF raises CompositionTarget.Rendering.
    /// Kept for comparison only; do not use as the default until it is proven smoother.
    /// </summary>
    CompositionTargetRendering
}

/// <summary>
/// Controls how large the D3DImage shared presentation surface should be.
/// </summary>
public enum WpfD3DImageSurfaceSizingMode
{
    /// <summary>
    /// Keep the shared D3DImage surface at the decoded/source media size. WPF performs final
    /// layout scaling. This avoids expensive D3D9 shared-surface churn during window resize and
    /// is the stable default for overlay-friendly WPF playback.
    /// </summary>
    SourceNative,

    /// <summary>
    /// Keep the shared D3DImage surface source-sized, but cap very large sources to the configured
    /// maximum size while preserving aspect ratio. This is intended for laptop/performance-friendly
    /// WPF playback of 4K/oversized sources without using a window-sized interop surface.
    /// </summary>
    SourceCapped,

    /// <summary>
    /// Experimental: recreate the shared D3DImage surface at the displayed media rectangle size.
    /// This can reduce WPF scaling, but it is expensive during resize and may flicker/stutter.
    /// </summary>
    DisplayRectangle
}

/// <summary>
/// Immutable configuration for a MediaPlayer instance.
/// These settings are determined at creation time and cannot be changed during playback.
/// This ensures consistent performance and avoids mid-stream resource switching.
/// </summary>
public readonly record struct MediaPlayerConfiguration
{
    /// <summary>
    /// Video decoding method for this MediaPlayer instance.
    /// </summary>
    public VideoDecodingMethod VideoDecoding { get; init; }

    /// <summary>
    /// Rendering method for this MediaPlayer instance.
    /// </summary>
    public RenderingMethod Rendering { get; init; }

    /// <summary>
    /// Minimum resolution threshold for GPU rendering decisions.
    /// </summary>
    public int GpuRenderingMinResolution { get; init; }

    /// <summary>
    /// Maximum GPU memory usage in MB for this instance.
    /// </summary>
    public int MaxGpuMemoryMB { get; init; }

    /// <summary>
    /// Whether useful state-change and warning logging is enabled for this instance.
    /// This is intended to show backend changes, decode fallbacks, surface creation, seek recovery,
    /// and other information that helps diagnose playback without flooding the debug output.
    /// </summary>
    public bool EnableLogging { get; init; }

    /// <summary>
    /// Whether periodic performance summaries are written. Enable this while comparing presentation
    /// modes or diagnosing stutter. Routine decoder, renderer, and presentation-loop counters use
    /// this flag instead of the general logging flag.
    /// </summary>
    public bool EnablePerformanceLogging { get; init; }

    /// <summary>
    /// Enables very noisy diagnostic details such as decoder candidate dumps, individual FFmpeg packet
    /// errors, WPF callback cadence, and seek preview details. Leave false for normal debugging.
    /// </summary>
    public bool EnableVerboseLogging { get; init; }

    /// <summary>
    /// Initial presentation lead, measured in display refresh intervals. The pipeline now treats this
    /// as the starting point for an adaptive controller rather than a fixed forever value. It nudges
    /// the active lead up when frames arrive late or are dropped, and nudges it down if playback is
    /// consistently early. Use 0.75 as the normal default for 23.976/24fps material on 60Hz displays.
    /// </summary>
    public double PresentationLeadRefreshIntervals { get; init; }

    /// <summary>
    /// Native HWND swap-chain Present mode. Keep VSync for normal playback; Immediate is useful for
    /// isolating whether DXGI vblank blocking is contributing to stutter.
    /// </summary>
    public NativeSwapChainPresentMode NativeSwapChainPresentMode { get; init; }

    /// <summary>
    /// Controls how the WPF D3DImage/ImageSource presentation path is scheduled. The stable default
    /// is the independent presentation loop. CompositionTargetRendering remains available as an
    /// experiment, but it should be tested with SourceNative or SourceCapped sizing so resize-driven
    /// D3D9 shared-surface recreation does not pollute the result. Native HWND mode ignores this and
    /// continues to use the DXGI swap-chain presentation loop.
    /// </summary>
    public WpfD3DImagePresentationMode WpfD3DImagePresentationMode { get; init; }

    /// <summary>
    /// Controls whether the WPF D3DImage shared surface stays at the source media size, is capped
    /// from the source size for laptop/performance-friendly playback, or is recreated to match the
    /// displayed rectangle. SourceNative is the stable default because real-world testing showed
    /// display-sized surface recreation causes resize flicker and can increase stutter.
    /// </summary>
    public WpfD3DImageSurfaceSizingMode WpfD3DImageSurfaceSizingMode { get; init; }

    /// <summary>
    /// Maximum WPF D3DImage surface width used when <see cref="WpfD3DImageSurfaceSizingMode"/> is
    /// <see cref="Core.Configuration.WpfD3DImageSurfaceSizingMode.SourceCapped"/>. The source aspect
    /// ratio is preserved and smaller sources are never upscaled merely to hit this cap.
    /// </summary>
    public int MaxWpfD3DImageSurfaceWidth { get; init; }

    /// <summary>
    /// Maximum WPF D3DImage surface height used when <see cref="WpfD3DImageSurfaceSizingMode"/> is
    /// <see cref="Core.Configuration.WpfD3DImageSurfaceSizingMode.SourceCapped"/>. The source aspect
    /// ratio is preserved and smaller sources are never upscaled merely to hit this cap.
    /// </summary>
    public int MaxWpfD3DImageSurfaceHeight { get; init; }

    /// <summary>
    /// Creates a default configuration with optimal settings for best quality.
    /// Uses automatic detection for video decoding and rendering with generous memory limits.
    /// </summary>
    public MediaPlayerConfiguration()
    {
        VideoDecoding = VideoDecodingMethod.Auto;
        Rendering = RenderingMethod.Auto;
        GpuRenderingMinResolution = 0;
        MaxGpuMemoryMB = 1024;
        EnableLogging = false;
        EnablePerformanceLogging = false;
        EnableVerboseLogging = false;
        PresentationLeadRefreshIntervals = 0.75;
        NativeSwapChainPresentMode = NativeSwapChainPresentMode.VSync;
        WpfD3DImagePresentationMode = WpfD3DImagePresentationMode.IndependentPresentationLoop;
        WpfD3DImageSurfaceSizingMode = WpfD3DImageSurfaceSizingMode.SourceNative;
        MaxWpfD3DImageSurfaceWidth = 1920;
        MaxWpfD3DImageSurfaceHeight = 1080;
    }

    /// <summary>
    /// Creates a configuration with specific video decoding and rendering methods.
    /// </summary>
    public MediaPlayerConfiguration(VideoDecodingMethod videoDecoding, RenderingMethod rendering)
    {
        VideoDecoding = videoDecoding;
        Rendering = rendering;
        GpuRenderingMinResolution = 0;
        MaxGpuMemoryMB = 1024;
        EnableLogging = false;
        EnablePerformanceLogging = false;
        EnableVerboseLogging = false;
        PresentationLeadRefreshIntervals = 0.75;
        NativeSwapChainPresentMode = NativeSwapChainPresentMode.VSync;
        WpfD3DImagePresentationMode = WpfD3DImagePresentationMode.IndependentPresentationLoop;
        WpfD3DImageSurfaceSizingMode = WpfD3DImageSurfaceSizingMode.SourceNative;
        MaxWpfD3DImageSurfaceWidth = 1920;
        MaxWpfD3DImageSurfaceHeight = 1080;
    }

    /// <summary>
    /// Auto configuration - same as default constructor for compatibility.
    /// </summary>
    public static MediaPlayerConfiguration Auto => new();
}
