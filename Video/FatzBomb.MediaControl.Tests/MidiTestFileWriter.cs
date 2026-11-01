using System.Text;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Writes small Standard MIDI Files for tests (no MIDI fixtures are checked in).</summary>
internal sealed class MidiTestFileWriter(int ticksPerQuarterNote = 480)
{
    private readonly List<List<(long Tick, byte[] Bytes)>> _tracks = [];

    public MidiTestFileWriter Track(Action<TrackBuilder> build)
    {
        var builder = new TrackBuilder();
        build(builder);
        _tracks.Add(builder.Events);
        return this;
    }

    public byte[] ToArray(int format = 1)
    {
        using var output = new MemoryStream();
        output.Write("MThd"u8);
        WriteBigEndian(output, 6, 4);
        WriteBigEndian(output, format, 2);
        WriteBigEndian(output, _tracks.Count, 2);
        WriteBigEndian(output, ticksPerQuarterNote, 2);
        foreach (List<(long Tick, byte[] Bytes)> track in _tracks)
        {
            using var body = new MemoryStream();
            long previous = 0;
            foreach ((long tick, byte[] bytes) in track.OrderBy(static e => e.Tick))
            {
                WriteVariableLength(body, tick - previous);
                body.Write(bytes);
                previous = tick;
            }

            WriteVariableLength(body, 0);
            body.Write([0xFF, 0x2F, 0x00]);
            output.Write("MTrk"u8);
            WriteBigEndian(output, (int)body.Length, 4);
            body.Position = 0;
            body.CopyTo(output);
        }

        return output.ToArray();
    }

    /// <summary>Wraps SMF bytes in a RIFF RMID container (.rmi).</summary>
    public static byte[] WrapRmid(byte[] smf)
    {
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        output.Write(BitConverter.GetBytes(4 + 8 + smf.Length + (smf.Length & 1)));
        output.Write("RMID"u8);
        output.Write("data"u8);
        output.Write(BitConverter.GetBytes(smf.Length));
        output.Write(smf);
        if ((smf.Length & 1) != 0)
            output.WriteByte(0);
        return output.ToArray();
    }

    private static void WriteBigEndian(Stream stream, int value, int bytes)
    {
        for (var i = bytes - 1; i >= 0; i--)
            stream.WriteByte((byte)(value >> (8 * i)));
    }

    private static void WriteVariableLength(Stream stream, long value)
    {
        var buffer = new Stack<byte>();
        buffer.Push((byte)(value & 0x7F));
        while ((value >>= 7) > 0)
            buffer.Push((byte)((value & 0x7F) | 0x80));
        foreach (var b in buffer)
            stream.WriteByte(b);
    }

    internal sealed class TrackBuilder
    {
        public List<(long Tick, byte[] Bytes)> Events { get; } = [];

        public TrackBuilder Meta(long tick, byte type, byte[] data)
        {
            using var bytes = new MemoryStream();
            bytes.WriteByte(0xFF);
            bytes.WriteByte(type);
            WriteVariableLength(bytes, data.Length);
            bytes.Write(data);
            Events.Add((tick, bytes.ToArray()));
            return this;
        }

        public TrackBuilder Text(long tick, string text) => Meta(tick, 0x01, Encoding.UTF8.GetBytes(text));

        public TrackBuilder Copyright(long tick, string text) => Meta(tick, 0x02, Encoding.UTF8.GetBytes(text));

        public TrackBuilder Name(long tick, string text) => Meta(tick, 0x03, Encoding.UTF8.GetBytes(text));

        public TrackBuilder Lyric(long tick, string text) => Meta(tick, 0x05, Encoding.UTF8.GetBytes(text));

        public TrackBuilder Tempo(long tick, int microsecondsPerQuarterNote) =>
            Meta(tick, 0x51, [(byte)(microsecondsPerQuarterNote >> 16), (byte)(microsecondsPerQuarterNote >> 8), (byte)microsecondsPerQuarterNote]);

        public TrackBuilder TimeSignature(long tick, byte numerator, byte denominatorPower, byte clocks = 24, byte thirtySeconds = 8) =>
            Meta(tick, 0x58, [numerator, denominatorPower, clocks, thirtySeconds]);

        public TrackBuilder KeySignature(long tick, sbyte accidentals, byte minor) =>
            Meta(tick, 0x59, [(byte)accidentals, minor]);

        /// <summary>A note on and off with running status for the off (note on, velocity 0).</summary>
        public TrackBuilder Note(long tick, long length, byte note = 60)
        {
            Events.Add((tick, [0x90, note, 100]));
            Events.Add((tick + length, [note, 0]));
            return this;
        }

        public TrackBuilder SysEx(long tick) => Add(tick, [0xF0, 0x03, 0x7E, 0x09, 0xF7]);

        private TrackBuilder Add(long tick, byte[] bytes)
        {
            Events.Add((tick, bytes));
            return this;
        }
    }
}
