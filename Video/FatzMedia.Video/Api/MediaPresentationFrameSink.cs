#pragma warning disable IDE0130
using System.Diagnostics.CodeAnalysis;

namespace FatzMedia.Video;

/// <summary>
/// Receives a complete composed BGRA frame from a renderer-backed presentation target.
/// The pixel span is valid only for the duration of the call and must be copied before returning.
/// </summary>
public interface IMediaPresentationFrameSink
{
    void PresentFrame(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride);
}

/// <summary>
/// Owns a thread-safe opaque token used to expose a managed presentation-frame sink through the
/// neutral native-surface descriptor contract. The underlying token registry strongly roots each
/// sink while its handle is allocated, removes the entry atomically on dispose, and never resolves
/// a stale token to a later sink. Dispose only after the corresponding surface is detached.
/// </summary>
public sealed class MediaPresentationFrameSinkHandle : IDisposable
{
    /// <summary>
    /// Thread-safe registry that strongly roots sinks while they are exposed to unmanaged code.
    /// The dictionary itself serves as the root set — no separate GCHandle is needed.
    /// </summary>
    private static readonly OpaqueTokenRegistry<IMediaPresentationFrameSink> Registry =
        new("The presentation frame-sink token space has been exhausted.");

    private long _token;

    /// <summary>
    /// Allocates a monotonic nonzero token and registers the sink in the global thread-safe registry.
    /// </summary>
    public MediaPresentationFrameSinkHandle(IMediaPresentationFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _token = Registry.Register(sink);
    }

    /// <summary>
    /// <see langword="true"/> after construction and before the first <see cref="Dispose"/> call;
    /// <see langword="false"/> once disposed.
    /// </summary>
    public bool IsAllocated => Volatile.Read(ref _token) != 0;

    /// <summary>
    /// Returns the opaque nonzero token that identifies this sink in the global registry.
    /// Returns 0 once disposed or if the handle was never allocated.
    /// </summary>
    public nint Value => (nint)Volatile.Read(ref _token);

    /// <summary>
    /// Creates a native surface descriptor carrying the opaque frame-sink token.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when this instance has been disposed.</exception>
    public MediaNativeSurfaceDescriptor CreateSurfaceDescriptor()
    {
        long token = Volatile.Read(ref _token);
        ObjectDisposedException.ThrowIf(token == 0, this);
        return new(
            MediaNativeSurfaceTypeIds.ManagedBgraFrameSink,
            [new MediaNativeHandle(MediaNativeHandleIds.FrameSink, (nint)token)]);
    }

    /// <summary>
    /// Resolves an opaque token into the registered <see cref="IMediaPresentationFrameSink"/>,
    /// if and only if the owning <see cref="MediaPresentationFrameSinkHandle"/> has not been
    /// disposed. Tokens are never reused — a stale token always fails resolution.
    /// </summary>
    /// <param name="value">The opaque token previously obtained from <see cref="Value"/>.</param>
    /// <param name="sink">The resolved sink if successful; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the token was valid and the sink was resolved.</returns>
    public static bool TryResolve(nint value, out IMediaPresentationFrameSink? sink)
    {
        return Registry.TryResolve(value, out sink);
    }

    /// <summary>
    /// Resolves the sink of a valid <see cref="MediaNativeSurfaceTypeIds.ManagedBgraFrameSink"/>
    /// surface whose owning handle has not been disposed.
    /// </summary>
    /// <param name="surface">The presentation surface to resolve.</param>
    /// <param name="sink">The resolved sink if successful; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the surface carried a live frame-sink token.</returns>
    public static bool TryResolve(MediaPresentationSurfaceDescriptor? surface, [NotNullWhen(true)] out IMediaPresentationFrameSink? sink)
    {
        sink = null;
        return surface is { IsValid: true }
            && surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink
            && surface.NativeSurface.TryGetHandle(MediaNativeHandleIds.FrameSink, out nint handle)
            && TryResolve(handle, out sink)
            && sink != null;
    }

    /// <summary>
    /// Atomically removes the sink from the global registry and releases strong rooting.
    /// Repeated calls are safe and are no-ops after the first dispose.
    /// </summary>
    public void Dispose()
    {
        long token = Interlocked.Exchange(ref _token, 0);
        if (token != 0)
            Registry.Remove(token);
    }
}
