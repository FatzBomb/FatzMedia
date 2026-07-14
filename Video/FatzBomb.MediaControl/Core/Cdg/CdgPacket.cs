namespace FatzBomb.MediaControl.Core.Cdg;

/// <summary>
/// Represents a CDG packet with command and instruction data.
/// This is the basic unit of CDG processing.
/// </summary>
internal readonly struct CdgPacket
{
    private readonly ReadOnlyMemory<byte> _data;

    public CdgPacket(ReadOnlySpan<byte> data)
    {
        _data = data.ToArray();
        Command = (CdgConstants.Command)(data[0] & 0x3F);
        Instruction = (CdgConstants.Instruction)(data[1] & 0x3F);
    }

    public CdgConstants.Command Command { get; }
    public CdgConstants.Instruction Instruction { get; }
    public ReadOnlySpan<byte> DataSpan => _data.Span.Slice(4, 16);
}
