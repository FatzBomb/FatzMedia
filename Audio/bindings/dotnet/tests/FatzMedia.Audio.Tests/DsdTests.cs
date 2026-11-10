using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace FatzMedia.Audio.Tests;

/// <summary>DSD files (DSF, DSDIFF) through the binding: the output rate option and the DSDIFF tag format.</summary>
public class DsdTests
{
    const int DsdRate = 2822400;   // DSD64

    /// <summary>A stereo DSF of one block group (4096 bytes a channel) of the DSD idle pattern.</summary>
    static byte[] Dsf()
    {
        var b = new byte[92 + 2 * 4096];
        Encoding.ASCII.GetBytes("DSD ").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), (ulong)b.Length);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(b, 28);
        var f = b.AsSpan(32);
        BinaryPrimitives.WriteUInt64LittleEndian(f, 52);
        BinaryPrimitives.WriteUInt32LittleEndian(f[8..], 1);          // version
        BinaryPrimitives.WriteUInt32LittleEndian(f[16..], 2);         // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(f[20..], 2);         // channels
        BinaryPrimitives.WriteUInt32LittleEndian(f[24..], DsdRate);
        BinaryPrimitives.WriteUInt32LittleEndian(f[28..], 1);         // LSB first
        BinaryPrimitives.WriteUInt64LittleEndian(f[32..], 4096 * 8);  // samples a channel
        BinaryPrimitives.WriteUInt32LittleEndian(f[40..], 4096);      // block size
        Encoding.ASCII.GetBytes("data").CopyTo(b, 80);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(84), 12 + 2 * 4096);
        b.AsSpan(92).Fill(0x96);
        return b;
    }

    static byte[] Chunk(string id, byte[] body)
    {
        var c = new byte[12 + body.Length + (body.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(c, 0);
        BinaryPrimitives.WriteUInt64BigEndian(c.AsSpan(4), (ulong)body.Length);
        body.CopyTo(c, 12);
        return c;
    }

    static byte[] Text(string s)
    {
        var t = Encoding.ASCII.GetBytes(s);
        var b = new byte[4 + t.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)t.Length);
        t.CopyTo(b, 4);
        return b;
    }

    /// <summary>A stereo DSDIFF with a title and an artist in its DIIN chunk.</summary>
    static byte[] Dff()
    {
        var fs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(fs, DsdRate);
        var chnl = new byte[] { 0, 2 }.Concat(Encoding.ASCII.GetBytes("SLFTSRGT")).ToArray();
        var prop = Encoding.ASCII.GetBytes("SND ").Concat(Chunk("FS  ", fs)).Concat(Chunk("CHNL", chnl))
            .Concat(Chunk("CMPR", Encoding.ASCII.GetBytes("DSD \x0enot compressed"))).ToArray();
        var audio = new byte[2 * 4000];
        Array.Fill(audio, (byte)0x69);
        var diin = Chunk("DITI", Text("Master Title")).Concat(Chunk("DIAR", Text("Master Artist"))).ToArray();
        var form = Encoding.ASCII.GetBytes("DSD ").Concat(Chunk("FVER", [1, 5, 0, 0])).Concat(Chunk("PROP", prop))
            .Concat(Chunk("DSD ", audio)).Concat(Chunk("DIIN", diin)).ToArray();
        return Chunk("FRM8", form);
    }

    [Fact]
    public void Dsf_DefaultAndChosenRate()
    {
        using var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true });
        using (var s = engine.OpenMemory(Dsf(), new StreamOptions { DecodeOnly = true }))
        {
            Assert.Equal("dsd", s.Info.Codec);
            Assert.Equal("dsf", s.Info.Container);
            Assert.Equal(88200, s.Info.Format.SampleRate);
            Assert.Equal(4096 / 4, s.Info.LengthFrames);
            Assert.Equal(4096 / 4, TestAudio.ReadAll(s));
        }
        using (var s = engine.OpenMemory(Dsf(), new StreamOptions { DecodeOnly = true, DsdRate = 176400 }))
        {
            Assert.Equal(176400, s.Info.Format.SampleRate);
            Assert.Equal(4096 / 2, s.Info.LengthFrames);
        }
    }

    [Fact]
    public void Dff_TitleAndArtist()
    {
        var tags = Tags.Read(Dff());
        Assert.Equal("Master Title", tags.Title);
        Assert.Equal("Master Artist", tags.Artist);
        Assert.Contains(tags.Items, i => i.Key == "DITI" && i.Format == TagFormat.Dsdiff);
        Assert.Equal("dff", tags.Info?.Container);
        Assert.Equal(88200, tags.SampleRate);
    }
}
