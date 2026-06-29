using FatzBombMedia.MediaControl;
using FatzBombMedia.MediaControl.Core.Abstractions;
using System.IO;

namespace FatzBombMedia.MediaControl.Controllers;

public enum MediaBackgroundKind
{
    None = 0,
    Image = 1,
    Video = 2
}

public sealed class MediaBackgroundCandidate
{
    private MediaBackgroundCandidate(MediaBackgroundKind kind, string displayName, string? path, byte[]? data, MediaInputSource? source = null)
    {
        Kind = kind;
        DisplayName = displayName;
        Path = path;
        Data = data;
        Source = source;
    }

    public MediaBackgroundKind Kind { get; }
    public string DisplayName { get; }
    public string? Path { get; }
    public byte[]? Data { get; }
    public MediaInputSource? Source { get; }

    public static MediaBackgroundCandidate FromPath(MediaBackgroundKind kind, string path) => new(kind, System.IO.Path.GetFileName(path), path, data: null, source: MediaInputSource.FromFile(path));

    public static MediaBackgroundCandidate FromSource(MediaBackgroundKind kind, MediaInputSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new MediaBackgroundCandidate(kind, source.DisplayName, source.FilePath, data: null, source);
    }

    public static MediaBackgroundCandidate FromArchiveEntry(MediaBackgroundKind kind, IArchiveEntryInfo entry)
    {
        using MemoryStream stream = entry.Size is > 0 and <= int.MaxValue
            ? new MemoryStream((int)entry.Size.Value)
            : new MemoryStream();

        entry.WriteTo(stream);
        return new MediaBackgroundCandidate(kind, entry.Key, path: null, stream.ToArray());
    }
}
