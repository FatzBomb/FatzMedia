using Kermalis.EndianBinaryIO;

namespace Kermalis.SoundFont2;

public sealed class BAGSubChunk : SF2Chunk
{
    private readonly List<SF2Bag> _bags = [];
    public uint Count => (uint)_bags.Count;

    internal BAGSubChunk(SF2 inSf2, bool isPreset) : base(inSf2, isPreset ? "pbag" : "ibag") { }
    internal BAGSubChunk(SF2 inSf2, EndianBinaryReader reader) : base(inSf2, reader)
    {
        for (var i = 0; i < Size / SF2Bag.SIZE; i++)
        {
            _bags.Add(new SF2Bag(reader));
        }
    }

    internal void AddBag(SF2Bag bag)
    {
        _bags.Add(bag);
        Size = Count * SF2Bag.SIZE;
        _sf2.UpdateSize();
    }

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        for (var i = 0; i < Count; i++)
        {
            _bags[i].Write(writer);
        }
    }

    public override string ToString() => $"Bag Chunk - Name = \"{ChunkName}\"" +
            $",\nBag count = {Count}";
}
