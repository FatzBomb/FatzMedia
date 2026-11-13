using System.Diagnostics;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Media;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Core.Threading;
using FatzMedia.Video.Core.Video;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 039A engine routing for network video with a real <see cref="PipelineMediaEngine"/> and
/// a test video engine: capability rows, retry, cancellation, modes, buffering forwarding.
/// </summary>
[Collection("File type registry")]
public sealed class NetworkVideoPlaybackTests
{
    static readonly Uri ClipUri = new("http://media.example/clip.mp4");
    // The hint, not the extension: the portable tests register no native video extensions.
    static readonly MediaInputSource ClipSource = MediaInputSource.FromUri(ClipUri, MediaNetworkContent.Video);
    static readonly Uri PlaylistUri = new("http://media.example/live/index.m3u8");

    static readonly MediaSourceCapabilities SeekableFile = Core.Network.NetworkVideoPolicy.Resolve(new(false, 4_000_000, true, 10));
    static readonly MediaSourceCapabilities LivePlaylist = Core.Network.NetworkVideoPolicy.Resolve(new(true, null, false, 0));

    [Fact]
    public void NetworkVideo_OpensThroughTheVideoPipelineWithTheProbedRow()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        var audio = new TestAudioEngine();
        using var engine = CreateEngine(audio, video);

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
        using var engine = CreateEngine(new TestAudioEngine(), video);

        Assert.True(engine.Open(MediaInputSource.FromUri(PlaylistUri)));

        Assert.Equal(MediaSourceClass.NetworkLive, engine.CurrentCapabilities!.SourceClass);
        Assert.False(engine.Seek(5));
    }

    [Fact]
    public void ConnectFailures_AreRetriedPerPolicy_ThenReportedAsOpenFailed()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => false, LastNetworkError = "Connection refused" };
        var policy = new NetworkRetryPolicy { MaxRetries = 2, InitialDelay = TimeSpan.Zero };
        using var engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(ClipSource));

        Assert.Equal(3, video.NetworkUrls.Count);
        Assert.Equal(MediaErrorCode.OpenFailed, error!.Code);
        Assert.Contains("Connection refused", error.Message, StringComparison.Ordinal);
        Assert.Null(engine.CurrentCapabilities);
    }

    [Fact]
    public void CertificateFailures_AreNotRetried_AndReportTheHost()
    {
        const string Rejected = "certificate verification failed for media.example: error:0A000086:SSL routines::certificate verify failed (Input/output error)";
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => false, LastNetworkError = Rejected };
        var policy = new NetworkRetryPolicy { MaxRetries = 3, InitialDelay = TimeSpan.Zero };
        using var engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(MediaInputSource.FromUri(new Uri("https://media.example/clip.mp4"), MediaNetworkContent.Video)));

        Assert.Single(video.NetworkUrls);
        Assert.Equal(MediaErrorCode.OpenFailed, error!.Code);
        Assert.Contains(Rejected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkTlsPolicy_ReachesTheVideoOpen_DefaultingToVerification()
    {
        FfmpegNetworkOptions? seen = null;
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, options) => { seen = options; return true; }, NetworkCapabilities = SeekableFile };
        using (var engine = CreateEngine(new TestAudioEngine(), video))
            Assert.True(engine.Open(ClipSource));
        Assert.Same(NetworkTlsPolicy.Default, seen!.Tls);

        var tls = new NetworkTlsPolicy { AllowInvalidCertificates = true };
        using var optedOut = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkTls = tls });
        Assert.True(optedOut.Open(MediaInputSource.FromUri(new Uri("https://media.example/clip.mp4"), MediaNetworkContent.Video)));
        Assert.Same(tls, seen.Tls);
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
        using var engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });

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
        using var engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        var open = Task.Run(() => engine.Open(ClipSource));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var cancel = Stopwatch.StartNew();
        engine.CancelPendingNetworkOpen();

        Assert.False(await open.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(cancel.Elapsed < TimeSpan.FromSeconds(2), $"cancel took {cancel.Elapsed}");
        Assert.Single(video.NetworkUrls);
        Assert.Contains("cancelled", error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadCancellation_EndsANetworkVideoConnectWithoutAnErrorEvent()
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
        using var engine = CreateEngine(new TestAudioEngine(), video, new MediaPlayerConfiguration { NetworkRetry = policy });
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;
        using var load = new CancellationTokenSource();

        var open = Task.Run(() =>
        {
            using (MediaOpenCancellation.Enter(load.Token))
                return engine.Open(ClipSource);
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        load.Cancel();

        Assert.False(await open.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Single(video.NetworkUrls);
        Assert.Null(error);
    }

    [Fact]
    public void CancelPendingNetworkOpen_WithNoOpenInProgress_DoesNothing()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using var engine = CreateEngine(new TestAudioEngine(), video);

        engine.CancelPendingNetworkOpen();

        Assert.True(engine.Open(ClipSource));
    }

    [Fact]
    public void BackgroundMode_AcceptsNetworkVideo_AndRejectsNetworkAudio()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        var audio = new TestAudioEngine();
        using var engine = CreateEngine(audio, video, new MediaPlayerConfiguration { Mode = MediaPlayerMode.Background });

        Assert.True(engine.Open(ClipSource));
        engine.Close();
        Assert.False(engine.Open(MediaInputSource.FromUri(new Uri("http://radio.example/live"), MediaNetworkContent.Audio)));
        Assert.Null(audio.LastNetworkUrl);
    }

    [Fact]
    public void WithoutAudio_FullModeStillOpensNetworkVideo()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using var engine = CreateEngine(new TestAudioEngine(), video, audioAvailable: false);

        Assert.True(engine.Open(ClipSource));
    }

    [Fact]
    public void AudioMode_WithoutAudio_RejectsNetworkVideoAsAudioUnavailable()
    {
        using var engine = CreateEngine(new TestAudioEngine(), new TestVideoEngine(), new MediaPlayerConfiguration { Mode = MediaPlayerMode.Audio }, audioAvailable: false);
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(ClipSource));
        Assert.Equal(MediaErrorCode.NativeLibraryMissing, error!.Code);
    }

    [Fact]
    public void VideoBufferingChanges_AreForwardedByTheEngine()
    {
        var video = new TestVideoEngine { IsOpen = false, NetworkOpen = (_, _) => true, NetworkCapabilities = SeekableFile };
        using var engine = CreateEngine(new TestAudioEngine(), video);
        List<bool> changes = [];
        engine.NetworkBufferingChanged += (_, args) => changes.Add(args.IsBuffering);
        Assert.True(engine.Open(ClipSource));

        video.RaiseNetworkBuffering(true, 0);
        video.RaiseNetworkBuffering(false, 0.5);

        Assert.Equal([true, false], changes);
    }

    static PipelineMediaEngine CreateEngine(
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
            new SystemPrecisionWaiterFactory(),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance),
            configuration ?? MediaPlayerConfiguration.Auto,
            audioAvailable: audioAvailable);

    sealed class FixedVideoFactory(TestVideoEngine video) : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) => video;
    }

}
