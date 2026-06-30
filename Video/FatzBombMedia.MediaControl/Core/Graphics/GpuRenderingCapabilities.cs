using SharpGen.Runtime;
using System.Diagnostics;
using Vortice.Direct3D;
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
    /// Detects system GPU capabilities and DirectX support.
    /// </summary>
    private static void DetectCapabilities()
    {
        try
        {
            _capabilities = new GpuCapabilityInfo
            {
                // DirectX 11 detection - check if D3D11 DLLs exist and can be loaded
                SupportsD3D11 = CheckD3D11Support()
            };

            // D3D9 support removed - modern D3D11 approach only
            // No longer setting SupportsD3D9 as it's obsolete

            // Try to get more accurate GPU info via DXGI
            DetectGpuInfoViaDxgi();

            Debug.WriteLine($"[GPU] Capabilities: D3D11={_capabilities.SupportsD3D11}, Dedicated={_capabilities.HasDedicatedGpu}");
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
            Result result = D3D11.D3D11CreateDevice(
                adapter: null,
                driverType: DriverType.Hardware,
                flags: DeviceCreationFlags.BgraSupport,
                featureLevels: [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                device: out ID3D11Device? device,
                featureLevel: out FeatureLevel featureLevel);

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
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            Result result = factory.EnumAdapters1(0, out IDXGIAdapter1? adapter); // Primary adapter

            if (result.Success && adapter != null)
            {
                using (adapter)
                {
                    AdapterDescription1 desc = adapter.Description1;

                    // Convert dedicated video memory from bytes to MB
                    var dedicatedMemoryMB = (int)(desc.DedicatedVideoMemory.Value / (1024 * 1024));

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
        }
    }
}

/// <summary>
/// Contains detailed information about system GPU capabilities.
/// </summary>
public sealed class GpuCapabilityInfo
{
    /// <summary>
    /// Whether DirectX 11 is available and supported.
    /// </summary>
    public bool SupportsD3D11 { get; internal set; } = false;

    /// <summary>
    /// Whether the system likely has a dedicated GPU.
    /// </summary>
    public bool HasDedicatedGpu { get; internal set; } = false;

    /// <summary>
    /// Gets a summary string of GPU capabilities for debugging.
    /// </summary>
    public override string ToString() => $"D3D11: {SupportsD3D11}, Dedicated: {HasDedicatedGpu}";
}

/// <summary>
/// Available GPU rendering methods for optimal performance.
/// </summary>
public enum GpuRenderingMethod
{
    /// <summary>
    /// DirectX 11 upload and GPU presentation through the active presentation backend.
    /// </summary>
    D3D11,

    /// <summary>
    /// Host-specific CPU fallback rendering implemented by whichever UI adapter is active.
    /// </summary>
    CpuFallback
}
