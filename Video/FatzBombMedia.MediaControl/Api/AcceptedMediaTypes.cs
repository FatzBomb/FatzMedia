using FatzBombMedia.MediaControl.Core.Files;

#pragma warning disable IDE0130
namespace FatzBombMedia.MediaControl;

/// <summary>
/// Readable snapshot of media extensions accepted by the current control runtime.
/// </summary>
public sealed record AcceptedMediaTypes(
    IReadOnlyList<string> Audio,
    IReadOnlyList<string> Video,
    IReadOnlyList<string> Archive,
    IReadOnlyList<string> Image,
    IReadOnlyList<string> Cdg)
{
    public IReadOnlyList<string> All { get; } = [.. Audio, .. Video, .. Archive, .. Image, .. Cdg];

    public IReadOnlyList<MediaFileFilter> Filters { get; } =
    [
        new("Supported media", [.. Audio, .. Video, .. Archive, .. Image, .. Cdg]),
        new("Audio", Audio),
        new("Video", Video),
        new("Archive", Archive),
        new("Image", Image),
        new("CD+G", Cdg),
        new("All files", ["*"])
    ];

    public string OpenFileDialogFilter => string.Join("|", Filters
        .Where(filter => filter.Extensions.Count > 0)
        .Select(filter => filter.ToOpenFileDialogFilter()));

    internal static AcceptedMediaTypes FromCurrentRuntime() => new(
            Sorted(FileTypes.Audio),
            Sorted(FileTypes.Video),
            Sorted(FileTypes.Archive),
            Sorted(FileTypes.Image),
            Sorted(FileTypes.Cdg));

    private static IReadOnlyList<string> Sorted(IEnumerable<string> extensions) => [.. extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed == "*" ? trimmed : trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }
}
