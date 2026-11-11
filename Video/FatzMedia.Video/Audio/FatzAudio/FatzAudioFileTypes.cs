using FatzMedia.Video.Core.Files;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// Registers the audio extensions FatzAudio opens itself as <see cref="FileTypes.Audio"/>, once per process, when
/// the first engine starts. FatzAudio also reads the audio tracks of video containers and the CD+G and HLS
/// formats; those stay video, CD+G and network sources, so they are left out.
/// </summary>
static class FatzAudioFileTypes
{
    static readonly HashSet<string> NotAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3g2", ".3gp", ".asf", ".avi", ".cdg", ".dvr-ms", ".flv", ".m3u8", ".m4v", ".mkv", ".mov", ".mp4", ".ogv", ".ogx",
        ".webm", ".wmv",
    };

    static readonly Lock Gate = new();
    static bool _registered;

    /// <summary>Adds FatzAudio's audio extensions to <see cref="FileTypes.Audio"/> the first time it is called.</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            FileTypes.Audio.UnionWith(AudioExtensions(Fa.AudioEngine.SupportedFormats.SelectMany(static format => format.Extensions)));
            _registered = true;
        }
    }

    /// <summary>The audio extensions among FatzAudio's format extensions (written without a dot), with their dot.</summary>
    internal static IEnumerable<string> AudioExtensions(IEnumerable<string> formatExtensions) =>
        formatExtensions
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Select(static extension => "." + extension.Trim().TrimStart('.'))
            .Where(static extension => !NotAudio.Contains(extension) && !FileTypes.Video.Contains(extension));
}
