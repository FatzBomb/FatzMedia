using FatzMedia.Video.Core.Video.Hdr;

namespace FatzMedia.Video.Core.Graphics;

/// <summary>The transfer function of HDR video the engine tone maps (Stages 038A and 042A).</summary>
public enum HdrTransferFunction
{
    /// <summary>SMPTE ST 2084 (PQ), HDR10's: the signal is absolute display luminance.</summary>
    Pq = 0,
    /// <summary>
    /// ARIB STD-B67 / BT.2100 hybrid log-gamma (broadcast HDR): the signal is relative scene light,
    /// shown through the BT.2100 OOTF for a display of <see cref="HdrToneMapRequest.SourcePeakNits"/>.
    /// </summary>
    Hlg
}

/// <summary>
/// Asks a renderer to tone map a native HDR frame (BT.2020; PQ or, from Stage 042A, HLG) to SDR
/// with the BT.2390 curve the engine's CPU tone mapper uses (Stage 038A): the source's range up
/// to <see cref="SourcePeakNits"/> is compressed into [0, <see cref="TargetPeakNits"/>], which is
/// SDR white; output primaries are BT.709 and the SDR gamma is 2.4.
/// </summary>
/// <param name="SourcePeakNits">
/// The source's peak luminance: from its HDR10 metadata for PQ; for HLG the nominal display peak
/// its OOTF is evaluated for (1000 nits).
/// </param>
/// <param name="TargetPeakNits">The luminance shown as SDR white.</param>
public readonly record struct HdrToneMapRequest(double SourcePeakNits, double TargetPeakNits)
{
    /// <summary>The source's transfer function; PQ unless set.</summary>
    public HdrTransferFunction Transfer { get; init; }

    /// <summary>
    /// For HLG, the BT.2100 system gamma for <see cref="SourcePeakNits"/> (1.2 at 1000 nits);
    /// 1 for PQ, which has no OOTF to apply.
    /// </summary>
    public double HlgSystemGamma => Transfer == HdrTransferFunction.Hlg ? HlgCurve.SystemGamma(SourcePeakNits) : 1;

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
/// Lets a renderer hand HDR tone mapping back to the producer (Stage 038A): once requested,
/// the producer sends HDR frames already tone mapped on the CPU. Thread-safe.
/// </summary>
public sealed class HdrToneMapFallback
{
    int _requested;

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
