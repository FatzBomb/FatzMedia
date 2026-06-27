using FatzBombMedia.MediaControl.Core.Graphics.D3D.Device;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.DirectComposition;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation.NativeHwnd;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Upload;
using FatzBombMedia.MediaControl.Core.Graphics.D3D.Video;
using Vortice.Direct3D11;
using System.Diagnostics;
using System.Windows.Media;

namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Tracks the active presentation surface and centralizes backend activation/sizing.
/// The renderer should coordinate frame upload/composition; this controller owns the
/// bookkeeping that says which backend target texture is active and how large it is.
/// </summary>
internal sealed class D3D11PresentationSurfaceController
{
    private readonly D3D11PresentationBackendSet _backends;

    public D3D11PresentationSurfaceController(D3D11PresentationBackendSet backends)
    {
        _backends = backends;
    }

    private long _targetRevision;

    public ID3D11Texture2D? TargetTexture { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long TargetRevision => _targetRevision;
    public IntPtr NativeHwnd { get; private set; }
    public bool LastActivationCreated { get; private set; }
    public ImageSource? ImageSource => _backends.D3DImage.ImageSource;
    public bool IsReady => TryGetActiveTargetSnapshot(applyPendingNativeResize: false, reason: null, out _);
    public bool IsNativeHwndActive => _backends.NativeHwnd.IsActive;
    public bool IsDirectCompositionActive => _backends.DirectComposition.IsActive;
    public string PresentationBackend => _backends.ActiveKind switch
    {
        D3D11PresentationBackendKind.DirectCompositionSingleTarget => "DirectCompositionNativeLayers",
        D3D11PresentationBackendKind.NativeHwndSwapChain => "NativeHwndSwapChain",
        D3D11PresentationBackendKind.D3DImage => "D3DImage",
        _ => "None"
    };


    public bool TryGetActiveTargetSnapshot(
        bool applyPendingNativeResize,
        string? reason,
        out D3D11PresentationTargetSnapshot snapshot)
    {
        snapshot = default;

        if (applyPendingNativeResize && !ApplyPendingNativeHwndResizeIfNeeded())
        {
            LogInvalidTarget(reason, "pending native HWND resize failed");
            return false;
        }

        var active = _backends.Active;
        if (active == null)
        {
            LogInvalidTarget(reason, "no active backend");
            return false;
        }

        var texture = active.TargetTexture;
        var width = active.Width;
        var height = active.Height;
        if (texture == null || width <= 0 || height <= 0)
        {
            LogInvalidTarget(reason, $"invalid active target texture/dimensions (texture null: {texture == null}, {width}x{height})");
            return false;
        }

        if (!ReferenceEquals(texture, TargetTexture) || width != Width || height != Height || active.Kind != _backends.ActiveKind)
        {
            Adopt(active, active.Kind == D3D11PresentationBackendKind.NativeHwndSwapChain ? _backends.NativeHwnd.Hwnd : NativeHwnd);
        }

        snapshot = new D3D11PresentationTargetSnapshot(
            TargetTexture!,
            Width,
            Height,
            NativeHwnd,
            active.Kind,
            PresentationBackend,
            _targetRevision);
        return snapshot.IsValid;
    }

    private static void LogInvalidTarget(string? reason, string detail)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        Debug.WriteLine($"[D3D11PresentationSurfaceController] Skipping {reason}: {detail}.");
    }

    public bool OwnsTexture(ID3D11Texture2D? texture)
    {
        return _backends.OwnsTexture(texture);
    }

    public void CleanupBackends()
    {
        _backends.Cleanup();
    }

    public void Reset()
    {
        TargetTexture = null;
        Width = 0;
        Height = 0;
        NativeHwnd = IntPtr.Zero;
        LastActivationCreated = false;
        _targetRevision++;
    }

    public bool ActivatePresentationSurface(
        bool isDisposed,
        bool isValidTarget,
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Func<Action, bool> activateSurface,
        string description,
        string backendName,
        bool enableLogging,
        Action<int, int, bool> notifyPresented)
    {
        if (isDisposed || !isValidTarget || !deviceResources.HasDevice || width <= 0 || height <= 0)
        {
            return false;
        }

        return activateSurface(() =>
        {
            notifyPresented(Width, Height, true);

            if (!enableLogging)
            {
                return;
            }

            Debug.WriteLine($"[VorticeD3D11] Created {description} for {width}x{height}");
            Debug.WriteLine($"[VorticeD3D11] Presentation backend: {backendName}");
        });
    }

    public bool CreateD3DImageSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented)
    {
        return ActivatePresentationSurface(
            isDisposed,
            true,
            deviceResources,
            width,
            height,
            onCreated => ActivateD3DImageSurface(
                deviceResources,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "D3DImage shared presentation surface",
            "D3DImage",
            enableLogging,
            notifyPresented);
    }

    public bool CreateNativeHwndSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented)
    {
        return ActivatePresentationSurface(
            isDisposed,
            hwnd != IntPtr.Zero,
            deviceResources,
            width,
            height,
            onCreated => ActivateNativeHwndSurface(
                deviceResources,
                hwnd,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "native HWND swap-chain presentation surface",
            "NativeHwndSwapChain",
            enableLogging,
            notifyPresented);
    }

    public bool CreateDirectCompositionSurface(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        bool enableLogging,
        Action<int, int, bool> notifyPresented)
    {
        return ActivatePresentationSurface(
            isDisposed,
            hwnd != IntPtr.Zero,
            deviceResources,
            width,
            height,
            onCreated => ActivateDirectCompositionSurface(
                deviceResources,
                hwnd,
                width,
                height,
                cleanupBeforeCreate,
                onCreated),
            "DirectComposition native layered presentation surface",
            "DirectCompositionNativeLayers",
            enableLogging,
            notifyPresented);
    }

    public bool ActivateD3DImageSurface(
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureD3DImageSurface(deviceResources, width, height, cleanupBeforeCreate))
        {
            return false;
        }

        if (LastActivationCreated)
        {
            _backends.ClearActiveTargetToBlack(deviceResources);
            onCreated();
        }

        return true;
    }

    public bool ActivateNativeHwndSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureNativeHwndSurface(deviceResources, hwnd, width, height, cleanupBeforeCreate))
        {
            return false;
        }

        if (LastActivationCreated)
        {
            _backends.ClearActiveTargetToBlack(deviceResources);
            onCreated();
        }

        return true;
    }

    public bool ActivateDirectCompositionSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate,
        Action onCreated)
    {
        if (!EnsureDirectCompositionSurface(deviceResources, hwnd, width, height, cleanupBeforeCreate))
        {
            return false;
        }

        if (LastActivationCreated)
        {
            _backends.ClearActiveTargetToBlack(deviceResources);
            _backends.DirectComposition.ClearOverlayToTransparent(deviceResources);
            onCreated();
            _backends.DirectComposition.PresentOverlayAndCommit();
        }

        return true;
    }

    public bool EnsureD3DImageSurface(
        D3D11DeviceResources deviceResources,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (_backends.D3DImage.Matches(width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!_backends.D3DImage.Create(deviceResources, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(_backends.D3DImage);
        LastActivationCreated = true;
        return true;
    }

    public bool EnsureNativeHwndSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (_backends.NativeHwnd.Matches(hwnd, width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!_backends.NativeHwnd.Create(deviceResources, hwnd, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(_backends.NativeHwnd, hwnd);
        LastActivationCreated = true;
        return true;
    }

    public bool EnsureDirectCompositionSurface(
        D3D11DeviceResources deviceResources,
        IntPtr hwnd,
        int width,
        int height,
        Action cleanupBeforeCreate)
    {
        if (_backends.DirectComposition.Matches(hwnd, width, height))
        {
            LastActivationCreated = false;
            return true;
        }

        cleanupBeforeCreate();
        Reset();

        if (!_backends.DirectComposition.Create(deviceResources, hwnd, width, height))
        {
            cleanupBeforeCreate();
            return false;
        }

        Adopt(_backends.DirectComposition, hwnd);
        LastActivationCreated = true;
        return true;
    }

    public void SetDirectCompositionOutputSize(bool isDisposed, int width, int height, bool liveResize)
    {
        if (isDisposed || !_backends.DirectComposition.IsActive || Width <= 0 || Height <= 0)
        {
            return;
        }

        _backends.DirectComposition.SetOutputSize(
            width,
            height,
            Width,
            Height,
            liveResize);
    }

    public bool UploadDirectCompositionOverlayFrame(
        bool isDisposed,
        D3D11DeviceResources deviceResources,
        D3D11FrameUploadResources uploadResources,
        D3D11VideoProcessorPresenter videoProcessorPresenter,
        ReadOnlySpan<byte> pixelData,
        int width,
        int height,
        int stride,
        bool liveResize)
    {
        if (isDisposed || !_backends.DirectComposition.IsActive || width <= 0 || height <= 0)
        {
            return false;
        }

        return _backends.DirectComposition.UploadOverlayFrame(
            deviceResources,
            uploadResources.TextureCache,
            videoProcessorPresenter,
            pixelData,
            width,
            height,
            stride,
            liveResize,
            uploadResources.RecordUploadWithCurrentTarget);
    }

    public void ClearPresentedFrame(
        D3D11DeviceResources deviceResources,
        bool isDisposed,
        bool liveResize,
        Action raiseFramePresented)
    {
        if (isDisposed)
        {
            return;
        }

        _backends.ClearPresentedFrame(
            deviceResources,
            liveResize ? 1 : 0,
            syncImmediate: true,
            () => isDisposed,
            raiseFramePresented);
    }

    public void NotifyFramePresented(
        bool liveResize,
        bool syncImmediate,
        Func<bool> isDisposed,
        Action raiseFramePresented)
    {
        _backends.NotifyFramePresented(
            liveResize ? 1 : 0,
            syncImmediate,
            isDisposed,
            raiseFramePresented);
    }

    public void QueueNativeHwndResize(bool isDisposed, int width, int height)
    {
        if (isDisposed || !_backends.NativeHwnd.IsActive)
        {
            return;
        }

        _backends.NativeHwnd.QueueResize(Math.Max(1, width), Math.Max(1, height));
    }

    public bool ResizeNativeHwndPresentation(bool isDisposed, int width, int height)
    {
        if (isDisposed || !_backends.NativeHwnd.IsActive)
        {
            return false;
        }

        var resized = _backends.NativeHwnd.Resize(Math.Max(1, width), Math.Max(1, height));
        SyncNativeHwndState();
        return resized;
    }

    public bool ApplyPendingNativeHwndResizeIfNeeded()
    {
        if (!_backends.NativeHwnd.IsActive)
        {
            return true;
        }

        var resized = _backends.NativeHwnd.ApplyPendingResizeIfNeeded();
        SyncNativeHwndState();
        return resized;
    }

    private void SyncNativeHwndState()
    {
        if (_backends.NativeHwnd.IsActive)
        {
            Adopt(_backends.NativeHwnd, _backends.NativeHwnd.Hwnd);
        }
    }

    private void Adopt(ID3D11PresentationBackend backend, IntPtr nativeHwnd = default)
    {
        TargetTexture = backend.TargetTexture;
        Width = backend.Width;
        Height = backend.Height;
        NativeHwnd = nativeHwnd;
        _targetRevision++;
    }
}
