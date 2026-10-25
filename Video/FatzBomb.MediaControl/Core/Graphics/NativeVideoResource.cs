namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>Stable identifier for a native graphics or decode backend.</summary>
public readonly record struct MediaGraphicsBackendId(string Value)
{
    public static MediaGraphicsBackendId Direct3D11 { get; } = new("d3d11");

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Describes who owns and releases a native video resource.</summary>
public enum NativeVideoResourceLifetime
{
    /// <summary>
    /// Core owns the backing decoded frame. Consumers may use the descriptor synchronously
    /// while Core retains that frame, but must not release or retain the native handles.
    /// </summary>
    ProducerOwned
}

/// <summary>Opaque native hardware-device identity supplied by a platform decoder backend.</summary>
public sealed record NativeVideoDeviceResource(
    MediaGraphicsBackendId BackendId,
    nint DeviceHandle,
    nint DeviceContextHandle = 0);

/// <summary>
/// Renderer-neutral descriptor for a native decoded video frame. Core retains ownership of
/// the backing decoder frame according to <see cref="Lifetime"/>.
/// </summary>
public sealed record NativeVideoFrameResource(
    NativeVideoDeviceResource Device,
    nint ResourceHandle,
    int SubresourceIndex,
    NativeVideoResourceLifetime Lifetime = NativeVideoResourceLifetime.ProducerOwned,
    nint SynchronizationHandle = 0,
    ulong SynchronizationValue = 0)
{
    public MediaGraphicsBackendId BackendId => Device.BackendId;
    public bool IsValid => ResourceHandle != 0;

    /// <summary>How the frame's YCbCr values map to RGB, resolved by the producer (never unspecified when Core decodes it).</summary>
    public VideoColorSpace ColorSpace { get; init; }

    /// <summary>
    /// Set for an HDR10 or HLG frame the renderer should tone map to SDR (Stages 038A, 042A); null for SDR
    /// frames and when tone mapping is off.
    /// </summary>
    public HdrToneMapRequest? HdrToneMap { get; init; }
}
