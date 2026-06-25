using System.Windows.Media;
using System.Windows.Interop;
using System.Windows;
using System.Diagnostics;
using System.IO;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBombMedia.MediaControl.Core.Graphics;

/// <summary>
/// Provides GPU capability detection and DirectX hardware acceleration support.
/// Uses modern Vortice.Windows instead of deprecated SharpDX.
/// </summary>
public static class GpuRenderingCapabilities
{
    private static bool _capabilitiesDetected = false;
    private static GpuCapabilityInfo _capabilities = new();

    /// <summary>
    /// Gets the current GPU capabilities for the system.
    /// Performs detection on first access.
    /// </summary>
    public static GpuCapabilityInfo Capabilities
    {
        get
        {
            if (!_capabilitiesDetected)
            {
                DetectCapabilities();
                _capabilitiesDetected = true;
            }
            return _capabilities;
        }
    }

    /// <summary>
    /// Determines the optimal rendering method for the given content parameters.
    /// </summary>
    /// <param name="width">Content width in pixels</param>
    /// <param name="height">Content height in pixels</param>
    /// <returns>Recommended rendering method</returns>
    public static GpuRenderingMethod GetOptimalRenderingMethod(int width, int height)
    {
        var pixelCount = width * height;
        var capabilities = Capabilities;

        // Since we simplified to always use best quality settings, use hardcoded optimal values
        // Always try GPU rendering for best quality (no minimum resolution limit)
        var gpuRenderingMinResolution = 0;
        
        if (pixelCount < gpuRenderingMinResolution)
        {
            Debug.WriteLine($"[GPU] Small resolution {width}x{height}, using CPU rendering");
            return GpuRenderingMethod.WriteableBitmap;
        }

        // Always enable GPU rendering for best quality
        var enableGpuRendering = true;
        
        if (enableGpuRendering)
        {
            // Check DirectX 11 support (primary method)
            if (capabilities.SupportsD3D11)
            {
                Debug.WriteLine($"[GPU] Using D3D11-accelerated rendering for {width}x{height} content (auto-selected)");
                return GpuRenderingMethod.D3D11;
            }
        }

        // Fallback to CPU rendering
        Debug.WriteLine($"[GPU] No GPU acceleration available, using optimized CPU rendering for {width}x{height}");
        return GpuRenderingMethod.WriteableBitmap;
    }

    private static bool CanUseRenderingMethod(GpuRenderingMethod method, GpuCapabilityInfo capabilities)
    {
        return method switch
        {
            GpuRenderingMethod.D3D11 => capabilities.SupportsD3D11,
            GpuRenderingMethod.WriteableBitmap => true,
            _ => false
        };
    }

    /// <summary>
    /// Detects system GPU capabilities and DirectX support.
    /// </summary>
    private static void DetectCapabilities()
    {
        try
        {
            _capabilities = new GpuCapabilityInfo();

            // Check DirectX 11 support via WPF RenderCapability
            var renderingTier = RenderCapability.Tier;
            _capabilities.WpfRenderingTier = renderingTier >> 16;
            _capabilities.SupportsHardwareAcceleration = _capabilities.WpfRenderingTier > 0;

            // DirectX 11 detection - check if D3D11 DLLs exist and can be loaded
            _capabilities.SupportsD3D11 = CheckD3D11Support();

            // D3D9 support removed - modern D3D11 approach only
            // No longer setting SupportsD3D9 as it's obsolete

            // Check for dedicated GPU via WPF tier
            _capabilities.HasDedicatedGpu = _capabilities.WpfRenderingTier >= 2;

            // Try to get more accurate GPU info via DXGI
            DetectGpuInfoViaDxgi();

            Debug.WriteLine($"[GPU] Capabilities: Tier={_capabilities.WpfRenderingTier}, D3D11={_capabilities.SupportsD3D11}, Dedicated={_capabilities.HasDedicatedGpu}, VRAM=~{_capabilities.EstimatedGpuMemoryMB}MB");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GPU] Capability detection failed: {ex.Message}");
            _capabilities = new GpuCapabilityInfo(); // Safe fallback
        }
    }

    private static bool CheckD3D11Support()
    {
        try
        {
            // Check if D3D11 DLL exists
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "d3d11.dll")))
            {
                Debug.WriteLine("[GPU] d3d11.dll not found");
                return false;
            }

            // Try to create a D3D11 device
            var result = D3D11.D3D11CreateDevice(
                adapter: null,
                driverType: Vortice.Direct3D.DriverType.Hardware,
                flags: DeviceCreationFlags.BgraSupport,
                featureLevels: [Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1],
                device: out var device,
                featureLevel: out var featureLevel);

            if (result.Success && device != null)
            {
                Debug.WriteLine($"[GPU] D3D11 device test successful - Feature level: {featureLevel}");

                // Clean up test device
                device.Dispose();

                return true;
            }
            else
            {
                Debug.WriteLine($"[GPU] D3D11 device test failed: {result}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GPU] D3D11 support check failed: {ex.Message}");
            return false;
        }
    }

    private static void DetectGpuInfoViaDxgi()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var result = factory.EnumAdapters1(0, out var adapter); // Primary adapter

            if (result.Success && adapter != null)
            {
                using (adapter)
                {
                    var desc = adapter.Description1;

                    // Convert dedicated video memory from bytes to MB
                    var dedicatedMemoryMB = (int)(desc.DedicatedVideoMemory.Value / (1024 * 1024));

                    _capabilities.EstimatedGpuMemoryMB = Math.Max(dedicatedMemoryMB, 128); // At least 128MB
                    _capabilities.GpuDescription = desc.Description;
                    _capabilities.HasDedicatedGpu = dedicatedMemoryMB > 256; // Assume dedicated if > 256MB VRAM

                    Debug.WriteLine($"[GPU] Detected: {desc.Description} with {dedicatedMemoryMB}MB VRAM");
                }
            }
            else
            {
                Debug.WriteLine($"[GPU] Failed to enumerate primary adapter: {result}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GPU] DXGI GPU info detection failed: {ex.Message}");
            _capabilities.EstimatedGpuMemoryMB = _capabilities.HasDedicatedGpu ? 512 : 128;
        }
    }
}

/// <summary>
/// Contains detailed information about system GPU capabilities.
/// </summary>
public sealed class GpuCapabilityInfo
{
    /// <summary>
    /// WPF rendering tier (0 = software, 1 = partial hardware, 2 = full hardware).
    /// </summary>
    public int WpfRenderingTier { get; internal set; } = 0;

    /// <summary>
    /// Whether any form of hardware acceleration is supported.
    /// </summary>
    public bool SupportsHardwareAcceleration { get; internal set; } = false;

    /// <summary>
    /// Whether DirectX 11 is available and supported.
    /// </summary>
    public bool SupportsD3D11 { get; internal set; } = false;

    /// <summary>
    /// Whether the system likely has a dedicated GPU.
    /// </summary>
    public bool HasDedicatedGpu { get; internal set; } = false;

    /// <summary>
    /// Estimated GPU memory in MB.
    /// </summary>
    public int EstimatedGpuMemoryMB { get; internal set; } = 128;

    /// <summary>
    /// GPU description string from DXGI.
    /// </summary>
    public string GpuDescription { get; internal set; } = "Unknown";

    /// <summary>
    /// Gets a summary string of GPU capabilities for debugging.
    /// </summary>
    public override string ToString()
    {
        return $"Tier: {WpfRenderingTier}, D3D11: {SupportsD3D11}, Dedicated: {HasDedicatedGpu}, VRAM: {EstimatedGpuMemoryMB}MB, GPU: {GpuDescription}";
    }
}

/// <summary>
/// Available GPU rendering methods for optimal performance.
/// </summary>
public enum GpuRenderingMethod
{
    /// <summary>
    /// DirectX 11 upload and WPF D3DImage presentation through a shared surface.
    /// </summary>
    D3D11,

    /// <summary>
    /// CPU-based WriteableBitmap rendering with optimizations.
    /// Fallback method, always available.
    /// </summary>
    WriteableBitmap
}
