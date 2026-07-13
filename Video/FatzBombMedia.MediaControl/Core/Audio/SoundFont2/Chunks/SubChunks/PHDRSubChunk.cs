using Kermalis.EndianBinaryIO;

namespace Kermalis.SoundFont2;

public sealed class PHDRSubChunk : SF2Chunk
{
    private readonly List<SF2PresetHeader> _presets = [];
    public uint Count => (uint)_presets.Count;

    internal PHDRSubChunk(SF2 inSf2) : base(inSf2, "phdr") { }
    internal PHDRSubChunk(SF2 inSf2, EndianBinaryReader reader) : base(inSf2, reader)
    {
        for (var i = 0; i < Size / SF2PresetHeader.SIZE; i++)
        {
            _presets.Add(new SF2PresetHeader(reader));
        }
    }

    internal void AddPreset(SF2PresetHeader preset)
    {
        _presets.Add(preset);
        Size = Count * SF2PresetHeader.SIZE;
        _sf2.UpdateSize();
    }

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        for (var i = 0; i < Count; i++)
        {
            _presets[i].Write(writer);
        }
    }

    public override string ToString() => $"Preset Header Chunk - Preset count = {Count}";
}
