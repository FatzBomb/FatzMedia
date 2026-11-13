using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Subtitle;
using FatzMedia.Video.Pipeline;

namespace FatzMedia.Video.Tests;

/// <summary>
/// ENG-039 P5-1: embedded subtitle tracks are decoded on a background task after the video opened,
/// cancelled by the next open, and announced through <c>SubtitleTracksChanged</c>.
/// </summary>
public sealed class EmbeddedSubtitleScanTests : IDisposable
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    static readonly EmbeddedSubtitleTrack[] EmbeddedTracks =
    [
        new(2, "SUBRIP", [new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Embedded")]),
    ];

    readonly string _sidecar = Path.Combine(Path.GetTempPath(), $"fatzbomb-scan-{Guid.NewGuid():N}.srt");

    public EmbeddedSubtitleScanTests() =>
        File.WriteAllText(_sidecar, "1\n00:00:01,000 --> 00:00:02,000\nSidecar\n");

    public void Dispose() => File.Delete(_sidecar);

    [Fact]
    public async Task EmbeddedTracksArrivingLater_AreAddedAndSelected_WithoutBlockingTheOpen()
    {
        using var decoder = new GatedDecoder(EmbeddedTracks);
        using var coordinator = CreateCoordinator(decoder.Decode);
        var changes = 0;
        coordinator.SubtitleTracksChanged += (_, _) => Interlocked.Increment(ref changes);

        Assert.True(coordinator.OpenVideo("movie.mkv"));
        Assert.Empty(coordinator.SubtitleTracks);
        Assert.Null(coordinator.SelectedSubtitleTrackId);

        decoder.Release();
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);

        var track = Assert.Single(coordinator.SubtitleTracks);
        Assert.Equal("embedded:2", track.Id);
        Assert.Equal("embedded:2", coordinator.SelectedSubtitleTrackId);
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task EmbeddedTracksArrivingLater_TakePrecedenceOverTheAutoSelectedSidecar()
    {
        using var decoder = new GatedDecoder(EmbeddedTracks);
        using var coordinator = CreateCoordinator(decoder.Decode);

        Assert.True(coordinator.OpenVideo("movie.mkv", _sidecar));
        Assert.Equal("sidecar", coordinator.SelectedSubtitleTrackId);

        decoder.Release();
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);

        Assert.Equal("embedded:2", coordinator.SelectedSubtitleTrackId);
        Assert.Equal(["embedded:2", "sidecar"], coordinator.SubtitleTracks.Select(static track => track.Id).Order());
    }

    [Theory]
    [InlineData("sidecar")]
    [InlineData(null)]
    public async Task EmbeddedTracksArrivingLater_NeverOverrideTheTrackTheUserChose(string? chosen)
    {
        using var decoder = new GatedDecoder(EmbeddedTracks);
        using var coordinator = CreateCoordinator(decoder.Decode);
        Assert.True(coordinator.OpenVideo("movie.mkv", _sidecar));

        Assert.True(coordinator.SelectSubtitleTrack(chosen));
        decoder.Release();
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);

        Assert.Equal(chosen, coordinator.SelectedSubtitleTrackId);
        Assert.Contains(coordinator.SubtitleTracks, static track => track.Id == "embedded:2");
    }

    [Fact]
    public async Task EmbeddedTrackRequestedBeforeItArrived_IsSelectedWhenTheScanDeliversIt()
    {
        // A host restoring a saved embedded-track choice on MediaLoaded, before the scan finished.
        EmbeddedSubtitleTrack[] tracks =
        [
            .. EmbeddedTracks,
            new(3, "ASS", [new SubtitleCue(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Second")]),
        ];
        using var decoder = new GatedDecoder(tracks);
        using var coordinator = CreateCoordinator(decoder.Decode);
        Assert.True(coordinator.OpenVideo("movie.mkv"));

        Assert.False(coordinator.SelectSubtitleTrack("embedded:3"));
        decoder.Release();
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);

        Assert.Equal("embedded:3", coordinator.SelectedSubtitleTrackId);
    }

    [Fact]
    public async Task NextOpen_CancelsScan_AndLateResultIsDropped()
    {
        // The first scan ignores its token and returns its tracks after the next open, as a decoder
        // already past its last packet would.
        using var first = new GatedDecoder(EmbeddedTracks, honourCancellation: false);
        using var second = new GatedDecoder([]);
        using var coordinator = CreateCoordinator((path, token) =>
            path == "first.mkv" ? first.Decode(path, token) : second.Decode(path, token));
        var changes = 0;
        coordinator.SubtitleTracksChanged += (_, _) => Interlocked.Increment(ref changes);

        Assert.True(coordinator.OpenVideo("first.mkv"));
        var firstScan = coordinator.EmbeddedSubtitleScan;
        Assert.True(first.Started.Wait(Timeout));

        Assert.True(coordinator.OpenVideo("second.mkv"));
        Assert.True(first.Token.IsCancellationRequested);

        first.Release();
        second.Release();
        await firstScan.WaitAsync(Timeout);
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);

        Assert.Empty(coordinator.SubtitleTracks);
        Assert.Null(coordinator.SelectedSubtitleTrackId);
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task Close_CancelsScan_AndACancelledDecoderEndsQuietly()
    {
        using var decoder = new GatedDecoder(EmbeddedTracks);
        using var coordinator = CreateCoordinator(decoder.Decode);
        Assert.True(coordinator.OpenVideo("movie.mkv"));
        Assert.True(decoder.Started.Wait(Timeout));

        coordinator.Close();

        // The decoder throws OperationCanceledException once cancelled; the scan task still completes.
        await coordinator.EmbeddedSubtitleScan.WaitAsync(Timeout);
        Assert.True(coordinator.EmbeddedSubtitleScan.IsCompletedSuccessfully);
        Assert.Empty(coordinator.SubtitleTracks);
    }

    [Fact]
    public async Task SubtitleTracksChanged_IsForwardedToHost()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video };
        using var player = new PlayerCore(new ScanTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<(SubtitleTracksChangedEventArgs Args, bool AfterLoaded, bool Inline)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raising = false;
        player.MediaLoaded += (_, _) => loaded.TrySetResult();
        // Inline = raised inside the engine's raise call. A thread id would not do: once this test awaits,
        // the pool may run the delivery on the thread that raised.
        player.SubtitleTracksChanged += (_, e) => changed.TrySetResult((e, loaded.Task.IsCompleted, Volatile.Read(ref raising)));
        Assert.True(player.Load("movie.mkv"));

        engine.SubtitleTracks = [new SubtitleTrack("embedded:2", "Embedded SUBRIP (stream 2)", IsEmbedded: true)];
        engine.SelectedSubtitleTrackId = "embedded:2";
        Volatile.Write(ref raising, true);
        engine.RaiseSubtitleTracksChanged();
        Volatile.Write(ref raising, false);

        (var args, var afterLoaded, var inline) = await changed.Task.WaitAsync(Timeout);
        Assert.True(afterLoaded, "SubtitleTracksChanged arrived before MediaLoaded");
        Assert.False(inline, "SubtitleTracksChanged was raised on the engine's thread, inside its raise call");
        Assert.Equal("embedded:2", Assert.Single(args.Tracks).Id);
        Assert.Equal("embedded:2", args.SelectedTrackId);
    }

    [Fact]
    public void SubtitleTracksChanged_IsNotForwardedAfterClose()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video };
        using var player = new PlayerCore(new ScanTestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var raised = 0;
        player.SubtitleTracksChanged += (_, _) => Interlocked.Increment(ref raised);
        Assert.True(player.Load("movie.mkv"));
        player.Close();

        // Close takes the next control queue turn, so the queued delivery has run when it returns.
        engine.RaiseSubtitleTracksChanged();
        player.Close();

        Assert.Equal(0, Volatile.Read(ref raised));
    }

    static PlaybackCoordinator CreateCoordinator(Func<string, CancellationToken, IReadOnlyList<EmbeddedSubtitleTrack>> decoder)
    {
        var configuration = MediaPlayerConfiguration.Auto;
        Assert.Equal(MediaPlayerMode.Full, configuration.Mode);
        return new PlaybackCoordinator(
            new TestAudioEngine(),
            new TestCdgEngine(),
            new SubtitleEngine(),
            new OpenVideoFactory(),
            configuration,
            audioAvailable: false,
            embeddedSubtitleDecoder: decoder);
    }

    /// <summary>A decoder that blocks until released, then returns its tracks or, when cancelled, throws.</summary>
    sealed class GatedDecoder(IReadOnlyList<EmbeddedSubtitleTrack> tracks, bool honourCancellation = true) : IDisposable
    {
        readonly ManualResetEventSlim _release = new();

        public ManualResetEventSlim Started { get; } = new();

        public CancellationToken Token { get; private set; }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Dispose();
            Started.Dispose();
        }

        public IReadOnlyList<EmbeddedSubtitleTrack> Decode(string path, CancellationToken token)
        {
            Token = token;
            Started.Set();
            if (honourCancellation)
            {
                _ = WaitHandle.WaitAny([_release.WaitHandle, token.WaitHandle], Timeout);
                token.ThrowIfCancellationRequested();
            }
            else
            {
                _ = _release.Wait(Timeout, CancellationToken.None);
            }

            return tracks;
        }
    }

    sealed class OpenVideoFactory : IVideoEngineFactory
    {
        public IVideoEngine Create(IClockSource clock, MediaState state, IAudioEngine audioEngine, MediaPlayerConfiguration configuration, PlaybackSyncState sync) =>
            new TestVideoEngine { IsOpen = false };
    }


    sealed class ScanTestPlatformFactory(TestMediaEngine engine) : IPlatformFactory
    {
        public string LastNativeLoadMessage => string.Empty;

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration) =>
            new NullPresentationCoordinator();

        public IServiceProvider CreateServiceProvider(MediaPlayerConfiguration configuration, IReadOnlyList<string> nativeLibraryPaths) =>
            new Provider(engine);
    }

    sealed class Provider(TestMediaEngine engine) : IServiceProvider
    {
        readonly IArchiveService _archives = new NotSupportedArchiveService();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMediaEngine) ? engine
            : serviceType == typeof(IAudioEngine) ? engine.TestAudio
            : serviceType == typeof(IArchiveService) ? _archives
            : null;
    }
}
