using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Media;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Pipeline;
using System.Diagnostics;

namespace FatzBomb.MediaControl.Tests;

/// <summary>
/// Stage 039A engine routing for network video with a real <see cref="PipelineMediaEngine"/> and
/// a test video engine: capability rows, retry, cancellation, modes, buffering forwarding.
/// </summary>
[Collection("File type registry")]
public sealed class NetworkVideoPlaybackTests
{
    private static readonly Uri ClipUri = new("http://media.example/clip.mp4");
    // The hint, not the extension: the portable tests register no native video extensions.
    private static readonly MediaInputSource ClipSource = MediaInputSource.FromUri(ClipUri, MediaNetworkContent.Video);
    private static readonly Uri PlaylistUri = new("http://media.example/live/index.m3u8");

    private static readonly MediaSourceCapabilities SeekableFile = Core.Network.NetworkVideoPolicy.Resolve(new(false, 4_000_000, true, 10));
    private static readonly MediaSourceCapabilities LivePlaylist = Core.Network.NetworkVideoPolicy.Resolve(new(true, null, false, 0));

    [Fact]
    public void NetworkVideo_OpensThroughTheVideoPipelineWithTheProbedRow()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        var audio = new TestAudioEngine();
        using PipelineMediaEngine engine = CreateEngine(audio, video);

        Assert.True(engine.Open(ClipSource));

        Assert.Equal([ClipUri.AbsoluteUri], video.NetworkUrls);
        Assert.Null(audio.LastNetworkUrl);
        Assert.Same(SeekableFile, engine.CurrentCapabilities);
        Assert.True(engine.Seek(5));
    }

    [Fact]
    public void HlsPlaylist_ClassifiesAsVideo_AndALiveRowRejectsSeeks()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = LivePlaylist };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video);

        Assert.True(engine.Open(MediaInputSource.FromUri(PlaylistUri)));

        Assert.Equal(MediaSourceClass.NetworkLive, engine.CurrentCapabilities!.SourceClass);
        Assert.False(engine.Seek(5));
    }

    [Fact]
    public void ConnectFailures_AreRetriedPerPolicy_ThenReportedAsOpenFailed()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => false, LastNetworkError = "Connection refused" };
        var policy = new NetworkRetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(ClipSource));

        Assert.Equal(3, video.NetworkUrls.Count);
        Assert.Equal(MediaErrorCode.OpenFailed, error!.Code);
        Assert.Contains("Connection refused", error.Message, StringComparison.Ordinal);
        Assert.Null(engine.CurrentCapabilities);
    }

    [Fact]
    public void ARetrySucceeds_AfterAFailedAttempt()
    {
        var attempts = 0;
        var video = new TestVideoEngine
        {
            IsOpen = false,
            NetworkCapabilities = SeekableFile,
            LastNetworkError = "Connection refused",
            NetworkOpen = (_, _) => ++attempts == 2,
        };
        var policy = new NetworkRetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });

        Assert.True(engine.Open(ClipSource));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancelPendingNetworkOpen_EndsAConnectInProgressAtOnce()
    {
        using var started = new ManualResetEventSlim();
        var video = new TestVideoEngine
        {
            IsOpen = false,
            LastNetworkError = "cancelled",
            NetworkOpen = (url, options) =>
            {
                started.Set();
                _ = options.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                return false;
            },
        };
        var policy = new NetworkRetryPolicy { MaxRetries = 5, InitialDelay = TimeSpan.FromSeconds(10) };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Task<bool> open = Task.Run(() => engine.Open(ClipSource));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var cancel = Stopwatch.StartNew();
        ((INetworkMediaControl)engine).CancelPendingNetworkOpen();

        Assert.Same(open, await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(5))));
        Assert.False(await open);
        Assert.True(cancel.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {cancel.Elapsed}");
        Assert.Single(video.NetworkUrls);
        Assert.Contains("cancelled", error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelPendingNetworkOpen_WithNoOpenInProgress_DoesNothing()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video);

        ((INetworkMediaControl)engine).CancelPendingNetworkOpen();

        Assert.True(engine.Open(ClipSource));
    }

    [Fact]
    public void BackgroundMode_AcceptsNetworkVideo_AndRejectsNetworkAudio()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        var audio = new TestAudioEngine();
        using PipelineMediaEngine engine = CreateEngine(audio, video, new MediaPlayerConfiguration { Mode = MediaPlayerMode.Background });

        Assert.True(engine.Open(ClipSource));
        engine.Close();
        Assert.False(engine.Open(MediaInputSource.FromUri(new Uri("http://radio.example/live"), MediaNetworkContent.Audio)));
        Assert.Null(audio.LastNetworkUrl);
    }

    [Fact]
    public void WithoutBass_FullModeStillOpensNetworkVideo()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video, audioAvailable: false);

        Assert.True(engine.Open(ClipSource));
    }

    [Fact]
    public void AudioMode_WithoutBass_RejectsNetworkVideoAsAudioUnavailable()
    {
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), new TestVideoEngine(), new MediaPlayerConfiguration { Mode = MediaPlayerMode.Audio }, audioAvailable: false);
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(ClipSource));
        Assert.Equal(MediaErrorCode.NativeLibraryMissing, error!.Code);
    }

    [Fact]
    public void VideoBufferingChanges_AreForwardedByTheEngine()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using PipelineMediaEngine engine = CreateEngine(new TestAudioEngine(), video);
        List<bool> changes = [];
        ((INetworkMediaControl)engine).NetworkBufferingChanged += (_, args) => changes.Add(args.IsBuffering);
        Assert.True(engine.Open(ClipSource));

        video.RaiseNetworkBuffering(true, 0);
        video.RaiseNetworkBuffering(false, 0.5);

        Assert.Equal([true, false], changes);
    }

    private static PipelineMediaEngine CreateEngine(
        TestAudioEngine audio,
        TestVideoEngine video,
        MediaPlayerConfiguration? configuration = null,
        bool audioAvailable = true) =>
        new(
            audio,
            new TestCdgEngine(),
            new SubtitleEngine(),
            new NotSupportedArchiveService(),
            new FixedVideoFactory(video),
            new NoMidiLyrics(),
            new SystemPrecisionWaiterFactory(),
            SoftwareOnlyVideoHardwareAccelerationServices.Instance,
            configuration ?? MediaPlayerConfiguration.Auto,
            audioAvailable: audioAvailable);

    private sealed class FixedVideoFactory(TestVideoEngine video) : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) => video;
    }

    private sealed class NoMidiLyrics : IMidiLyricProvider
    {
        public bool IsMidiFile(string path) => false;

        public IReadOnlyList<SubtitleCue>? ExtractLyrics(int sourceHandle, string path, TimeSpan transitionDuration) => null;
    }
}
