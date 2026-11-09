using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Tests;

public class CdgSnapshotOwnershipTests
{
    private static byte[] MakePacket(CdgConstants.Instruction instruction, params byte[] data)
    {
        var packet = new byte[CdgConstants.PacketSize];
        packet[0] = (byte)CdgConstants.Command.Graphic;
        packet[1] = (byte)instruction;
        for (var i = 0; i < data.Length && i < 16; i++)
            packet[4 + i] = data[i];
        return packet;
    }

    private static byte[] TilePacket(byte color0, byte color1, int row, int column, byte pattern)
    {
        var data = new byte[16];
        data[0] = color0;
        data[1] = color1;
        data[2] = (byte)row;
        data[3] = (byte)column;
        for (var i = 4; i < 16; i++)
            data[i] = pattern;
        return MakePacket(CdgConstants.Instruction.TileBlockNormal, data);
    }

    private static byte[] CurrentIndices(CdgStateMachine sm)
    {
        var indices = new byte[CdgConstants.TotalPixels];
        sm.CopyColorIndices(indices);
        return indices;
    }

    [Fact]
    public void CreateSnapshot_UnchangedState_ReturnsPreviousInstanceWithoutCopying()
    {
        var sm = new CdgStateMachine();
        CdgRenderSnapshot first = sm.CreateSnapshot();
        CdgRenderSnapshot second = sm.CreateSnapshot();
        CdgRenderSnapshot third = sm.CreateSnapshot();

        // The first snapshot consumes the reset's full-frame dirty region, so the second one
        // is a clean snapshot sharing the first one's buffers; idle requests then reuse it.
        Assert.True(first.HasDirtyRegions);
        Assert.NotSame(first, second);
        Assert.False(second.HasDirtyRegions);
        Assert.True(first.ColorIndices.Equals(second.ColorIndices));
        Assert.Same(second, third);

        CdgSnapshotStatistics stats = sm.SnapshotStatistics;
        Assert.Equal(2, stats.SnapshotsCreated);
        Assert.Equal(1, stats.ColorIndexCopies);
        Assert.Equal(1, stats.SnapshotsReused);
        Assert.Equal(CdgConstants.TotalPixels, stats.ColorIndexBytesCopied);
    }

    [Fact]
    public void CreateSnapshot_AfterVisualChange_CreatesNewSnapshotAndLeavesOldOneUntouched()
    {
        var sm = new CdgStateMachine();
        _ = sm.CreateSnapshot();
        CdgRenderSnapshot before = sm.CreateSnapshot();
        var beforeIndices = before.ColorIndices.ToArray();

        Assert.True(sm.ProcessPacket(TilePacket(0, 7, row: 3, column: 4, pattern: 0x3F)));
        CdgRenderSnapshot after = sm.CreateSnapshot();

        Assert.NotSame(before, after);
        Assert.True(after.Revision > before.Revision);
        Assert.True(after.HasDirtyRegions);
        Assert.Equal(beforeIndices, before.ColorIndices.ToArray());
        Assert.Equal(CurrentIndices(sm), after.ColorIndices.ToArray());
    }

    [Fact]
    public void CreateSnapshot_SharesPaletteUntilItChanges()
    {
        var sm = new CdgStateMachine();
        CdgRenderSnapshot first = sm.CreateSnapshot();
        _ = sm.ProcessPacket(TilePacket(0, 3, row: 1, column: 1, pattern: 0x2A));
        CdgRenderSnapshot second = sm.CreateSnapshot();

        Assert.True(first.PaletteArgb.Equals(second.PaletteArgb));

        var colorData = new byte[16];
        colorData[0] = 0x3C;
        _ = sm.ProcessPacket(MakePacket(CdgConstants.Instruction.LoadColorTableLower, colorData));
        CdgRenderSnapshot third = sm.CreateSnapshot();

        Assert.False(second.PaletteArgb.Equals(third.PaletteArgb));
        Assert.NotEqual(second.PaletteArgb.Span[0], third.PaletteArgb.Span[0]);
        Assert.Equal(0xFFFF0000u, (uint)third.PaletteArgb.Span[0]);
        Assert.Equal(2, sm.SnapshotStatistics.PaletteCopies);
    }

    [Fact]
    public void CreateSnapshot_BackgroundOverrideChange_IsNotReused()
    {
        var sm = new CdgStateMachine();
        _ = sm.CreateSnapshot();
        CdgRenderSnapshot clean = sm.CreateSnapshot();

        sm.BackgroundOverrideArgb = 0xFF102030;
        CdgRenderSnapshot overridden = sm.CreateSnapshot();

        Assert.NotSame(clean, overridden);
        Assert.NotEqual(clean.BackgroundWashArgb, overridden.BackgroundWashArgb);
        Assert.True(overridden.HasDirtyRegions);
    }

    [Fact]
    public void CreateSnapshot_PendingDirtyRegionsWithoutConsuming_NeverReuses()
    {
        var sm = new CdgStateMachine();
        _ = sm.ProcessPacket(MakePacket(CdgConstants.Instruction.MemoryPreset, 0x05, 0x00));

        CdgRenderSnapshot first = sm.CreateSnapshot(consumeDirtyRegions: false);
        CdgRenderSnapshot second = sm.CreateSnapshot(consumeDirtyRegions: false);

        Assert.NotSame(first, second);
        Assert.True(second.HasDirtyRegions);
        Assert.Equal(0, sm.SnapshotStatistics.SnapshotsReused);
    }

    [Fact]
    public void CreateSnapshot_AfterReset_CreatesFullFrameSnapshot()
    {
        var sm = new CdgStateMachine();
        _ = sm.CreateSnapshot();
        CdgRenderSnapshot clean = sm.CreateSnapshot();

        sm.Reset();
        CdgRenderSnapshot reset = sm.CreateSnapshot();

        Assert.NotSame(clean, reset);
        Assert.Contains(CdgDirtyRect.FullFrame, reset.DirtyRects);
    }

    /// <summary>
    /// Representative karaoke cadence: 300 CDG packets per second presented at 60 Hz, with a lyric
    /// line drawn in bursts and idle gaps (CDG streams are mostly empty subcode packets between
    /// lines). Every presented snapshot must equal the state at its tick, and idle ticks must not
    /// copy the 64,800-byte index frame.
    /// </summary>
    [Fact]
    public void RepresentativeStream_ReusesIdleTicksAndMatchesState()
    {
        const int PacketsPerSecond = 300;
        const int TicksPerSecond = 60;
        const int Seconds = 10;
        const int PacketsPerTick = PacketsPerSecond / TicksPerSecond;
        var sm = new CdgStateMachine();
        var emptyPacket = new byte[CdgConstants.PacketSize];
        var packetIndex = 0;
        var changedTicks = 0;
        var idleTicks = 0;

        for (var tick = 0; tick < Seconds * TicksPerSecond; tick++)
        {
            var changedThisTick = false;
            for (var p = 0; p < PacketsPerTick; p++, packetIndex++)
            {
                // Draw for the first 1.5 s of every 4 s window, idle otherwise.
                var drawing = packetIndex % (4 * PacketsPerSecond) < (3 * PacketsPerSecond / 2);
                byte[] packet = drawing
                    ? TilePacket(
                        0,
                        (byte)(1 + (packetIndex / PacketsPerSecond % 15)),
                        row: 6 + (packetIndex / 50 % 4),
                        column: packetIndex % 50,
                        pattern: (byte)(packetIndex % 63 + 1))
                    : emptyPacket;
                changedThisTick |= sm.ProcessPacket(packet);
            }

            CdgRenderSnapshot snapshot = sm.CreateSnapshot();
            Assert.Equal(sm.Revision, snapshot.Revision);
            Assert.Equal(CurrentIndices(sm), snapshot.ColorIndices.ToArray());
            if (changedThisTick)
                changedTicks++;
            else
                idleTicks++;
        }

        CdgSnapshotStatistics stats = sm.SnapshotStatistics;
        Assert.True(idleTicks > 0);
        Assert.Equal(Seconds * TicksPerSecond, stats.SnapshotsCreated + stats.SnapshotsReused);

        // Only ticks that changed the frame copy it (the first tick draws, so the reset frame is
        // never copied on its own); a clean follow-up after consumed dirty regions shares
        // buffers, and everything else is reused.
        Assert.Equal(changedTicks, stats.ColorIndexCopies);
        Assert.True(stats.SnapshotsCreated <= (2 * changedTicks) + 1, $"created={stats.SnapshotsCreated} changed={changedTicks}");
        Assert.True(stats.SnapshotsReused >= idleTicks - changedTicks, $"reused={stats.SnapshotsReused} idle={idleTicks}");
        Assert.Equal(stats.ColorIndexCopies * CdgConstants.TotalPixels, stats.ColorIndexBytesCopied);
        Assert.Equal(1, stats.PaletteCopies);
    }
}
