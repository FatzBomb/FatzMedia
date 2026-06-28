using FatzBombMedia.MediaControl.Core.Abstractions;
using System.IO;

namespace FatzBombMedia.MediaControl.Controllers;

internal enum MediaBackgroundKind
{
    None = 0,
    Image = 1,
    Video = 2
}

internal sealed class MediaBackgroundCandidate
{
    private MediaBackgroundCandidate(MediaBackgroundKind kind, string displayName, string? path, byte[]? data)
    {
        Kind = kind;
        DisplayName = displayName;
        Path = path;
        Data = data;
    }

    public MediaBackgroundKind Kind { get; }
    public string DisplayName { get; }
    public string? Path { get; }
    public byte[]? Data { get; }

    public static MediaBackgroundCandidate FromPath(MediaBackgroundKind kind, string path) => new(kind, System.IO.Path.GetFileName(path), path, data: null);

    public static MediaBackgroundCandidate FromArchiveEntry(MediaBackgroundKind kind, IArchiveEntryInfo entry)
    {
        using MemoryStream stream = entry.Size is > 0 and <= int.MaxValue
            ? new MemoryStream((int)entry.Size.Value)
            : new MemoryStream();

        entry.WriteTo(stream);
        return new MediaBackgroundCandidate(kind, entry.Key, path: null, stream.ToArray());
    }
}
