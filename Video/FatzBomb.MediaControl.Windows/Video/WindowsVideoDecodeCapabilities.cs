namespace FatzBomb.MediaControl.Windows.Video;

/// <summary>
/// Caches FFmpeg codec/container capabilities combined with Windows D3D11/DXVA
/// hardware-profile availability.
/// </summary>
internal static class WindowsVideoDecodeCapabilities
{
    private static readonly Lock CacheLock = new();
    private static VideoDecodeCapabilities? _cached;

    public static VideoDecodeCapabilities GetOrScan()
    {
        if (_cached != null)
            return _cached;

        lock (CacheLock)
            return _cached ??= HardwareDecoderScanner.Scan();
    }
}
