namespace FatzMedia.Video.Core.Audio;

/// <summary>The MIDI text and signature events the lyric and metadata builders read.</summary>
enum MidiMarkKind
{
    /// <summary>Lyric events (meta event 5).</summary>
    Lyric,

    /// <summary>Text events (meta event 1).</summary>
    Text,

    /// <summary>Track names (meta event 3).</summary>
    TrackName,

    /// <summary>Copyright notices (meta event 2).</summary>
    Copyright,

    /// <summary>Key signatures (meta event 89) as "a b": sharps (+) or flats (-), then 0 major or 1 minor.</summary>
    KeySignature,

    /// <summary>Time signatures (meta event 88) as "n/d c b", e.g. "4/4 24 8".</summary>
    TimeSignature,
}

/// <summary>
/// One text event of a MIDI source. <see cref="Position"/> is in the reader's own unit (ticks for a
/// file); only its order and <see cref="IMidiEventReader.PositionToSeconds"/> give it meaning.
/// </summary>
readonly record struct MidiMark(long Position, int Track, string? Text);

/// <summary>A tempo change (meta event 81): microseconds per quarter note at <see cref="Position"/>.</summary>
readonly record struct MidiTempoEvent(long Position, int MicrosecondsPerQuarterNote);

/// <summary>
/// Engine-neutral reader of the text, signature and tempo events of one MIDI source. The lyric cue
/// and karaoke metadata builders read MIDI only through it, so they need no audio engine handle;
/// the FatzAudio engine implements it with FatzAudio's MIDI event API.
/// </summary>
interface IMidiEventReader
{
    /// <summary>
    /// Returns the events of <paramref name="kind"/> in chronological order (lowest track first
    /// at equal positions); empty when there are none or they cannot be read. Never throws.
    /// </summary>
    IReadOnlyList<MidiMark> GetMarks(MidiMarkKind kind);

    /// <summary>Returns the tempo events in chronological order; empty when none. Never throws.</summary>
    IReadOnlyList<MidiTempoEvent> GetTempoEvents();

    /// <summary>Gets the length of the source in seconds at normal speed (may be non-finite when unknown).</summary>
    double GetDurationSeconds();

    /// <summary>Converts a <see cref="MidiMark.Position"/> to seconds at normal speed.</summary>
    double PositionToSeconds(long position);
}

/// <summary>
/// Audio-engine capability: reads the MIDI events of the media the engine has open. An engine
/// without it has no MIDI lyrics or metadata.
/// </summary>
interface IMidiEventReaderSource
{
    /// <summary>
    /// Returns a reader over the open media's MIDI events, or <see langword="null"/> when the
    /// engine has nothing to read (no media open, or no MIDI channel).
    /// </summary>
    IMidiEventReader? GetMidiEventReader();
}
