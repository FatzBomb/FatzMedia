using Kermalis.EndianBinaryIO;
using System.Collections;

namespace Kermalis.DLS2;

// Pool Table Chunk - Page 54 of spec
public sealed class PoolTableChunk : DLSChunk, IReadOnlyList<uint>
{
    internal const string EXPECTED_NAME = "ptbl";

    internal uint[] PoolCues;

    public uint this[int index] => PoolCues[index];
    public int Count => PoolCues.Length;

    internal PoolTableChunk() : base(EXPECTED_NAME)
    {
        PoolCues = [];

        UpdateSize();
    }
    internal PoolTableChunk(EndianBinaryReader reader) : base(EXPECTED_NAME, reader)
    {
        var endOffset = GetEndOffset(reader);
        var byteSize = reader.ReadUInt32();
        if (byteSize != 8)
        {
            throw new InvalidDataException($"PoolTableChunk byteSize was not 8 bytes ({byteSize})");
        }

        PoolCues = new uint[reader.ReadUInt32()];
        reader.ReadUInt32s(PoolCues);
        EatRemainingBytes(reader, endOffset);
    }

    internal override void UpdateSize() => Size = 4 // byteSize
            + 4 // numCues
            + (uint)(4u * PoolCues.Length); // _poolCues

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        writer.WriteUInt32(8);
        writer.WriteUInt32((uint)PoolCues.Length);
        writer.WriteUInt32s(PoolCues);
    }

    public IEnumerator<uint> GetEnumerator() => ((IEnumerable<uint>)PoolCues).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => PoolCues.GetEnumerator();
}
