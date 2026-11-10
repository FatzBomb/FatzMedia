namespace FatzMedia.Video.Core.Subtitle;

/// <summary>
/// A karaoke picture drawn by the engine itself and shown on the subtitle layer, which every renderer composites
/// (037A, FatzAudio): the audio engine's MIDI/KAR lyric screen (instead of rasterizing the lyric cues), or the CD+G
/// engine's scaled full-colour picture (instead of the classic indexed one).
/// </summary>
interface IKaraokeOverlaySource
{
    /// <summary>Whether there is a picture this source draws now.</summary>
    bool DrawsOverlay { get; }

    /// <summary>
    /// Draws the picture at <paramref name="time"/> on a <paramref name="canvasWidth"/> x
    /// <paramref name="canvasHeight"/> canvas into memory from <paramref name="rentPixels"/> (which may hand back the
    /// memory of the previous frame when no composition holds it; only what changed is drawn then). Null when there
    /// is nothing to draw (nothing loaded, or the song closed).
    /// </summary>
    /// <param name="time">The song position to draw (the lyric position, offset applied); a CD+G engine draws the
    /// position it last rendered instead.</param>
    /// <param name="canvasWidth">Width of the subtitle canvas the frame is placed on.</param>
    /// <param name="canvasHeight">Height of that canvas.</param>
    /// <param name="rentPixels">Gives memory of the requested length from the overlay pool.</param>
    /// <param name="changed">False when the frame is the one the rented memory already held.</param>
    SubtitleRasterResult? DrawOverlay(TimeSpan time, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels, out bool changed);
}
