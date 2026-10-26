using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Rendering.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzBomb.MediaControl.Core.Graphics.D3D.Device;

/// <summary>
/// Owns the lifetime of the D3D11 device, immediate context, and optional video interfaces.
/// Keep device creation and COM lifetime rules out of the presentation/coordinator renderer.
/// </summary>
internal sealed class D3D11DeviceResources(Direct3D11DeviceSelection selection = Direct3D11DeviceSelection.Auto) : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1];
    private static readonly DeviceCreationFlags[] CreationFlagOptions =
    [
        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
        DeviceCreationFlags.BgraSupport
    ];

    // The adapter Windows uses when no display driver is installed: WARP behind a hardware
    // driver type, so a "hardware" device on it is still a software rasterizer.
    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDriverDeviceId = 0x8C;

    private readonly Direct3D11DeviceSelection _selection = selection;
    private bool _isSoftwareRasterizer;

    public ID3D11Device? Device { get; private set; }
    public ID3D11DeviceContext? Context { get; private set; }
    public ID3D11VideoDevice? VideoDevice { get; private set; }
    public ID3D11VideoContext? VideoContext { get; private set; }

    /// <summary>The video context's DXGI color-space interface (Windows 10 and later), or null.</summary>
    public ID3D11VideoContext1? VideoContext1 { get; private set; }
    public IntPtr NativeDevicePointer { get; private set; }

    private ID3D11Multithread? _multithreadProtection;

    public bool HasDevice => Device != null && Context != null;
    public bool HasVideoProcessorSupport => VideoDevice != null && VideoContext != null;

    public bool Initialize(MediaPlayerConfiguration configuration)
    {
        Cleanup();

        try
        {
            if (!TryCreateDevice(_selection, out CreatedDevice? created, out var failure))
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to create D3D11 device ({_selection}): {failure}");
                return false;
            }

            Device = created.Device;
            Context = created.Context;
            InitializeVideoInterfaces(configuration);
            NativeDevicePointer = Device.NativePointer;

            // Stage 036A: name the device every time one is created, so a log always shows whether
            // video ran on the GPU or on WARP, and why.
            (var description, _isSoftwareRasterizer) = Describe(created);
            if (created.HardwareFailure != null)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Warning,
                    "D3D11",
                    $"No GPU device could be created ({created.HardwareFailure}); using {description}.");
            }
            else
            {
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", $"Direct3D11 device: {description}.");
            }

            return true;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to initialize: {ex.Message}", innerException: ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Creates and releases a device with <paramref name="selection"/> and reports what it got
    /// (Stage 036A: lets a host choose another renderer before any session exists).
    /// </summary>
    public static MediaPresentationAvailability Probe(Direct3D11DeviceSelection selection)
    {
        try
        {
            if (!TryCreateDevice(selection, out CreatedDevice? created, out var failure))
                return MediaPresentationAvailability.Unavailable($"no Direct3D11 device ({selection}): {failure}");

            try
            {
                (var description, var softwareRasterizer) = Describe(created);
                return MediaPresentationAvailability.Available(created.HardwareFailure == null
                    ? description
                    : $"{description} (no GPU device: {created.HardwareFailure})") with
                {
                    // A hardware decoder would hand over frames on a GPU device the renderer then
                    // adopts, leaving WARP; decode in software to stay on the device probed here.
                    RequiresSoftwareDecoding = softwareRasterizer
                };
            }
            finally
            {
                created.Context.Dispose();
                created.Device.Dispose();
            }
        }
        catch (Exception ex)
        {
            return MediaPresentationAvailability.Unavailable($"Direct3D11 probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries each driver type the selection allows (hardware, then WARP for Auto), each with and
    /// without video support. <see cref="CreatedDevice.HardwareFailure"/> says why the GPU was
    /// skipped when WARP was used under Auto.
    /// </summary>
    private static bool TryCreateDevice(
        Direct3D11DeviceSelection selection,
        [NotNullWhen(true)] out CreatedDevice? created,
        out string failure)
    {
        DriverType[] driverTypes = selection switch
        {
            Direct3D11DeviceSelection.Hardware => [DriverType.Hardware],
            Direct3D11DeviceSelection.Warp => [DriverType.Warp],
            _ => [DriverType.Hardware, DriverType.Warp]
        };

        string? hardwareFailure = null;
        string? warpFailure = null;
        foreach (DriverType driverType in driverTypes)
        {
            SharpGen.Runtime.Result result = default;
            foreach (DeviceCreationFlags creationFlags in CreationFlagOptions)
            {
                result = D3D11.D3D11CreateDevice(
                    adapter: null,
                    driverType: driverType,
                    flags: creationFlags,
                    featureLevels: FeatureLevels,
                    device: out ID3D11Device? device,
                    featureLevel: out FeatureLevel featureLevel,
                    immediateContext: out ID3D11DeviceContext? context);

                if (!result.Failure && device != null && context != null)
                {
                    created = new CreatedDevice(device, context, driverType, featureLevel, driverType == DriverType.Hardware ? null : hardwareFailure);
                    failure = string.Empty;
                    return true;
                }

                context?.Dispose();
                device?.Dispose();
            }

            if (driverType == DriverType.Hardware)
                hardwareFailure = result.ToString();
            else
                warpFailure = result.ToString();
        }

        created = null;
        failure = (hardwareFailure, warpFailure) switch
        {
            ({ } gpu, { } warp) => $"GPU: {gpu}; WARP: {warp}",
            ({ } gpu, null) => $"GPU: {gpu}",
            (null, { } warp) => $"WARP: {warp}",
            _ => "no driver type tried"
        };
        return false;
    }

    private static (string Description, bool IsSoftwareRasterizer) Describe(CreatedDevice created)
    {
        var adapter = "unknown adapter";
        var basicRenderDriver = false;
        try
        {
            using IDXGIDevice dxgiDevice = created.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter dxgiAdapter = dxgiDevice.GetAdapter();
            AdapterDescription description = dxgiAdapter.Description;
            adapter = description.Description.TrimEnd('\0');
            basicRenderDriver = description.VendorId == MicrosoftVendorId && description.DeviceId == BasicRenderDriverDeviceId;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            // The adapter name is only for the log.
        }

        var kind = created.DriverType == DriverType.Warp
            ? "WARP (Windows software rasterizer)"
            : basicRenderDriver ? "software rasterizer (no display driver)" : "GPU";
        return ($"{kind}, {adapter}, feature level {created.FeatureLevel}", created.DriverType == DriverType.Warp || basicRenderDriver);
    }

    private sealed record CreatedDevice(
        ID3D11Device Device,
        ID3D11DeviceContext Context,
        DriverType DriverType,
        FeatureLevel FeatureLevel,
        string? HardwareFailure);

    // Hardware frame release safety (007B): this method adopts the FFmpeg/D3D11 decoder's device AND
    // immediate context (AddRef'd) so the coordinator's blit + Present run on the same sequential command
    // stream as the decoder. This shared-context serialization is what prevents the decoder from
    // recycling a hardware texture before the coordinator's GPU blit has finished consuming it.
    //
    // If a future change decouples the coordinator onto a separate device or deferred context, a
    // D3D11 fence or deferred-release ring must be added — the shared-context safety invariant would
    // no longer hold.
    public bool InitializeFromNativeDevice(IntPtr devicePointer, IntPtr deviceContextPointer, MediaPlayerConfiguration configuration)
    {
        if (devicePointer == IntPtr.Zero)
        {
            return false;
        }

        Cleanup();

        try
        {
            Device = AddRefAndWrap(devicePointer, static pointer => new ID3D11Device(pointer));
            Context = deviceContextPointer != IntPtr.Zero
                ? AddRefAndWrap(deviceContextPointer, static pointer => new ID3D11DeviceContext(pointer))
                : Device.ImmediateContext;

            InitializeVideoInterfaces(configuration);
            NativeDevicePointer = devicePointer;
            if (_isSoftwareRasterizer)
            {
                DiagnosticRouter.Raise(
                    MediaDiagnosticLevel.Info,
                    "D3D11",
                    "Hardware-decoded video brought its GPU device; presenting on it instead of the software rasterizer. Use software decoding to stay on it.");
                _isSoftwareRasterizer = false;
            }

            if (configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Attached to FFmpeg D3D11 hardware device for direct texture presentation");
            return HasVideoProcessorSupport;
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to attach native D3D11 device: {ex.Message}", innerException: ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>
    /// Whether the device behind <paramref name="devicePointer"/> (for example a decoder's) was
    /// removed by a GPU reset or driver update. False for a null pointer or when it cannot be asked.
    /// </summary>
    public static bool IsDeviceRemoved(IntPtr devicePointer)
    {
        if (devicePointer == IntPtr.Zero)
            return false;

        try
        {
            using ID3D11Device device = AddRefAndWrap(devicePointer, static pointer => new ID3D11Device(pointer));
            return device.DeviceRemovedReason.Failure;
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return false;
        }
    }

    private void InitializeVideoInterfaces(MediaPlayerConfiguration configuration)
    {
        CleanupVideoInterfaces();

        if (Device == null || Context == null)
        {
            return;
        }

        VideoDevice = Device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        VideoContext = Context.QueryInterfaceOrNull<ID3D11VideoContext>();
        VideoContext1 = Context.QueryInterfaceOrNull<ID3D11VideoContext1>();
        _multithreadProtection = Device.QueryInterfaceOrNull<ID3D11Multithread>();
        _ = (_multithreadProtection?.SetMultithreadProtected(true));

        if (VideoDevice == null || VideoContext == null)
        {
            // Once per device: explains hardware video drawn through CPU uploads.
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", "Direct3D11 video processor unavailable; video frames are uploaded from the CPU.");
        }
        else if (configuration.EnableLogging)
        {
            Debug.WriteLine("[VorticeD3D11] D3D11 video processor available");
        }
    }

    private void CleanupVideoInterfaces()
    {
        _multithreadProtection?.Dispose();
        _multithreadProtection = null;
        VideoDevice?.Dispose();
        VideoContext1?.Dispose();
        VideoContext?.Dispose();
        VideoDevice = null;
        VideoContext1 = null;
        VideoContext = null;
    }

    public void Cleanup()
    {
        CleanupVideoInterfaces();
        Context?.Dispose();
        Context = null;
        Device?.Dispose();
        Device = null;
        NativeDevicePointer = IntPtr.Zero;
    }

    public void Dispose() => Cleanup();

    private static T AddRefAndWrap<T>(IntPtr pointer, Func<IntPtr, T> factory)
    {
        _ = Marshal.AddRef(pointer);
        return factory(pointer);
    }
}
