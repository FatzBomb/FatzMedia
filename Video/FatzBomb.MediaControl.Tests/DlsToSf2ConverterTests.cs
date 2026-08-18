using FatzBomb.MediaControl.Core.Audio;
using System.Text;

namespace FatzBomb.MediaControl.Tests;

public sealed class DlsToSf2ConverterTests
{
    public static TheoryData<byte[], string> InvalidDlsFiles => new()
    {
        { CreateHeader("NOPE", "DLS "), "RIFF header not found." },
        { CreateHeader("RIFF", "XXXX"), "DLS type not found." },
        { CreateHeader("RIFF", "DLS "), "Required DLS chunks (lins, wvpl) not found." }
    };

    [Theory]
    [MemberData(nameof(InvalidDlsFiles))]
    public void ConvertAndSave_RejectsMalformedDls(byte[] contents, string expectedMessage)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "FatzBombMedia.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(tempDirectory);
        var sourcePath = Path.Combine(tempDirectory, "input.dls");
        var outputPath = Path.Combine(tempDirectory, "output.sf2");

        try
        {
            File.WriteAllBytes(sourcePath, contents);

            var exception = Assert.Throws<InvalidDataException>(() =>
                DlsToSf2Converter.ConvertAndSave(sourcePath, outputPath));

            Assert.Equal(expectedMessage, exception.Message);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ConvertAndSave_ConvertsMinimalDlsToSf2()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "FatzBombMedia.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(tempDirectory);
        var sourcePath = Path.Combine(tempDirectory, "minimal.dls");
        var outputPath = Path.Combine(tempDirectory, "minimal.sf2");

        try
        {
            File.WriteAllBytes(sourcePath, CreateMinimalDls());

            string? result = DlsToSf2Converter.ConvertAndSave(sourcePath, outputPath);

            Assert.Equal(outputPath, result);
            byte[] sf2 = File.ReadAllBytes(outputPath);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(sf2, 0, 4));
            Assert.Equal(sf2.Length - 8, (int)BitConverter.ToUInt32(sf2, 4));
            Assert.Equal("sfbk", Encoding.ASCII.GetString(sf2, 8, 4));

            int phdr = FindChunk(sf2, "phdr") + 8;
            Assert.Equal("Inst0", ReadPaddedString(sf2, phdr, 20));
            Assert.Equal((ushort)7, BitConverter.ToUInt16(sf2, phdr + 20));
            Assert.Equal((ushort)5, BitConverter.ToUInt16(sf2, phdr + 22));

            int igen = FindChunk(sf2, "igen") + 8;
            Assert.Equal((ushort)43, BitConverter.ToUInt16(sf2, igen));
            Assert.Equal((short)((100 << 8) | 10), BitConverter.ToInt16(sf2, igen + 2));
            Assert.Equal((ushort)44, BitConverter.ToUInt16(sf2, igen + 4));
            Assert.Equal((short)((110 << 8) | 20), BitConverter.ToInt16(sf2, igen + 6));

            int shdr = FindChunk(sf2, "shdr") + 8;
            Assert.Equal("Sample0", ReadPaddedString(sf2, shdr, 20));
            Assert.Equal(44100u, BitConverter.ToUInt32(sf2, shdr + 36));
            Assert.Equal((byte)60, sf2[shdr + 40]);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static byte[] CreateHeader(string container, string type)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(container));
        writer.Write(4u);
        writer.Write(Encoding.ASCII.GetBytes(type));
        return stream.ToArray();
    }

    private static byte[] CreateMinimalDls()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteChunk(writer, "RIFF", riff =>
        {
            WriteFourCc(riff, "DLS ");
            WriteList(riff, "lins", lins =>
                WriteList(lins, "ins ", instrument =>
                {
                    WriteChunk(instrument, "insh", chunk =>
                    {
                        chunk.Write(1u);
                        chunk.Write(5u << 8);
                        chunk.Write(7u);
                    });
                    WriteList(instrument, "lrgn", regions =>
                        WriteList(regions, "rgn ", region =>
                        {
                            WriteChunk(region, "rgnh", chunk =>
                            {
                                chunk.Write((ushort)10);
                                chunk.Write((ushort)100);
                                chunk.Write((ushort)20);
                                chunk.Write((ushort)110);
                                chunk.Write((ushort)0);
                                chunk.Write((ushort)0);
                            });
                            WriteChunk(region, "wlnk", chunk =>
                            {
                                chunk.Write((ushort)0);
                                chunk.Write((ushort)0);
                                chunk.Write(1u);
                                chunk.Write(0u);
                            });
                            WriteWaveSampleChunk(region);
                        }));
                }));
            WriteList(riff, "wvpl", wavePool =>
                WriteList(wavePool, "wave", wave =>
                {
                    WriteChunk(wave, "fmt ", chunk =>
                    {
                        chunk.Write((ushort)1);
                        chunk.Write((ushort)1);
                        chunk.Write(44100u);
                        chunk.Write(88200u);
                        chunk.Write((ushort)2);
                        chunk.Write((ushort)16);
                    });
                    WriteChunk(wave, "data", chunk =>
                    {
                        chunk.Write((short)1);
                        chunk.Write((short)2);
                        chunk.Write((short)-2);
                        chunk.Write((short)0);
                    });
                    WriteWaveSampleChunk(wave);
                }));
        });
        return stream.ToArray();
    }

    private static void WriteWaveSampleChunk(BinaryWriter writer) =>
        WriteChunk(writer, "wsmp", chunk =>
        {
            chunk.Write(20u);
            chunk.Write((ushort)60);
            chunk.Write((short)0);
            chunk.Write(0);
            chunk.Write(0u);
            chunk.Write(0u);
        });

    private static void WriteList(BinaryWriter writer, string type, Action<BinaryWriter> writeContents) =>
        WriteChunk(writer, "LIST", chunk =>
        {
            WriteFourCc(chunk, type);
            writeContents(chunk);
        });

    private static void WriteChunk(BinaryWriter writer, string name, Action<BinaryWriter> writeContents)
    {
        WriteFourCc(writer, name);
        long sizePosition = writer.BaseStream.Position;
        writer.Write(0u);
        long contentStart = writer.BaseStream.Position;
        writeContents(writer);
        long contentEnd = writer.BaseStream.Position;
        uint size = checked((uint)(contentEnd - contentStart));
        writer.BaseStream.Position = sizePosition;
        writer.Write(size);
        writer.BaseStream.Position = contentEnd;
        if ((size & 1) != 0)
            writer.Write((byte)0);
    }

    private static void WriteFourCc(BinaryWriter writer, string value) =>
        writer.Write(Encoding.ASCII.GetBytes(value));

    private static int FindChunk(byte[] contents, string name)
    {
        int index = contents.AsSpan().IndexOf(Encoding.ASCII.GetBytes(name));
        Assert.True(index >= 0, $"Chunk '{name}' was not found.");
        return index;
    }

    private static string ReadPaddedString(byte[] contents, int offset, int length)
    {
        string value = Encoding.ASCII.GetString(contents, offset, length);
        int terminator = value.IndexOf('\0');
        return terminator >= 0 ? value[..terminator] : value;
    }
}
