using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Audio;
using FatzBomb.MediaControl.Core.Configuration;
using FatzBomb.MediaControl.Core.Files;
using FatzBomb.MediaControl.Core.Subtitle;
using FatzBomb.MediaControl.Core.Threading;
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
    public void NetworkVideo_IsRejectedWithUnsupportedFormat(string address, MediaNetworkContent hint)
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
            new NullArchiveService(),
            new ClosedVideoFactory(),
            new NoMidiLyrics(),
            new SystemPrecisionWaiterFactory(),
            SoftwareOnlyVideoHardwareAccelerationServices.Instance,
            MediaPlayerConfiguration.Auto);

    private sealed class NullArchiveService : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new NotSupportedException();
    }

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
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new NetworkTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var buffering = new TaskCompletionSource<MediaBufferingChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new TaskCompletionSource<MediaStreamMetadataChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.BufferingChanged += (_, e) => buffering.TrySetResult(e);
        player.StreamMetadataChanged += (_, e) => metadata.TrySetResult(e);

        engine.TestAudio.RaiseBuffering(true, 0.3);
        engine.TestAudio.RaiseStreamMetadata("One More Time", "Daft Punk");

        MediaBufferingChangedEventArgs bufferingArgs = await buffering.Task.WaitAsync(Timeout);
        MediaStreamMetadataChangedEventArgs metadataArgs = await metadata.Task.WaitAsync(Timeout);
        Assert.True(bufferingArgs.IsBuffering);
        Assert.Equal(0.3, bufferingArgs.BufferFill);
        Assert.Equal("Daft Punk", metadataArgs.Artist);
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
            new NullCoordinator();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            new Provider(engine);
    }

    private sealed class Provider(TestMediaEngine engine) : IServiceProvider
    {
        private readonly IArchiveService _archives = new NullArchives();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMediaEngine) ? engine
            : serviceType == typeof(IAudioEngine) ? engine.TestAudio
            : serviceType == typeof(IArchiveService) ? _archives
            : null;
    }

    private sealed class NullArchives : IArchiveService
    {
        public IArchiveHandle Open(string path) => throw new NotSupportedException();
    }

    private sealed class NullCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented { add { } remove { } }

        public VideoDisplayMode VideoDisplayMode { get; set; }
        public BaseVisualTransform BaseVisualTransform { get; set; }

        public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => false;
        public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;
        public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;
        public bool DetachPresentationSurface(string surfaceId) => false;
        public void RefreshCurrentPosition() { }
        public void RunOnRenderThread(Action action) => action();
        public void StartRendering() { }
        public void StopRendering() { }
        public void RefreshAttachments() { }
        public void Dispose() { }
    }
}
