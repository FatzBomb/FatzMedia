using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
using FatzBomb.MediaControl.Core.Video;
using FatzBomb.MediaControl.Pipeline;

namespace FatzBomb.MediaControl.Tests;

[Collection("File type registry")]
public sealed class NetworkPipelineTests
{
    private static readonly Uri RadioUri = new("http://radio.example/live");

    [Fact]
    public void OpenNetworkAudio_RoutesToNetworkAudioSourceAndReportsProbedCapabilities()
    {
        var audio = new TestAudioEngine
        {
            NetworkCapabilities = MediaSourceCapabilityMatrix.ForNetwork(new NetworkStreamProbe(null, false, true)),
        };
        using PipelineMediaEngine engine = CreateEngine(audio);

        Assert.True(engine.Open(MediaInputSource.FromUri(RadioUri, MediaNetworkContent.Audio)));

        Assert.Equal(RadioUri.AbsoluteUri, audio.LastNetworkUrl);
        Assert.Equal(MediaSourceClass.NetworkLive, engine.CurrentCapabilities!.SourceClass);
        Assert.True(engine.IsOpen);
    }

    [Fact]
    public void AutoHint_UnknownExtensionOpensAsAudio()
    {
        var audio = new TestAudioEngine();
        using PipelineMediaEngine engine = CreateEngine(audio);

        Assert.True(engine.Open(MediaInputSource.FromUri(RadioUri)));
        Assert.NotNull(audio.LastNetworkUrl);
    }

    [Fact]
    public void UnprobedCapabilitiesFallBackToConservativeLiveRow()
    {
        var audio = new TestAudioEngine { NetworkCapabilities = null };
        using PipelineMediaEngine engine = CreateEngine(audio);

        Assert.True(engine.Open(MediaInputSource.FromUri(RadioUri)));
        Assert.Same(MediaSourceCapabilityMatrix.NetworkUnprobed, engine.CurrentCapabilities);
    }

    [Theory]
    [InlineData("https://cdn.example/clip.mp4", MediaNetworkContent.Auto)]
    [InlineData("https://cdn.example/stream", MediaNetworkContent.Video)]
    public void NetworkVideo_WithNoPlayableVideoStream_IsRejectedWithUnsupportedFormat(string address, MediaNetworkContent hint)
    {
        FileTypes.Video.Add(".mp4");
        try
        {
            var audio = new TestAudioEngine();
            using PipelineMediaEngine engine = CreateEngine(audio);
            MediaErrorInfo? error = null;
            ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

            Assert.False(engine.Open(MediaInputSource.FromUri(new Uri(address), hint)));

            Assert.Null(audio.LastNetworkUrl);
            Assert.Equal(MediaErrorCode.UnsupportedFormat, error!.Code);
        }
        finally
        {
            _ = FileTypes.Video.Remove(".mp4");
        }
    }

    [Theory]
    [InlineData("https://radio.example/live")]
    [InlineData("http://radio.example/live/index.m3u8")]
    public void HttpsAndHlsAudio_DoNotOpenThroughTheAudioEnginesOwnNetworkSupport(string address)
    {
        // ENG-034: BASS cannot verify certificates (and has no HLS); these go through FFmpeg's
        // network path into the soundtrack engine. The portable tests load no FFmpeg, so the open
        // itself cannot succeed here (Linux native tests cover it); what matters is the route.
        var audio = new TestAudioEngine();
        using PipelineMediaEngine engine = CreateEngine(audio);

        try
        {
            _ = engine.Open(MediaInputSource.FromUri(new Uri(address), MediaNetworkContent.Audio));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            // FFmpeg is not loaded in the portable tests.
        }

        Assert.Null(audio.LastNetworkUrl);
    }

    [Fact]
    public void HttpsAudio_WithoutBass_IsRejectedAsAudioUnavailable()
    {
        var audio = new TestAudioEngine();
        using var engine = new PipelineMediaEngine(
            audio,
            new TestCdgEngine(),
            new SubtitleEngine(),
            new NotSupportedArchiveService(),
            new ClosedVideoFactory(),
            new NoMidiLyrics(),
            new SystemPrecisionWaiterFactory(),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance),
            MediaPlayerConfiguration.Auto,
            audioAvailable: false);
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(MediaInputSource.FromUri(new Uri("https://radio.example/live"))));

        Assert.Equal(MediaErrorCode.NativeLibraryMissing, error!.Code);
        Assert.Null(audio.LastNetworkUrl);
    }

    [Fact]
    public void ConnectFailure_RaisesOpenFailed()
    {
        var audio = new TestAudioEngine { NetworkOpenSucceeds = false };
        using PipelineMediaEngine engine = CreateEngine(audio);
        MediaErrorInfo? error = null;
        ((IMediaErrorSource)engine).ErrorOccurred += info => error = info;

        Assert.False(engine.Open(MediaInputSource.FromUri(RadioUri)));
        Assert.Equal(MediaErrorCode.OpenFailed, error!.Code);
        Assert.Null(engine.CurrentCapabilities);
    }

    [Fact]
    public void Seek_IsRejectedForNonSeekableNetworkSources()
    {
        var audio = new TestAudioEngine { NetworkCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using PipelineMediaEngine engine = CreateEngine(audio);
        Assert.True(engine.Open(MediaInputSource.FromUri(RadioUri)));

        Assert.False(engine.Seek(10));
    }

    [Fact]
    public void Close_ClearsCapabilities()
    {
        var audio = new TestAudioEngine();
        using PipelineMediaEngine engine = CreateEngine(audio);
        Assert.True(engine.Open(MediaInputSource.FromUri(RadioUri)));

        engine.Close();

        Assert.Null(engine.CurrentCapabilities);
    }

    [Theory]
    [InlineData(new[] { "HTTP/1.1 200 OK", "Content-Length: 4096" }, 4096L)]
    [InlineData(new[] { "content-length:  12 " }, 12L)]
    [InlineData(new[] { "Content-Length: 0" }, null)]
    [InlineData(new[] { "Content-Length: abc" }, null)]
    [InlineData(new[] { "icy-name: Radio" }, null)]
    public void TryReadContentLength_ParsesHeader(string[] headers, long? expected) =>
        Assert.Equal(expected, AudioStreamManager.TryReadContentLength(headers));

    internal static PipelineMediaEngine CreateEngine(TestAudioEngine audio) =>
        new(
            audio,
            new TestCdgEngine(),
            new SubtitleEngine(),
            new NotSupportedArchiveService(),
            new ClosedVideoFactory(),
            new NoMidiLyrics(),
            new SystemPrecisionWaiterFactory(),
            new RendererVideoDecodeServices(SoftwareOnlyVideoHardwareAccelerationServices.Instance),
            MediaPlayerConfiguration.Auto);

    private sealed class ClosedVideoFactory : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) =>
            new TestVideoEngine { IsOpen = false };
    }

    private sealed class NoMidiLyrics : IMidiLyricProvider
    {
        public bool IsMidiFile(string path) => false;

        public IReadOnlyList<SubtitleCue>? ExtractLyrics(int sourceHandle, string sourcePath, TimeSpan transitionDuration) => null;
    }
}

public sealed class PlayerNetworkEventTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task BufferingAndMetadata_AreForwardedToHost()
    {
        var engine = new TestMediaEngine { CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var buffering = new TaskCompletionSource<MediaBufferingChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<MediaStreamMetadataChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.BufferingChanged += (_, e) => buffering.TrySetResult(e);
        player.StreamMetadataChanged += (_, e) => metadata.TrySetResult(e);
        Assert.True(player.Load("radio.mp3"));

        engine.TestAudio.RaiseBuffering(true, 0.3);
        engine.TestAudio.RaiseStreamMetadata("One More Time", "Daft Punk");

        MediaBufferingChangedEventArgs bufferingArgs = await buffering.Task.WaitAsync(Timeout);
        MediaStreamMetadataChangedEventArgs metadataArgs = await metadata.Task.WaitAsync(Timeout);
        Assert.True(bufferingArgs.IsBuffering);
        Assert.Equal(0.3, bufferingArgs.BufferFill);
        Assert.Equal("Daft Punk", metadataArgs.Artist);
    }

    [Fact]
    public async Task NetworkAudioTitles_FromTheEngine_AreForwardedToHost()
    {
        // ENG-034: https audio plays through FFmpeg; its titles come from the engine, not BASS.
        var engine = new TestMediaEngine { CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var metadata = new TaskCompletionSource<MediaStreamMetadataChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.StreamMetadataChanged += (_, e) => metadata.TrySetResult(e);
        Assert.True(player.Load("radio.mp3"));

        engine.RaiseNetworkStreamMetadata("Around the World", "Daft Punk", "Daft Punk - Around the World");

        MediaStreamMetadataChangedEventArgs args = await metadata.Task.WaitAsync(Timeout);
        Assert.Equal("Around the World", args.Title);
        Assert.Equal("Daft Punk", args.Artist);
    }

    [Fact]
    public async Task NetworkVideoBuffering_FromTheEngine_IsForwardedToHost()
    {
        var engine = new TestMediaEngine { CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var buffering = new TaskCompletionSource<MediaBufferingChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.BufferingChanged += (_, e) => buffering.TrySetResult(e);
        Assert.True(player.Load("clip.mp4"));

        engine.RaiseNetworkBuffering(true, 0.1);

        MediaBufferingChangedEventArgs args = await buffering.Task.WaitAsync(Timeout);
        Assert.True(args.IsBuffering);
        Assert.Equal(0.1, args.BufferFill);
    }

    [Fact]
    public void CloseAndDispose_CancelANetworkOpenInProgress()
    {
        var engine = new TestMediaEngine();
        var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);

        player.Close();
        Assert.Equal(1, engine.CancelPendingNetworkOpenCount);
        player.Dispose();
        Assert.Equal(2, engine.CancelPendingNetworkOpenCount);
    }

    [Fact]
    public async Task LoopMedia_NetworkSourceEnd_StopsInsteadOfRestarting()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("radio.mp3"));
        Assert.True(player.Play());
        engine.CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed;
        player.LoopMedia = true;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var looped = 0;
        player.MediaEnded += (_, _) => ended.TrySetResult();
        player.MediaLooped += (_, _) => Interlocked.Increment(ref looped);

        engine.CompletePlayback();
        await ended.Task.WaitAsync(Timeout);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);

        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(0, Volatile.Read(ref looped));
    }

    [Fact]
    public async Task MetadataPublishedDuringLoad_ArrivesAfterMediaLoaded()
    {
        var engine = new TestMediaEngine { CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var metadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLoaded += (_, _) => order.Enqueue("loaded");
        player.StreamMetadataChanged += (_, _) =>
        {
            order.Enqueue("metadata");
            metadata.TrySetResult();
        };
        engine.BeforeOpen = () => engine.TestAudio.RaiseStreamMetadata("Title", "Artist");

        Assert.True(player.Load("radio.mp3"));
        await metadata.Task.WaitAsync(Timeout);

        Assert.Equal(["loaded", "metadata"], order);
    }

    [Fact]
    public async Task NotificationsQueuedBehindASwitchToALocalSource_AreDropped()
    {
        // Review follow-up: a buffering or title event from the old stream, queued behind a Load
        // of a local file, must not reach the host as the new source's state.
        var engine = new TestMediaEngine { CurrentCapabilities = MediaSourceCapabilityMatrix.NetworkUnprobed };
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var raised = 0;
        player.BufferingChanged += (_, _) => Interlocked.Increment(ref raised);
        player.StreamMetadataChanged += (_, _) => Interlocked.Increment(ref raised);
        engine.BeforeOpen = () =>
        {
            engine.TestAudio.RaiseBuffering(true, 0.2);
            engine.TestAudio.RaiseStreamMetadata("Old", "Stream");
            engine.CurrentCapabilities = MediaSourceCapabilityMatrix.LocalFile;
        };

        Assert.True(player.Load("song.mp3"));
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);
        await player.FlushControlOperationsAsync().WaitAsync(Timeout);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public void Dispose_UnsubscribesNetworkEvents()
    {
        var engine = new TestMediaEngine();
        var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var raised = 0;
        player.BufferingChanged += (_, _) => Interlocked.Increment(ref raised);

        player.Dispose();
        engine.TestAudio.RaiseBuffering(true, 0.1);

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public void CurrentSourceCapabilities_NullWhenClosed()
    {
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);

        Assert.Null(player.CurrentSourceCapabilities);
    }

    private sealed class NetworkTestPlatformFactory(TestMediaEngine engine) : IMediaControlPlatformFactory
    {
        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            new NullPresentationCoordinator();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            new Provider(engine);
    }

    private sealed class Provider(TestMediaEngine engine) : IServiceProvider
    {
        private readonly IArchiveService _archives = new NotSupportedArchiveService();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMediaEngine) ? engine
            : serviceType == typeof(IAudioEngine) ? engine.TestAudio
            : serviceType == typeof(IArchiveService) ? _archives
            : null;
    }
}
