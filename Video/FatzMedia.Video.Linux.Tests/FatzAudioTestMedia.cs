using System.Text;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Writes the small files the FatzAudio engine tests play (037A slice 2): PCM WAVs with tone and
/// silence sections, a one-instrument SF2 soundfont (this machine has no GM bank, and FatzAudio needs
/// one to open MIDI) and a Soft Karaoke .kar.
/// </summary>
internal static class FatzAudioTestMedia
{
    public const int SampleRate = 44100;

    /// <summary>One section of a test WAV: a sine at <paramref name="Hz"/> and <paramref name="Amplitude"/> (0 = silence).</summary>
    public readonly record struct Section(double Seconds, double Hz = 440, double Amplitude = 0.5);

    /// <summary>Writes a 16-bit stereo 44.1 kHz WAV made of <paramref name="sections"/>.</summary>
    public static string WriteWav(string directory, string name, params Section[] sections)
    {
        var path = Path.Combine(directory, name);
        var frames = sections.Sum(static section => (int)Math.Round(section.Seconds * SampleRate));
        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = frames * 2 * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2 * sizeof(short));
        writer.Write((short)(2 * sizeof(short)));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (Section section in sections)
        {
            var count = (int)Math.Round(section.Seconds * SampleRate);
            for (var i = 0; i < count; i++)
            {
                var sample = (short)Math.Round(section.Amplitude * short.MaxValue * Math.Sin(2 * Math.PI * section.Hz * i / SampleRate));
                writer.Write(sample);
                writer.Write(sample);
            }
        }

        return path;
    }

    /// <summary>
    /// Writes a minimal SF2: one preset (bank 0, program 0) whose one instrument plays a looped
    /// 441 Hz sine sample over every key.
    /// </summary>
    public static string WriteSoundFont(string directory)
    {
        var path = Path.Combine(directory, "test.sf2");
        const int sampleCount = 4400; // 44 whole cycles of 441 Hz at 44.1 kHz, so the loop is seamless
        var smpl = new MemoryStream();
        using (var w = new BinaryWriter(smpl, Encoding.ASCII, leaveOpen: true))
        {
            for (var i = 0; i < sampleCount; i++)
                w.Write((short)Math.Round(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * 441 * i / SampleRate)));
            for (var i = 0; i < 46; i++)
                w.Write((short)0); // the zero guard the spec asks for after each sample
        }

        byte[] info = List("INFO",
            Chunk("ifil", Bytes(w => { w.Write((ushort)2); w.Write((ushort)1); })),
            Chunk("isng", Ascii("EMU8000", 8)),
            Chunk("INAM", Ascii("Test", 6)));
        byte[] sdta = List("sdta", Chunk("smpl", smpl.ToArray()));
        byte[] pdta = List("pdta",
            Chunk("phdr", Bytes(w =>
            {
                PresetHeader(w, "Sine", preset: 0, bank: 0, bag: 0);
                PresetHeader(w, "EOP", preset: 0, bank: 0, bag: 1);
            })),
            Chunk("pbag", Bytes(w => { Bag(w, 0); Bag(w, 1); })),
            Chunk("pmod", new byte[10]),
            Chunk("pgen", Bytes(w => { Gen(w, 41, 0); Gen(w, 0, 0); })), // instrument 0
            Chunk("inst", Bytes(w =>
            {
                w.Write(Ascii("Sine", 20));
                w.Write((ushort)0);
                w.Write(Ascii("EOI", 20));
                w.Write((ushort)1);
            })),
            Chunk("ibag", Bytes(w => { Bag(w, 0); Bag(w, 2); })),
            Chunk("imod", new byte[10]),
            Chunk("igen", Bytes(w => { Gen(w, 54, 1); Gen(w, 53, 0); Gen(w, 0, 0); })), // looped, sample 0
            Chunk("shdr", Bytes(w =>
            {
                w.Write(Ascii("Sine", 20));
                w.Write(0u);
                w.Write((uint)sampleCount);
                w.Write(0u);
                w.Write((uint)sampleCount);
                w.Write((uint)SampleRate);
                w.Write((byte)69);
                w.Write((sbyte)0);
                w.Write((ushort)0);
                w.Write((ushort)1); // mono
                w.Write(new byte[46]); // EOS
            })));

        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(4 + info.Length + sdta.Length + pdta.Length);
        file.Write("sfbk"u8);
        file.Write(info);
        file.Write(sdta);
        file.Write(pdta);
        return path;

        static void PresetHeader(BinaryWriter w, string name, ushort preset, ushort bank, ushort bag)
        {
            w.Write(Ascii(name, 20));
            w.Write(preset);
            w.Write(bank);
            w.Write(bag);
            w.Write(0u);
            w.Write(0u);
            w.Write(0u);
        }

        static void Bag(BinaryWriter w, ushort generator)
        {
            w.Write(generator);
            w.Write((ushort)0);
        }

        static void Gen(BinaryWriter w, ushort oper, ushort amount)
        {
            w.Write(oper);
            w.Write(amount);
        }
    }

    /// <summary>The lines <see cref="WriteKaraoke"/> sings, in order.</summary>
    public static readonly string[] KaraokeLines = ["Hello world", "Second line", "Third page"];

    /// <summary>
    /// Writes a format 1 Soft Karaoke file at 120 bpm: a tempo, 3/4 time and E minor in the
    /// conductor track, the "@T" title lines and the words in a "Words" track (<c>\</c> starts a
    /// page, <c>/</c> a line), one syllable every half second from 2 s, and a note per syllable.
    /// </summary>
    public static string WriteKaraoke(string directory)
    {
        var path = Path.Combine(directory, "test.kar");
        (string Text, int Tick)[] words =
        [
            ("\\Hel", 0), ("lo ", 1), ("world", 2),
            ("/Sec", 3), ("ond ", 4), ("line", 5),
            ("\\Third ", 6), ("page", 7),
        ];
        const int division = 480;
        const int start = 4 * division;

        byte[] conductor = Track(
            Meta(0, 0x03, Encoding.ASCII.GetBytes("Test Song")),
            Meta(0, 0x51, [0x07, 0xA1, 0x20]), // 500000 us per quarter: 120 bpm
            Meta(0, 0x58, [3, 2, 24, 8]),
            Meta(0, 0x59, [1, 1])); // one sharp, minor: E minor

        List<byte[]> wordEvents =
        [
            Meta(0, 0x03, Encoding.ASCII.GetBytes("Words")),
            Meta(0, 0x01, Encoding.ASCII.GetBytes("@KMIDI KARAOKE FILE")),
            Meta(0, 0x01, Encoding.ASCII.GetBytes("@TTest Title")),
            Meta(0, 0x01, Encoding.ASCII.GetBytes("@TTest Artist")),
        ];
        var previous = 0;
        foreach ((var text, var index) in words)
        {
            var tick = start + (index * division);
            wordEvents.Add(Meta(tick - previous, 0x01, Encoding.ASCII.GetBytes(text)));
            previous = tick;
        }

        List<byte[]> noteEvents = [Event(0, 0xC0, 0)];
        previous = 0;
        for (var i = 0; i < words.Length; i++)
        {
            var on = start + (i * division);
            noteEvents.Add(Event(on - previous, 0x90, 60 + i, 100));
            noteEvents.Add(Event(division / 2, 0x80, 60 + i, 0));
            previous = on + (division / 2);
        }

        using var file = new BinaryWriter(File.Create(path));
        file.Write("MThd"u8);
        WriteBigEndian(file, 6);
        WriteBigEndian16(file, 1);
        WriteBigEndian16(file, 3);
        WriteBigEndian16(file, division);
        file.Write(conductor);
        file.Write(Track([.. wordEvents]));
        file.Write(Track([.. noteEvents]));
        return path;
    }

    private static byte[] Track(params byte[][] events)
    {
        var body = events.SelectMany(static e => e).Concat(new byte[] { 0x00, 0xFF, 0x2F, 0x00 }).ToArray();
        var track = new MemoryStream();
        using var w = new BinaryWriter(track);
        w.Write("MTrk"u8);
        WriteBigEndian(w, body.Length);
        w.Write(body);
        return track.ToArray();
    }

    private static byte[] Meta(int delta, byte type, byte[] data) =>
        [.. VariableLength(delta), 0xFF, type, .. VariableLength(data.Length), .. data];

    private static byte[] Event(int delta, byte status, params int[] data) =>
        [.. VariableLength(delta), status, .. data.Select(static value => (byte)value)];

    private static byte[] VariableLength(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7F) };
        while ((value >>= 7) > 0)
            bytes.Insert(0, (byte)((value & 0x7F) | 0x80));
        return [.. bytes];
    }

    private static void WriteBigEndian(BinaryWriter w, int value)
    {
        w.Write((byte)(value >> 24));
        w.Write((byte)(value >> 16));
        w.Write((byte)(value >> 8));
        w.Write((byte)value);
    }

    private static void WriteBigEndian16(BinaryWriter w, int value)
    {
        w.Write((byte)(value >> 8));
        w.Write((byte)value);
    }

    private static byte[] Chunk(string id, byte[] data)
    {
        var padded = data.Length % 2 == 0 ? data : [.. data, 0];
        return [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(data.Length), .. padded];
    }

    private static byte[] List(string type, params byte[][] chunks)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(static chunk => chunk)];
        return [.. "LIST"u8.ToArray(), .. BitConverter.GetBytes(body.Length), .. body];
    }

    private static byte[] Ascii(string text, int length)
    {
        var bytes = new byte[length];
        _ = Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, length - 1), bytes, 0);
        return bytes;
    }

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
            write(writer);
        return stream.ToArray();
    }
}
