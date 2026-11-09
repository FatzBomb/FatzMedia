namespace FatzMedia.Video.Core.Configuration;

/// <summary>
/// What a player loads and accepts (Stage 043A). Each mode builds only the services it needs.
/// </summary>
public enum MediaPlayerMode
{
    /// <summary>The complete player: audio, video, images, CD+G, subtitles, MIDI and network audio.</summary>
    Full = 0,

    /// <summary>
    /// A looping picture source: video (picture only, no audio decoded), images and BGRA sources
    /// such as <see cref="MediaInputSource.FromColor(uint, string?)"/>. No audio output is
    /// initialized and no audio device monitor runs. Loads of audio-only files, CD+G, MIDI and
    /// network audio return <see langword="false"/> with a diagnostic.
    /// </summary>
    Background = 1,

    /// <summary>
    /// Audio output only: no presentation coordinator, GPU device, surfaces or video decoder.
    /// Accepts audio files, MIDI, the soundtrack of a video file and the audio of MP3+G (the
    /// graphics are ignored). Loads of images, BGRA sources and video files without an audio
    /// stream return <see langword="false"/> with a diagnostic; surface attach calls return
    /// <see langword="false"/>.
    /// </summary>
    Audio = 2,
}
