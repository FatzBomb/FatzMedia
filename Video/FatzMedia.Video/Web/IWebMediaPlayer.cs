#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>The playback state a web media service reports.</summary>
public enum WebMediaState
{
    /// <summary>Nothing loaded or not started yet.</summary>
    Unstarted = 0,

    /// <summary>The item finished.</summary>
    Ended = 1,

    /// <summary>Playing.</summary>
    Playing = 2,

    /// <summary>Paused.</summary>
    Paused = 3,

    /// <summary>Waiting for data.</summary>
    Buffering = 4,

    /// <summary>Loaded and ready to play.</summary>
    Cued = 5,
}

/// <summary>Why a web item failed to play.</summary>
public enum WebMediaErrorKind
{
    /// <summary>Not classified.</summary>
    Unknown = 0,

    /// <summary>The service rejected the request or the id.</summary>
    InvalidItem = 1,

    /// <summary>The item cannot play in an embedded player (owner disabled embedding, or it needs
    /// sign-in or a purchase).</summary>
    EmbeddingNotAllowed = 2,

    /// <summary>The item is gone, private or not available here.</summary>
    NotFound = 3,

    /// <summary>The service could not verify where the page came from (YouTube error 153: the embed
    /// was sent without a usable Referer; the page must be served from an https host name).</summary>
    OriginRejected = 4,

    /// <summary>The player itself failed (for example HTML5 playback).</summary>
    PlayerFailure = 5,
}

/// <summary>Maps the codes a service reports to <see cref="WebMediaErrorKind"/> and a plain message.</summary>
public static class WebMediaErrors
{
    /// <summary>Describes a YouTube IFrame Player error code.</summary>
    public static (WebMediaErrorKind Kind, string Message) FromYouTubeCode(int code) => code switch
    {
        2 => (WebMediaErrorKind.InvalidItem, "YouTube rejected the video id."),
        5 => (WebMediaErrorKind.PlayerFailure, "YouTube's player could not play this video (HTML5 player error)."),
        100 => (WebMediaErrorKind.NotFound, "This YouTube video was not found, or it is private."),
        101 or 150 => (WebMediaErrorKind.EmbeddingNotAllowed, "The owner of this YouTube video does not allow it to play in other applications."),
        153 => (WebMediaErrorKind.OriginRejected, "YouTube could not verify where the player page came from (error 153)."),
        _ => (WebMediaErrorKind.Unknown, $"YouTube reported error {code}."),
    };
}

/// <summary>Things a web item cannot do because the service owns the picture and the sound.</summary>
public enum WebMediaFeature
{
    /// <summary>Play, pause and seek.</summary>
    Transport = 0,

    /// <summary>Volume and mute.</summary>
    Volume = 1,

    /// <summary>Playback speed, in the steps the service offers.</summary>
    Rate = 2,

    /// <summary>Routing the sound through FatzAudio.</summary>
    AudioRouting = 3,

    /// <summary>Key change.</summary>
    KeyChange = 4,

    /// <summary>Fine tempo change.</summary>
    Tempo = 5,

    /// <summary>Mixing or crossfading with other items.</summary>
    Mixing = 6,

    /// <summary>The CD+G overlay and other video overlays on the item.</summary>
    CdgOverlay = 7,

    /// <summary>Pulling out, saving or caching the picture or the sound.</summary>
    Extraction = 8,

    /// <summary>Playing with the player hidden (background or audio-only).</summary>
    BackgroundPlayback = 9,
}

/// <summary>States which features a web item has. Everything the service owns reports not supported.</summary>
public static class WebMediaSupport
{
    /// <summary>Gets whether web items support <paramref name="feature"/>.</summary>
    public static bool IsSupported(WebMediaFeature feature) =>
        feature is WebMediaFeature.Transport or WebMediaFeature.Volume or WebMediaFeature.Rate;

    /// <summary>Throws <see cref="NotSupportedException"/> naming the feature when it is not supported.</summary>
    public static void Require(WebMediaFeature feature)
    {
        if (!IsSupported(feature))
            throw new NotSupportedException(Describe(feature));
    }

    /// <summary>Gets the message hosts show for an unsupported feature.</summary>
    public static string Describe(WebMediaFeature feature) => feature switch
    {
        WebMediaFeature.AudioRouting => "Web media plays its own sound; it cannot be routed through FatzAudio.",
        WebMediaFeature.KeyChange => "Web media does not support a key change.",
        WebMediaFeature.Tempo => "Web media supports only the speeds the service offers, not fine tempo.",
        WebMediaFeature.Mixing => "Web media cannot be mixed or crossfaded with other items.",
        WebMediaFeature.CdgOverlay => "Overlays such as CD+G cannot be drawn over web media.",
        WebMediaFeature.Extraction => "Web media cannot be extracted, saved or cached.",
        WebMediaFeature.BackgroundPlayback => "Web media cannot play with its player hidden.",
        _ => $"{feature} is supported.",
    };
}

/// <summary>Raised when the playback state of a web item changes.</summary>
public sealed class WebMediaStateChangedEventArgs(WebMediaState state, TimeSpan position) : EventArgs
{
    /// <summary>Gets the new state.</summary>
    public WebMediaState State { get; } = state;

    /// <summary>Gets the position when the state changed.</summary>
    public TimeSpan Position { get; } = position;
}

/// <summary>Raised when a web item fails.</summary>
public sealed class WebMediaErrorEventArgs(WebMediaErrorKind kind, string message, int? serviceCode) : EventArgs
{
    /// <summary>Gets the kind of failure.</summary>
    public WebMediaErrorKind Kind { get; } = kind;

    /// <summary>Gets a plain message.</summary>
    public string Message { get; } = message;

    /// <summary>Gets the service's own code, if it gave one.</summary>
    public int? ServiceCode { get; } = serviceCode;
}

/// <summary>
/// A web item player a UI adapter provides (Stage 060A). Core defines only this contract; the web view
/// lives in the adapter. Position and state events arrive on a background thread (see the Video rules for
/// host events). FatzMedia never sees the service's passwords or tokens: <see cref="SignInAsync"/> only
/// opens the service's own sign-in page in the same persistent web view profile.
/// </summary>
public interface IWebMediaPlayer : IAsyncDisposable
{
    /// <summary>Gets the loaded item.</summary>
    WebMediaReference? Current { get; }

    /// <summary>Gets the playback state.</summary>
    WebMediaState State { get; }

    /// <summary>Gets the position, updated about every 250 ms while playing.</summary>
    TimeSpan Position { get; }

    /// <summary>Gets the duration, or zero while unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Gets the volume, 0 to 1.</summary>
    double Volume { get; }

    /// <summary>Gets whether the sound is muted.</summary>
    bool IsMuted { get; }

    /// <summary>Gets the playback speed.</summary>
    double Rate { get; }

    /// <summary>Gets the speeds the service offers.</summary>
    IReadOnlyList<double> AvailableRates { get; }

    /// <summary>Raised when <see cref="State"/> changes.</summary>
    event EventHandler<WebMediaStateChangedEventArgs>? StateChanged;

    /// <summary>Raised about every 250 ms while playing, with the new position.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Raised when an item fails to play.</summary>
    event EventHandler<WebMediaErrorEventArgs>? Error;

    /// <summary>Loads an item. It starts only when <paramref name="autoplay"/> is true (the user chose to start it).</summary>
    Task LoadAsync(WebMediaReference item, bool autoplay, CancellationToken cancellationToken = default);

    /// <summary>Plays.</summary>
    Task PlayAsync();

    /// <summary>Pauses.</summary>
    Task PauseAsync();

    /// <summary>Seeks.</summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>Sets the volume, 0 to 1.</summary>
    Task SetVolumeAsync(double volume);

    /// <summary>Mutes or unmutes.</summary>
    Task SetMutedAsync(bool muted);

    /// <summary>Sets the speed (one of <see cref="AvailableRates"/>).</summary>
    Task SetRateAsync(double rate);

    /// <summary>Opens the service's sign-in page in the player's persistent web view profile.</summary>
    Task SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the player's web view profile (cookies, storage), signing out of every service.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
