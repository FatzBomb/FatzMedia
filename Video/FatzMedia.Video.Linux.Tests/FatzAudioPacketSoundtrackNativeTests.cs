using System.Diagnostics;
using FatzMedia.Video.Audio;
using FatzMedia.Video.Core.Audio;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Linux.Tests;

/// <summary>
/// Soundtracks decoded by FatzAudio from FFmpeg's packets (037A, <c>AudioEngineDecodesSoundtracks</c>): real video
/// files from the shared sample folder (<c>FATZBOMB_SAMPLE_FILES</c>, else <c>D:\development\FatzBomb\shared\sample-files</c>),
/// FatzAudio's null output, real time. Returns at once without FFmpeg or the samples (CI has neither).
/// </summary>
[Collection(FatzAudioNativeGroup.Name)]
public sealed class FatzAudioPacketSoundtrackNativeTests : IDisposable
{
    FatzAudioEngine? _player;
    VideoAudioEngine? _audio;
    CancellationTokenSource? _pump;
    Task? _pumpTask;

    public void Dispose()
    {
        try
        {
            _pump?.Cancel();
            _pumpTask?.Wait();
        }
        finally
        {
            _pump?.Dispose();
            _audio?.Dispose();
            _player?.Dispose();
        }
    }

    [Theory]
    [InlineData(@"edge-cases\LunchLady Land [1080p H.264 23.976fps, stereo AAC, 4m51s].mp4")]
    [InlineData(@"formats\audio-sample [AC3 stereo, 46s].ac3")]
    [InlineData(@"formats\audio-sample [MP3 stereo, 15s].mp3")]
    public void PacketsPlay_FollowTheClock_SeekAndScaleWithTempo(string sample)
    {
        var audio = Open(sample, decodePackets: true);
        if (audio is null)
            return;

        Assert.True(audio.IsPacketMode);
        Assert.True(audio.Play());
        Thread.Sleep(300);
        var (moved, elapsed) = Measure(audio, TimeSpan.FromMilliseconds(800));
        Assert.InRange(moved / elapsed, 0.85, 1.15);

        // A seek lands within one packet (the packet that spans the target plays from its start).
        var target = Math.Min(10, audio.Duration / 2);
        Assert.True(audio.Seek(target));
        Thread.Sleep(50);
        Assert.InRange(audio.Position, target - 0.06, target + 0.2);

        audio.SetTempo(50);
        Thread.Sleep(300);
        (moved, elapsed) = Measure(audio, TimeSpan.FromMilliseconds(1000));
        Assert.InRange(moved / elapsed, 1.5 * 0.85, 1.5 * 1.15);
    }

    [Fact]
    public void WithoutTheSwitch_FfmpegDecodes()
    {
        var audio = Open(@"formats\audio-sample [MP3 stereo, 15s].mp3", decodePackets: false);
        if (audio is null)
            return;

        Assert.False(audio.IsPacketMode);
        Assert.True(audio.Play());
        Thread.Sleep(500);
        Assert.True(audio.Position > 0.2);
    }

    VideoAudioEngine? Open(string sample, bool decodePackets)
    {
        var root = Environment.GetEnvironmentVariable("FATZBOMB_SAMPLE_FILES") is { Length: > 0 } configured
            ? configured
            : @"D:\development\FatzBomb\shared\sample-files";
        var path = Path.Combine(root, sample.Replace('\\', Path.DirectorySeparatorChar));
        if (!FatzAudioNativeGroup.CanRun || !File.Exists(path) || FatzAudioVideoAudioNativeTests.FfmpegDirectory() is not { } ffmpeg)
            return null;

        FatzAudioVideoAudioNativeTests.ActivateFfmpeg(ffmpeg);
        _player = (FatzAudioEngine)new FatzAudioEngineFactory(nullOutput: true).Create(new MediaPlayerConfiguration
        {
            Normalization = new NormalizationOptions { CacheDirectory = string.Empty },
            AudioEngineDecodesSoundtracks = decodePackets,
        });
        // Network-style open (no loudness scan): the sample folder is read-only and nothing is written next to it.
        var audio = _audio = new VideoAudioEngine(((IPushAudioOutputSource)_player).CreatePushAudioOutput(), null);
        Assert.True(audio.Open(path));
        Assert.True(audio.PrebufferAudio());
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
        return audio;
    }

    static (double Moved, double ElapsedSeconds) Measure(VideoAudioEngine audio, TimeSpan duration)
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

        return (audio.Position - start, watch.Elapsed.TotalSeconds);
    }
}
