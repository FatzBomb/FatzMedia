using FatzBombMedia.MediaControl.Core.Video;

#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

public sealed record CodecCapability(
    string CodecName,
    string FriendlyName,
    bool SoftwareDecodeAvailable,
    string? SoftwareDecoderName,
    bool HardwareDecodeAvailable,
    string? HardwareMethod
);

public sealed record ContainerCapability(
    string Extension,
    string FormatName,
    string FriendlyName,
    IReadOnlyList<CodecCapability> PossibleCodecs,
    bool AnyHardwareDecodeAvailable
);

public sealed class VideoDecodeCapabilities
{
    private readonly Dictionary<string, CodecCapability> _codecs;
    private readonly Dictionary<string, ContainerCapability> _containers;

    private static VideoDecodeCapabilities? _cached;
    private static readonly Lock _cacheLock = new();

    internal VideoDecodeCapabilities(
        Dictionary<string, CodecCapability> codecs,
        Dictionary<string, ContainerCapability> containers)
    {
        _codecs = codecs;
        _containers = containers;
    }

    public IReadOnlyDictionary<string, CodecCapability> Codecs => _codecs;
    public IReadOnlyDictionary<string, ContainerCapability> Containers => _containers;

    public IReadOnlyList<string> HardwareAcceleratedCodecNames =>
        [.. _codecs.Values.Where(c => c.HardwareDecodeAvailable).Select(c => c.CodecName)];

    public IReadOnlyList<string> SoftwareOnlyCodecNames =>
        [.. _codecs.Values.Where(c => !c.HardwareDecodeAvailable && c.SoftwareDecodeAvailable).Select(c => c.CodecName)];

    public CodecCapability? ForCodec(string codecName) =>
        _codecs.TryGetValue(codecName.ToLowerInvariant(), out CodecCapability? capability) ? capability : null;

    public bool IsHardwareDecodeAvailable(string codecName) =>
        ForCodec(codecName)?.HardwareDecodeAvailable ?? false;

    public bool HasAnyHardwareMethod(string codecName) =>
        ForCodec(codecName)?.HardwareMethod != null;

    public ContainerCapability? ForExtension(string extension)
    {
        var normalized = NormalizeExtension(extension);
        return _containers.TryGetValue(normalized, out ContainerCapability? capability) ? capability : null;
    }

    internal static VideoDecodeCapabilities GetOrScan()
    {
        if (_cached != null)
            return _cached;

        lock (_cacheLock)
        {
            if (_cached != null)
                return _cached;

            _cached = HardwareDecoderScanner.Scan();
            return _cached;
        }
    }

    internal static void InvalidateCache()
    {
        lock (_cacheLock)
            _cached = null;
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}
