namespace FatzBomb.MediaControl.Controllers;

/// <summary>
/// Optional capability of an <see cref="IMediaPresentationCoordinatorFactory"/>: reports, before
/// any session exists, whether the renderer can run on this machine (Stage 036A).
/// <see cref="FallbackPresentationCoordinatorFactory"/> uses it to pick the first usable renderer.
/// </summary>
public interface IMediaPresentationAvailabilityProbe
{
    /// <summary>
    /// Checks whether the renderer can create what it needs (for example a GPU device), releases
    /// anything it created, and says what it found. Does not throw.
    /// </summary>
    MediaPresentationAvailability ProbeAvailability();
}

/// <summary>The result of <see cref="IMediaPresentationAvailabilityProbe.ProbeAvailability"/>.</summary>
/// <param name="IsAvailable">Whether the renderer can run.</param>
/// <param name="Detail">What was found (for example the device and adapter), or why it cannot run.</param>
public sealed record MediaPresentationAvailability(bool IsAvailable, string Detail)
{
    /// <summary>
    /// Gets whether the host should decode video in software: the renderer cannot present
    /// hardware-decoded frames, or runs on a software rasterizer (WARP), where a hardware decoder's
    /// frames would pull presentation onto another device.
    /// </summary>
    /// <remarks>
    /// The software renderer and a Vulkan device without video decoding also enforce this
    /// themselves: their players' decoders open in software whatever the configuration asks.
    /// WARP does not, so a host using it must honour this flag.
    /// </remarks>
    public bool RequiresSoftwareDecoding { get; init; }

    /// <summary>
    /// Gets whether the renderer can only draw into a managed frame-sink surface (for example a
    /// WPF-composited host), not into a native window.
    /// </summary>
    public bool RequiresFrameSinkSurface { get; init; }

    public static MediaPresentationAvailability Available(string detail) => new(true, detail);

    public static MediaPresentationAvailability Unavailable(string reason) => new(false, reason);
}
