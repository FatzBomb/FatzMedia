using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Audio.Fade;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Internal consumers reach the audio engine through its seams (fades, filters, trim).
/// </summary>
public sealed class AudioEngineSeamTests
{
    [Fact]
    public void AudioEngines_ProvideTheEffectAndTrimSeams()
    {
        foreach (var engine in new[] { typeof(FatzAudioEngine), typeof(VideoAudioEngine), typeof(SoundtrackAudioEngine) })
        {
            Assert.True(typeof(IAudioFadeGainTarget).IsAssignableFrom(engine), engine.Name);
            Assert.True(typeof(IAudioFilterHost).IsAssignableFrom(engine), engine.Name);
        }

        Assert.True(typeof(IAudioTrimWindowProvider).IsAssignableFrom(typeof(FatzAudioEngine)));
        Assert.True(typeof(IAudioAnalysisOpenControl).IsAssignableFrom(typeof(FatzAudioEngine)));
    }
    [Fact]
    public async Task Fades_RunThroughTheGainSeam()
    {
        var engine = new SeamAudioEngine();
        var runner = new AudioFadeRunner(static (_, _) => Task.CompletedTask);

        Assert.True(await runner.RunAsync(engine, AudioFadeEnvelope.Outgoing, TimeSpan.FromSeconds(1), 0, CancellationToken.None));

        Assert.Equal([(1d, TimeSpan.Zero), (0d, TimeSpan.FromSeconds(1))], engine.FadeCalls);
    }

    [Fact]
    public void TrimAndEqualizer_RunThroughTheirSeams()
    {
        var engine = new SeamAudioEngine
        {
            Duration = 100,
            ActiveTrimWindow = new MediaAnalysisResult(100, 5, 95, 90, [new SilenceRegion(0, 5), new SilenceRegion(95, 100)], 1),
        };
        using var pipeline = new AudioPipeline(engine);
        var settings = AudioEqualizerSettings.Disabled;

        Assert.True(pipeline.Open("song.mp3"));
        Assert.True(engine.SetEqualizer(settings));

        Assert.Equal(90, pipeline.DurationSeconds);
        Assert.True(engine.AnalyzeSilenceRequested);
        Assert.Same(settings, engine.Equalizer);
    }

    /// <summary>A minimal engine with the analysis, trim, fade and filter seams.</summary>
    sealed class SeamAudioEngine : IAudioEngine, IAudioAnalysisOpenControl, IAudioTrimWindowProvider, IAudioFadeGainTarget, IAudioFilterHost
    {
        int _fadeSession;

        public List<(double Gain, TimeSpan Ramp)> FadeCalls { get; } = [];
        public bool AnalyzeSilenceRequested { get; private set; }
        public MediaAnalysisResult? ActiveTrimWindow { get; init; }
        public AudioEqualizerSettings Equalizer { get; private set; } = AudioEqualizerSettings.Disabled;

        public AudioEngineState State { get; private set; } = AudioEngineState.Stopped;

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
}
