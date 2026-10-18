using FatzBomb.MediaControl.Core.Cdg;

namespace FatzBomb.MediaControl.Tests;

public class CdgStateMachineTests
{
    private static byte[] MakePacket(byte instruction, ReadOnlySpan<byte> data)
    {
        var packet = new byte[CdgConstants.PacketSize];
        packet[0] = (byte)CdgConstants.Command.Graphic;
        packet[1] = instruction;
        for (var i = 0; i < data.Length && i < 16; i++)
            packet[4 + i] = data[i];
        return packet;
    }

    private static byte[] MakePacket(byte instruction, params byte[] data) => MakePacket(instruction, (ReadOnlySpan<byte>)data);

    private static byte[] GetColorIndices(CdgStateMachine sm)
    {
        var indices = new byte[CdgConstants.TotalPixels];
        sm.CopyColorIndices(indices);
        return indices;
    }

    [Fact]
    public void Constructor_ResetsToBlankFrameWithDefaultPalette()
    {
        var sm = new CdgStateMachine();

        Assert.Equal(CdgConstants.FrameWidth, sm.Width);
        Assert.Equal(CdgConstants.FrameHeight, sm.Height);
        Assert.True(sm.Revision > 0);
        Assert.Equal(0u, sm.BackgroundWashArgb);

        var indices = GetColorIndices(sm);
        Assert.All(indices, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Reset_ClearsPixelsAndBumpsRevision()
    {
        var sm = new CdgStateMachine();
        var revisionBefore = sm.Revision;

        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00));

        var revisionAfterFill = sm.Revision;
        Assert.True(revisionAfterFill > revisionBefore);

        sm.Reset();

        Assert.True(sm.Revision > revisionAfterFill);
        Assert.All(GetColorIndices(sm), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ProcessPacket_RejectsNonGraphicCommand()
    {
        var sm = new CdgStateMachine();
        var packet = new byte[CdgConstants.PacketSize];
        packet[0] = 0x00; // not Graphic (0x09)
        packet[1] = (byte)CdgConstants.Instruction.MemoryPreset;

        var changed = sm.ProcessPacket(packet);

        Assert.False(changed);
    }

    [Fact]
    public void ProcessPacket_RejectsUnknownInstruction()
    {
        var sm = new CdgStateMachine();

        var changed = sm.ProcessPacket(MakePacket(0x7F, 0, 0));

        Assert.False(changed);
    }

    [Fact]
    public void ProcessPacket_RejectsUndersizedPacket()
    {
        var sm = new CdgStateMachine();
        var tooSmall = new byte[CdgConstants.PacketSize - 1];

        var changed = sm.ProcessPacket(tooSmall);

        Assert.False(changed);
    }

    [Fact]
    public void MemoryPreset_FillsFrameWithColorAndBumpsRevision()
    {
        var sm = new CdgStateMachine();
        var revisionBefore = sm.Revision;

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00)); // color 5, repeat 0 (fill)

        Assert.True(changed);
        Assert.True(sm.Revision > revisionBefore);

        var indices = GetColorIndices(sm);
        Assert.All(indices, b => Assert.Equal(5, b));
    }

    [Fact]
    public void MemoryPreset_WithNonZeroRepeatDoesNotFillButStillReportsChanged()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x07, 0x00)); // fill with 7 first

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x03, 0x01)); // color 3, repeat != 0 → no fill

        Assert.True(changed); // current behavior: always true
        var indices = GetColorIndices(sm);
        Assert.All(indices, b => Assert.Equal(7, b)); // unchanged pixels
    }

    [Fact]
    public void MemoryPreset_WithSameColorStillReturnsTrue()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00)); // fill with 5

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00)); // fill with 5 again

        Assert.True(changed); // current behavior: always true even if no pixel changed
    }

    [Fact]
    public void BorderPreset_SetsBorderPixelsToColor()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x00, 0x00)); // fill with 0

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.BorderPreset,
            0x09, 0x00)); // border color 9

        Assert.True(changed);

        var indices = GetColorIndices(sm);
        // Top-left corner pixel should be border color
        Assert.Equal(9, indices[0]);
        // Center pixel should remain 0 (interior)
        var centerIndex = (CdgConstants.FrameHeight / 2 * CdgConstants.FrameWidth) + (CdgConstants.FrameWidth / 2);
        Assert.Equal(0, indices[centerIndex]);
    }

    [Fact]
    public void TileBlockNormal_WritesTilePixelsFromRowBits()
    {
        var sm = new CdgStateMachine();
        const byte color0 = 1;
        const byte color1 = 2;
        // All pixel bits set (6 bits per row, 12 rows) → every pixel = color1
        var data = new byte[16];
        data[0] = color0;
        data[1] = color1;
        data[2] = 0; // row 0
        data[3] = 0; // col 0
        for (var i = 0; i < 12; i++)
            data[4 + i] = 0x3F;

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.TileBlockNormal,
            data));

        Assert.True(changed);

        var indices = GetColorIndices(sm);
        // Tile is 6 wide × 12 tall at origin → all should be color1
        for (var row = 0; row < CdgConstants.TileHeight; row++)
            for (var col = 0; col < CdgConstants.TileWidth; col++)
                Assert.Equal(color1, indices[(row * CdgConstants.FrameWidth) + col]);
        // Pixel just outside the tile should be 0
        Assert.Equal(0, indices[CdgConstants.TileWidth]);
    }

    [Fact]
    public void TileBlockNormal_OutOfBoundsReturnsFalse()
    {
        var sm = new CdgStateMachine();
        // Column index 0x3F * 6 = 378 > 300 → out of bounds
        var data = new byte[] { 1, 2, 0x1F, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.TileBlockNormal,
            data));

        Assert.False(changed);
    }

    [Fact]
    public void TileBlockXor_XorsAgainstExistingPixels()
    {
        var sm = new CdgStateMachine();
        // Fill with color 3
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x03, 0x00));

        // Tile with color0=1, color1=2, all bits set → every pixel = color1 ^ 3 = 2 ^ 3 = 1
        var data = new byte[16];
        data[0] = 1;
        data[1] = 2;
        data[2] = 0;
        data[3] = 0;
        for (var i = 0; i < 12; i++)
            data[4 + i] = 0x3F;

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.TileBlockXor,
            data));

        Assert.True(changed);

        var indices = GetColorIndices(sm);
        Assert.Equal(1, indices[0]); // 2 ^ 3 = 1
    }

    [Fact]
    public void ScrollPreset_ShiftsRightAndFillsExposedArea()
    {
        var sm = new CdgStateMachine();
        // Fill frame with color 5
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00));

        // Scroll right by 6px (hCmd=1 → hPx=+6), color=3, copy=false
        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.ScrollPreset,
            0x03, 0x10, 0x00)); // color 3, h=0x10 (hCmd=1), v=0

        Assert.True(changed);

        var indices = GetColorIndices(sm);
        // Leftmost 6 columns should be fill color 3
        for (var row = 0; row < CdgConstants.FrameHeight; row++)
            Assert.Equal(3, indices[(row * CdgConstants.FrameWidth) + 0]);
        // Column 6 should be the old color 5 (shifted right)
        Assert.Equal(5, indices[(CdgConstants.FrameWidth * 0) + CdgConstants.TileWidth]);
    }

    [Fact]
    public void ScrollPreset_NoMovementReturnsFalse()
    {
        var sm = new CdgStateMachine();

        // hCmd=0, vCmd=0 → no scroll
        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.ScrollPreset,
            0x03, 0x00, 0x00));

        Assert.False(changed);
    }

    [Fact]
    public void LoadColorTableLower_UpdatesPaletteInSnapshot()
    {
        var sm = new CdgStateMachine();
        CdgRenderSnapshot snapshotBefore = sm.CreateSnapshot();
        var paletteBefore = snapshotBefore.PaletteArgb.ToArray();

        // 8 colors, 16 bytes. Each color: high byte (R[5:0] + G[1:0]), low byte (G[3:0] + B[3:0]).
        // Color 0: R=0x3C>>2=15, G=0, B=0 → (15*17, 0, 0) = (255,0,0)
        var colorData = new byte[16];
        colorData[0] = 0x3C; // R=0x3C (6-bit R=60→15), G high=0x00
        colorData[1] = 0x00; // G low=0x00, B=0x00

        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.LoadColorTableLower,
            colorData));

        CdgRenderSnapshot snapshotAfter = sm.CreateSnapshot();
        var paletteAfter = snapshotAfter.PaletteArgb.ToArray();

        Assert.NotEqual(paletteBefore[0], paletteAfter[0]);
        // ARGB: A=255, R=255, G=0, B=0 → 0xFFFF0000
        Assert.Equal(0xFFFF0000, (uint)paletteAfter[0]);
    }

    [Fact]
    public void DefineTransparentColor_ReportsChanged()
    {
        var sm = new CdgStateMachine();

        var changed = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.DefineTransparentColor,
            0x05));

        Assert.True(changed);
    }

    [Fact]
    public void CopyColorIndices_ThrowsWhenDestinationTooSmall()
    {
        var sm = new CdgStateMachine();
        var tooSmall = new byte[CdgConstants.TotalPixels - 1];

        _ = Assert.Throws<ArgumentException>(() => sm.CopyColorIndices(tooSmall));
    }

    [Fact]
    public void CreateSnapshot_DirtyRegionsAreRelativeToTheLastConsumingSnapshot()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket((byte)CdgConstants.Instruction.MemoryPreset, 0x05, 0x00));
        CdgRenderSnapshot first = sm.CreateSnapshot();
        Assert.Equal(-1, first.DirtyBaseRevision);

        _ = sm.ProcessPacket(MakePacket((byte)CdgConstants.Instruction.MemoryPreset, 0x06, 0x00));
        CdgRenderSnapshot peek = sm.CreateSnapshot(consumeDirtyRegions: false);
        _ = sm.ProcessPacket(MakePacket((byte)CdgConstants.Instruction.MemoryPreset, 0x07, 0x00));
        CdgRenderSnapshot second = sm.CreateSnapshot();

        Assert.Equal(first.Revision, peek.DirtyBaseRevision);
        Assert.Equal(first.Revision, second.DirtyBaseRevision);
        Assert.True(second.HasDirtyRegions);
    }

    [Fact]
    public void CreateSnapshot_ConsumesDirtyRegionsOnFirstCall()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00));

        CdgRenderSnapshot first = sm.CreateSnapshot(consumeDirtyRegions: true);
        Assert.True(first.HasDirtyRegions);

        CdgRenderSnapshot second = sm.CreateSnapshot(consumeDirtyRegions: true);
        Assert.False(second.HasDirtyRegions);
    }

    [Fact]
    public void CreateSnapshot_PreservesDirtyRegionsWhenNotConsuming()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(
            (byte)CdgConstants.Instruction.MemoryPreset,
            0x05, 0x00));

        CdgRenderSnapshot first = sm.CreateSnapshot(consumeDirtyRegions: false);
        Assert.True(first.HasDirtyRegions);

        CdgRenderSnapshot second = sm.CreateSnapshot(consumeDirtyRegions: false);
        Assert.True(second.HasDirtyRegions);
    }

    [Fact]
    public void BackgroundOverrideArgb_ForcesNonZeroBackgroundWash()
    {
        var sm = new CdgStateMachine
        {
            BackgroundOverrideArgb = 0xFFFF0000 // opaque red
        };

        Assert.NotEqual(0u, sm.BackgroundWashArgb);
    }

    [Fact]
    public void BackgroundOpacity_ClampsToByteRange()
    {
        var sm = new CdgStateMachine
        {
            BackgroundOpacity = 200 // clamped to 100
        };
        Assert.Equal(100, sm.BackgroundOpacity);

        sm.BackgroundOpacity = 50;
        Assert.Equal(50, sm.BackgroundOpacity);
    }

    [Fact]
    public void BackgroundOpacity_ReadsBackEveryPercentAsSet()
    {
        var sm = new CdgStateMachine();
        for (byte percent = 0; percent <= 100; percent++)
        {
            sm.BackgroundOpacity = percent;
            Assert.Equal(percent, sm.BackgroundOpacity);
        }
    }
}
