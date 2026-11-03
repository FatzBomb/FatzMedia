using FatzBomb.MediaControl.Core.Audio;
using Fa = global::FatzAudio;

namespace FatzBomb.MediaControl.Audio.FatzAudio;

/// <summary>
/// <see cref="IMidiEventReader"/> over FatzAudio's MIDI texts and marks (037A slice 2), so the lyric
/// and karaoke metadata builders run unchanged on the FatzAudio engine. A snapshot: it is read once
/// from the open stream and makes no native call afterwards. Positions are 100 ns ticks of the time
/// FatzAudio reports for each event (where it is heard at normal speed).
/// </summary>
/// <remarks>
/// FatzAudio returns lyrics cleaned up: the Soft Karaoke marks (<c>\</c> new page, <c>/</c> new
/// line) and the line ends of lyric events are taken off the text and given as flags. They are put
/// back as <c>\</c> and <c>/</c> prefixes, which <see cref="MidiLyricCueBuilder"/> reads as before.
/// FatzAudio types the words of a .kar file as lyrics, so a .kar's words come through
/// <see cref="MidiMarkKind.Lyric"/> where BASS gave them as text events; the builder takes lyric
/// events first and text events only when there are none, so it builds the same lines.
/// </remarks>
internal sealed class FatzAudioMidiEventReader : IMidiEventReader
{
    private readonly IReadOnlyList<Fa.MidiText> _texts;
    private readonly IReadOnlyList<Fa.MidiMark> _marks;
    private readonly double _durationSeconds;

    internal FatzAudioMidiEventReader(IReadOnlyList<Fa.MidiText> texts, IReadOnlyList<Fa.MidiMark> marks, double durationSeconds)
    {
        _texts = texts;
        _marks = marks;
        _durationSeconds = durationSeconds;
    }

    /// <summary>Reads the MIDI events of <paramref name="stream"/>; null when it is not MIDI.</summary>
    public static FatzAudioMidiEventReader? TryCreate(Fa.AudioStream stream, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.MidiInfo is null ? null : new FatzAudioMidiEventReader(stream.MidiTexts, stream.MidiMarks, durationSeconds);
    }

    public IReadOnlyList<MidiMark> GetMarks(MidiMarkKind kind) => kind switch
    {
        MidiMarkKind.Lyric => Texts(Fa.MidiTextType.Lyric, withLayoutMarks: true),
        MidiMarkKind.Text => Texts(Fa.MidiTextType.Text, withLayoutMarks: false),
        MidiMarkKind.TrackName => Texts(Fa.MidiTextType.TrackName, withLayoutMarks: false),
        MidiMarkKind.Copyright => Texts(Fa.MidiTextType.Copyright, withLayoutMarks: false),
        // "sharps mode" and "numerator/denominator", as BASS writes them; FatzAudio has no MIDI
        // clocks or 32nds per beat, so the standard 24 and 8 fill those (the builder reads only the
        // first word).
        MidiMarkKind.KeySignature => Marks(Fa.MidiMarkType.KeySignature, static mark => $"{mark.Value} {mark.Value2}"),
        MidiMarkKind.TimeSignature => Marks(Fa.MidiMarkType.TimeSignature, static mark => $"{mark.Value}/{mark.Value2} 24 8"),
        _ => [],
    };

    public IReadOnlyList<MidiTempoEvent> GetTempoEvents() =>
        [.. Order(_marks.Where(static mark => mark.Type == Fa.MidiMarkType.Tempo), static mark => mark.Position, static mark => mark.Track)
            .Select(static mark => new MidiTempoEvent(mark.Position.Ticks, mark.Value))];

    public double GetDurationSeconds() => _durationSeconds;

    public double PositionToSeconds(long position) => TimeSpan.FromTicks(position).TotalSeconds;

    private MidiMark[] Texts(Fa.MidiTextType type, bool withLayoutMarks) =>
        [.. Order(_texts.Where(text => text.Type == type), static text => text.Position, static text => text.Track)
            .Select(text => new MidiMark(text.Position.Ticks, text.Track, withLayoutMarks ? WithLayoutMarks(text) : text.Text))];

    private MidiMark[] Marks(Fa.MidiMarkType type, Func<Fa.MidiMark, string> format) =>
        [.. Order(_marks.Where(mark => mark.Type == type), static mark => mark.Position, static mark => mark.Track)
            .Select(mark => new MidiMark(mark.Position.Ticks, mark.Track, format(mark)))];

    /// <summary>Puts the Soft Karaoke page (<c>\</c>) or line (<c>/</c>) mark back in front of a syllable.</summary>
    internal static string WithLayoutMarks(Fa.MidiText text) =>
        text.Flags.HasFlag(Fa.MidiTextFlags.NewParagraph) ? "\\" + text.Text
        : text.Flags.HasFlag(Fa.MidiTextFlags.NewLine) ? "/" + text.Text
        : text.Text;

    // Chronological, lowest track first at equal times (the order BASS returns marks in).
    private static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, TimeSpan> position, Func<T, int> track) =>
        items.OrderBy(position).ThenBy(track);
}
