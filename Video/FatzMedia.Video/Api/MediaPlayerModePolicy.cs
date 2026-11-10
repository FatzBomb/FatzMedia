using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Files;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>How a player in a given mode opens a kind of media (Stage 043A).</summary>
enum MediaModeRoute
{
    /// <summary>Opened the normal way. Kinds no mode knows still fail through the normal error path.</summary>
    Normal,

    /// <summary>
    /// Audio mode: only the audio is opened. A video file plays its soundtrack (no video decoder)
    /// and MP3+G plays its audio (the CD+G graphics are ignored).
    /// </summary>
    AudioOnly,

    /// <summary>The mode does not accept this kind: the load returns false with a diagnostic.</summary>
    Reject,
}

/// <summary>
/// What each <see cref="MediaPlayerMode"/> accepts (Stage 043A spec table). Pure decisions, so the
/// engine and the tests share one source of truth.
/// </summary>
static class MediaPlayerModePolicy
{
    /// <summary>Diagnostic category for mode decisions (rejected loads, ignored settings).</summary>
    public const string DiagnosticCategory = "PlayerMode";

    /// <summary>Decides how <paramref name="mode"/> opens a local file or archive of <paramref name="type"/>.</summary>
    public static MediaModeRoute RouteFile(MediaPlayerMode mode, FileType type) => mode switch
    {
        MediaPlayerMode.Background => type switch
        {
            FileType.Audio or FileType.AudioArchive
                or FileType.AudioCdg or FileType.AudioCdgArchive
                or FileType.Cdg or FileType.CdgArchive => MediaModeRoute.Reject,
            _ => MediaModeRoute.Normal,
        },
        MediaPlayerMode.Audio => type switch
        {
            FileType.Video or FileType.VideoSubtitle or FileType.VideoArchive
                or FileType.AudioCdg or FileType.AudioCdgArchive => MediaModeRoute.AudioOnly,
            FileType.Image or FileType.Cdg or FileType.CdgArchive => MediaModeRoute.Reject,
            _ => MediaModeRoute.Normal,
        },
        _ => MediaModeRoute.Normal,
    };

    /// <summary>
    /// Decides whether <paramref name="mode"/> accepts a source of <paramref name="kind"/>. File
    /// sources are decided later by <see cref="RouteFile"/>; <paramref name="networkIsVideo"/> is the
    /// resolved content of a network source (see <see cref="RouteNetwork"/>).
    /// </summary>
    public static bool AcceptsSource(MediaPlayerMode mode, MediaInputSourceKind kind, bool networkIsVideo = false) => mode switch
    {
        MediaPlayerMode.Background => kind is MediaInputSourceKind.FilePath or MediaInputSourceKind.Bgra32Image
            || (kind == MediaInputSourceKind.NetworkUri && networkIsVideo),
        MediaPlayerMode.Audio => kind is MediaInputSourceKind.FilePath or MediaInputSourceKind.NetworkUri,
        _ => true,
    };

    /// <summary>
    /// Decides how <paramref name="mode"/> opens a network source (Stage 039A). Background plays
    /// network video as a picture (looping where the source is seekable) and rejects network
    /// audio; Audio plays network audio and the soundtrack of network video.
    /// </summary>
    public static MediaModeRoute RouteNetwork(MediaPlayerMode mode, bool isVideo) => mode switch
    {
        MediaPlayerMode.Background => isVideo ? MediaModeRoute.Normal : MediaModeRoute.Reject,
        MediaPlayerMode.Audio => isVideo ? MediaModeRoute.AudioOnly : MediaModeRoute.Normal,
        _ => MediaModeRoute.Normal,
    };

    /// <summary>
    /// Whether opening a network source through <paramref name="route"/> needs the audio engine
    /// (ENG-033): network audio and a soundtrack do; network video plays its picture on the
    /// playback clock without one.
    /// </summary>
    public static bool NetworkNeedsAudioEngine(MediaModeRoute route, bool isVideo) =>
        route == MediaModeRoute.AudioOnly || (route == MediaModeRoute.Normal && !isVideo);

    /// <summary>Whether <paramref name="mode"/> loads sidecar, archive or explicit backgrounds behind its main media.</summary>
    public static bool UsesBackgrounds(MediaPlayerMode mode) => mode != MediaPlayerMode.Audio;

    /// <summary>Whether <paramref name="mode"/> creates a presentation coordinator (renderer, GPU device, surfaces).</summary>
    public static bool UsesPresentation(MediaPlayerMode mode) => mode != MediaPlayerMode.Audio;

    /// <summary>Whether <paramref name="mode"/> initializes audio output and runs the audio device monitor.</summary>
    public static bool UsesAudioOutput(MediaPlayerMode mode) => mode != MediaPlayerMode.Background;

    /// <summary>Whether <paramref name="mode"/> always loops (Background: <c>LoopMedia</c> cannot be turned off).</summary>
    public static bool ForcesLoop(MediaPlayerMode mode) => mode == MediaPlayerMode.Background;

    /// <summary>
    /// The error for a load that needs audio while the player has no audio engine (ENG-033): its
    /// native library (FatzAudio) is missing or its output could not start.
    /// </summary>
    public const string AudioUnavailableMessage = "Audio engine unavailable";

    /// <summary>
    /// Whether opening a file of <paramref name="type"/> through <paramref name="route"/> needs the
    /// audio engine. Video plays its picture on the playback clock without one; images and CD+G
    /// graphics never need it; audio files, MP3+G and soundtracks (Audio mode) do.
    /// </summary>
    public static bool NeedsAudioEngine(MediaModeRoute route, FileType type) =>
        route == MediaModeRoute.AudioOnly
        || (route == MediaModeRoute.Normal && type is FileType.Audio or FileType.AudioArchive or FileType.AudioCdg or FileType.AudioCdgArchive);

    /// <summary>Describes a file kind for a rejection diagnostic.</summary>
    public static string Describe(FileType type) => type switch
    {
        FileType.Audio or FileType.AudioArchive => "audio files",
        FileType.AudioCdg or FileType.AudioCdgArchive => "CD+G karaoke (MP3+G)",
        FileType.Cdg or FileType.CdgArchive => "CD+G graphics",
        FileType.Image => "images",
        FileType.Video or FileType.VideoSubtitle or FileType.VideoArchive => "video files",
        _ => type.ToString(),
    };

    /// <summary>Describes a source kind for a rejection diagnostic.</summary>
    public static string Describe(MediaInputSourceKind kind, bool networkIsVideo = false) => kind switch
    {
        MediaInputSourceKind.Bgra32Image => "BGRA image sources",
        MediaInputSourceKind.NetworkUri => networkIsVideo ? "network video" : "network audio",
        _ => "files",
    };

    /// <summary>The diagnostic message for a load the mode rejects.</summary>
    public static string RejectionMessage(MediaPlayerMode mode, string what, string displayName) =>
        $"{mode} mode does not accept {what}; {displayName} was not loaded.";
}
