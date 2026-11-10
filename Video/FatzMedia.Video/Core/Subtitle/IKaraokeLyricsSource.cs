namespace FatzMedia.Video.Core.Subtitle;

/// <summary>
/// An audio engine that draws the open song's MIDI/KAR lyrics itself (037A: FatzAudio's lyric surface): the
/// presentation then shows its frames on the subtitle layer instead of rasterizing the lyric cues.
/// </summary>
interface IKaraokeLyricsSource
{
    /// <summary>Whether the open song has lyrics the engine draws.</summary>
    bool DrawsLyrics { get; }

    /// <summary>
    /// Draws the lyric screen at <paramref name="time"/> on a <paramref name="canvasWidth"/> x
    /// <paramref name="canvasHeight"/> canvas into memory from <paramref name="rentPixels"/> (which may hand back the
    /// memory of the previous frame when no composition holds it; only what changed is drawn then). Null when there
    /// is nothing to draw (no lyrics, or the song closed).
    /// </summary>
    /// <param name="time">The song position to draw (the presentation's lyric position, offset applied).</param>
    /// <param name="canvasWidth">Width of the subtitle canvas the frame is placed on.</param>
    /// <param name="canvasHeight">Height of that canvas.</param>
    /// <param name="rentPixels">Gives memory of the requested length from the overlay pool.</param>
    /// <param name="changed">False when the frame is the one the rented memory already held.</param>
    SubtitleRasterResult? DrawLyrics(TimeSpan time, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels, out bool changed);
}
