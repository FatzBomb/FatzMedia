#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

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

    internal VideoDecodeCapabilities(
        Dictionary<string, CodecCapability> codecs,
        Dictionary<string, ContainerCapability> containers)
    {
        _codecs = codecs;
        _containers = containers;
    }

    /// <summary>
    /// Creates an immutable codec/container capability snapshot supplied by a platform scanner.
    /// </summary>
    public static VideoDecodeCapabilities Create(
        IEnumerable<CodecCapability> codecs,
        IEnumerable<ContainerCapability> containers)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        ArgumentNullException.ThrowIfNull(containers);

        return new VideoDecodeCapabilities(
            codecs.ToDictionary(capability => capability.CodecName, StringComparer.OrdinalIgnoreCase),
            containers.ToDictionary(capability => capability.Extension, StringComparer.OrdinalIgnoreCase));
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

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : $".{trimmed.ToLowerInvariant()}";
    }
}
