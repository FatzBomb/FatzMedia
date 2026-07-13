using Kermalis.EndianBinaryIO;

namespace Kermalis.DLS2;

// Format Chunk - Page 57 of spec
public sealed class FormatChunk : DLSChunk
{
    internal const string EXPECTED_NAME = "fmt ";

    public WaveInfo WaveInfo { get; }
    public FormatInfo FormatInfo { get; }

    public FormatChunk(WaveFormat format) : base(EXPECTED_NAME)
    {
        WaveInfo = new WaveInfo { FormatTag = format };
        FormatInfo = format == WaveFormat.Extensible ? new ExtensibleInfo() : new PCMInfo();

        UpdateSize();
    }
    internal FormatChunk(EndianBinaryReader reader) : base(EXPECTED_NAME, reader)
    {
        var endOffset = GetEndOffset(reader);
        WaveInfo = new WaveInfo(reader);
        FormatInfo = WaveInfo.FormatTag == WaveFormat.Extensible ? new ExtensibleInfo(reader) : new PCMInfo(reader);
        EatRemainingBytes(reader, endOffset);
    }

    internal override void UpdateSize() => Size = 14 // WaveFormat
            + (WaveInfo.FormatTag == WaveFormat.Extensible ? 26u : 2u); // FormatInfo

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        WaveInfo.Write(writer);
        FormatInfo.Write(writer);
    }
}
