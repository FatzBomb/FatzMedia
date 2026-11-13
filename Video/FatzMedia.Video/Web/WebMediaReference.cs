#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// A recognised online media item (Stage 060A): which service it belongs to and the service's own id for
/// it. Core only names the item; a UI adapter's web view plays it. Nothing here touches the network.
/// </summary>
/// <param name="ProviderId">The service, for example <see cref="WebMediaProviders.YouTubeId"/>.</param>
/// <param name="ItemId">The service's id for the item (a YouTube video id).</param>
/// <param name="OriginalUri">The address the host supplied.</param>
public sealed record WebMediaReference(string ProviderId, string ItemId, Uri OriginalUri)
{
    /// <summary>Gets a short name for the item for display and diagnostics.</summary>
    public string DisplayName => $"{ProviderId}:{ItemId}";
}

/// <summary>What an address turned out to be.</summary>
public enum WebMediaParseOutcome
{
    /// <summary>Not an address of any web media service; the host plays it as an ordinary source.</summary>
    NotWebMedia = 0,

    /// <summary>A playable item of a supported service.</summary>
    Recognized = 1,

    /// <summary>An address of a supported service that names something this player cannot play
    /// (a playlist, channel or search). <see cref="WebMediaParseResult.Message"/> says what.</summary>
    Unsupported = 2,
}

/// <summary>The result of looking at an address.</summary>
public sealed record WebMediaParseResult
{
    /// <summary>Gets what the address was.</summary>
    public required WebMediaParseOutcome Outcome { get; init; }

    /// <summary>Gets the item for <see cref="WebMediaParseOutcome.Recognized"/>.</summary>
    public WebMediaReference? Reference { get; init; }

    /// <summary>Gets the reason for <see cref="WebMediaParseOutcome.Unsupported"/>.</summary>
    public string? Message { get; init; }

    internal static WebMediaParseResult NotWeb { get; } = new() { Outcome = WebMediaParseOutcome.NotWebMedia };

    internal static WebMediaParseResult Ok(WebMediaReference reference) =>
        new() { Outcome = WebMediaParseOutcome.Recognized, Reference = reference };

    internal static WebMediaParseResult NotSupported(string message) =>
        new() { Outcome = WebMediaParseOutcome.Unsupported, Message = message };
}

/// <summary>
/// One online service Core can recognise. Contains no web view and no network code.
/// </summary>
public interface IWebMediaProvider
{
    /// <summary>Gets the service id.</summary>
    string Id { get; }

    /// <summary>Looks at an absolute http or https address.</summary>
    WebMediaParseResult Parse(Uri address);
}
