using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Cdg;
using FatzMedia.Video.Core.Subtitle;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// CD+G drawn by FatzAudio (037A slice 3), on the real FatzAudio library from Audio/ (Linux and Windows; nothing
/// is played): a position reached by playing or seeking shows the same picture as a fresh engine rendered straight there.
/// </summary>
[Collection(FatzAudioNativeGroup.Name)]
public sealed class FatzAudioCdgEngineNativeTests : IDisposable
{
    const int PacketCount = 9_000; // 30 s: three FatzAudio checkpoints
    readonly string _root = Directory.CreateTempSubdirectory("fbm-fatzaudio-cdg-").FullName;
    readonly Fa.AudioEngine? _engine = FatzAudioNativeGroup.CanRun ? Fa.AudioEngine.Create(new Fa.AudioEngineOptions { NullOutput = true, NoCache = true }) : null;

    public void Dispose()
    {
        _engine?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ThePicture_MatchesAFreshRender_PlayingAndSeekingAnywhere()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var bytes = CreateStream(PacketCount, seed: 7);
        var path = Write("song.cdg", bytes);
        using var cdg = new FatzAudioCdgEngine(_engine!) { BackgroundOpacity = 40 };
        cdg.Load(path);
        Assert.Equal(PacketCount / 300.0, cdg.TotalDurationSeconds, 6);

        var random = new Random(11);
        int[] packs =
        [
            1, 2, 300, 301, 1_500, 4_000, 3_999, 2_000, 8_999, 9_000, 100, 6_000, 3_001,
            .. Enumerable.Range(0, 20).Select(_ => random.Next(1, PacketCount + 1))
        ];
        // Steady play first: a frame every 75 Hz tick, as the presentation loop renders.
        for (var n = 1; n <= 1_200; n += 4)
            AssertSame("song.cdg", bytes, n, cdg, opacity: 40, backgroundOverride: null);
        foreach (var n in packs)
            AssertSame("song.cdg", bytes, n, cdg, opacity: 40, backgroundOverride: null);

        cdg.BackgroundOverrideArgb = 0xC0204060;
        cdg.BackgroundOpacity = 100;
        foreach (var n in packs.Take(6))
            AssertSame("song.cdg", bytes, n, cdg, opacity: 100, backgroundOverride: 0xC0204060);
    }

    [Fact]
    public void Snapshots_AreReused_AndDirtyRectsCoverTheChange()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var bytes = CreateStream(PacketCount, seed: 3);
        using var cdg = new FatzAudioCdgEngine(_engine!);
        cdg.Load(Write("song.cdg", bytes));
        var invalidations = 0;
        cdg.FrameInvalidated += (_, _) => invalidations++;

        cdg.RenderAtSeconds(TimeAt(600));
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var first));
        Assert.Contains(CdgDirtyRect.FullFrame, first.DirtyRects);
        Assert.True(first.IsWellFormed);

        // Nothing new: no invalidation; a clean snapshot sharing the first one's buffers, then that one again.
        var before = invalidations;
        cdg.RenderAtSeconds(TimeAt(600));
        Assert.Equal(before, invalidations);
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var clean));
        Assert.Empty(clean.DirtyRects);
        Assert.True(clean.ColorIndices.Equals(first.ColorIndices));
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var again));
        Assert.Same(clean, again);
        first = clean;

        // A few packs on: only what they touched is dirty, relative to the first snapshot.
        var n = 600;
        CdgRenderSnapshot next;
        do
        {
            n++;
            cdg.RenderAtSeconds(TimeAt(n));
            Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out next));
        }
        while (next.Revision == first.Revision);

        Assert.Equal(first.Revision, next.DirtyBaseRevision);
        Assert.NotEmpty(next.DirtyRects);
        var changed = Enumerable.Range(0, CdgConstants.FrameWidth * CdgConstants.FrameHeight)
            .Where(i => first.ColorIndices.Span[i] != next.ColorIndices.Span[i]);
        Assert.All(changed, i => Assert.Contains(next.DirtyRects, r => Covers(r, i % CdgConstants.FrameWidth, i / CdgConstants.FrameWidth)));
    }

    [Fact]
    public void Reset_IsABlankPicture_AndTheNextRenderIsWhole()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var bytes = CreateStream(PacketCount, seed: 5);
        using var cdg = new FatzAudioCdgEngine(_engine!);
        cdg.Load(Write("song.cdg", bytes));
        cdg.RenderAtSeconds(TimeAt(2_000));
        cdg.Reset();

        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var blank));
        Assert.All(blank.ColorIndices.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0u, blank.BackgroundWashArgb);

        // FatzAudio's surface is still at 2,000 packs: the whole frame comes across.
        AssertSame("song.cdg", bytes, 2_000, cdg, opacity: 100, backgroundOverride: null);
    }

    [Fact]
    public void ACdgWithoutAWholePack_LoadsAsAnEmptySong()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        using var cdg = new FatzAudioCdgEngine(_engine!);
        var opened = 0;
        cdg.Opened += (_, _) => opened++;
        cdg.Load(Write("empty.cdg", new byte[10]));

        Assert.Equal(1, opened);
        Assert.Equal(0, cdg.TotalDurationSeconds);
        cdg.RenderAtSeconds(1);
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var snapshot));
        Assert.True(snapshot.IsWellFormed);
    }

    [Fact]
    public void LoadingAnotherSong_ReplacesThePicture()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var first = CreateStream(PacketCount, seed: 1);
        var second = CreateStream(PacketCount / 2, seed: 2);
        using var cdg = new FatzAudioCdgEngine(_engine!);
        cdg.Load(Write("first.cdg", first));
        cdg.RenderAtSeconds(TimeAt(5_000));
        cdg.Load(new BytesEntry("second.cdg", second));

        Assert.Equal(PacketCount / 2 / 300.0, cdg.TotalDurationSeconds, 6);
        AssertSame("second.cdg", second, 1_000, cdg, opacity: 100, backgroundOverride: null);
    }

    [Fact]
    public void Scaled_ThePictureIsTheOverlay_TheSnapshotOnlyTheWash()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var bytes = CreateStream(PacketCount, seed: 9);
        using var classic = new FatzAudioCdgEngine(_engine!);
        using var scaled = new FatzAudioCdgEngine(_engine!, new CdgScalingOptions { Scale = CdgScale.High, Style = CdgScaleStyle.AntiAliased }.WithDefaultEffects());
        classic.Load(Write("classic.cdg", bytes));
        scaled.Load(Write("scaled.cdg", bytes));
        Assert.False(((IKaraokeOverlaySource)classic).DrawsOverlay);
        IKaraokeOverlaySource overlay = scaled;
        Assert.True(overlay.DrawsOverlay);

        classic.RenderAtSeconds(TimeAt(3_000));
        scaled.RenderAtSeconds(TimeAt(3_000));
        Assert.True(classic.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var indexed));
        Assert.True(scaled.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var washOnly));
        Assert.True(washOnly.ColorIndices.IsEmpty && washOnly.IsWellFormed);
        Assert.Equal(indexed.BackgroundWashArgb, washOnly.BackgroundWashArgb);

        var buffer = new byte[1920 * 1080 * 4];
        Memory<byte> Rent(int length) => buffer.AsMemory(0, length);
        var frame = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out var changed)!.Value;
        Assert.True(changed);
        Assert.Equal((1920, 1080), (frame.CanvasWidth, frame.CanvasHeight));
        Assert.Equal(1080, frame.Height); // Show at 1080 high: 5x, 1500 x 1080, centred
        Assert.Equal(1500, frame.Width);
        Assert.Equal(210, frame.CanvasLeft);
        var visible = 0;
        var pixels = frame.Pixels.Span;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                visible++;
        }

        Assert.True(visible > 1000, $"only {visible} visible pixels");
        _ = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out changed);
        Assert.False(changed);

        scaled.RenderAtSeconds(TimeAt(6_000));
        _ = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out changed);
        Assert.True(changed);
    }

    [Fact]
    public void Scaling_ChangesWhileASongPlays()
    {
        if (!FatzAudioNativeGroup.CanRun)
            return;

        var bytes = CreateStream(PacketCount, seed: 11);
        using var reference = new FatzAudioCdgEngine(_engine!);
        using var cdg = new FatzAudioCdgEngine(_engine!);
        reference.Load(Write("reference.cdg", bytes));
        cdg.Load(Write("live.cdg", bytes));
        reference.RenderAtSeconds(TimeAt(3_000));
        cdg.RenderAtSeconds(TimeAt(3_000));
        Assert.True(reference.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var classic));
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out _));
        IKaraokeOverlaySource overlay = cdg;
        var invalidated = 0;
        cdg.FrameInvalidated += (_, _) => invalidated++;

        // On: the picture moves to the overlay, made from the song already loaded.
        cdg.Scaling = new CdgScalingOptions { Scale = CdgScale.High, Style = CdgScaleStyle.Traced };
        Assert.Equal(1, invalidated);
        Assert.True(overlay.DrawsOverlay);
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var washOnly));
        Assert.True(washOnly.ColorIndices.IsEmpty);
        var buffer = new byte[3840 * 2160 * 4];
        Memory<byte> Rent(int length) => buffer.AsMemory(0, length);
        var frame = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out var changed)!.Value;
        Assert.True(changed);
        Assert.Equal((1500, 1080), (frame.Width, frame.Height));

        // A new look draws the frame again; the same options change nothing.
        cdg.Scaling = cdg.Scaling.WithDefaultEffects() with { BevelStrength = 5, BevelSize = 2 };
        _ = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out changed);
        Assert.True(changed);
        var same = cdg.Scaling;
        cdg.Scaling = same;
        Assert.Equal(2, invalidated);

        // The pixel-art style draws too.
        cdg.Scaling = cdg.Scaling with { Style = CdgScaleStyle.PixelArt };
        _ = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out changed);
        Assert.True(changed);

        // The scale: drawn at its size, the canvas says so, and the renderers fit it to the screen. High follows the
        // screen the presentation reports.
        cdg.Scaling = cdg.Scaling with { Scale = CdgScale.Low };
        frame = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out _)!.Value;
        Assert.Equal((768, 432), (frame.CanvasWidth, frame.CanvasHeight));
        Assert.Equal((600, 432), (frame.Width, frame.Height));
        cdg.DisplayHeight = 2160;
        cdg.Scaling = cdg.Scaling with { Scale = CdgScale.High };
        frame = overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out _)!.Value;
        Assert.Equal((3840, 2160), (frame.CanvasWidth, frame.CanvasHeight));
        Assert.Equal((3000, 2160), (frame.Width, frame.Height));

        // Off: the classic picture again, whole, without a new render.
        cdg.Scaling = cdg.Scaling with { Scale = CdgScale.None };
        Assert.False(overlay.DrawsOverlay);
        Assert.Null(overlay.DrawOverlay(TimeSpan.Zero, 1920, 1080, Rent, out _));
        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var back));
        Assert.True(FirstMismatch(classic.ColorIndices.Span, back.ColorIndices.Span) < 0, "the classic picture differs after scaling was turned off");
        Assert.Contains(CdgDirtyRect.FullFrame, back.DirtyRects);
    }

    // Mid-pack: floor(t x 300) = packs there; the FatzAudio engine must show the same.
    static double TimeAt(int packs) => (packs + 0.5) / CdgConstants.PacketsPerSecond;

    static bool Covers(CdgDirtyRect r, int x, int y) => x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    void AssertSame(string name, byte[] bytes, int packs, FatzAudioCdgEngine cdg, byte opacity, uint? backgroundOverride)
    {
        cdg.RenderAtSeconds(TimeAt(packs));
        using var freshEngine = new FatzAudioCdgEngine(_engine!) { BackgroundOpacity = opacity, BackgroundOverrideArgb = backgroundOverride };
        freshEngine.Load(Write($"fresh-{name}", bytes));
        freshEngine.RenderAtSeconds(TimeAt(packs));
        Assert.True(freshEngine.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var expected));

        Assert.True(cdg.TryCreateCdgSnapshot(consumeDirtyRegions: true, out var actual));
        var mismatch = FirstMismatch(expected.ColorIndices.Span, actual.ColorIndices.Span);
        Assert.True(mismatch < 0, $"pixels differ at {packs} packs, first at ({mismatch % 300}, {mismatch / 300})");
        Assert.Equal(expected.BackgroundColorIndex, actual.BackgroundColorIndex);
        Assert.Equal(expected.BackgroundWashArgb, actual.BackgroundWashArgb);
        foreach (var index in actual.ColorIndices.ToArray().Distinct())
            Assert.Equal(expected.PaletteArgb.Span[index].ToString("X8", System.Globalization.CultureInfo.InvariantCulture), actual.PaletteArgb.Span[index].ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
    }

    static int FirstMismatch(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Shaped like a real disc: mostly tile blocks, rare clears and
    /// colour loads. Scrolls carry no fine offset,
    /// and the colour tables come first so every colour in use is a loaded one.
    /// </summary>
    static byte[] CreateStream(int packets, int seed)
    {
        var random = new Random(seed);
        var stream = new byte[packets * CdgConstants.PacketSize];
        for (var index = 0; index < packets; index++)
        {
            var packet = stream.AsSpan(index * CdgConstants.PacketSize, CdgConstants.PacketSize);
            var kind = index switch
            {
                0 => 99_600,
                1 => 99_800,
                2 => 99_999,
                _ => random.Next(100_000),
            };
            if (kind < 10_000)
                continue;

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
            if (packet[1] is (byte)CdgConstants.Instruction.ScrollCopy or (byte)CdgConstants.Instruction.ScrollPreset)
            {
                packet[5] &= 0x30; // horizontal: command only, offset 0
                packet[6] &= 0x30; // vertical: command only, offset 0
            }
        }

        return stream;
    }

    sealed class BytesEntry(string key, byte[] bytes) : IArchiveEntryInfo
    {
        public string Key => key;

        public long? Size => bytes.Length;

        public void WriteTo(Stream stream) => stream.Write(bytes);
    }
}
