namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>The MIDI text and signature events the lyric and metadata builders read.</summary>
internal enum MidiMarkKind
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
/// One text event of a MIDI source. <see cref="Position"/> is in the reader's own unit (bytes for
/// BASS, ticks for a file); only its order and <see cref="IMidiEventReader.PositionToSeconds"/>
/// give it meaning.
/// </summary>
internal readonly record struct MidiMark(long Position, int Track, string? Text);

/// <summary>A tempo change (meta event 81): microseconds per quarter note at <see cref="Position"/>.</summary>
internal readonly record struct MidiTempoEvent(long Position, int MicrosecondsPerQuarterNote);

/// <summary>
/// Engine-neutral reader of the text, signature and tempo events of one MIDI source (037A slice 1).
/// The lyric cue and karaoke metadata builders read MIDI only through it, so they need no audio
/// engine handle. <see cref="BassMidiEventReader"/> reads an open BASS MIDI channel (today's path);
/// <see cref="StandardMidiFileReader"/> reads the file itself.
/// </summary>
/// <remarks>
/// The builders call the members in a fixed order and each at most as often as they did on BASS
/// before this seam, so the BASS reader makes the same native calls in the same order.
/// </remarks>
internal interface IMidiEventReader
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
/// Audio-engine capability (037A slice 1): reads the MIDI events of the media the engine has open.
/// An engine without it gets its MIDI lyrics from the file (<see cref="StandardMidiFileReader"/>).
/// </summary>
internal interface IMidiEventReaderSource
{
    /// <summary>
    /// Returns a reader over the open media's MIDI events, or <see langword="null"/> when the
    /// engine has nothing to read (no media open, or no MIDI channel).
    /// </summary>
    IMidiEventReader? GetMidiEventReader();
}
