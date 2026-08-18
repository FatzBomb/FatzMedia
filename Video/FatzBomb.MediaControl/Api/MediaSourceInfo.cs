using FatzBomb.MediaControl.Core.Abstractions;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>Stable identity and content information for a loaded media source.</summary>
public sealed class MediaSourceInfo
{
    internal MediaSourceInfo(MediaInputSource source, MediaContent content)
    {
        ArgumentNullException.ThrowIfNull(source);
        Kind = source.Kind;
        DisplayName = source.DisplayName;
        FilePath = source.FilePath;
        Content = content;
    }

    public MediaInputSourceKind Kind { get; }
    public string DisplayName { get; }
    public string? FilePath { get; }
    public MediaContent Content { get; }
}
