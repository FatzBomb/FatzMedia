using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Subtitle;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Audio;

/// <summary>
/// MIDI/KAR lyrics drawn by FatzAudio's lyric surface (037A, the slice 3 piece left at first): the presentation
/// shows its frames on the subtitle layer, which every renderer composites, instead of rasterizing our lyric cues.
/// The surface renders ahead on FatzAudio's workers, so a frame on the presentation thread is a copy of what
/// changed. The look starts from FatzAudio's Classic preset (our own lyric look) with the font and colours of
/// <c>MediaPlayerConfiguration.KaraokeStyle</c>.
/// </summary>
sealed partial class FatzAudioEngine : IKaraokeLyricsSource
{
    // Guarded by _gate: the open song's lyric surface, made on the first frame.
    bool _streamHasLyrics;
    Fa.LyricSurface? _lyricSurface;
    // Which surface revision each pooled buffer holds, so a buffer that comes back gets only the change.
    ConditionalWeakTable<byte[], HeldFrame> _heldFrames = [];

    sealed class HeldFrame
    {
        public ulong Revision;
        public ulong LayoutRevision;
    }

    bool IKaraokeLyricsSource.DrawsLyrics
    {
        get
        {
            lock (_gate)
                return _stream is not null && _streamHasLyrics;
        }
    }

    SubtitleRasterResult? IKaraokeLyricsSource.DrawLyrics(TimeSpan time, int canvasWidth, int canvasHeight, Func<int, Memory<byte>> rentPixels, out bool changed)
    {
        changed = false;
        Fa.LyricSurface surface;
        ConditionalWeakTable<byte[], HeldFrame> held;
        lock (_gate)
        {
            if (_disposed || _stream is not { } stream || !_streamHasLyrics)
                return null;

            if (_lyricSurface is null || !ReferenceEquals(_lyricSurface.Stream, stream))
            {
                _lyricSurface = CreateLyricSurface(stream, canvasWidth, canvasHeight);
                _heldFrames = [];
                if (_lyricSurface is null)
                {
                    _streamHasLyrics = false;
                    return null;
                }
            }

            surface = _lyricSurface;
            held = _heldFrames;
        }

        try
        {
            var size = new Fa.PixelSize(canvasWidth, canvasHeight);
            if (surface.PresentationSize != size)
                surface.PresentationSize = size;

            var layout = surface.GetLayout();
            var stride = layout.Width * 4;
            var length = stride * layout.Height;
            if (length <= 0)
                return null;

            var memory = rentPixels(length)[..length];
            HeldFrame? frame = null;
            ulong heldRevision = 0;
            if (MemoryMarshal.TryGetArray<byte>(memory, out var segment) && segment.Offset == 0 && segment.Array is { } array)
            {
                frame = held.GetOrCreateValue(array);
                if (frame.LayoutRevision == layout.LayoutRevision)
                    heldRevision = frame.Revision;
            }

            var update = surface.UpdateInto(memory.Span, stride, layout.Width, layout.Height, heldRevision, time);
            // Render-ahead with nothing finished yet (the first frame): the buffer holds no lyric frame.
            if (update.Behind && update.Revision == heldRevision && heldRevision == 0)
                return null;

            changed = update.Revision != heldRevision;
            if (frame is not null)
            {
                frame.Revision = update.Revision;
                frame.LayoutRevision = update.LayoutRevision;
            }

            return new SubtitleRasterResult(memory, layout.Width, layout.Height, stride, layout.BitmapX, layout.BitmapY, canvasWidth, canvasHeight);
        }
        catch (ObjectDisposedException)
        {
            return null; // the song closed meanwhile
        }
        catch (Fa.AudioException ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"FatzAudio could not draw the lyrics: {ex.Message}", innerException: ex);
            return null;
        }
    }

    Fa.LyricSurface? CreateLyricSurface(Fa.AudioStream stream, int width, int height)
    {
        try
        {
            var surface = stream.CreateLyricSurface(FatzAudioMappings.ToLyricStyle(_karaokeStyle));
            surface.PresentationSize = new Fa.PixelSize(width, height);
            surface.RenderAhead = true;
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, Category, $"Lyrics drawn by FatzAudio ({surface.GetLayout().FontUsed}).");
            return surface;
        }
        catch (Exception ex) when (ex is Fa.AudioException or ArgumentException or ObjectDisposedException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, Category, $"FatzAudio cannot draw these lyrics ({ex.Message}); our own lyric renderer draws them.", innerException: ex);
            return null;
        }
    }

    // The surface goes with its stream (closing the stream frees it). Caller holds _gate.
    void ForgetLyricSurfaceLocked()
    {
        _lyricSurface = null;
        _streamHasLyrics = false;
    }

    static bool HasLyrics(Fa.AudioStream stream)
    {
        try
        {
            return stream.MidiInfo is not null && stream.MidiTexts.Any(static text => text.Type == Fa.MidiTextType.Lyric);
        }
        catch (Exception ex) when (ex is Fa.AudioException or ObjectDisposedException)
        {
            return false;
        }
    }
}
