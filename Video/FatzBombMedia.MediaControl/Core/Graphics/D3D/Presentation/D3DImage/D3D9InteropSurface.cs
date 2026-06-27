using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.D3DImage;

internal sealed class D3D9InteropSurface : IDisposable
{
    private const uint D3DSdkVersion = 32;
    private const uint D3DAdapterDefault = 0;
    private const uint D3DDevTypeHal = 1;
    private const uint D3DCreateFpuPreserve = 0x00000002;
    private const uint D3DCreateMultithreaded = 0x00000004;
    private const uint D3DCreateHardwareVertexProcessing = 0x00000040;
    private const uint D3DSwapEffectDiscard = 1;
    private const uint D3DPresentIntervalImmediate = 0x80000000;
    private const uint D3DUsageRenderTarget = 0x00000001;
    private const uint D3DFormatUnknown = 0;
    private const uint D3DFormatA8R8G8B8 = 21;
    private const uint D3DPoolDefault = 0;

    private IntPtr _direct3D;
    private IntPtr _device;
    private IntPtr _texture;
    private IntPtr _surface;
    private bool _disposed;

    public IntPtr SurfacePointer => _surface;

    public static D3D9InteropSurface? Create(IntPtr sharedHandle, int width, int height)
    {
        if (sharedHandle == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return null;
        }

        var surface = new D3D9InteropSurface();
        return surface.Initialize(sharedHandle, width, height) ? surface : null;
    }

    private bool Initialize(IntPtr sharedHandle, int width, int height)
    {
        try
        {
            var result = Direct3DCreate9Ex(D3DSdkVersion, out _direct3D);
            if (result < 0 || _direct3D == IntPtr.Zero)
            {
                Debug.WriteLine($"[D3D9Interop] Direct3DCreate9Ex failed: 0x{result:X8}");
                Dispose();
                return false;
            }

            var presentParameters = new D3DPresentParameters
            {
                Windowed = 1,
                SwapEffect = D3DSwapEffectDiscard,
                BackBufferFormat = D3DFormatUnknown,
                BackBufferWidth = (uint)width,
                BackBufferHeight = (uint)height,
                BackBufferCount = 1,
                PresentationInterval = D3DPresentIntervalImmediate,
                DeviceWindowHandle = GetDesktopWindow()
            };

            var createDeviceEx = GetComDelegate<CreateDeviceExDelegate>(_direct3D, 20);
            result = createDeviceEx(
                _direct3D,
                D3DAdapterDefault,
                D3DDevTypeHal,
                presentParameters.DeviceWindowHandle,
                D3DCreateFpuPreserve | D3DCreateMultithreaded | D3DCreateHardwareVertexProcessing,
                ref presentParameters,
                IntPtr.Zero,
                out _device);

            if (result < 0 || _device == IntPtr.Zero)
            {
                Debug.WriteLine($"[D3D9Interop] CreateDeviceEx failed: 0x{result:X8}");
                Dispose();
                return false;
            }

            var createTexture = GetComDelegate<CreateTextureDelegate>(_device, 23);
            var sharedHandleCopy = sharedHandle;
            result = createTexture(
                _device,
                (uint)width,
                (uint)height,
                1,
                D3DUsageRenderTarget,
                D3DFormatA8R8G8B8,
                D3DPoolDefault,
                out _texture,
                ref sharedHandleCopy);

            if (result < 0 || _texture == IntPtr.Zero)
            {
                Debug.WriteLine($"[D3D9Interop] CreateTexture(shared) failed: 0x{result:X8}");
                Dispose();
                return false;
            }

            var getSurfaceLevel = GetComDelegate<GetSurfaceLevelDelegate>(_texture, 18);
            result = getSurfaceLevel(_texture, 0, out _surface);
            if (result < 0 || _surface == IntPtr.Zero)
            {
                Debug.WriteLine($"[D3D9Interop] GetSurfaceLevel failed: 0x{result:X8}");
                Dispose();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3D9Interop] Initialization failed: {ex.Message}");
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseCom(ref _surface);
        ReleaseCom(ref _texture);
        ReleaseCom(ref _device);
        ReleaseCom(ref _direct3D);
        _disposed = true;
    }

    private static T GetComDelegate<T>(IntPtr comPointer, int vtableSlot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comPointer);
        var methodPointer = Marshal.ReadIntPtr(vtable, vtableSlot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPointer);
    }

    private static void ReleaseCom(ref IntPtr comPointer)
    {
        if (comPointer == IntPtr.Zero)
        {
            return;
        }

        Marshal.Release(comPointer);
        comPointer = IntPtr.Zero;
    }

    [DllImport("d3d9.dll", ExactSpelling = true)]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr direct3D);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDesktopWindow();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceExDelegate(
        IntPtr self,
        uint adapter,
        uint deviceType,
        IntPtr focusWindow,
        uint behaviorFlags,
        ref D3DPresentParameters presentationParameters,
        IntPtr fullscreenDisplayMode,
        out IntPtr returnedDeviceInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureDelegate(
        IntPtr self,
        uint width,
        uint height,
        uint levels,
        uint usage,
        uint format,
        uint pool,
        out IntPtr texture,
        ref IntPtr sharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSurfaceLevelDelegate(
        IntPtr self,
        uint level,
        out IntPtr surface);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPresentParameters
    {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public uint BackBufferFormat;
        public uint BackBufferCount;
        public uint MultiSampleType;
        public uint MultiSampleQuality;
        public uint SwapEffect;
        public IntPtr DeviceWindowHandle;
        public int Windowed;
        public int EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreenRefreshRateInHz;
        public uint PresentationInterval;
    }
}
