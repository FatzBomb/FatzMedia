#pragma warning disable IDE0130
using System.Text.RegularExpressions;

namespace FatzMedia.Video;

/// <summary>
/// The online services Core recognises (Stage 060A: YouTube) and the address check that runs before an
/// address goes to network playback. Only the host name decides: anything that is not a YouTube host
/// is <see cref="WebMediaParseOutcome.NotWebMedia"/> and plays as before.
/// </summary>
public static partial class WebMediaProviders
{
    /// <summary>The YouTube provider id.</summary>
    public const string YouTubeId = "youtube";

    /// <summary>Gets the recognised services.</summary>
    public static IReadOnlyList<IWebMediaProvider> All { get; } = [new YouTubeProvider()];

    /// <summary>Checks an address against every recognised service.</summary>
    public static WebMediaParseResult Parse(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
            return WebMediaParseResult.NotWeb;
        foreach (var provider in All)
        {
            var result = provider.Parse(address);
            if (result.Outcome != WebMediaParseOutcome.NotWebMedia)
                return result;
        }

        return WebMediaParseResult.NotWeb;
    }

    /// <summary>Checks text the way a host passes it to <c>Open</c>: only an absolute http or https
    /// address can be web media; a file path or anything else is <see cref="WebMediaParseOutcome.NotWebMedia"/>.</summary>
    public static WebMediaParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
            return WebMediaParseResult.NotWeb;
        return Parse(uri);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();

    const string PlaylistMessage = "YouTube playlists are not supported; open a single video address.";
    const string ChannelMessage = "YouTube channel pages are not supported; open a single video address.";

    sealed class YouTubeProvider : IWebMediaProvider
    {
        public string Id => YouTubeId;

        public WebMediaParseResult Parse(Uri address)
        {
            var host = address.Host.ToLowerInvariant();
            var segments = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (host == "youtu.be")
                return Item(address, segments.Length > 0 ? segments[0] : null);

            if (host is not ("youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com"))
                return WebMediaParseResult.NotWeb;

            if (segments.Length == 0)
                return WebMediaParseResult.NotSupported("A YouTube address must name a video; the YouTube home page cannot be played.");

            switch (segments[0].ToLowerInvariant())
            {
                case "watch":
                    var v = QueryValue(address.Query, "v");
                    if (v is null)
                    {
                        return QueryValue(address.Query, "list") is not null
                            ? WebMediaParseResult.NotSupported(PlaylistMessage)
                            : WebMediaParseResult.NotSupported("This YouTube address does not name a video (no v=).");
                    }

                    return Item(address, v);
                case "embed" or "shorts" or "live" or "v":
                    if (segments.Length < 2)
                        return WebMediaParseResult.NotSupported("This YouTube address does not name a video.");
                    return segments[1] == "videoseries"
                        ? WebMediaParseResult.NotSupported(PlaylistMessage)
                        : Item(address, segments[1]);
                case "playlist":
                    return WebMediaParseResult.NotSupported(PlaylistMessage);
                case "results":
                    return WebMediaParseResult.NotSupported("YouTube search pages are not supported; open a single video address.");
                case "channel" or "c" or "user" or "feed":
                    return WebMediaParseResult.NotSupported(ChannelMessage);
                default:
                    return segments[0].StartsWith('@')
                        ? WebMediaParseResult.NotSupported(ChannelMessage)
                        : WebMediaParseResult.NotSupported("This YouTube address is not a video address.");
            }
        }

        static WebMediaParseResult Item(Uri address, string? id) =>
            id is not null && VideoIdPattern().IsMatch(id)
                ? WebMediaParseResult.Ok(new WebMediaReference(YouTubeId, id, address))
                : WebMediaParseResult.NotSupported("This YouTube address does not contain a valid video id.");

        static string? QueryValue(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && string.Equals(part[..eq], key, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(part[(eq + 1)..]);
            }

            return null;
        }
    }
}
