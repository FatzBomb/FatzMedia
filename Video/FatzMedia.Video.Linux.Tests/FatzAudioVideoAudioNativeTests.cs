using System.Diagnostics;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Video audio on FatzAudio (037A slice 5): <see cref="VideoAudioEngine"/> decoding with the real FFmpeg and
/// playing into a FatzAudio push stream on the null output device, its position following FatzAudio's clock.
/// Needs FFmpeg: <c>FATZBOMB_FFMPEG_DIR</c> (or <c>FATZBOMB_LINUX_FFMPEG_DIR</c> on Linux), else on Windows the
/// test player's <c>native</c> folder; returns at once without it. Real time, so the bounds are generous.
/// </summary>
[Collection(FatzAudioNativeGroup.Name)]
public sealed class FatzAudioVideoAudioNativeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("fbm-fatzaudio-video-").FullName;
    FatzAudioEngine? _player;
    VideoAudioEngine? _audio;
    CancellationTokenSource? _pump;
    Task? _pumpTask;

    public void Dispose()
    {
        StopPump();
        _audio?.Dispose();
        _player?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Position_FollowsFatzAudiosClock_AndAdvancesInRealTime()
    {
        var audio = Open(6);
        if (audio is null)
            return;

        Assert.InRange(audio.Duration, 5.9, 6.1);
        Assert.Equal(0, audio.Position, 3);
        Assert.True(audio.Play());
        var (start, end, elapsed) = Measure(audio, TimeSpan.FromMilliseconds(800));
        Assert.InRange((end - start) / elapsed, 0.85, 1.15);
    }

    [Theory]
    [InlineData(-25, 0.75)]
    [InlineData(50, 1.5)]
    public void Tempo_ScalesTheClock(int percent, double rate)
    {
        var audio = Open(10);
        if (audio is null)
            return;

        audio.SetTempo(percent);
        Assert.True(audio.Play());
        Thread.Sleep(300); // past the start and the tempo stretcher's priming
        var (start, end, elapsed) = Measure(audio, TimeSpan.FromMilliseconds(1200));
        Assert.InRange((end - start) / elapsed, rate * 0.88, rate * 1.12);
    }

    [Fact]
    public void Seek_WhilePlaying_StartsAtTheTarget_AndGoesOn()
    {
        var audio = Open(10);
        if (audio is null)
            return;

        Assert.True(audio.Play());
        Thread.Sleep(300);
        Assert.True(audio.Seek(6.0));
        Assert.InRange(audio.Position, 5.99, 6.15);
        Thread.Sleep(500);
        Assert.InRange(audio.Position, 6.2, 6.8);
    }

    [Fact]
    public void Pause_HoldsTheClock_ASeekWhilePausedShowsTheTarget_AndResumeGoesOn()
    {
        var audio = Open(10);
        if (audio is null)
            return;

        Assert.True(audio.Play());
        Thread.Sleep(400);
        Assert.True(audio.Pause());
        Thread.Sleep(100);
        var paused = audio.Position;
        Thread.Sleep(300);
        Assert.Equal(paused, audio.Position, 3);

        Assert.True(audio.Seek(4.0, resumePlayback: false));
        Assert.Equal(4.0, audio.Position, 2);
        Thread.Sleep(200);
        Assert.Equal(4.0, audio.Position, 2);

        Assert.True(audio.Play());
        Thread.Sleep(500);
        Assert.InRange(audio.Position, 4.2, 4.8);
    }

    [Fact]
    public void Loop_WrapsGaplessly_AndCountsEachWrap()
    {
        var audio = Open(1.0);
        if (audio is null)
            return;

        audio.LoopPlayback = true;
        Assert.True(audio.Play());
        var watch = Stopwatch.StartNew();
        var previous = 0.0;
        var wrapsSeen = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(2.6))
        {
            var position = audio.Position;
            Assert.InRange(position, 0, 1.0);
            if (position < previous - 0.5)
                wrapsSeen++;
            previous = position;
            Thread.Sleep(10);
        }

        Assert.InRange(audio.CompletedLoopCount, 2, 3);
        Assert.Equal(audio.CompletedLoopCount, wrapsSeen);
    }

    [Fact]
    public void Stop_GoesBackToTheStart()
    {
        var audio = Open(6);
        if (audio is null)
            return;

        Assert.True(audio.Play());
        Thread.Sleep(400);
        Assert.True(audio.Stop());
        Assert.Equal(0, audio.Position, 3);
        Assert.Equal(AudioEngineState.Stopped, audio.State);
        Assert.True(audio.Play());
        Thread.Sleep(400);
        Assert.InRange(audio.Position, 0.2, 0.6);
    }

    [Fact]
    public void Normalization_IsFatzAudiosLoudness_FromTheScanByPath()
    {
        var audio = Open(6, amplitude: 0.1);
        if (audio is null)
            return;

        var gains = new System.Collections.Concurrent.BlockingCollection<double>();
        audio.NormalizationGainChanged += (_, gain) => gains.Add(gain);
        Assert.True(audio.Play());
        Assert.True(gains.TryTake(out var gain, TimeSpan.FromSeconds(10)), "no loudness for the soundtrack");
        // A 0.1 sine measures about -20.7 LUFS: +6.7 dB to reach -14.
        Assert.InRange(gain, 1.9, 2.5);
        Assert.Equal(gain, audio.NormalizationGain, 2);
    }

    VideoAudioEngine? Open(double seconds, double amplitude = 0.5)
    {
        if (!FatzAudioNativeGroup.CanRun || FfmpegDirectory() is not { } ffmpegDirectory)
            return null;

        ActivateFfmpeg(ffmpegDirectory);
        var wav = FatzAudioTestMedia.WriteWav(_root, $"tone-{Guid.NewGuid():N}.wav", new FatzAudioTestMedia.Section(seconds, Amplitude: amplitude));
        _player = (FatzAudioEngine)new FatzAudioEngineFactory(nullOutput: true).Create(new MediaPlayerConfiguration
        {
            Normalization = new NormalizationOptions { CacheDirectory = Path.Combine(_root, "cache") },
        });
        var audio = _audio = new VideoAudioEngine(((IPushAudioOutputSource)_player).CreatePushAudioOutput(), null);
        Assert.True(audio.Open(wav));
        Assert.True(audio.PrebufferAudio());
        StartPump(audio);
        return audio;
    }

    // What the video pipeline's decode loop does: keep the output fed.
    void StartPump(VideoAudioEngine audio)
    {
        _pump = new CancellationTokenSource();
        var token = _pump.Token;
        _pumpTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                if (!audio.DecodeAndPushAudio())
                    Thread.Sleep(5);
            }
        });
    }

    void StopPump()
    {
        _pump?.Cancel();
        _pumpTask?.Wait();
        _pump?.Dispose();
    }

    static (double Start, double End, double ElapsedSeconds) Measure(VideoAudioEngine audio, TimeSpan duration)
    {
        var watch = Stopwatch.StartNew();
        var start = audio.Position;
        var previous = start;
        while (watch.Elapsed < duration)
        {
            var position = audio.Position;
            Assert.True(position >= previous - 0.0005, $"Position went back: {previous:F4} -> {position:F4}");
            previous = position;
            Thread.Sleep(5);
        }

        return (start, audio.Position, watch.Elapsed.TotalSeconds);
    }

    internal static void ActivateFfmpeg(string directory)
    {
        if (OperatingSystem.IsLinux())
            FfmpegAudioReaderNativeTests.ActivateFfmpeg(directory);
        else
            FFmpeg.AutoGen.ffmpeg.RootPath = directory;
    }

    internal static string? FfmpegDirectory()
    {
        foreach (var variable in new[] { "FATZBOMB_FFMPEG_DIR", "FATZBOMB_LINUX_FFMPEG_DIR" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured && Directory.Exists(configured))
                return configured;
        }

        if (!OperatingSystem.IsWindows())
            return null;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var native = Path.Combine(directory.FullName, "FatzMedia.Video.Sample.Player", "native");
            if (Directory.Exists(native) && Directory.EnumerateFiles(native, "avcodec-*.dll").Any())
                return native;
        }

        return null;
    }
}
