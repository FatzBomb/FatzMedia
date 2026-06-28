namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Represents a CDG packet with command and instruction data.
/// This is the basic unit of CDG processing.
/// </summary>
internal readonly struct DxCdgPacket
{
    private readonly ReadOnlyMemory<byte> _data;
    
    public DxCdgPacket(ReadOnlySpan<byte> data)
    {
        _data = data.ToArray();
        Command = (DxCdgConstants.Command)(data[0] & 0x3F);
        Instruction = (DxCdgConstants.Instruction)(data[1] & 0x3F);
    }
    
    public DxCdgConstants.Command Command { get; }
    public DxCdgConstants.Instruction Instruction { get; }
    public ReadOnlySpan<byte> DataSpan => _data.Span.Slice(4, 16);
}