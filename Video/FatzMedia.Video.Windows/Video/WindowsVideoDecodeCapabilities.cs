namespace FatzMedia.Video.Windows.Video;

/// <summary>
/// Caches FFmpeg codec/container capabilities combined with Windows D3D11/DXVA
/// hardware-profile availability.
/// </summary>
static class WindowsVideoDecodeCapabilities
{
    static readonly Lock CacheLock = new();
    static VideoDecodeCapabilities? _cached;

    public static VideoDecodeCapabilities GetOrScan()
    {
        if (_cached != null)
            return _cached;

        lock (CacheLock)
            return _cached ??= HardwareDecoderScanner.Scan();
    }
}
