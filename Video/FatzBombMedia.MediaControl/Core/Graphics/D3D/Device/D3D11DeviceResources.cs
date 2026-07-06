using System.Diagnostics;
using System.Runtime.InteropServices;
using FatzBombMedia.MediaControl.Core.Configuration;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;

/// <summary>
/// Owns the lifetime of the D3D11 device, immediate context, and optional video interfaces.
/// Keep device creation and COM lifetime rules out of the presentation/coordinator renderer.
/// </summary>
internal sealed class D3D11DeviceResources : IDisposable
{
    public ID3D11Device? Device { get; private set; }
    public ID3D11DeviceContext? Context { get; private set; }
    public ID3D11VideoDevice? VideoDevice { get; private set; }
    public ID3D11VideoContext? VideoContext { get; private set; }
    public IntPtr NativeDevicePointer { get; private set; }

    private ID3D11Multithread? _multithreadProtection;

    public bool HasDevice => Device != null && Context != null;
    public bool HasVideoProcessorSupport => VideoDevice != null && VideoContext != null;

    public bool Initialize(MediaPlayerConfiguration configuration)
    {
        Cleanup();

        try
        {
            DeviceCreationFlags[] creationFlagOptions =
            [
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                DeviceCreationFlags.BgraSupport
            ];

            SharpGen.Runtime.Result result = default;
            FeatureLevel actualFeatureLevel = default;
            foreach (DeviceCreationFlags creationFlags in creationFlagOptions)
            {
                result = D3D11.D3D11CreateDevice(
                    adapter: null,
                    driverType: DriverType.Hardware,
                    flags: creationFlags,
                    featureLevels: [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                    device: out ID3D11Device? device,
                    featureLevel: out actualFeatureLevel,
                    immediateContext: out ID3D11DeviceContext? context);

                if (!result.Failure && device != null && context != null)
                {
                    Device = device;
                    Context = context;
                    break;
                }

                context?.Dispose();
                device?.Dispose();
            }

            if (result.Failure || Device == null || Context == null)
            {
                Debug.WriteLine($"[VorticeD3D11] Failed to create D3D11 device: {result}");
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to create D3D11 device: {result}");
                return false;
            }

            InitializeVideoInterfaces(configuration);
            NativeDevicePointer = Device.NativePointer;
            if (configuration.EnableLogging) Debug.WriteLine($"[VorticeD3D11] D3D11 device created successfully with feature level: {actualFeatureLevel}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Failed to initialize: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to initialize: {ex.Message}", innerException: ex);
            Cleanup();
            return false;
        }
    }

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
            if (configuration.EnableLogging) Debug.WriteLine("[VorticeD3D11] Attached to FFmpeg D3D11 hardware device for direct texture presentation");
            return HasVideoProcessorSupport;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Failed to attach native D3D11 device: {ex.Message}");
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11", $"Failed to attach native D3D11 device: {ex.Message}", innerException: ex);
            Cleanup();
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
        _multithreadProtection = Device.QueryInterfaceOrNull<ID3D11Multithread>();
        _ = (_multithreadProtection?.SetMultithreadProtected(true));

        if (VideoDevice == null || VideoContext == null)
        {
            Debug.WriteLine("[VorticeD3D11] D3D11 video processor unavailable; falling back to CPU texture uploads");
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
        VideoContext?.Dispose();
        VideoDevice = null;
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
