using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// The self-contained player controls every UI adapter offers (Stage 046A): each owns one
/// player, created on demand, and plays one kind of media.
/// </summary>
public enum LightweightPlayerKind
{
    /// <summary>
    /// <c>KaraokeSurface</c>: CD+G (graphics alone, audio + CD+G, archives) and MIDI/KAR lyrics, in
    /// <see cref="MediaPlayerMode.Full"/> mode, without looping. Video and plain audio are rejected.
    /// </summary>
    Karaoke,

    /// <summary>
    /// <c>BackgroundSurface</c>: images, BGRA sources and the picture of a video, looping, in
    /// <see cref="MediaPlayerMode.Background"/> mode (no audio output, no audio engine). Audio,
    /// CD+G and lyrics are rejected.
    /// </summary>
    Background,
}

/// <summary>
/// The rules of the karaoke and background controls (Stage 046A), moved out of the WPF adapter so
/// the WPF, Avalonia and WinForms controls accept, configure and start media the same way. Pure
/// decisions; <see cref="LightweightPlayerController"/> applies them.
/// </summary>
public static class LightweightPlayerPolicy
{
    private static readonly HashSet<string> MidiKaraokeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".kar", ".mid", ".midi", ".rmi"
    };

    /// <summary>Gets whether <paramref name="path"/> names a MIDI file, which shows its lyrics as karaoke.</summary>
    public static bool IsMidiKaraokeFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && MidiKaraokeExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Gets the configuration a control's player is created with: hardware video decoding, and
    /// <see cref="MediaPlayerMode.Background"/> for <see cref="LightweightPlayerKind.Background"/>
    /// (so a silent background never starts the audio engine) or <see cref="MediaPlayerMode.Full"/>.
    /// </summary>
    public static MediaPlayerConfiguration CreateConfiguration(LightweightPlayerKind kind) =>
        new MediaPlayerConfiguration(VideoDecodingMethod.Hardware) with
        {
            Mode = kind == LightweightPlayerKind.Background ? MediaPlayerMode.Background : MediaPlayerMode.Full,
        };

    /// <summary>
    /// Applies the kind's player settings once, after creation: karaoke shows MIDI lyrics and does
    /// not loop; a background loops (Background mode forces it). Both zoom the picture.
    /// </summary>
    public static void ConfigurePlayer(MediaControlPlayer player, LightweightPlayerKind kind)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (kind == LightweightPlayerKind.Karaoke)
        {
            player.LoopMedia = false;
            player.ShowMidiLyrics = true;
        }
        else
        {
            player.LoopMedia = true;
        }

        player.VideoDisplayMode = VideoDisplayMode.Zoom;
    }

    /// <summary>
    /// Decides, after a load inspected the media, whether the control keeps it (a rejected item is
    /// closed). Karaoke: CD+G without video, or a MIDI file (<paramref name="path"/>) with lyrics.
    /// Background: an image or video, without CD+G or lyrics.
    /// </summary>
    /// <param name="kind">The control.</param>
    /// <param name="content">What the load found (<see cref="IPlayerSession.Content"/>).</param>
    /// <param name="path">The file loaded, if any (decides MIDI karaoke by its extension).</param>
    public static bool AcceptsLoadedMedia(LightweightPlayerKind kind, MediaContent content, string? path) => kind switch
    {
        LightweightPlayerKind.Karaoke => !content.HasFlag(MediaContent.Video)
            && (content.HasFlag(MediaContent.Cdg)
                || (IsMidiKaraokeFile(path) && content.HasFlag(MediaContent.Subtitle))),
        _ => (content.HasFlag(MediaContent.Image) || content.HasFlag(MediaContent.Video))
            && !content.HasFlag(MediaContent.Cdg)
            && !content.HasFlag(MediaContent.Subtitle),
    };

    /// <summary>
    /// Whether an accepted item is started after its load when the control plays automatically:
    /// always for karaoke, only video for a background (an image shows without playing).
    /// </summary>
    public static bool PlaysAfterLoad(LightweightPlayerKind kind, MediaContent content) =>
        kind == LightweightPlayerKind.Karaoke || content.HasFlag(MediaContent.Video);
}
