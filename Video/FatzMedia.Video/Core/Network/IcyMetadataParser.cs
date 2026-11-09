namespace FatzMedia.Video.Core.Network;

/// <summary>Parsed stream metadata.</summary>
internal readonly record struct StreamMetadata(string? Title, string? Artist, string? RawTitle);

/// <summary>
/// Parses ICY (<c>StreamTitle='Artist - Title';StreamUrl='...';</c>) and Ogg-comment
/// (<c>TITLE=</c>/<c>ARTIST=</c>) metadata.
/// </summary>
internal static class IcyMetadataParser
{
    private const string StreamTitleKey = "StreamTitle='";

    public static StreamMetadata ParseIcy(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return default;

        var start = metadata.IndexOf(StreamTitleKey, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return default;

        start += StreamTitleKey.Length;
        // Titles may contain apostrophes; the field ends at the first "';" or the final quote.
        var end = metadata.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
            end = metadata.LastIndexOf('\'');
        if (end < start)
            return default;

        var raw = metadata[start..end].Trim();
        return SplitArtistTitle(raw);
    }

    public static StreamMetadata ParseOggComments(IEnumerable<string>? comments)
    {
        if (comments is null)
            return default;

        string? title = null;
        string? artist = null;
        foreach (var comment in comments)
        {
            var separator = comment.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = comment[..separator];
            var value = comment[(separator + 1)..].Trim();
            if (value.Length == 0)
                continue;

            if (title is null && key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                title = value;
            else if (artist is null && key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
                artist = value;
        }

        if (title is null && artist is null)
            return default;

        return new StreamMetadata(title, artist, artist is null ? title : title is null ? artist : $"{artist} - {title}");
    }

    private static StreamMetadata SplitArtistTitle(string raw)
    {
        if (raw.Length == 0)
            return default;

        var separator = raw.IndexOf(" - ", StringComparison.Ordinal);
        return separator <= 0
            ? new StreamMetadata(raw, null, raw)
            : new StreamMetadata(raw[(separator + 3)..].Trim(), raw[..separator].Trim(), raw);
    }
}
