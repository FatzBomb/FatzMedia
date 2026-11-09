using System.Collections.Concurrent;
using System.Diagnostics;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Fa = global::FatzMedia.Audio;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// The FatzAudio engine (037A slice 2) on the real FatzAudio native library built from Audio/
/// (FatzAudioNativeDir), playing to FatzAudio's null output device (this machine has no sound device). Timing
/// is real time, so waits are generous. Linux only, like the rest of this project; Windows and a
/// real device are the owner's smoke.
/// </summary>
[Collection(FatzAudioNativeCollection.Name)]
public sealed class FatzAudioEngineNativeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Directory.CreateTempSubdirectory("fbm-fatzaudio-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void OpenPlaySeekPauseStop_FollowTheClock()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wav = FatzAudioTestMedia.WriteWav(_root, "tone.wav", new FatzAudioTestMedia.Section(6));
        using FatzAudioEngine engine = CreateEngine();
        List<AudioEngineState> states = [];
        engine.StateChanged += (_, state) => { lock (states) states.Add(state); };

        Assert.True(engine.Open(wav));
        Assert.Equal(0, engine.PlayHandle);
        Assert.InRange(engine.Duration, 5.99, 6.01);
        Assert.Equal(AudioEngineState.Stopped, engine.State);

        Assert.True(engine.Play());
        Assert.Equal(AudioEngineState.Playing, engine.State);
        var positions = Sample(engine, TimeSpan.FromMilliseconds(600));
        for (var i = 1; i < positions.Count; i++)
            Assert.True(positions[i] >= positions[i - 1], $"Position went back: {positions[i - 1]:F4} -> {positions[i]:F4}");
        Assert.True(positions[^1] - positions[0] > 0.3, $"Position barely moved: {positions[0]:F3} -> {positions[^1]:F3}");

        engine.Position = 3.0;
        Assert.InRange(engine.Position, 2.95, 3.1);
        Thread.Sleep(300);
        Assert.InRange(engine.Position, 3.0, 3.5);

        Assert.True(engine.Pause());
        Assert.Equal(AudioEngineState.Paused, engine.State);
        Thread.Sleep(150); // the device buffer drains, then the clock holds
        var paused = engine.Position;
        Thread.Sleep(200);
        Assert.Equal(paused, engine.Position, 3);

        Assert.True(engine.Stop());
        Assert.Equal(AudioEngineState.Stopped, engine.State);
        Assert.InRange(engine.Position, 0, 0.01);
        lock (states)
            Assert.Equal([AudioEngineState.Playing, AudioEngineState.Paused, AudioEngineState.Stopped], states);
    }

    [Fact]
    public void Loop_RaisesPlaybackEndedOncePerWrapWhilePlaying_ThenEndsWhenTurnedOff()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wav = FatzAudioTestMedia.WriteWav(_root, "short.wav", new FatzAudioTestMedia.Section(0.6));
        using FatzAudioEngine engine = CreateEngine();
        var ends = new BlockingCollection<AudioEngineState>();
        engine.PlaybackEnded += (_, _) => ends.Add(engine.State);
        ((IAudioLoopControl)engine).LoopPlayback = true;

        Assert.True(engine.Open(wav));
        Assert.True(engine.Play());
        Assert.True(ends.TryTake(out AudioEngineState first, Timeout));
        Assert.True(ends.TryTake(out AudioEngineState second, Timeout));
        Assert.Equal(AudioEngineState.Playing, first);
        Assert.Equal(AudioEngineState.Playing, second);
        Assert.Equal(AudioEngineState.Playing, engine.State);

        ((IAudioLoopControl)engine).LoopPlayback = false;
        // A wrap may already be on its way; the end follows within one more pass.
        AudioEngineState last;
        do
            Assert.True(ends.TryTake(out last, Timeout));
        while (last == AudioEngineState.Playing);
        Assert.Equal(AudioEngineState.Stopped, engine.State);
        Assert.False(ends.TryTake(out _, TimeSpan.FromMilliseconds(900)));
    }

    [Fact]
    public void SilenceTrim_AppliesFromTheFirstOpen_AndEndsAtTheTrimmedEnd()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // 2 s of silence with a 50 ms click at 0.5 s (shorter than the 200 ms minimum sound, so
        // it is trimmed like a count-in tick), 3 s of tone, then 2 s of silence.
        var wav = FatzAudioTestMedia.WriteWav(
            _root,
            "padded.wav",
            new FatzAudioTestMedia.Section(0.5, Amplitude: 0),
            new FatzAudioTestMedia.Section(0.05, Hz: 1000, Amplitude: 0.5),
            new FatzAudioTestMedia.Section(1.45, Amplitude: 0),
            new FatzAudioTestMedia.Section(3),
            new FatzAudioTestMedia.Section(2, Amplitude: 0));
        using FatzAudioEngine engine = CreateEngine(new MediaPlayerConfiguration { SilenceTrim = new SilenceTrimOptions { Enabled = true } });
        using var pipeline = new AudioPipeline(engine);
        var ended = new ManualResetEventSlim();
        pipeline.PlaybackEnded += (_, _) => ended.Set();

        Assert.True(pipeline.Open(wav));
        MediaAnalysisResult? window = ((IAudioTrimWindowProvider)engine).ActiveTrimWindow;
        Assert.NotNull(window);
        // Our padding (0.1 s) stays at each edge.
        Assert.InRange(window.PlayableStart, 1.85, 1.95);
        Assert.InRange(window.PlayableEnd, 5.05, 5.15);
        Assert.InRange(pipeline.DurationSeconds, 3.1, 3.3);
        Assert.InRange(pipeline.PositionSeconds, 0, 0.01);
        Assert.Equal(TimeSpan.FromSeconds(window.PlayableStart).TotalSeconds, engine.CurrentStream!.Markers.CueIn!.Value.TotalSeconds, 2);

        Assert.True(pipeline.Seek(pipeline.DurationSeconds - 0.3));
        Assert.True(pipeline.Play());
        Assert.True(ended.Wait(Timeout));
        Assert.Equal(MediaPlayerState.Stopped, pipeline.State.PlaybackState);
        Assert.InRange(engine.Position, window.PlayableEnd - 0.05, window.PlayableEnd + 0.05);

        // Played again from stopped, it starts at the trimmed start.
        Assert.True(pipeline.Stop());
        Assert.InRange(pipeline.PositionSeconds, 0, 0.01);
    }

    [Fact]
    public void Equalizer_CutsTheBandItIsSetTo()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wav = FatzAudioTestMedia.WriteWav(_root, "1k.wav", new FatzAudioTestMedia.Section(8, Hz: 1000));
        using FatzAudioEngine engine = CreateEngine();
        Assert.True(engine.Open(wav));
        Assert.True(engine.Play());
        var flat = SettledRms(engine);

        AudioEqualizerSettings cut = new()
        {
            Enabled = true,
            Bands = [new AudioEqualizerBand(1000, -15)],
        };
        Assert.True(((IAudioFilterHost)engine).SetEqualizer(cut));
        Fa.EqualizerBand band = Assert.Single(engine.CurrentStream!.GetEqualizer());
        Assert.Equal(1000, band.Frequency, 0);
        Assert.Equal(-15, band.GainDb, 1);
        var equalized = SettledRms(engine);
        // -15 dB is 0.18 of the level; the RMS window sees the full cut once it has glided.
        Assert.True(equalized < flat * 0.3, $"EQ did not cut: {flat:F4} -> {equalized:F4}");

        Assert.True(((IAudioFilterHost)engine).SetEqualizer(AudioEqualizerSettings.Disabled));
        Assert.Empty(engine.CurrentStream!.GetEqualizer());
    }

    [Fact]
    public async Task Fades_RampTheStreamVolume_AndAStaleSessionIsRejected()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wav = FatzAudioTestMedia.WriteWav(_root, "fade.wav", new FatzAudioTestMedia.Section(6));
        using FatzAudioEngine engine = CreateEngine();
        IAudioFadeGainTarget fade = engine;
        Assert.Equal(0, fade.BeginFadeSession());

        Assert.True(engine.Open(wav));
        var session = fade.BeginFadeSession();
        Assert.NotEqual(0, session);
        Assert.True(fade.SetFadeGain(session, 0, TimeSpan.Zero));
        Assert.Equal(0f, engine.CurrentStream!.Volume);

        Assert.True(fade.SetFadeGain(session, 1, TimeSpan.FromMilliseconds(300)));
        Assert.True(engine.Play());
        Assert.True(SpinUntil(() => engine.CurrentStream!.Volume >= 0.999f), $"Ramp did not finish: {engine.CurrentStream!.Volume}");

        // The runner's ramps, end to end, through the same runner the player uses.
        var runner = new AudioFadeRunner();
        Assert.True(await runner.RunAsync(fade, AudioFadeEnvelope.Outgoing, TimeSpan.FromMilliseconds(300), 0, CancellationToken.None));
        Assert.True(SpinUntil(() => engine.CurrentStream!.Volume <= 0.001f), $"Fade-out did not reach 0: {engine.CurrentStream!.Volume}");

        // Stop ends the session and restores full gain, so the next play is not muted.
        Assert.True(engine.Stop());
        Assert.False(fade.SetFadeGain(session, 0, TimeSpan.Zero));
        Assert.Equal(1f, engine.CurrentStream!.Volume);
    }

    [Fact]
    public async Task Normalization_IsFatzAudiosLoudness_AtTheConfiguredTarget_AndCached()
    {
        if (!OperatingSystem.IsLinux())
            return;

        // A 1 kHz sine at 0.1 in both channels measures about -20 LUFS: +6 dB (x2) to reach -14.
        var wav = FatzAudioTestMedia.WriteWav(_root, "quiet.wav", new FatzAudioTestMedia.Section(5, Hz: 1000, Amplitude: 0.1));
        // Two levels that do not exist yet, as the default per-user folder on a fresh machine.
        var cache = Path.Combine(_root, "FatzMedia.Video", "AudioCache");
        var configuration = new MediaPlayerConfiguration { Normalization = new NormalizationOptions { CacheDirectory = cache } };
        using (FatzAudioEngine engine = CreateEngine(configuration))
        {
            Assert.Equal(0, await engine.AnalyzePeakAsync(CancellationToken.None));
            var gains = new BlockingCollection<double>();
            engine.NormalizationGainChanged += (_, gain) => gains.Add(gain);
            engine.SetVolume(0.5);

            Assert.True(engine.Open(wav));
            Assert.True(engine.IsNormalizationReady);
            Assert.True(gains.TryTake(out var gain, Timeout));
            Assert.InRange(gain, 1.9, 2.1);
            Assert.Equal(gain, engine.NormalizationGain, 6);
            Fa.Loudness loudness = engine.CurrentStream!.GetLoudness();
            Assert.Equal(Fa.LoudnessSource.Scan, loudness.Source);
            Assert.InRange(loudness.IntegratedLufs, -20.5, -19.5);

            // The peak queries answer from FatzAudio's true peak: a 0.1 sine peaks at about 0.1.
            Assert.InRange(await engine.AnalyzePeakAsync(CancellationToken.None), 0.095, 0.105);
            Assert.InRange(await engine.AnalyzePeakQuickAsync(CancellationToken.None), 0.095, 0.105);

            // The gain is FatzAudio's, on the stream: the bus carries only the user volume.
            Assert.Equal(0.5f, engine.OutputVolume, 3);

            // Off: FatzAudio applies nothing; the gain reported is still the song's, as on BASS.
            engine.Normalize = false;
            Assert.True(SpinUntil(() => engine.CurrentStream!.GetLoudness().GainDb == 0));
            Assert.InRange(engine.NormalizationGain, 1.9, 2.1);
            engine.Normalize = true;
            Assert.InRange(engine.NormalizationGain, 1.9, 2.1);
        }

        // The scan went to the cache folder; a new engine (a new run) reads it back at open.
        Assert.NotEmpty(Directory.GetFiles(cache, "*.loudness"));
        using (FatzAudioEngine engine = CreateEngine(configuration with { Normalization = configuration.Normalization with { TargetLufs = -20 } }))
        {
            var gains = new BlockingCollection<double>();
            engine.NormalizationGainChanged += (_, gain) => gains.Add(gain);
            Assert.True(engine.Open(wav));
            Assert.True(gains.TryTake(out var gain, Timeout));
            Assert.InRange(gain, 0.95, 1.05);
            Assert.Equal(Fa.LoudnessSource.Cache, engine.CurrentStream!.GetLoudness().Source);
            Assert.False(gains.TryTake(out _, TimeSpan.FromMilliseconds(200)));
        }
    }

    [Fact]
    public void Karaoke_LyricsAndMetadata_ComeThroughTheMidiEventReader()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var soundFont = FatzAudioTestMedia.WriteSoundFont(_root);
        var kar = FatzAudioTestMedia.WriteKaraoke(_root);
        using FatzAudioEngine engine = CreateEngine(new MediaPlayerConfiguration { MidiSoundfontPath = soundFont });

        Assert.True(engine.Open(kar));
        IMidiEventReader? reader = ((IMidiEventReaderSource)engine).GetMidiEventReader();
        Assert.NotNull(reader);
        Assert.Equal(["\\Hel", "lo ", "world", "/Sec", "ond ", "line", "\\Third ", "page"], reader.GetMarks(MidiMarkKind.Lyric).Select(static mark => mark.Text));
        Assert.Equal(2.0, reader.PositionToSeconds(reader.GetMarks(MidiMarkKind.Lyric)[0].Position), 3);
        Assert.Equal(500000, Assert.Single(reader.GetTempoEvents()).MicrosecondsPerQuarterNote);

        var cues = MidiLyricCueBuilder.ExtractLyrics(reader, kar);
        Assert.NotNull(cues);
        var lines = cues.SelectMany(static cue => cue.KaraokeTiming!.Lines).Select(static line => line.Text.Trim()).Distinct().ToArray();
        Assert.Equal(FatzAudioTestMedia.KaraokeLines, lines);
        KaraokeMetadata metadata = cues[0].KaraokeTiming!.Metadata!;
        Assert.Equal("Test Title", metadata.Title);
        Assert.Equal("Test Artist", metadata.Artist);
        Assert.Equal("3/4", metadata.TimeSignature);
        Assert.Equal("Em", metadata.KeySignature); // our metadata format, as from BASS's "1 1"

        // It plays: the soundfont renders the notes.
        Assert.True(engine.Play());
        Assert.True(SpinUntil(() => engine.Position > 0.2));
    }

    [Fact]
    public void ArchiveEntry_OpensFromMemory()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var wav = FatzAudioTestMedia.WriteWav(_root, "entry.wav", new FatzAudioTestMedia.Section(2));
        using FatzAudioEngine engine = CreateEngine();
        Assert.True(engine.Open(new FileArchiveEntry(wav)));
        Assert.InRange(engine.Duration, 1.99, 2.01);
        Assert.Null(((IAudioTrimWindowProvider)engine).ActiveTrimWindow);
        Assert.True(engine.Play());
        Assert.True(SpinUntil(() => engine.Position > 0.1));
    }

    [Fact]
    public void ServiceProvider_UsesTheFactoryEngine_AndItsDeviceMonitor()
    {
        if (!OperatingSystem.IsLinux())
            return;

        IServiceProvider services = PlayerServiceProvider.Create(
            WithoutUserCache(new MediaPlayerConfiguration { AudioEngineFactory = new FatzAudioEngineFactory(nullOutput: true) }),
            [],
            new AudioPlatformServices());
        try
        {
            _ = Assert.IsType<FatzAudioEngine>(services.GetRequiredService<IAudioEngine>());
            _ = Assert.IsType<AudioEngineDeviceMonitor>(services.GetRequiredService<IAudioDeviceMonitor>());
            Assert.Same(UnavailableMediaFileAnalyzer.Instance, services.GetRequiredService<IMediaFileAnalyzer>());
        }
        finally
        {
            (services as IDisposable)?.Dispose();
        }
    }

    // The loudness cache is off unless a test sets its own folder: tests never write to the user's.
    private static FatzAudioEngine CreateEngine(MediaPlayerConfiguration? configuration = null) =>
        (FatzAudioEngine)new FatzAudioEngineFactory(nullOutput: true).Create(WithoutUserCache(configuration ?? new MediaPlayerConfiguration()));

    private static MediaPlayerConfiguration WithoutUserCache(MediaPlayerConfiguration configuration) =>
        configuration.Normalization.CacheDirectory is null
            ? configuration with { Normalization = configuration.Normalization with { CacheDirectory = string.Empty } }
            : configuration;

    private static List<double> Sample(FatzAudioEngine engine, TimeSpan duration)
    {
        List<double> positions = [];
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            positions.Add(engine.Position);
            Thread.Sleep(5);
        }

        return positions;
    }

    // The RMS heard after the EQ glide and the meter window have settled.
    private static double SettledRms(FatzAudioEngine engine)
    {
        _ = engine.CurrentStream!.GetLevels(TimeSpan.FromMilliseconds(200));
        Thread.Sleep(600);
        Fa.StreamLevels levels = engine.CurrentStream!.GetLevels(TimeSpan.FromMilliseconds(200));
        return levels.Rms.Max();
    }

    private static bool SpinUntil(Func<bool> condition) => SpinWait.SpinUntil(() =>
    {
        if (condition())
            return true;
        Thread.Sleep(10);
        return false;
    }, Timeout);

    private sealed class FileArchiveEntry(string path) : IArchiveEntryInfo
    {
        public string Key => Path.GetFileName(path);

        public long? Size => new FileInfo(path).Length;

        public void WriteTo(Stream stream)
        {
            using FileStream file = File.OpenRead(path);
            file.CopyTo(stream);
        }
    }

    // Reports audio as available without loading BASS: the BASS engine is still created (and
    // fails to initialize here), then the factory's engine replaces it.
    private sealed class AudioPlatformServices : IPlatformServices
    {
        public void EnsureNativeLibrariesLoaded(IReadOnlyList<string> nativeLibraryPaths)
        {
        }

        public bool IsAudioAvailable => true;

        public void AddPlatformServices(IServiceCollection services, MediaPlayerConfiguration configuration)
        {
            _ = services.AddSingleton<Core.Threading.IPrecisionWaiterFactory>(_ => new Threading.LinuxPrecisionWaiterFactory());
            _ = services.AddSingleton<IVideoPipelinePlatformServices>(_ => new Video.LinuxVideoPipelinePlatformServices(LinuxPlatformOptions.Default));
            _ = services.AddSingleton<IVideoHardwareAccelerationServices>(_ => SoftwareOnlyVideoHardwareAccelerationServices.Instance);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FatzAudioNativeCollection
{
    public const string Name = "FatzAudio native";
}
