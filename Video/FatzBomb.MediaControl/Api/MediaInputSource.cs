#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Framework-neutral media input supplied to the Core engine. Adapters may convert framework-specific
/// objects into one of these neutral forms before calling Core APIs.
/// </summary>
public sealed class MediaInputSource
{
    private MediaInputSource(MediaInputSourceKind kind, string displayName, string? filePath, byte[]? bgraPixels, int width, int height, int stride)
    {
        Kind = kind;
        DisplayName = displayName;
        FilePath = filePath;
        BgraPixels = bgraPixels;
        Width = width;
        Height = height;
        Stride = stride;
    }

    public MediaInputSourceKind Kind { get; }
    public string DisplayName { get; }
    public string? FilePath { get; }
    public byte[]? BgraPixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public bool IsFile => Kind == MediaInputSourceKind.FilePath && !string.IsNullOrWhiteSpace(FilePath);
    public bool IsBgraImage => Kind == MediaInputSourceKind.Bgra32Image && BgraPixels is { Length: > 0 } && Width > 0 && Height > 0 && Stride >= Width * 4;

    public static MediaInputSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MediaInputSource(MediaInputSourceKind.FilePath, Path.GetFileName(path), path, null, 0, 0, 0);
    }

    public static MediaInputSource FromBgra32Image(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, string? displayName = null)
    {
        var requiredLength = ValidateBgra32Image(bgraPixels.Length, width, height, stride, nameof(bgraPixels));
        var copy = bgraPixels[..requiredLength].ToArray();
        return FromOwnedBgra32Image(copy, width, height, stride, displayName);
    }

    /// <summary>
    /// Creates a BGRA image source by taking ownership of the supplied buffer. Callers must not
    /// mutate the buffer after this call.
    /// </summary>
    public static MediaInputSource FromOwnedBgra32Image(byte[] bgraPixels, int width, int height, int stride, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(bgraPixels);
        _ = ValidateBgra32Image(bgraPixels.Length, width, height, stride, nameof(bgraPixels));
        return new MediaInputSource(
            MediaInputSourceKind.Bgra32Image,
            string.IsNullOrWhiteSpace(displayName) ? "BGRA image source" : displayName,
            filePath: null,
            bgraPixels,
            width,
            height,
            stride);
    }

    private static int ValidateBgra32Image(int bufferLength, int width, int height, int stride, string bufferParameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width * 4);

        var requiredLength = checked(stride * height);
        return bufferLength < requiredLength
            ? throw new ArgumentException("The BGRA pixel buffer is smaller than stride * height.", bufferParameterName)
            : requiredLength;
    }
}

public enum MediaInputSourceKind
{
    FilePath = 0,
    Bgra32Image = 1
}
