#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Framework-neutral media input supplied to the Core engine. Adapters may convert framework-specific
/// objects into one of these neutral forms before calling Core APIs.
/// </summary>
public sealed class MediaInputSource
{
    MediaInputSource(
        MediaInputSourceKind kind,
        string displayName,
        string? filePath,
        byte[]? bgraPixels,
        int width,
        int height,
        int stride,
        Uri? uri = null,
        MediaNetworkContent networkContent = MediaNetworkContent.Auto,
        WebMediaReference? webMedia = null)
    {
        WebMedia = webMedia;
        Uri = uri;
        NetworkContent = networkContent;
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

    /// <summary>Gets the network address for <see cref="MediaInputSourceKind.NetworkUri"/> sources.</summary>
    public Uri? Uri { get; }

    /// <summary>Gets the online item for <see cref="MediaInputSourceKind.WebMedia"/> sources (Stage 060A).</summary>
    public WebMediaReference? WebMedia { get; }

    /// <summary>Gets the host's content hint for a network source.</summary>
    public MediaNetworkContent NetworkContent { get; }

    public bool IsFile => Kind == MediaInputSourceKind.FilePath && !string.IsNullOrWhiteSpace(FilePath);
    public bool IsNetwork => Kind == MediaInputSourceKind.NetworkUri && Uri is not null;
    public bool IsWebMedia => Kind == MediaInputSourceKind.WebMedia && WebMedia is not null;
    public bool IsBgraImage => Kind == MediaInputSourceKind.Bgra32Image && BgraPixels is { Length: > 0 } && Width > 0 && Height > 0 && Stride >= Width * 4;

    public static MediaInputSource FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MediaInputSource(MediaInputSourceKind.FilePath, Path.GetFileName(path), path, null, 0, 0, 0);
    }

    /// <summary>
    /// Creates a direct HTTP/HTTPS network source. Behavior is decided by the source's probed
    /// capabilities (see <see cref="MediaSourceCapabilityMatrix"/>), never by URL text.
    /// </summary>
    /// <param name="uri">An absolute <c>http</c> or <c>https</c> address.</param>
    /// <param name="content">
    /// Optional hint for what the stream carries. <see cref="MediaNetworkContent.Auto"/> classifies
    /// by the URI path's file extension (as local files are) and falls back to audio.
    /// </param>
    public static MediaInputSource FromUri(Uri uri, MediaNetworkContent content = MediaNetworkContent.Auto)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only absolute http and https URIs are supported.", nameof(uri));
        if (!Enum.IsDefined(content))
            throw new ArgumentOutOfRangeException(nameof(content), content, "Unknown network content hint.");

        var segment = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1]).Trim('/') : string.Empty;
        var displayName = string.IsNullOrWhiteSpace(segment) ? uri.Host : segment;
        return new MediaInputSource(MediaInputSourceKind.NetworkUri, displayName, null, null, 0, 0, 0, uri, content);
    }

    /// <summary>
    /// Creates a source for an online item a UI adapter plays in its web view (Stage 060A). Core only
    /// carries the reference; a player without a web view host reports an error when it loads it.
    /// </summary>
    public static MediaInputSource FromWebMedia(WebMediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return new MediaInputSource(MediaInputSourceKind.WebMedia, reference.DisplayName, null, null, 0, 0, 0, reference.OriginalUri, webMedia: reference);
    }

    /// <summary>
    /// Makes a source from the text a host passes to <c>Open</c>: a YouTube address becomes a web
    /// source, any other http or https address a network source (<see cref="FromUri"/>), anything else a
    /// file path. YouTube addresses that name no single video (playlist, channel, search) throw
    /// <see cref="NotSupportedException"/> with the reason, never reaching network playback.
    /// </summary>
    public static MediaInputSource Parse(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var web = WebMediaProviders.Parse(address);
        switch (web.Outcome)
        {
            case WebMediaParseOutcome.Recognized:
                return FromWebMedia(web.Reference!);
            case WebMediaParseOutcome.Unsupported:
                throw new NotSupportedException(web.Message);
        }

        return Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? FromUri(uri)
            : FromFile(address);
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

    /// <summary>Width and height, in pixels, of the image <see cref="FromColor"/> creates.</summary>
    internal const int SolidColorSize = 8;

    /// <summary>
    /// Creates a solid-colour source (Stage 043A): a small BGRA image that the image path scales to
    /// the output, for a Background-mode player or any image slot.
    /// </summary>
    /// <param name="argb">The colour as 0xAARRGGBB, with straight (not premultiplied) alpha.</param>
    /// <param name="displayName">Optional name; defaults to the colour in hex.</param>
    public static MediaInputSource FromColor(uint argb, string? displayName = null)
    {
        const int Stride = SolidColorSize * 4;
        var pixels = new byte[Stride * SolidColorSize];
        var b = (byte)argb;
        var g = (byte)(argb >> 8);
        var r = (byte)(argb >> 16);
        var a = (byte)(argb >> 24);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        return FromOwnedBgra32Image(
            pixels,
            SolidColorSize,
            SolidColorSize,
            Stride,
            string.IsNullOrWhiteSpace(displayName) ? $"Solid colour #{argb:X8}" : displayName);
    }

    static int ValidateBgra32Image(int bufferLength, int width, int height, int stride, string bufferParameterName)
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
    Bgra32Image = 1,
    NetworkUri = 2,

    /// <summary>An online item played by a UI adapter's web view (Stage 060A).</summary>
    WebMedia = 3
}

/// <summary>
/// Host hint for what a network source carries.
/// </summary>
public enum MediaNetworkContent
{
    /// <summary>Classify by the URI path extension; unknown extensions are treated as audio.</summary>
    Auto = 0,

    /// <summary>The stream is audio (files, internet radio, live audio).</summary>
    Audio = 1,

    /// <summary>The stream is video with optional audio.</summary>
    Video = 2
}
