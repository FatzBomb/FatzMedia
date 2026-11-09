using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using FatzMedia.Video.Core.Graphics.Rendering;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Core.Graphics.D3D.Presentation.Shared;

/// <summary>
/// Stage 027A slice 2: presents the composed primary frame into DXGI shared textures with keyed
/// mutexes that a UI compositor (Avalonia's <c>ICompositionGpuInterop</c>) imports, so no pixel is
/// read back. The renderer composes into a private target as for every backend; <see cref="Present"/>
/// copies it on the GPU into the next free exported texture (two, alternating) under that
/// texture's keyed mutex and announces the frame. The compositor acquires the mutex with
/// <see cref="MediaSharedGpuImageRing.ConsumerKey"/>, so it never samples a half-written frame,
/// and the renderer never writes a texture the compositor has not released.
/// </summary>
sealed partial class D3D11SharedTextureBackend : ID3D11PresentationBackend
{
    internal const int BufferCount = 2;

    const int WaitTimeout = 0x102;
    const int WaitAbandoned = 0x80;

    /// <summary>
    /// How long a frame waits for the compositor to give a texture back before it is dropped. Kept
    /// short (half a 60 Hz frame): the wait holds the presentation lock that UI attach, resize and
    /// detach also take.
    /// </summary>
    internal static readonly TimeSpan ReleaseWaitTimeout = TimeSpan.FromMilliseconds(8);

    const uint AcquireTimeoutMilliseconds = 100;

    static long _nextGeneration;

    readonly SharedTexture?[] _textures = new SharedTexture?[BufferCount];
    D3D11DeviceResources? _deviceResources;
    ID3D11Texture2D? _targetTexture;
    IMediaSharedGpuImageConsumer? _consumer;
    MediaSharedGpuImageRing? _ring;
    MediaSharedGpuImageSet? _set;
    MediaSharedGpuImageSelection _selection;
    bool _recreate;
    bool _dropReported;
    bool _deviceLostReported;
    // Failures that can repeat every frame: one report per episode, until a frame goes through.
    readonly RendererDiagnosticLatch _presentFailure = new();
    readonly RendererDiagnosticLatch _reexport = new();
    // ENG-039 G6: the consumer and selection the last Info "exported" line named. A resize re-exports
    // for the same consumer at every step, which goes to the debug output only. An identity hash,
    // not a reference, so a detached consumer is not kept alive by this backend.
    int _reportedConsumerId;
    MediaSharedGpuImageSelection? _reportedSelection;

    public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.SharedTexture;
    public bool IsActive => _targetTexture != null && _set != null && _consumer != null;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public ID3D11Texture2D? TargetTexture => _targetTexture;
    public D3D11TargetViewCache TargetViews { get; } = new();

    /// <summary>What this backend exports: keyed-mutex textures as NT or legacy global handles.</summary>
    internal static MediaSharedGpuImageCapability[] ProducerCapabilities { get; } =
    [
        new(MediaSharedGpuImageHandleKind.D3D11TextureNtHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
        new(MediaSharedGpuImageHandleKind.D3D11TextureGlobalSharedHandle, MediaSharedGpuImageSyncKinds.KeyedMutex),
    ];

    /// <summary>An adapter LUID as the 8 bytes compositors report (low part, then high part, little-endian).</summary>
    internal static byte[] LuidBytes(uint lowPart, int highPart)
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), lowPart);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), highPart);
        return bytes;
    }

    /// <summary>
    /// Whether this backend is serving <paramref name="consumer"/> now: a new size for it is a
    /// resize, whose consumer keeps showing its last frame until the next one (ENG-039 G4).
    /// </summary>
    public bool Serves(IMediaSharedGpuImageConsumer consumer) => IsActive && ReferenceEquals(_consumer, consumer);

    public bool Matches(IMediaSharedGpuImageConsumer consumer, int width, int height) =>
        IsActive && !_recreate && ReferenceEquals(_consumer, consumer) && Width == width && Height == height;

    /// <summary>
    /// Negotiates with the consumer, creates the private target and the exported textures, and
    /// hands the set to the consumer. Returns <see langword="false"/> with a reason the consumer
    /// should hear when this device cannot serve it.
    /// </summary>
    public bool Create(
        D3D11DeviceResources deviceResources,
        IMediaSharedGpuImageConsumer consumer,
        int width,
        int height,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(deviceResources);
        ArgumentNullException.ThrowIfNull(consumer);
        Cleanup();
        if (!deviceResources.HasDevice || width <= 0 || height <= 0)
        {
            reason = "the Direct3D11 device is not ready";
            return false;
        }

        var request = consumer.Request;
        if (!request.TrySelect(ProducerCapabilities, [], out var selection, out reason)
            || !request.MatchesDevice(GetAdapterLuid(deviceResources.Device!), [], out reason))
        {
            return false;
        }

        try
        {
            var device = deviceResources.Device!;
            _targetTexture = device.CreateTexture2D(Describe(width, height, ResourceOptionFlags.None));
            var sharing = ResourceOptionFlags.SharedKeyedMutex
                | (selection.HandleKind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle ? ResourceOptionFlags.SharedNTHandle : ResourceOptionFlags.None);
            for (var i = 0; i < BufferCount; i++)
                _textures[i] = CreateSharedTexture(device, width, height, sharing, selection.HandleKind);
        }
        catch (Exception exception) when (exception is SharpGenException or InvalidOperationException)
        {
            reason = $"creating the shared textures failed: {exception.Message}";
            Cleanup();
            return false;
        }

        _deviceResources = deviceResources;
        // The "frame dropped" line is once per consumer: a resize's re-export keeps it latched.
        if (RuntimeHelpers.GetHashCode(consumer) != _reportedConsumerId)
            _dropReported = false;
        _consumer = consumer;
        _selection = selection;
        Width = width;
        Height = height;
        Publish();
        reason = null;
        return true;
    }

    /// <summary>Copies the composed target into the next free shared texture and announces it.</summary>
    public bool Present(int liveResizeFlag, bool syncImmediate)
    {
        // The compositor paces presentation; swap-chain timing options do not apply.
        _ = liveResizeFlag;
        _ = syncImmediate;
        if (!IsActive || _deviceResources?.Context is not { } context)
            return false;

        if (_recreate && !Republish())
            return false;

        var ring = _ring!;
        if (!ring.TryBeginWrite(ReleaseWaitTimeout, out var write))
        {
            // The compositor still holds both textures: it is behind, and a newer frame follows.
            if (!_dropReported)
            {
                _dropReported = true;
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", "Shared texture frame dropped: the compositor has not released a texture yet.");
            }

            return true;
        }

        var shared = _textures[write.BufferIndex]!;
        try
        {
            var acquired = AcquireSync(shared.Mutex, write.AcquireKey, AcquireTimeoutMilliseconds);
            if (acquired != 0)
            {
                ring.AbortWrite(write.BufferIndex);
                return HandleAcquireFailure(acquired);
            }

            context.CopyResource(shared.Texture, _targetTexture!);
            // Releasing submits the copy; the compositor's acquire of the consumer key waits for it.
            shared.Mutex.ReleaseSync(MediaSharedGpuImageRing.ConsumerKey);
        }
        catch (SharpGenException exception)
        {
            ring.AbortWrite(write.BufferIndex);
            ReportPresentException(exception);
            return false;
        }

        _deviceLostReported = false;
        _presentFailure.Reset();
        _reexport.Reset();
        _consumer!.OnFrameReady(ring.CompleteWrite(write.BufferIndex));
        return true;
    }

    public void Cleanup()
    {
        _set?.Retire();
        _set = null;
        _ring?.Dispose();
        _ring = null;
        DisposeSharedTextures();
        TargetViews.Release();
        _targetTexture?.Dispose();
        _targetTexture = null;
        _deviceResources = null;
        _consumer = null;
        _recreate = false;
        Width = 0;
        Height = 0;
        _deviceLostReported = false;
    }

    public void Dispose() => Cleanup();

    static Texture2DDescription Describe(int width, int height, ResourceOptionFlags options) => new(
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
        options);

    static SharedTexture CreateSharedTexture(
        ID3D11Device device,
        int width,
        int height,
        ResourceOptionFlags sharing,
        MediaSharedGpuImageHandleKind handleKind)
    {
        var texture = device.CreateTexture2D(Describe(width, height, sharing));
        IDXGIKeyedMutex? mutex = null;
        try
        {
            mutex = texture.QueryInterface<IDXGIKeyedMutex>();
            nint handle;
            if (handleKind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle)
            {
                using var resource = texture.QueryInterface<IDXGIResource1>();
                handle = resource.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
            }
            else
            {
                using var resource = texture.QueryInterface<IDXGIResource>();
                handle = resource.SharedHandle;
            }

            if (handle == 0)
                throw new InvalidOperationException("DXGI returned no shared handle.");

            return new SharedTexture(texture, mutex, handle, handleKind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle);
        }
        catch
        {
            mutex?.Dispose();
            texture.Dispose();
            throw;
        }
    }

    static byte[] GetAdapterLuid(ID3D11Device device)
    {
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var luid = adapter.Description.Luid;
            return LuidBytes(luid.LowPart, luid.HighPart);
        }
        catch (SharpGenException)
        {
            // Unknown: the consumer's identity check is skipped and its import decides.
            return [];
        }
    }

    /// <summary>
    /// <c>IDXGIKeyedMutex::AcquireSync</c> through the vtable: Vortice's wrapper hides the
    /// WAIT_TIMEOUT and WAIT_ABANDONED success codes, which mean the mutex was not acquired.
    /// </summary>
    internal static unsafe int AcquireSync(IDXGIKeyedMutex mutex, ulong key, uint milliseconds)
    {
        var native = mutex.NativePointer;
        var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)native)[8];
        return acquire(native, key, milliseconds);
    }

    bool HandleAcquireFailure(int result)
    {
        if (result < 0)
        {
            ReportPresentFailure(new Result(result));
            return false;
        }

        // Timed out or abandoned: the texture pair is out of step with the compositor. Export a
        // fresh set; the compositor imports it and the next frame proceeds.
        _ = _reexport.Raise(
            MediaDiagnosticLevel.Info,
            "D3D11",
            $"Shared texture AcquireSync returned 0x{result:X} ({(result == WaitTimeout ? "timeout" : result == WaitAbandoned ? "abandoned" : "unexpected")}); re-exporting the textures.");
        _recreate = true;
        return true;
    }

    /// <summary>
    /// Replaces the exported textures at the same size (after an abandoned or stuck mutex). The
    /// private target keeps the frame already composed into it (and stays the texture the
    /// presentation controller adopted), so only the shared textures and their set are new.
    /// </summary>
    bool Republish()
    {
        var consumer = _consumer!;
        var device = _deviceResources!.Device!;
        _recreate = false;
        _set?.Retire();
        _set = null;
        _ring?.Dispose();
        _ring = null;
        DisposeSharedTextures();
        try
        {
            var sharing = ResourceOptionFlags.SharedKeyedMutex
                | (_selection.HandleKind == MediaSharedGpuImageHandleKind.D3D11TextureNtHandle ? ResourceOptionFlags.SharedNTHandle : ResourceOptionFlags.None);
            for (var i = 0; i < BufferCount; i++)
                _textures[i] = CreateSharedTexture(device, Width, Height, sharing, _selection.HandleKind);
        }
        catch (Exception exception) when (exception is SharpGenException or InvalidOperationException)
        {
            Cleanup();
            consumer.OnUnavailable($"re-exporting the shared textures failed: {exception.Message}");
            return false;
        }

        Publish();
        return true;
    }

    void DisposeSharedTextures()
    {
        for (var i = 0; i < _textures.Length; i++)
        {
            _textures[i]?.Dispose();
            _textures[i] = null;
        }
    }

    void Publish()
    {
        var generation = Interlocked.Increment(ref _nextGeneration);
        var ring = new MediaSharedGpuImageRing(BufferCount);
        ring.Reset(generation);
        SharedTexture[] textures = [.. _textures.Select(static texture => texture!)];
        foreach (var texture in textures)
            texture.MarkPublished();
        _ring = ring;
        _set = new MediaSharedGpuImageSet(
            generation,
            _selection,
            Width,
            Height,
            MediaSharedGpuImageFormat.Bgra8Unorm,
            textures.Select(static texture => new MediaSharedGpuImageBuffer(texture.Handle)),
            (frame, consumed) => ring.Release(frame, consumed),
            // NT handles stay open until both sides are done with the set; legacy handles are not closable.
            _ =>
            {
                foreach (var texture in textures)
                    texture.CloseHandle();
            });
        var consumerId = RuntimeHelpers.GetHashCode(_consumer!);
        if (consumerId != _reportedConsumerId || _reportedSelection != _selection)
        {
            _reportedConsumerId = consumerId;
            _reportedSelection = _selection;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "D3D11", $"Shared textures exported: {_set}");
        }
        else
        {
            Debug.WriteLine($"[D3D11SharedTexture] Re-exported: {_set}");
        }

        _consumer!.OnImagesCreated(_set);
    }

    void ReportPresentFailure(Result result)
    {
        if (D3D11DeviceLostClassifier.IsDeviceLost(result))
            RaiseDeviceLostOnce(D3D11DeviceLostClassifier.Describe(result, swapChain: null));
        else
            _ = _presentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Shared texture present failed: {result}");
    }

    void ReportPresentException(SharpGenException exception)
    {
        Result removed = default;
        try
        {
            removed = _deviceResources?.Device?.DeviceRemovedReason ?? default;
        }
        catch (SharpGenException)
        {
            // The device may already be gone.
        }

        if (removed.Failure && D3D11DeviceLostClassifier.IsDeviceLost(removed))
            RaiseDeviceLostOnce($"Shared texture present detected GPU device lost: {D3D11DeviceLostClassifier.Describe(removed, swapChain: null)}");
        else
            _ = _presentFailure.Raise(MediaDiagnosticLevel.Warning, "D3D11", $"Shared texture present failed: {exception.Message}", exception);
    }

    void RaiseDeviceLostOnce(string message)
    {
        if (_deviceLostReported)
            return;

        _deviceLostReported = true;
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Error, "D3D11.DeviceLost", message);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    sealed class SharedTexture(ID3D11Texture2D texture, IDXGIKeyedMutex mutex, nint handle, bool isNtHandle) : IDisposable
    {
        nint _ntHandle = isNtHandle ? handle : 0;
        bool _published;

        public ID3D11Texture2D Texture { get; } = texture;
        public IDXGIKeyedMutex Mutex { get; } = mutex;
        public nint Handle { get; } = handle;

        /// <summary>Closes the NT handle once (the texture itself lives on in importers' references).</summary>
        public void CloseHandle()
        {
            var ntHandle = Interlocked.Exchange(ref _ntHandle, 0);
            if (ntHandle != 0)
                _ = D3D11SharedTextureBackend.CloseHandle(ntHandle);
        }

        /// <summary>The handle went to a set, whose release callback closes it once both sides are done.</summary>
        public void MarkPublished() => _published = true;

        public void Dispose()
        {
            // A texture never published (the next one of its pair failed to create) has no set
            // to close its NT handle, which would keep the GPU allocation alive (ENG-039 G5).
            if (!_published)
                CloseHandle();

            Mutex.Dispose();
            Texture.Dispose();
        }
    }
}
