using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Engine-neutral <see cref="IMidiEventReader"/> over a Standard MIDI File (.mid, .midi, .kar, or
/// a RIFF .rmi wrapper), read and parsed in managed code with no audio engine (037A slice 1). Used
/// for MIDI lyrics when the audio engine offers no <see cref="IMidiEventReaderSource"/>.
/// </summary>
/// <remarks>
/// Positions are ticks; seconds follow the file's tempo map (format 2 files are read as one
/// merged timeline). Text is decoded as UTF-8 when it is valid UTF-8, else as Latin-1 (BASS
/// uses the system ANSI code page, so text outside ASCII may differ from the BASS reader). Files
/// over <see cref="MaximumFileBytes"/> or that are not SMF are not read.
/// </remarks>
internal sealed class StandardMidiFileReader : IMidiEventReader
{
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int DefaultMicrosecondsPerQuarterNote = 500_000;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<MidiMarkKind, MidiMark[]> _marks;
    private readonly MidiTempoEvent[] _tempos;
    private readonly int _ticksPerQuarterNote;
    private readonly double _secondsPerTick;
    private readonly long _endTick;

    private StandardMidiFileReader(
        Dictionary<MidiMarkKind, MidiMark[]> marks,
        MidiTempoEvent[] tempos,
        int ticksPerQuarterNote,
        double secondsPerTick,
        long endTick)
    {
        _marks = marks;
        _tempos = tempos;
        _ticksPerQuarterNote = ticksPerQuarterNote;
        _secondsPerTick = secondsPerTick;
        _endTick = endTick;
    }

    /// <summary>Reads the file at <paramref name="path"/>; <see langword="null"/> when it is missing, too large or not SMF.</summary>
    public static StandardMidiFileReader? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaximumFileBytes)
            return null;

        return TryParse(File.ReadAllBytes(path));
    }

    /// <summary>Reads an archive entry; <see langword="null"/> when it is too large or not SMF.</summary>
    public static StandardMidiFileReader? TryLoad(IArchiveEntryInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Size is > MaximumFileBytes)
            return null;

        using var buffer = new MemoryStream();
        entry.WriteTo(buffer);
        return buffer.Length > MaximumFileBytes ? null : TryParse(buffer.ToArray());
    }

    /// <summary>Parses SMF (or RMID) bytes; <see langword="null"/> when they are not a MIDI file.</summary>
    public static StandardMidiFileReader? TryParse(ReadOnlySpan<byte> data)
    {
        data = UnwrapRiff(data);
        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8))
            return null;

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (headerLength < 6 || headerLength > (uint)(data.Length - 8))
            return null;

        var division = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        var ticksPerQuarterNote = 0;
        var secondsPerTick = 0.0;
        if ((division & 0x8000) != 0)
        {
            // SMPTE: -frames per second in the high byte, ticks per frame in the low byte.
            var framesPerSecond = -(sbyte)(division >> 8);
            var ticksPerFrame = division & 0xFF;
            if (framesPerSecond <= 0 || ticksPerFrame == 0)
                return null;

            secondsPerTick = 1.0 / ((framesPerSecond == 29 ? 29.97 : framesPerSecond) * ticksPerFrame);
        }
        else
        {
            ticksPerQuarterNote = division;
            if (ticksPerQuarterNote == 0)
                return null;
        }

        var events = new List<(MidiMarkKind Kind, MidiMark Mark)>();
        var tempos = new List<(MidiTempoEvent Tempo, int Track)>();
        long endTick = 0;
        var offset = 8 + (int)headerLength;
        var track = 0;
        while (offset + 8 <= data.Length)
        {
            ReadOnlySpan<byte> id = data.Slice(offset, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            offset += 8;
            var available = Math.Min((long)length, data.Length - offset);
            if (id.SequenceEqual("MTrk"u8))
            {
                endTick = Math.Max(endTick, ReadTrack(data.Slice(offset, (int)available), track, events, tempos));
                track++;
            }

            offset += (int)available;
        }

        if (track == 0)
            return null;

        var marks = new Dictionary<MidiMarkKind, MidiMark[]>();
        foreach (MidiMarkKind kind in Enum.GetValues<MidiMarkKind>())
        {
            // Chronological, lowest track first at equal positions (as BASS orders markers).
            marks[kind] = [.. events
                .Where(entry => entry.Kind == kind)
                .Select(static entry => entry.Mark)
                .OrderBy(static mark => mark.Position)
                .ThenBy(static mark => mark.Track)];
        }

        MidiTempoEvent[] orderedTempos = [.. tempos
            .OrderBy(static entry => entry.Tempo.Position)
            .ThenBy(static entry => entry.Track)
            .Select(static entry => entry.Tempo)];
        return new StandardMidiFileReader(marks, orderedTempos, ticksPerQuarterNote, secondsPerTick, endTick);
    }

    public IReadOnlyList<MidiMark> GetMarks(MidiMarkKind kind) =>
        _marks.TryGetValue(kind, out MidiMark[]? marks) ? marks : [];

    public IReadOnlyList<MidiTempoEvent> GetTempoEvents() => _tempos;

    public double GetDurationSeconds() => PositionToSeconds(_endTick);

    public double PositionToSeconds(long position)
    {
        if (position <= 0)
            return 0;

        if (_ticksPerQuarterNote == 0)
            return position * _secondsPerTick;

        double seconds = 0;
        long tick = 0;
        var microsecondsPerQuarterNote = DefaultMicrosecondsPerQuarterNote;
        foreach (MidiTempoEvent tempo in _tempos)
        {
            if (tempo.Position >= position)
                break;

            if (tempo.MicrosecondsPerQuarterNote <= 0)
                continue;

            seconds += TicksToSeconds(tempo.Position - tick, microsecondsPerQuarterNote);
            tick = tempo.Position;
            microsecondsPerQuarterNote = tempo.MicrosecondsPerQuarterNote;
        }

        return seconds + TicksToSeconds(position - tick, microsecondsPerQuarterNote);
    }

    private double TicksToSeconds(long ticks, int microsecondsPerQuarterNote) =>
        ticks * (microsecondsPerQuarterNote / 1_000_000d) / _ticksPerQuarterNote;

    private static ReadOnlySpan<byte> UnwrapRiff(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("RMID"u8))
            return data;

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 4)..]);
            var available = (int)Math.Min((long)length, data.Length - offset - 8);
            if (data.Slice(offset, 4).SequenceEqual("data"u8))
                return data.Slice(offset + 8, available);

            offset += 8 + available + (available & 1);
        }

        return [];
    }

    /// <summary>Reads one track's meta events; returns its last tick. A truncated track ends where its data ends.</summary>
    private static long ReadTrack(
        ReadOnlySpan<byte> data,
        int track,
        List<(MidiMarkKind Kind, MidiMark Mark)> events,
        List<(MidiTempoEvent Tempo, int Track)> tempos)
    {
        var offset = 0;
        long tick = 0;
        byte runningStatus = 0;
        while (offset < data.Length)
        {
            if (!TryReadVariableLength(data, ref offset, out var delta))
                break;

            tick += delta;
            if (offset >= data.Length)
                break;

            var status = data[offset];
            if (status >= 0x80)
                offset++;
            else if (runningStatus == 0)
                break;
            else
                status = runningStatus;

            if (status == 0xFF)
            {
                runningStatus = 0;
                if (offset >= data.Length)
                    break;

                var type = data[offset++];
                if (!TryReadVariableLength(data, ref offset, out var length) || length > data.Length - offset)
                    break;

                ReadOnlySpan<byte> payload = data.Slice(offset, (int)length);
                offset += (int)length;
                AddMetaEvent(type, payload, tick, track, events, tempos);
                if (type == 0x2F)
                    break;
            }
            else if (status is 0xF0 or 0xF7)
            {
                runningStatus = 0;
                if (!TryReadVariableLength(data, ref offset, out var length) || length > data.Length - offset)
                    break;

                offset += (int)length;
            }
            else if (status >= 0xF0)
            {
                // System common/real-time bytes do not belong in a file; stop rather than guess.
                break;
            }
            else
            {
                runningStatus = status;
                offset += (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            }
        }

        return tick;
    }

    private static void AddMetaEvent(
        byte type,
        ReadOnlySpan<byte> payload,
        long tick,
        int track,
        List<(MidiMarkKind Kind, MidiMark Mark)> events,
        List<(MidiTempoEvent Tempo, int Track)> tempos)
    {
        switch (type)
        {
            case 0x01:
                events.Add((MidiMarkKind.Text, new MidiMark(tick, track, DecodeText(payload))));
                break;
            case 0x02:
                events.Add((MidiMarkKind.Copyright, new MidiMark(tick, track, DecodeText(payload))));
                break;
            case 0x03:
                events.Add((MidiMarkKind.TrackName, new MidiMark(tick, track, DecodeText(payload))));
                break;
            case 0x05:
                events.Add((MidiMarkKind.Lyric, new MidiMark(tick, track, DecodeText(payload))));
                break;
            case 0x51 when payload.Length >= 3:
                tempos.Add((new MidiTempoEvent(tick, (payload[0] << 16) | (payload[1] << 8) | payload[2]), track));
                break;
            case 0x58 when payload.Length >= 4:
                // Same text as BASS: "numerator/denominator metronome-pulse 32nds-per-quarter".
                var timeSignature = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{payload[0]}/{1 << Math.Min((int)payload[1], 30)} {payload[2]} {payload[3]}");
                events.Add((MidiMarkKind.TimeSignature, new MidiMark(tick, track, timeSignature)));
                break;
            case 0x59 when payload.Length >= 2:
                // Same text as BASS: "sharps(+)/flats(-) minor(1)/major(0)".
                var keySignature = string.Create(CultureInfo.InvariantCulture, $"{(sbyte)payload[0]} {payload[1]}");
                events.Add((MidiMarkKind.KeySignature, new MidiMark(tick, track, keySignature)));
                break;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> payload)
    {
        try
        {
            return StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(payload);
        }
    }

    private static bool TryReadVariableLength(ReadOnlySpan<byte> data, ref int offset, out long value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            if (offset >= data.Length)
                return false;

            var b = data[offset++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return true;
        }

        return false;
    }
}
