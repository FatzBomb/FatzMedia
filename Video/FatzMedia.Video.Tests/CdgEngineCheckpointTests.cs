using FatzMedia.Video.Core.Cdg;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 022A slice 4: seeks restore the nearest replay checkpoint instead of replaying every
/// packet from the start. The result must match a replay from the first packet exactly.
/// </summary>
public sealed class CdgEngineCheckpointTests : IDisposable
{
    const int PacketCount = 25_000; // about 83 s: eight checkpoints
    readonly string _path = Path.Combine(Path.GetTempPath(), $"cdg-checkpoints-{Guid.NewGuid():N}.cdg");

    public CdgEngineCheckpointTests() => File.WriteAllBytes(_path, CreateRandomStream(PacketCount, seed: 7));

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void SeeksThroughCheckpoints_MatchAReplayFromTheFirstPacket()
    {
        using var engine = Open(backgroundOpacity: 40);
        var duration = engine.TotalDurationSeconds;
        engine.RenderAtSeconds(duration); // records every checkpoint on the way

        var random = new Random(11);
        double[] targets =
        [
            duration * 0.5, // backward: restore
            duration * 0.9, // long forward jump: restore a later checkpoint
            15.0, 15.0 + (1.0 / 300), 3.0, 0.0, 10.0, // exactly on a checkpoint boundary
            .. Enumerable.Range(0, 12).Select(_ => random.NextDouble() * duration)
        ];

        foreach (var seconds in targets)
        {
            engine.RenderAtSeconds(seconds);
            AssertSameState(Reference(seconds), engine, seconds);
        }

        engine.RenderAtSeconds(duration * 0.3, forceRebuild: true);
        AssertSameState(Reference(duration * 0.3), engine, duration * 0.3);
    }

    [Fact]
    public void AFirstSeekFarIntoTheSong_StartsFromACheckpoint_AndMatches()
    {
        // Checkpoints are built at load, so a seek into a part never played is bounded too.
        using var engine = Open(backgroundOpacity: 40);
        engine.RenderAtSeconds(74.5);

        AssertSameState(Reference(74.5), engine, 74.5);
    }

    [Fact]
    public void TheBackgroundOverride_SurvivesARestore()
    {
        using var engine = Open(backgroundOpacity: 40);
        engine.BackgroundOverrideArgb = 0xFF204060;
        engine.RenderAtSeconds(70);
        engine.RenderAtSeconds(35); // backward: restores a checkpoint

        AssertSameState(Reference(35, backgroundOverride: 0xFF204060), engine, 35);
        Assert.Equal(0xFF204060u, engine.BackgroundOverrideArgb);
    }

    [Fact]
    public void LoadingAnotherFile_ReplacesTheCheckpoints()
    {
        var otherPath = Path.Combine(Path.GetTempPath(), $"cdg-checkpoints-other-{Guid.NewGuid():N}.cdg");
        File.WriteAllBytes(otherPath, CreateRandomStream(PacketCount, seed: 99));
        try
        {
            using var engine = Open(backgroundOpacity: 40);
            engine.RenderAtSeconds(80);
            engine.Load(otherPath);
            engine.RenderAtSeconds(80);
            engine.RenderAtSeconds(45); // restores a checkpoint of the new file, not the old one

            AssertSameState(Reference(45, path: otherPath), engine, 45);
        }
        finally
        {
            File.Delete(otherPath);
        }
    }

    [Fact]
    public void ASeekBeforeTheFirstCheckpoint_ReplaysFromTheStart()
    {
        using var engine = Open(backgroundOpacity: 100);
        engine.RenderAtSeconds(5);
        engine.RenderAtSeconds(2);

        AssertSameState(Reference(2, backgroundOpacity: 100), engine, 2);
    }

    [Fact]
    public void ARepeatPresetStraddlingACheckpoint_SeeksLikeAReplay()
    {
        // Preset (colour 4, repeat 0) ends the packet before the checkpoint; its repeat is the
        // first packet after it. A lone repeat later clears too.
        var interval = CdgEngine.CheckpointIntervalPackets;
        var bytes = CreateRandomStream(interval * 2, seed: 3);
        void Put(int index, byte color, byte repeat)
        {
            var packet = bytes.AsSpan(index * CdgConstants.PacketSize, CdgConstants.PacketSize);
            packet.Clear();
            packet[0] = (byte)CdgConstants.Command.Graphic;
            packet[1] = (byte)CdgConstants.Instruction.MemoryPreset;
            packet[4] = color;
            packet[5] = repeat;
        }

        Put(interval - 1, 4, 0);
        Put(interval, 4, 1);
        Put(interval + 500, 9, 2);
        var path = Path.Combine(Path.GetTempPath(), $"cdg-repeat-{Guid.NewGuid():N}.cdg");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var engine = new CdgEngine { BackgroundOpacity = 40 };
            engine.Load(path);
            engine.RenderAtSeconds(engine.TotalDurationSeconds);
            foreach (var seconds in new[] { 40.0, 35.0, interval / 300.0, (interval + 1) / 300.0, (interval + 501) / 300.0, 3.0, 55.0 })
            {
                engine.RenderAtSeconds(seconds);
                AssertSameState(Reference(seconds, path: path), engine, seconds);
            }

            engine.RenderAtSeconds((interval + 501) / 300.0);
            Assert.True(engine.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var cleared));
            Assert.All(cleared.ColorIndices.ToArray(), b => Assert.Equal(9, b));
        }
        finally
        {
            File.Delete(path);
        }
    }

    CdgEngine Open(byte backgroundOpacity)
    {
        var engine = new CdgEngine();
        engine.Load(_path);
        engine.BackgroundOpacity = backgroundOpacity;
        return engine;
    }

    /// <summary>
    /// The expected state: every packet from the first one replayed on a bare state machine,
    /// independent of CdgEngine and its checkpoints.
    /// </summary>
    CdgRenderSnapshot Reference(double seconds, byte backgroundOpacity = 40, uint? backgroundOverride = null, string? path = null)
    {
        var bytes = File.ReadAllBytes(path ?? _path);
        var packetCount = bytes.Length / CdgConstants.PacketSize;
        var endIndex = (int)Math.Min(CdgEngine.PacketCountAt(seconds), packetCount);
        var replay = new CdgStateMachine { BackgroundOpacity = backgroundOpacity, BackgroundOverrideArgb = backgroundOverride };
        for (var index = 0; index < endIndex; index++)
            _ = replay.ProcessPacket(bytes.AsSpan(index * CdgConstants.PacketSize, CdgConstants.PacketSize));
        return replay.CreateSnapshot();
    }

    static void AssertSameState(CdgRenderSnapshot expected, CdgEngine engine, double seconds)
    {
        Assert.True(engine.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var actual));
        Assert.True(expected.ColorIndices.Span.SequenceEqual(actual.ColorIndices.Span), $"pixels differ at {seconds:F3} s");
        Assert.True(expected.PaletteArgb.Span.SequenceEqual(actual.PaletteArgb.Span), $"palette differs at {seconds:F3} s");
        Assert.Equal(expected.BackgroundColorIndex, actual.BackgroundColorIndex);
        Assert.Equal(expected.BackgroundWashArgb, actual.BackgroundWashArgb);
    }

    static byte[] CreateRandomStream(int packets, int seed)
    {
        // Shaped like a real disc: mostly tile blocks, with screen clears (memory presets) and
        // palette loads rare, so state from long ago still shows and a wrong checkpoint is caught.
        var random = new Random(seed);
        var stream = new byte[packets * CdgConstants.PacketSize];
        for (var index = 0; index < packets; index++)
        {
            var packet = stream.AsSpan(index * CdgConstants.PacketSize, CdgConstants.PacketSize);
            var kind = random.Next(100_000);
            if (kind < 10_000)
                continue; // an empty (non-graphic) packet, as real streams have

            packet[0] = (byte)CdgConstants.Command.Graphic;
            packet[1] = kind switch
            {
                < 70_000 => (byte)CdgConstants.Instruction.TileBlockNormal,
                < 98_000 => (byte)CdgConstants.Instruction.TileBlockXor,
                < 99_000 => (byte)CdgConstants.Instruction.ScrollCopy,
                < 99_500 => (byte)CdgConstants.Instruction.ScrollPreset,
                < 99_700 => (byte)CdgConstants.Instruction.LoadColorTableLower,
                < 99_900 => (byte)CdgConstants.Instruction.LoadColorTableUpper,
                < 99_950 => (byte)CdgConstants.Instruction.BorderPreset,
                < 99_980 => (byte)CdgConstants.Instruction.DefineTransparentColor,
                _ => (byte)CdgConstants.Instruction.MemoryPreset
            };
            for (var data = 4; data < 20; data++)
                packet[data] = (byte)(random.Next(256) & 0x3F);
        }

        return stream;
    }
}
