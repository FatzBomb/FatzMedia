using Kermalis.EndianBinaryIO;

namespace Kermalis.DLS2;

public abstract class DLSChunk
{
    /// <summary>Length 4</summary>
    public string ChunkName { get; }
    /// <summary>Size in bytes. Only accurate after <see cref="UpdateSize"/>.</summary>
    public uint Size { get; protected set; }

    protected DLSChunk(string chunkName) => ChunkName = chunkName;
    protected DLSChunk(string chunkName, EndianBinaryReader reader)
    {
        ChunkName = chunkName;
        Size = reader.ReadUInt32();
    }

    protected long GetEndOffset(EndianBinaryReader reader)
    {
        var end = reader.Stream.Position + Size;
        if (Size % 2 != 0)
            end++;
        return end;
    }
    protected void EatRemainingBytes(EndianBinaryReader reader, long endOffset)
    {
        if (reader.Stream.Position > endOffset)
        {
            throw new InvalidDataException($"Chunk was too short ({ChunkName} = {Size})");
        }
        reader.Stream.Position = endOffset;
    }

    internal abstract void UpdateSize();

    internal virtual void Write(EndianBinaryWriter writer)
    {
        UpdateSize();
        writer.WriteChars_Count(ChunkName, 4);
        writer.WriteUInt32(Size);
    }

    internal static List<DLSChunk> GetAllChunks(EndianBinaryReader reader, long endOffset)
    {
        var chunks = new List<DLSChunk>();
        while (reader.Stream.Position < endOffset)
            chunks.Add(SwitchNextChunk(reader));
        return chunks;
    }
    private static DLSChunk SwitchNextChunk(EndianBinaryReader reader)
    {
        var chunkName = reader.ReadString_Count(4);
        return chunkName switch
        {
            Level1ArticulatorChunk.EXPECTED_NAME => new Level1ArticulatorChunk(reader),
            Level2ArticulatorChunk.EXPECTED_NAME => new Level2ArticulatorChunk(reader),
            CollectionHeaderChunk.EXPECTED_NAME => new CollectionHeaderChunk(reader),
            DataChunk.EXPECTED_NAME => new DataChunk(reader),
            DLSIDChunk.EXPECTED_NAME => new DLSIDChunk(reader),
            FormatChunk.EXPECTED_NAME => new FormatChunk(reader),
            InstrumentHeaderChunk.EXPECTED_NAME => new InstrumentHeaderChunk(reader),
            ListChunk.EXPECTED_NAME => new ListChunk(reader),
            PoolTableChunk.EXPECTED_NAME => new PoolTableChunk(reader),
            RegionHeaderChunk.EXPECTED_NAME => new RegionHeaderChunk(reader),
            WaveLinkChunk.EXPECTED_NAME => new WaveLinkChunk(reader),
            WaveSampleChunk.EXPECTED_NAME => new WaveSampleChunk(reader),
            // InfoSubChunks
            "IARL" or "IART" or "ICMS" or "ICMD" or "ICOP" or "ICRD" or "IENG" or "IGNR" or "IKEY" or "IMED" or "INAM" or "IPRD" or "ISBJ" or "ISFT" or "ISRC" or "ISRF" or "ITCH" => new InfoSubChunk(chunkName, reader),
            _ => new UnsupportedChunk(chunkName, reader),
        };
    }
}
