using ManagedBass.Midi;
using MBass = ManagedBass.Bass;

namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// <see cref="IMidiEventReader"/> over an open BASS MIDI channel (the source/decode handle, before
/// tempo processing). It makes exactly the BASS calls the lyric and metadata builders made on the
/// handle before 037A slice 1: <c>BASS_MIDI_StreamGetMarks</c> and <c>BASS_MIDI_StreamGetEvents</c>
/// on all tracks (failures read as empty), <c>BASS_ChannelGetLength</c> and
/// <c>BASS_ChannelBytes2Seconds</c>.
/// </summary>
sealed class BassMidiEventReader : IMidiEventReader
{
    readonly int _handle;

    BassMidiEventReader(int handle) => _handle = handle;

    /// <summary>Returns a reader over <paramref name="handle"/>, or <see langword="null"/> for 0 (no channel).</summary>
    public static BassMidiEventReader? ForHandle(int handle) => handle == 0 ? null : new BassMidiEventReader(handle);

    public IReadOnlyList<MidiMark> GetMarks(MidiMarkKind kind)
    {
        try
        {
            var markers = BassMidi.StreamGetMarks(_handle, -1, ToBass(kind)) ?? [];
            var marks = new MidiMark[markers.Length];
            for (var i = 0; i < markers.Length; i++)
                marks[i] = new MidiMark(markers[i].Position, markers[i].Track, markers[i].Text);
            return marks;
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<MidiTempoEvent> GetTempoEvents()
    {
        MidiEvent[] events;
        try
        {
            events = BassMidi.StreamGetEvents(_handle, -1, MidiEventType.Tempo) ?? [];
        }
        catch
        {
            return [];
        }

        var tempos = new MidiTempoEvent[events.Length];
        for (var i = 0; i < events.Length; i++)
            tempos[i] = new MidiTempoEvent(events[i].Position, events[i].Parameter);
        return tempos;
    }

    public double GetDurationSeconds()
    {
        var durationBytes = MBass.ChannelGetLength(_handle);
        return MBass.ChannelBytes2Seconds(_handle, durationBytes);
    }

    public double PositionToSeconds(long position) => MBass.ChannelBytes2Seconds(_handle, position);

    static MidiMarkerType ToBass(MidiMarkKind kind) => kind switch
    {
        MidiMarkKind.Lyric => MidiMarkerType.Lyric,
        MidiMarkKind.Text => MidiMarkerType.Text,
        MidiMarkKind.TrackName => MidiMarkerType.TrackName,
        MidiMarkKind.Copyright => MidiMarkerType.Copyright,
        MidiMarkKind.KeySignature => MidiMarkerType.KeySignature,
        MidiMarkKind.TimeSignature => MidiMarkerType.TimeSignature,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
