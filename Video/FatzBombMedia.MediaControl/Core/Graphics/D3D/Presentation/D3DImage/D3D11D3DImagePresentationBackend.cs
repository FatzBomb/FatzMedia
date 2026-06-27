using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using WpfD3DImage = System.Windows.Interop.D3DImage;
using System.Windows.Media;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.D3DImage;

/// <summary>
/// Owns the WPF D3DImage presentation backend: shared D3D11 texture creation,
/// D3D9 interop surface binding, dirty-rect notification, and backend cleanup.
/// </summary>
internal sealed class D3D11D3DImagePresentationBackend : ID3D11PresentationBackend
{
    private readonly D3DImagePresentationNotifier _notifier = new();

    private ID3D11Texture2D? _targetTexture;
    private IDXGIResource? _sharedResource;
    private D3D9InteropSurface? _interopSurface;
    private WpfD3DImage? _image;

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.D3DImage;
    public bool IsActive => _targetTexture != null && _image != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture => _targetTexture;
    public ImageSource? ImageSource => _image;

    public bool Matches(int width, int height)
    {
        return IsActive && width == Width && height == Height;
    }

    public bool Create(D3D11DeviceResources deviceResources, int width, int height)
    {
        if (deviceResources.Device == null || width <= 0 || height <= 0)
        {
            return false;
        }

        Cleanup();

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

            _targetTexture = deviceResources.Device.CreateTexture2D(in textureDescription);
            _sharedResource = _targetTexture.QueryInterface<IDXGIResource>();
            var sharedHandle = _sharedResource.SharedHandle;
            if (sharedHandle == IntPtr.Zero)
            {
                Debug.WriteLine("[VorticeD3D11] Failed to get D3DImage shared texture handle");
                Cleanup();
                return false;
            }

            _interopSurface = D3D9InteropSurface.Create(sharedHandle, width, height);
            if (_interopSurface is null || _interopSurface.SurfacePointer == IntPtr.Zero)
            {
                Cleanup();
                return false;
            }

            var image = new WpfD3DImage();
            image.Lock();
            try
            {
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _interopSurface.SurfacePointer);
            }
            finally
            {
                image.Unlock();
            }

            _image = image;
            Width = width;
            Height = height;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VorticeD3D11] Failed to create D3DImage backend: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public void NotifyDirty(Func<bool> isDisposed, Action framePresented)
    {
        if (_image == null || Width <= 0 || Height <= 0)
        {
            return;
        }

        var currentImage = _image;
        _notifier.NotifyDirty(
            currentImage,
            Width,
            Height,
            isDisposed,
            image => ReferenceEquals(currentImage, image) && ReferenceEquals(_image, image),
            framePresented);
    }

    public void Present(int liveResizeFlag, bool syncImmediate)
    {
        // D3DImage presentation is driven by dirty-rect notification, not DXGI Present.
    }

    public void Cleanup()
    {
        _notifier.RetireSurfaces();

        if (_image != null)
        {
            try
            {
                _image.Lock();
                try
                {
                    _image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
                }
                finally
                {
                    _image.Unlock();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VorticeD3D11] D3DImage cleanup failed: {ex.Message}");
            }
        }

        _image = null;
        _interopSurface?.Dispose();
        _interopSurface = null;
        _sharedResource?.Dispose();
        _sharedResource = null;
        _targetTexture?.Dispose();
        _targetTexture = null;
        Width = 0;
        Height = 0;
    }

    public void Dispose()
    {
        Cleanup();
    }
}
