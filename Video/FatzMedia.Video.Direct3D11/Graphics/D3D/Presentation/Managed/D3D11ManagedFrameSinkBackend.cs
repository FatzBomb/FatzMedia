using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.Rendering;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Managed;

/// <summary>
/// Renders into an offscreen D3D11 target, then reads the completed frame back into a managed sink.
/// The synchronous GPU readback is intentionally opt-in and is slower than direct swap-chain
/// presentation, especially for high-resolution or high-refresh video. The CPU readback buffer
/// survives <see cref="Cleanup"/> and re-creation at another size (a live resize re-creates the
/// target at every step), and is released by <see cref="Dispose"/> (ENG-026).
/// </summary>
sealed class D3D11ManagedFrameSinkBackend : ID3D11PresentationBackend
{
    D3D11DeviceResources? _deviceResources;
    ID3D11Texture2D? _stagingTexture;
    IMediaPresentationFrameSink? _sink;
    byte[] _pixels = [];
    bool _deviceLostReported;
    readonly RendererDiagnosticLatch _createFailure = new();
    readonly RendererDiagnosticLatch _readbackFailure = new();

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.ManagedFrameSink;
    public bool IsActive => TargetTexture != null && _stagingTexture != null && _sink != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture { get; private set; }
    public D3D11TargetViewCache TargetViews { get; } = new();

    /// <summary>Gets the length of the retained readback buffer (tests).</summary>
    internal int ReadbackBufferLength => _pixels.Length;

    /// <summary>
    /// Whether this backend is serving <paramref name="sink"/> now: a new size for it is a resize,
    /// whose sink keeps showing its last frame until the next one (ENG-039 G4).
    /// </summary>
    public bool Serves(IMediaPresentationFrameSink sink) => IsActive && ReferenceEquals(_sink, sink);

    public bool Matches(IMediaPresentationFrameSink sink, int width, int height) =>
        IsActive && ReferenceEquals(_sink, sink) && Width == width && Height == height;

    public bool Create(
        D3D11DeviceResources deviceResources,
        IMediaPresentationFrameSink sink,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(deviceResources);
        ArgumentNullException.ThrowIfNull(sink);
        if (!deviceResources.HasDevice || width <= 0 || height <= 0)
            return false;

        Cleanup();
        try
        {
            var targetDescription = new Texture2DDescription(
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
            var stagingDescription = new Texture2DDescription(
                Format.B8G8R8A8_UNorm,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read,
                sampleCount: 1,
                sampleQuality: 0,
                ResourceOptionFlags.None);

            TargetTexture = deviceResources.Device!.CreateTexture2D(in targetDescription);
            _stagingTexture = deviceResources.Device.CreateTexture2D(in stagingDescription);
            _deviceResources = deviceResources;
            _sink = sink;
            Width = width;
            Height = height;
            _pixels = RetainedPixelBuffer.Ensure(_pixels, checked(width * height * 4));
            _createFailure.Reset();
            return true;
        }
        catch (Exception ex)
        {
            _ = _createFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Managed frame-sink target creation at {width}x{height} failed: {ex.Message}", ex);
            Cleanup();
            return false;
        }
    }

    public unsafe bool Present(int liveResizeFlag, bool syncImmediate)
    {
        // Map(Read) is the synchronization boundary for this readback path, so swap-chain
        // presentation timing options do not apply to the managed sink.
        _ = liveResizeFlag;
        _ = syncImmediate;
        if (!IsActive || _deviceResources?.Context == null)
            return false;

        var context = _deviceResources.Context;
        try
        {
            context.CopyResource(_stagingTexture!, TargetTexture!);
            var result = context.Map(
                _stagingTexture!,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None,
                out var mapped);
            if (result.Failure)
            {
                ReportPresentFailure(result);
                return false;
            }

            try
            {
                var stride = checked(Width * 4);
                var destination = _pixels.AsSpan(0, checked(stride * Height));
                for (var y = 0; y < Height; y++)
                {
                    var source = new ReadOnlySpan<byte>(
                        (void*)(mapped.DataPointer + checked((int)mapped.RowPitch * y)),
                        stride);
                    source.CopyTo(destination.Slice(y * stride, stride));
                }

                _sink!.PresentFrame(destination, Width, Height, stride);
            }
            finally
            {
                context.Unmap(_stagingTexture!, 0);
            }

            _deviceLostReported = false;
            _readbackFailure.Reset();
            return true;
        }
        catch (Exception ex)
        {
            ReportPresentException(ex);
            return false;
        }
    }

    void ReportPresentFailure(Result result)
    {
        if (D3D11DeviceLostClassifier.IsDeviceLost(result))
        {
            RaiseDeviceLostOnce(D3D11DeviceLostClassifier.Describe(result, swapChain: null));
        }
        else
        {
            _ = _readbackFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Managed frame-sink readback Map failed: {result}");
        }
    }

    void ReportPresentException(Exception ex)
    {
        var removedReason = GetDeviceRemovedReason();
        if (removedReason.Failure && D3D11DeviceLostClassifier.IsDeviceLost(removedReason))
        {
            RaiseDeviceLostOnce($"Managed frame-sink present detected GPU device lost: {D3D11DeviceLostClassifier.Describe(removedReason, swapChain: null)}");
        }
        else
        {
            _ = _readbackFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Managed frame-sink readback failed: {ex.Message}", ex);
        }
    }

    Result GetDeviceRemovedReason()
    {
        if (_deviceResources?.Device == null)
            return default;

        try
        {
            return _deviceResources.Device.DeviceRemovedReason;
        }
        catch
        {
            // The device may already be gone by the time the exception is handled; report the
            // failure as generic rather than fail the diagnostic path.
            return default;
        }
    }

    void RaiseDeviceLostOnce(string message)
    {
        if (_deviceLostReported)
            return;

        _deviceLostReported = true;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", message);
    }

    /// <summary>Releases the textures and the sink; the readback buffer is kept for the next <see cref="Create"/>.</summary>
    public void Cleanup()
    {
        TargetViews.Release();
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        TargetTexture?.Dispose();
        TargetTexture = null;
        _deviceResources = null;
        _sink = null;
        Width = 0;
        Height = 0;
        _deviceLostReported = false;
    }

    public void Dispose()
    {
        Cleanup();
        _pixels = [];
    }
}
