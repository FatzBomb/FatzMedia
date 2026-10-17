using FatzBomb.MediaControl.Core.Abstractions;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>Stable identity and content information for a loaded media source.</summary>
public sealed class MediaSourceInfo
{
    internal MediaSourceInfo(MediaInputSource source, MediaContent content, MediaSourceCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        Kind = source.Kind;
        DisplayName = source.DisplayName;
        FilePath = source.FilePath;
        Uri = source.Uri;
        Content = content;
        Capabilities = capabilities ?? MediaSourceCapabilityMatrix.For(source);
    }

    public MediaInputSourceKind Kind { get; }
    public string DisplayName { get; }
    public string? FilePath { get; }

    /// <summary>Gets the network address for network sources.</summary>
    public Uri? Uri { get; }

    public MediaContent Content { get; }

    /// <summary>Gets the capabilities resolved for this source when it was loaded.</summary>
    public MediaSourceCapabilities Capabilities { get; }
}
