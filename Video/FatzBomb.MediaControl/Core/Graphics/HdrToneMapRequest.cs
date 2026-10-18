using FatzBomb.MediaControl.Core.Video.Hdr;

namespace FatzBomb.MediaControl.Core.Graphics;

/// <summary>
/// Asks a renderer to tone map a native HDR10 (PQ, BT.2020) frame to SDR with the BT.2390 curve
/// the engine's CPU tone mapper uses (Stage 038A): the source's range up to
/// <see cref="SourcePeakNits"/> is compressed into [0, <see cref="TargetPeakNits"/>], which is
/// SDR white; output primaries are BT.709 and the SDR gamma is 2.4.
/// </summary>
/// <param name="SourcePeakNits">The source's peak luminance (from its HDR10 metadata).</param>
/// <param name="TargetPeakNits">The luminance shown as SDR white.</param>
public readonly record struct HdrToneMapRequest(double SourcePeakNits, double TargetPeakNits)
{
    /// <summary>
    /// Set by the producer: a renderer that cannot tone map the frame calls
    /// <see cref="HdrToneMapFallback.RequestCpuToneMapping"/>, and later frames arrive tone mapped
    /// on the CPU instead.
    /// </summary>
    public HdrToneMapFallback? Fallback { get; init; }

    /// <summary>The PQ signal of <see cref="SourcePeakNits"/>, in [0, 1].</summary>
    public double SourcePeakSignal => PqCurve.FromNits(SourcePeakNits);

    /// <summary>The PQ signal of <see cref="TargetPeakNits"/>, in [0, 1].</summary>
    public double TargetPeakSignal => PqCurve.FromNits(TargetPeakNits);

    /// <summary>The SDR gamma the tone-mapped output is encoded with (BT.1886).</summary>
    public const double SdrGamma = 2.4;
}

/// <summary>
/// Lets a renderer hand HDR10 tone mapping back to the producer (Stage 038A): once requested,
/// the producer sends HDR10 frames already tone mapped on the CPU. Thread-safe.
/// </summary>
public sealed class HdrToneMapFallback
{
    private int _requested;

    /// <summary>Gets whether a renderer asked for CPU tone mapping.</summary>
    public bool IsRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>Why the renderer asked, or null.</summary>
    public string? Reason { get; private set; }

    /// <summary>Asks for CPU tone mapping from now on; the first reason is kept.</summary>
    public void RequestCpuToneMapping(string reason)
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0)
            Reason = reason;
    }
}
