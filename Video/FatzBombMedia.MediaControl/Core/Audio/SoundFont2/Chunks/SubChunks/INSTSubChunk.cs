using Kermalis.EndianBinaryIO;

namespace Kermalis.SoundFont2;

public sealed class INSTSubChunk : SF2Chunk
{
    private readonly List<SF2Instrument> _instruments = [];
    public uint Count => (uint)_instruments.Count;

    internal INSTSubChunk(SF2 inSf2) : base(inSf2, "inst") { }
    internal INSTSubChunk(SF2 inSf2, EndianBinaryReader reader) : base(inSf2, reader)
    {
        for (var i = 0; i < Size / SF2Instrument.SIZE; i++)
        {
            _instruments.Add(new SF2Instrument(reader));
        }
    }

    internal uint AddInstrument(SF2Instrument instrument)
    {
        _instruments.Add(instrument);
        Size = Count * SF2Instrument.SIZE;
        _sf2.UpdateSize();
        return Count - 1;
    }

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        for (var i = 0; i < Count; i++)
        {
            _instruments[i].Write(writer);
        }
    }

    public override string ToString() => $"Instrument Chunk - Instrument count = {Count}";
}
