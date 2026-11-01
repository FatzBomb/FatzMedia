using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Audio.Fade;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// 037A slice 1: internal consumers reach the audio engine through seams, never through the BASS
/// handles on <see cref="IAudioEngine"/>. The engine double here throws on either handle, so any
/// remaining handle read fails the test.
/// </summary>
public sealed class AudioEngineSeamTests : IDisposable
{
    private readonly string _karPath = Path.Combine(Path.GetTempPath(), $"fatzbomb-seam-{Guid.NewGuid():N}.kar");

    public AudioEngineSeamTests() => File.WriteAllBytes(_karPath, StandardMidiFileReaderTests.CreateKaraokeFile());

    public void Dispose() => File.Delete(_karPath);

    [Fact]
    public void MidiLyrics_ComeFromTheFile_WhenTheEngineHasNoReader()
    {
        using PlaybackCoordinator coordinator = CreateCoordinator(new HandlelessAudioEngine(), new BassMidiLyricProvider());

        Assert.True(coordinator.OpenAudio(_karPath));

        Assert.Single(coordinator.SubtitleTracks);
        Assert.Contains(coordinator.Subtitle!.GetActiveCues(1.2), static cue => cue.Text.Contains("Hello world", StringComparison.Ordinal));
    }

    [Fact]
    public void MidiLyrics_ComeFromTheArchiveEntry_WhenTheEngineHasNoReader()
    {
        using PlaybackCoordinator coordinator = CreateCoordinator(new HandlelessAudioEngine(), new BassMidiLyricProvider());

        Assert.True(coordinator.OpenAudio(new StandardMidiFileReaderTests.BytesArchiveEntry("pack/song.kar", StandardMidiFileReaderTests.CreateKaraokeFile())));

        Assert.Single(coordinator.SubtitleTracks);
    }

    [Fact]
    public void MidiLyrics_ComeFromTheEngineReader_WhenTheEngineHasOne()
    {
        var reader = new RecordingMidiReader(StandardMidiFileReader.TryParse(StandardMidiFileReaderTests.CreateKaraokeFile())!);
        var engine = new HandlelessReaderAudioEngine(reader);
        using PlaybackCoordinator coordinator = CreateCoordinator(engine, new BassMidiLyricProvider());

        // No such file: the cues can only come from the engine's reader.
        Assert.True(coordinator.OpenAudio("missing/song.kar"));

        Assert.Single(coordinator.SubtitleTracks);
        Assert.NotEmpty(reader.Calls);
    }

    [Fact]
    public void HostSuppliedProvider_StillGetsTheSourceHandle()
    {
        var provider = new RecordingHandleProvider();
        using PlaybackCoordinator coordinator = CreateCoordinator(new TestAudioEngine(), provider);

        Assert.True(coordinator.OpenAudio(_karPath));

        Assert.Equal([(0, _karPath)], provider.Calls);
    }

    [Fact]
    public void BassEngines_ProvideTheMidiReaderSeam()
    {
        // Not constructed: BASS is absent here. The BASS engines read their source channel as before.
        Assert.True(typeof(IMidiEventReaderSource).IsAssignableFrom(typeof(AudioEngine)));
        Assert.True(typeof(IMidiEventReaderSource).IsAssignableFrom(typeof(VideoAudioEngine)));
        Assert.True(typeof(IMidiEventReaderSource).IsAssignableFrom(typeof(SoundtrackAudioEngine)));
        Assert.True(typeof(IMidiLyricReaderProvider).IsAssignableFrom(typeof(BassMidiLyricProvider)));
        Assert.Null(BassMidiEventReader.ForHandle(0));
    }

    [Fact]
    public void BassEngines_ImplementTheEffectAndTrimSeams()
    {
        foreach (Type engine in new[] { typeof(AudioEngine), typeof(VideoAudioEngine), typeof(SoundtrackAudioEngine) })
        {
            Assert.True(typeof(IAudioFadeGainTarget).IsAssignableFrom(engine), engine.Name);
            Assert.True(typeof(IAudioFilterHost).IsAssignableFrom(engine), engine.Name);
        }

        Assert.True(typeof(IAudioTrimWindowProvider).IsAssignableFrom(typeof(AudioEngine)));
        Assert.True(typeof(IAudioAnalysisOpenControl).IsAssignableFrom(typeof(AudioEngine)));
    }

    [Fact]
    public void CueBuilder_ReadsInThePreSeamBassOrder()
    {
        var reader = new RecordingMidiReader(StandardMidiFileReader.TryParse(StandardMidiFileReaderTests.CreateKaraokeFile())!);

        Assert.NotNull(MidiLyricCueBuilder.ExtractLyrics(reader, "song.kar", TimeSpan.FromSeconds(1)));

        // Before the seam on BASS: StreamGetMarks(Lyric), StreamGetMarks(Text), ChannelGetLength +
        // Bytes2Seconds, the metadata marks and tempo events, then Bytes2Seconds per lyric.
        Assert.Equal(
            [
                "Marks:Lyric", "Marks:Text", "Duration",
                "Marks:TrackName", "Marks:Copyright", "Marks:KeySignature", "Marks:TimeSignature", "Tempo",
                "Seconds:960", "Seconds:1200", "Seconds:1440", "Seconds:2400", "Seconds:2640",
            ],
            reader.Calls);
    }

    [Fact]
    public void CueBuilder_WithNoReader_ReturnsNull_AsHandleZeroDid()
    {
        Assert.Null(MidiLyricCueBuilder.ExtractLyrics((IMidiEventReader?)null, "song.kar"));
        Assert.Null(MidiLyricCueBuilder.ExtractLyrics(0, "song.kar"));
    }

    [Fact]
    public async Task Fades_RunOnAHandlelessEngine()
    {
        var engine = new HandlelessAudioEngine();
        var runner = new AudioFadeRunner(static (_, _) => Task.CompletedTask);

        Assert.True(await runner.RunAsync(engine, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));

        Assert.Equal([(1d, TimeSpan.Zero), (0d, TimeSpan.FromSeconds(1))], engine.FadeCalls);
    }

    [Fact]
    public void TrimAndEqualizer_RunOnAHandlelessEngine()
    {
        var engine = new HandlelessAudioEngine
        {
            Duration = 100,
            ActiveTrimWindow = new MediaAnalysisResult(100, 5, 95, 90, [new SilenceRegion(0, 5), new SilenceRegion(95, 100)], 1),
        };
        using var pipeline = new AudioPipeline(engine);
        AudioEqualizerSettings settings = AudioEqualizerSettings.Disabled;

        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(((IAudioFilterHost)engine).SetEqualizer(settings));

        Assert.Equal(90, pipeline.DurationSeconds);
        Assert.True(engine.AnalyzeSilenceRequested);
        Assert.Same(settings, engine.Equalizer);
    }

    private static PlaybackCoordinator CreateCoordinator(IAudioEngine engine, IMidiLyricProvider provider) =>
        new(
            engine,
            new TestCdgEngine(),
            new SubtitleEngine(),
            new NoVideoFactory(),
            provider,
            MediaPlayerConfiguration.Auto,
            audioAvailable: false);

    /// <summary>An engine with no BASS handles: reading either throws.</summary>
    private class HandlelessAudioEngine : IAudioEngine, IAudioAnalysisOpenControl, IAudioTrimWindowProvider, IAudioFadeGainTarget, IAudioFilterHost
    {
        private int _fadeSession;

        public List<(double Gain, TimeSpan Ramp)> FadeCalls { get; } = [];
        public bool AnalyzeSilenceRequested { get; private set; }
        public MediaAnalysisResult? ActiveTrimWindow { get; init; }
        public AudioEqualizerSettings Equalizer { get; private set; } = AudioEqualizerSettings.Disabled;

        public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;
        public int PlayHandle => throw new InvalidOperationException("No BASS handle");
        public int SourceHandle => throw new InvalidOperationException("No BASS handle");
        public bool Normalize { get; set; }
        public double NormalizationGain => 1;
        public bool IsNormalizationReady => true;
        public double Duration { get; init; }
        public double Position { get; set; }
        public int CurrentTempo => 0;

        public event EventHandler<double>? NormalizationGainChanged { add { } remove { } }
        public event EventHandler<AudioEngineState>? StateChanged { add { } remove { } }
        public event EventHandler? Opened { add { } remove { } }
        public event EventHandler? Closed { add { } remove { } }
        public event EventHandler<double>? PositionChanged { add { } remove { } }
        public event EventHandler? PlaybackEnded { add { } remove { } }

        public Task<double> AnalyzePeakQuickAsync(CancellationToken cancellationToken) => Task.FromResult(1d);
        public Task<double> AnalyzePeakAsync(CancellationToken cancellationToken) => Task.FromResult(1d);
        public bool Init(int device = -1, int sampleRate = 44100) => true;
        public bool Open(string filePath) => true;
        public bool Open(IArchiveEntryInfo archiveEntry) => true;

        bool IAudioAnalysisOpenControl.Open(string filePath, bool analyzeSilence)
        {
            AnalyzeSilenceRequested = analyzeSilence;
            return true;
        }

        public bool Play() => true;
        public bool Pause() => true;
        public bool Stop() => true;
        public void Close() { }
        public void SetVolume(double volume01) { }
        public void SetPitch(int semitones) { }
        public void SetTempo(int percent) { }
        public void Dispose() { }

        public int BeginFadeSession() => ++_fadeSession;

        public bool SetFadeGain(int session, double gain, TimeSpan ramp)
        {
            FadeCalls.Add((gain, ramp));
            return session == _fadeSession;
        }

        public bool SetEqualizer(AudioEqualizerSettings settings)
        {
            Equalizer = settings;
            return true;
        }
    }

    /// <summary>The handleless engine with its own MIDI reader, as a second audio engine would have.</summary>
    private sealed class HandlelessReaderAudioEngine(IMidiEventReader reader) : HandlelessAudioEngine, IMidiEventReaderSource
    {
        public IMidiEventReader? GetMidiEventReader() => reader;
    }

    /// <summary>Records the order of reads, which on BASS are the native calls.</summary>
    private sealed class RecordingMidiReader(IMidiEventReader inner) : IMidiEventReader
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<MidiMark> GetMarks(MidiMarkKind kind)
        {
            Calls.Add($"Marks:{kind}");
            return inner.GetMarks(kind);
        }

        public IReadOnlyList<MidiTempoEvent> GetTempoEvents()
        {
            Calls.Add("Tempo");
            return inner.GetTempoEvents();
        }

        public double GetDurationSeconds()
        {
            Calls.Add("Duration");
            return inner.GetDurationSeconds();
        }

        public double PositionToSeconds(long position)
        {
            Calls.Add($"Seconds:{position}");
            return inner.PositionToSeconds(position);
        }
    }

    private sealed class RecordingHandleProvider : IMidiLyricProvider
    {
        public List<(int Handle, string Path)> Calls { get; } = [];

        public bool IsMidiFile(string path) => MidiLyricCueBuilder.IsMidiFile(path);

        public IReadOnlyList<SubtitleCue>? ExtractLyrics(int sourceHandle, string sourcePath, TimeSpan transitionDuration)
        {
            Calls.Add((sourceHandle, sourcePath));
            return null;
        }
    }

    private sealed class NoVideoFactory : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) =>
            new TestVideoEngine { IsOpen = false };
    }
}
