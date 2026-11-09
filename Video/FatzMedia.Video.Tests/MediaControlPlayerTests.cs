using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Threading;
using System.Collections.Concurrent;

namespace FatzMedia.Video.Tests;

public sealed class MediaControlPlayerTests
{
    [Fact]
    public void Constructor_NullPlatformFactory_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new MediaControlPlayer(null!, MediaPlayerConfiguration.Auto, Array.Empty<string>()));

        Assert.Equal("platformFactory", exception.ParamName);
    }

    [Fact]
    public async Task Load_RaisesLoadedThenStateThenPosition()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var order = new ConcurrentQueue<string>();
        var positionRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaLoaded += (_, _) => order.Enqueue("loaded");
        player.StateChanged += (_, e) =>
        {
            if (e.CurrentState == MediaPlayerState.Stopped)
                order.Enqueue("state");
        };
        player.PositionChanged += (_, _) =>
        {
            order.Enqueue("position");
            positionRaised.TrySetResult();
        };

        Assert.True(player.Load("song.mp3"));
        await positionRaised.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(["loaded", "state", "position"], order);
    }

    [Fact]
    public async Task SyncLoad_PlayOnLoad_StopIssuedAfterLoadWins()
    {
        // The PlayOnLoad start waits for the quick analysis; a Stop issued after a sync Load used to
        // run first, and the start then played the media anyway.
        var engine = new TestMediaEngine();
        engine.TestAudio.NormalizationReady = false;
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []) { PlayOnLoad = true };

        Assert.True(player.Load("song.mp3"));
        _ = Task.Delay(200).ContinueWith(_ => engine.TestAudio.NormalizationReady = true, TaskScheduler.Default);
        _ = player.Stop();
        await player.FlushControlOperationsAsync();
        await Task.Delay(300);

        Assert.NotEqual(MediaPlayerState.Playing, player.State);
    }

    [Fact]
    public async Task SyncLoad_PlayOnLoad_StartsOnce()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []) { PlayOnLoad = true };

        Assert.True(player.Load("song.mp3"));
        await player.FlushControlOperationsAsync();

        Assert.Equal(1, engine.PlayCount);
        Assert.Equal(MediaPlayerState.Playing, player.State);
    }

    [Fact]
    public async Task Stop_RaisesStoppedThenStateThenPosition()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loadedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PositionChanged += (_, _) => loadedPosition.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        await loadedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.StateChanged += (_, e) =>
        {
            if (e.CurrentState == MediaPlayerState.Playing)
                playing.TrySetResult();
        };
        Assert.True(player.Play());
        await playing.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var order = new ConcurrentQueue<string>();
        var stoppedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaStopped += (_, _) => order.Enqueue("stopped");
        player.StateChanged += (_, e) =>
        {
            if (e.CurrentState == MediaPlayerState.Stopped)
                order.Enqueue("state");
        };
        player.PositionChanged += (_, e) =>
        {
            // While playing, the 250 ms position ticks also report zero (the test engine does not
            // advance), so only a zero position after the Stopped state counts as Stop's own event.
            if (e.Position == TimeSpan.Zero && order.Contains("state"))
            {
                order.Enqueue("position");
                stoppedPosition.TrySetResult();
            }
        };

        Assert.True(player.Stop());
        await stoppedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(["stopped", "state", "position"], order);
    }

    [Fact]
    public async Task LoadOverPlayingMedia_RaisesStoppedAndClosedForTheOldItemBeforeLoaded()
    {
        // A surface's auto-hide reads a MediaStopped that arrives after the new MediaLoaded as the
        // new item stopping: an image (one frame, never started) loaded over playing media stayed
        // hidden. The documented order is the old item's MediaStopped and MediaClosed, then MediaLoaded.
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        await player.FlushControlOperationsAsync();

        var order = new ConcurrentQueue<string>();
        player.MediaStopped += (_, _) => order.Enqueue("stopped");
        player.MediaClosed += (_, _) => order.Enqueue("closed");
        player.MediaLoaded += (_, _) => order.Enqueue("loaded");

        Assert.True(player.Load("next.mp3"));
        await player.FlushControlOperationsAsync();

        Assert.Equal(["stopped", "closed", "loaded"], order);
    }

    [Fact]
    public async Task Seek_RaisesCompletedThenPosition()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loadedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PositionChanged += (_, _) => loadedPosition.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        await loadedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var order = new ConcurrentQueue<string>();
        var seekPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaSeekCompletedEventArgs? seekCompleted = null;
        player.SeekCompleted += (_, e) =>
        {
            seekCompleted = e;
            order.Enqueue("seek");
        };
        player.PositionChanged += (_, e) =>
        {
            if (e.Position == TimeSpan.FromSeconds(12))
            {
                order.Enqueue("position");
                seekPosition.TrySetResult();
            }
        };

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        await seekPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(["seek", "position"], order);
        Assert.Equal(TimeSpan.FromSeconds(12), seekCompleted!.RequestedPosition);
        Assert.Equal(TimeSpan.FromSeconds(12), seekCompleted.ResultingPosition);
    }

    [Fact]
    public async Task SeekAsync_SeeksOnTheControlQueue_InOrderWithASynchronousSeek()
    {
        // Stage 022A slice 4: hosts send seek-bar drags through SeekAsync; a release Seek right
        // after must not be overtaken by a drag seek still queued.
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        var requested = new ConcurrentQueue<TimeSpan>();
        player.SeekCompleted += (_, e) => requested.Enqueue(e.RequestedPosition);

        Task<bool> drag = player.SeekAsync(8);
        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));

        Assert.True(await drag.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal([TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12)], requested);
        Assert.Equal(12, engine.Position, precision: 3);
    }

    [Fact]
    public async Task SeekAsync_DuringAScrub_LeavesThePreviewToTheSeekPump()
    {
        // Stage 022A slice 4 follow-up (owner capture cdg-2): a drag seek that also re-rendered
        // and presented the current frame held the video lock through a vsync present and stalled
        // the seek pump's preview render. Drag seeks skip that refresh, as Controls.Seek did; a
        // seek outside a scrub still refreshes.
        var engine = new TestMediaEngine();
        var factory = new TestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.BeginScrub());
        var before = Volatile.Read(ref factory.Coordinator.RefreshCount);

        Assert.True(await player.SeekAsync(30).WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(before, Volatile.Read(ref factory.Coordinator.RefreshCount));

        Assert.True(player.EndScrub());
        Assert.True(await player.SeekAsync(40).WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(Volatile.Read(ref factory.Coordinator.RefreshCount) > before);
    }

    [Fact]
    public async Task PropertySetters_RefreshOnTheControlQueueOnce()
    {
        // ENG-039: a setter presented on the host's thread; with a present stuck in the renderer
        // the UI froze. Setters now ask the control queue for one redraw.
        var engine = new TestMediaEngine();
        var factory = new TestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        await player.FlushControlOperationsAsync().WaitAsync(TestEnvironment.EventuallyTimeout);
        var before = Volatile.Read(ref factory.Coordinator.RefreshCount);

        // Hold the control queue with a seek, so the changes below pile up behind it.
        using var seekEntered = new ManualResetEventSlim();
        using var releaseSeek = new ManualResetEventSlim();
        engine.OnSeek = seconds =>
        {
            seekEntered.Set();
            _ = releaseSeek.Wait(TestEnvironment.EventuallyTimeout);
        };
        Task<bool> seek = player.SeekAsync(10);
        Assert.True(seekEntered.Wait(TestEnvironment.EventuallyTimeout));

        player.AudioSyncOffset = TimeSpan.FromMilliseconds(100);
        _ = player.AdjustCdgSyncOffset(1);
        player.ResetSyncOffsets();
        player.VideoDisplayMode = VideoDisplayMode.Stretch;
        player.BaseVisualTransform = new BaseVisualTransform { Brightness = 0.25f };
        player.TransparentBackground = true;
        Assert.Equal(before, Volatile.Read(ref factory.Coordinator.RefreshCount));

        releaseSeek.Set();
        Assert.True(await seek.WaitAsync(TestEnvironment.EventuallyTimeout));
        await player.FlushControlOperationsAsync().WaitAsync(TestEnvironment.EventuallyTimeout);

        // The seek's own refresh, then one for all six changes (none ran on this thread: the
        // count did not move while the queue was held).
        Assert.Equal(before + 2, Volatile.Read(ref factory.Coordinator.RefreshCount));
    }

    [Fact]
    public async Task DeviceRecovered_ReopensVideoAtPositionAndState()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video };
        var opens = 0;
        engine.BeforeOpen = () => opens++;
        var factory = new TestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("clip.mp4"));
        Assert.True(player.Play());
        Assert.True(player.Seek(30));

        factory.Coordinator.RaiseDeviceRecovered();
        await player.FlushControlOperationsAsync().WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(2, opens);
        Assert.Equal(30, engine.Position, precision: 3);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
    }

    [Fact]
    public async Task DeviceRecovered_ReopensOnce_EvenIfRaisedTwice()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Video };
        var opens = 0;
        engine.BeforeOpen = () => opens++;
        var factory = new TestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("clip.mp4"));

        // Both before the queued reopen runs: hold the queue with a seek.
        using var seekEntered = new ManualResetEventSlim();
        using var releaseSeek = new ManualResetEventSlim();
        engine.OnSeek = seconds =>
        {
            seekEntered.Set();
            _ = releaseSeek.Wait(TestEnvironment.EventuallyTimeout);
        };
        Task<bool> seek = player.SeekAsync(10);
        Assert.True(seekEntered.Wait(TestEnvironment.EventuallyTimeout));
        factory.Coordinator.RaiseDeviceRecovered();
        factory.Coordinator.RaiseDeviceRecovered();
        engine.OnSeek = null;
        releaseSeek.Set();
        Assert.True(await seek.WaitAsync(TestEnvironment.EventuallyTimeout));
        await player.FlushControlOperationsAsync().WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task DeviceRecovered_AudioOnly_DoesNotReopen()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        var opens = 0;
        engine.BeforeOpen = () => opens++;
        var factory = new TestPlatformFactory(engine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());

        factory.Coordinator.RaiseDeviceRecovered();
        await player.FlushControlOperationsAsync().WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(1, opens);
        Assert.Equal(MediaPlayerState.Playing, engine.State);
    }

    [Fact]
    public async Task SilentScrub_MutesBeforeTheDragSeek_AndRestoresTheVolumeWhenTheScrubEnds()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        player.Effects.Volume = 0.6;
        player.ScrubAudio = MediaScrubAudio.SilentWhileMoving;
        player.ScrubAudioMuter.QuietPeriod = TimeSpan.FromHours(1); // the drag never rests here
        var volumeAtSeek = double.NaN;
        engine.OnSeek = _ => volumeAtSeek = engine.TestAudio.Volume;

        Assert.True(player.BeginScrub());
        Assert.True(await player.SeekAsync(30).WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(0, volumeAtSeek);
        Assert.Equal(0, engine.TestAudio.Volume);

        // A volume change mid-drag is kept for later, not applied over the mute.
        player.Effects.Volume = 0.8;
        Assert.Equal(0, engine.TestAudio.Volume);
        Assert.Equal(0.8, player.Effects.Volume);

        Assert.True(player.Seek(30)); // the release seek
        Assert.True(player.EndScrub());
        Assert.Equal(0.8, engine.TestAudio.Volume);

        // Outside a scrub, seeks never mute.
        Assert.True(player.Seek(10));
        Assert.Equal(0.8, volumeAtSeek);
    }

    [Fact]
    public async Task AudibleScrub_IsTheDefault_AndNeverMutes()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        player.Effects.Volume = 0.6;
        var volumeAtSeek = double.NaN;
        engine.OnSeek = _ => volumeAtSeek = engine.TestAudio.Volume;

        Assert.Equal(MediaScrubAudio.Audible, player.ScrubAudio);
        Assert.True(player.BeginScrub());
        Assert.True(await player.SeekAsync(30).WaitAsync(TestEnvironment.EventuallyTimeout));

        Assert.Equal(0.6, volumeAtSeek);
        Assert.True(player.EndScrub());
    }

    [Fact]
    public async Task SilentScrub_LoadingMidDrag_LeavesTheNewMediaAudible()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        player.Effects.Volume = 0.6;
        player.ScrubAudio = MediaScrubAudio.SilentWhileMoving;
        player.ScrubAudioMuter.QuietPeriod = TimeSpan.FromHours(1); // the drag never rests here
        Assert.True(player.BeginScrub());
        Assert.True(await player.SeekAsync(30).WaitAsync(TestEnvironment.EventuallyTimeout));

        Assert.True(player.Load("next.mp3"));

        Assert.Equal(0.6, engine.TestAudio.Volume);
        Assert.False(player.Controls.IsScrubbing);
    }

    [Fact]
    public async Task SeekAsync_RequestsWaitingToRun_CollapseToTheNewest()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        var seeks = new ConcurrentQueue<double>();
        using var firstEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        engine.OnSeek = seconds =>
        {
            seeks.Enqueue(seconds);
            if (!firstEntered.IsSet)
            {
                firstEntered.Set();
                release.Wait(TestEnvironment.EventuallyTimeout);
            }
        };

        Task<bool> running = player.SeekAsync(1);
        Assert.True(firstEntered.Wait(TestEnvironment.EventuallyTimeout));
        Task<bool>[] waiting = [player.SeekAsync(2), player.SeekAsync(3), player.SeekAsync(4)];
        release.Set();

        Assert.True(await running.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.All(await Task.WhenAll(waiting).WaitAsync(TestEnvironment.EventuallyTimeout), Assert.True);
        Assert.Equal([1d, 4d], seeks);
        Assert.Equal(4, engine.Position, precision: 3);
    }

    [Fact]
    public async Task VideoSeek_RaisesCompletedAfterTheSettledPositionSignal()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(12);

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.RequestedPosition);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task VideoNoOpSeek_RaisesCompletedWithoutAnotherPositionSignal()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        video.Position = 12;
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.RequestedPosition);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task ScrubVideoSeek_CompletesOnThePreviewedFrameButNotAtTheOldPosition()
    {
        // 035A review: during a scrub the immediate check after Seek must stay strict (the video
        // seek is only queued), while the settle may land on the previewed frame near the target.
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.BeginScrub());
        Assert.True(player.Seek(TimeSpan.FromSeconds(30)));
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(25);
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(28.6);

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), args.RequestedPosition);
        Assert.Equal(TimeSpan.FromSeconds(28.6), args.ResultingPosition);
    }

    [Fact]
    public async Task VideoSeek_IgnoresPositionOutsideSettleTolerance()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        video.RaisePositionChanged(13);
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(12);

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task SmallVideoSeek_DoesNotCompleteFromThePreSeekPosition()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        video.Position = 11.9;
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(12);

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task VideoSeek_LatestRequestWinsSettleCompletion()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(8)));
        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        video.RaisePositionChanged(8);
        await Task.Delay(50);
        Assert.False(completed.Task.IsCompleted);

        video.RaisePositionChanged(12);

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.RequestedPosition);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task CdgNoOpSeek_RaisesCompletedWithoutFrameInvalidation()
    {
        var engine = new TestMediaEngine
        {
            Content = MediaContent.Audio | MediaContent.Cdg,
            TestCdg = new TestCdgEngine()
        };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        var completed = new TaskCompletionSource<MediaSeekCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SeekCompleted += (_, e) => completed.TrySetResult(e);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));

        MediaSeekCompletedEventArgs args = await completed.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), args.ResultingPosition);
    }

    [Fact]
    public async Task Stop_ClearsPendingVideoSeekCompletion()
    {
        var video = new TestVideoEngine();
        var engine = new TestMediaEngine { Content = MediaContent.Video, TestVideo = video };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("video.mp4"));
        Assert.True(player.Play());
        var completionCount = 0;
        player.SeekCompleted += (_, _) => Interlocked.Increment(ref completionCount);

        Assert.True(player.Seek(TimeSpan.FromSeconds(12)));
        Assert.True(player.Stop());
        video.RaisePositionChanged(12);
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref completionCount));
    }

    [Fact]
    public async Task Pause_StopsPeriodicPositionNotifications()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loadedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PositionChanged += (_, _) => loadedPosition.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        await loadedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.True(player.Play());
        Assert.True(player.Pause());
        var countAfterPause = 0;
        player.PositionChanged += (_, _) => Interlocked.Increment(ref countAfterPause);

        await Task.Delay(PlaybackPositionNotifier.DefaultInterval + TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, Volatile.Read(ref countAfterPause));
    }

    [Fact]
    public async Task NaturalEnd_RaisesEndedThenStateThenPosition()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loadedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PositionChanged += (_, _) => loadedPosition.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        await loadedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.StateChanged += (_, e) =>
        {
            if (e.CurrentState == MediaPlayerState.Playing)
                playing.TrySetResult();
        };
        Assert.True(player.Play());
        await playing.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        var order = new ConcurrentQueue<string>();
        var endedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => order.Enqueue("ended");
        player.StateChanged += (_, e) =>
        {
            if (e.CurrentState == MediaPlayerState.Stopped)
                order.Enqueue("state");
        };
        player.PositionChanged += (_, e) =>
        {
            if (e.Position == TimeSpan.FromSeconds(engine.Duration))
            {
                order.Enqueue("position");
                endedPosition.TrySetResult();
            }
        };

        engine.CompletePlayback();
        await endedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(["ended", "state", "position"], order);
    }

    [Fact]
    public async Task InternalLoop_RaisesEndedThenLoopedThenPosition()
    {
        var engine = new TestMediaEngine { HandlesLoopInternally = true };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var loadedPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PositionChanged += (_, _) => loadedPosition.TrySetResult();
        Assert.True(player.Load("song.mp3"));
        await loadedPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        player.LoopMedia = true;

        var order = new ConcurrentQueue<string>();
        var loopPosition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => order.Enqueue("ended");
        player.MediaLooped += (_, _) => order.Enqueue("looped");
        player.PositionChanged += (_, e) =>
        {
            if (e.Position == TimeSpan.Zero)
            {
                order.Enqueue("position");
                loopPosition.TrySetResult();
            }
        };

        engine.RaiseMediaEnded();
        await loopPosition.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.Equal(["ended", "looped", "position"], order);
    }

    [Fact]
    public async Task MediaEndedHandler_StopCancelsQueuedLoopRestart()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        player.LoopMedia = true;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopResult = false;
        player.MediaEnded += (_, _) => stopResult = player.Stop();
        player.MediaStopped += (_, _) => stopped.TrySetResult();

        engine.RaiseMediaEnded();
        await stopped.Task.WaitAsync(TestEnvironment.EventuallyTimeout);

        Assert.True(stopResult);
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public async Task EndSignalQueuedDuringStop_DoesNotRestartOrRaiseEnded()
    {
        using var releaseStop = new ManualResetEventSlim();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Assert.True(player.Play());
        player.LoopMedia = true;
        engine.BeforeStop = () =>
        {
            stopEntered.TrySetResult();
            releaseStop.Wait();
        };
        var endedCount = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => Interlocked.Increment(ref endedCount);
        player.MediaStopped += (_, _) => stopped.TrySetResult();

        Task<bool> stop = player.StopAsync();
        await stopEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        engine.RaiseMediaEnded();
        releaseStop.Set();

        Assert.True(await stop.WaitAsync(TestEnvironment.EventuallyTimeout));
        await stopped.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(0, Volatile.Read(ref endedCount));
        Assert.Equal(1, engine.PlayCount);
    }

    [Fact]
    public void Load_WithPitchAndTempoReset_RaisesResetEventsBeforeLoaded()
    {
        var engine = new TestMediaEngine();
        var configuration = new MediaPlayerConfiguration
        {
            ResetPitchOnMediaLoad = true,
            ResetTempoOnMediaLoad = true
        };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), configuration, []);
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;
        var order = new List<string>();
        MediaValueChangedEventArgs<int>? pitchReset = null;
        MediaValueChangedEventArgs<int>? tempoReset = null;
        player.PitchChanged += (_, e) =>
        {
            pitchReset = e;
            order.Add("pitch");
        };
        player.TempoChanged += (_, e) =>
        {
            tempoReset = e;
            order.Add("tempo");
        };
        player.MediaLoaded += (_, _) => order.Add("loaded");

        Assert.True(player.Load("song.mp3"));

        Assert.Equal(["pitch", "tempo", "loaded"], order);
        Assert.Equal(MediaChangeReason.LoadReset, pitchReset!.Reason);
        Assert.Equal(0, pitchReset.CurrentValue);
        Assert.Equal(MediaChangeReason.LoadReset, tempoReset!.Reason);
        Assert.Equal(100, tempoReset.CurrentValue);
        Assert.Equal(0, player.Effects.Pitch);
        Assert.Equal(100, player.Effects.Tempo);
    }

    [Fact]
    public async Task LoadAsync_WithTempoResetOnly_PreservesPitch()
    {
        var engine = new TestMediaEngine();
        var configuration = new MediaPlayerConfiguration { ResetTempoOnMediaLoad = true };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), configuration, []);
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;
        var pitchEvents = 0;
        MediaValueChangedEventArgs<int>? tempoReset = null;
        player.PitchChanged += (_, _) => pitchEvents++;
        player.TempoChanged += (_, e) => tempoReset = e;

        Assert.True(await player.LoadAsync("song.mp3"));

        Assert.Equal(0, pitchEvents);
        Assert.Equal(5, player.Effects.Pitch);
        Assert.Equal(100, player.Effects.Tempo);
        Assert.Equal(MediaChangeReason.LoadReset, tempoReset!.Reason);
    }

    [Fact]
    public void Load_DefaultConfigurationPreservesPitchAndTempo()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;
        var changeEvents = 0;
        player.PitchChanged += (_, _) => changeEvents++;
        player.TempoChanged += (_, _) => changeEvents++;

        Assert.True(player.Load("song.mp3"));

        Assert.Equal(0, changeEvents);
        Assert.Equal(5, player.Effects.Pitch);
        Assert.Equal(125, player.Effects.Tempo);
    }

    [Fact]
    public void Reset_UsesNewLoadResetConfiguration()
    {
        var firstEngine = new TestMediaEngine();
        var resetEngine = new TestMediaEngine();
        var factory = new TestPlatformFactory(firstEngine, resetEngine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var resetConfiguration = new MediaPlayerConfiguration
        {
            ResetPitchOnMediaLoad = true,
            ResetTempoOnMediaLoad = true
        };

        Assert.True(player.Reset(resetConfiguration, []));
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;

        Assert.True(player.Load("song.mp3"));
        Assert.Equal(0, player.Effects.Pitch);
        Assert.Equal(100, player.Effects.Tempo);
    }

    [Fact]
    public void Reset_CanBeRetriedAfterTheNewEngineFailsToStart()
    {
        var firstEngine = new TestMediaEngine();
        var retryEngine = new TestMediaEngine();
        var factory = new TestPlatformFactory(firstEngine, retryEngine);
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        factory.BeforeCreateServiceProvider = callCount =>
        {
            if (callCount == 2)
                throw new InvalidOperationException("Audio device unavailable.");
        };

        _ = Assert.Throws<InvalidOperationException>(() => player.Reset(MediaPlayerConfiguration.Auto, []));

        Assert.True(player.Reset(MediaPlayerConfiguration.Auto, []));
        Assert.True(player.Load("song.mp3"));
        Assert.True(retryEngine.IsOpen);
    }

    [Fact]
    public async Task Reset_WaitsForAdmittedLoadBeforeDisposingItsEngine()
    {
        using var releaseLoad = new ManualResetEventSlim();
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new TestMediaEngine
        {
            BeforeOpen = () =>
            {
                loadEntered.TrySetResult();
                releaseLoad.Wait();
            }
        };
        var resetEngine = new TestMediaEngine();
        using var player = new MediaControlPlayer(
            new TestPlatformFactory(firstEngine, resetEngine),
            MediaPlayerConfiguration.Auto,
            []);

        Task<bool> load = player.LoadAsync("song.mp3");
        await loadEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Task<bool> reset = Task.Run(() => player.Reset(MediaPlayerConfiguration.Auto, []));

        await Task.Delay(50);
        Assert.False(reset.IsCompleted);

        releaseLoad.Set();
        // Reset cancelled the load (ENG-024): it reports false and leaves nothing open.
        Assert.False(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.True(await reset.WaitAsync(TestEnvironment.EventuallyTimeout));
        Assert.Equal(1, firstEngine.DisposeCount);
        Assert.True(player.Load("replacement.mp3"));
    }

    [Fact]
    public async Task Dispose_WaitsForAdmittedLoadBeforeDisposingItsEngine()
    {
        using var releaseLoad = new ManualResetEventSlim();
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new TestMediaEngine
        {
            BeforeOpen = () =>
            {
                loadEntered.TrySetResult();
                releaseLoad.Wait();
            }
        };
        var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);

        Task<bool> load = player.LoadAsync("song.mp3");
        await loadEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Task dispose = Task.Run(player.Dispose);

        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        releaseLoad.Set();
        // Dispose cancelled the load (ENG-024).
        Assert.False(await load.WaitAsync(TestEnvironment.EventuallyTimeout));
        await dispose.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(1, engine.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
        player.Dispose();
    }

    [Fact]
    public async Task Dispose_WaitsForConcurrentResetAndDisposesTheRebuiltEngine()
    {
        using var releaseReset = new ManualResetEventSlim();
        var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEngine = new TestMediaEngine();
        var resetEngine = new TestMediaEngine();
        var factory = new TestPlatformFactory(firstEngine, resetEngine);
        var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        factory.BeforeCreateServiceProvider = callCount =>
        {
            if (callCount == 2)
            {
                resetEntered.TrySetResult();
                releaseReset.Wait();
            }
        };

        Task<bool> reset = Task.Run(() => player.Reset(MediaPlayerConfiguration.Auto, []));
        await resetEntered.Task.WaitAsync(TestEnvironment.EventuallyTimeout);
        Task dispose = Task.Run(player.Dispose);

        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);

        releaseReset.Set();
        Assert.True(await reset.WaitAsync(TestEnvironment.EventuallyTimeout));
        await dispose.WaitAsync(TestEnvironment.EventuallyTimeout);
        Assert.Equal(1, firstEngine.DisposeCount);
        Assert.Equal(1, resetEngine.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }

    [Fact]
    public void Dispose_FromInlineControlEvent_IsRejectedUntilTheOperationCompletes()
    {
        var engine = new TestMediaEngine();
        var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.True(player.Load("song.mp3"));
        Exception? disposeException = null;
        player.MediaStarted += (_, _) => disposeException = Record.Exception(player.Dispose);

        Assert.True(player.Play());

        Assert.IsType<InvalidOperationException>(disposeException);
        player.Dispose();
        Assert.Equal(1, engine.DisposeCount);
    }

    [Fact]
    public void FailedLoad_DoesNotResetPitchOrTempo()
    {
        var engine = new TestMediaEngine { OpenSucceeds = false };
        var configuration = new MediaPlayerConfiguration
        {
            ResetPitchOnMediaLoad = true,
            ResetTempoOnMediaLoad = true
        };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), configuration, []);
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;
        var resetEvents = 0;
        player.PitchChanged += (_, e) => resetEvents += e.Reason == MediaChangeReason.LoadReset ? 1 : 0;
        player.TempoChanged += (_, e) => resetEvents += e.Reason == MediaChangeReason.LoadReset ? 1 : 0;

        Assert.False(player.Load("missing.mp3"));
        Assert.Equal(0, resetEvents);
        Assert.Equal(5, player.Effects.Pitch);
        Assert.Equal(125, player.Effects.Tempo);
    }

    [Fact]
    public void HasVisualContentIncludesMainVisualsAndResolvedBackgrounds()
    {
        var engine = new TestMediaEngine { Content = MediaContent.Audio };
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        Assert.False(player.HasVisualContent);

        engine.Content = MediaContent.Video;
        Assert.True(player.HasVisualContent);

        engine.Content = MediaContent.Audio;
        _ = engine.SetBackground(MediaBackgroundCandidate.FromPath(MediaBackgroundKind.Image, "background.png"));
        Assert.True(player.HasVisualContent);
    }

    [Fact]
    public void BaseVisualTransformChangedFiresOnlyOnActualChange()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        var changes = new List<MediaValueChangedEventArgs<BaseVisualTransform>>();
        player.BaseVisualTransformChanged += (_, e) => changes.Add(e);
        var transform = new BaseVisualTransform { Brightness = 0.25f };

        player.BaseVisualTransform = transform;
        player.BaseVisualTransform = transform;

        MediaValueChangedEventArgs<BaseVisualTransform> change = Assert.Single(changes);
        Assert.Equal(BaseVisualTransform.Identity, change.PreviousValue);
        Assert.Equal(transform, change.CurrentValue);
        Assert.Equal(MediaChangeReason.User, change.Reason);
        Assert.Equal(transform, player.BaseVisualTransform);
    }

    [Fact]
    public void LoadResetOptionsCanBeChangedForTheNextLoad()
    {
        var engine = new TestMediaEngine();
        using var player = new MediaControlPlayer(new TestPlatformFactory(engine), MediaPlayerConfiguration.Auto, []);
        player.Effects.Pitch = 5;
        player.Effects.Tempo = 125;
        player.ResetPitchOnMediaLoad = true;
        player.ResetTempoOnMediaLoad = false;

        Assert.True(player.Load("first.mp3"));
        Assert.Equal(0, player.Effects.Pitch);
        Assert.Equal(125, player.Effects.Tempo);

        player.Effects.Pitch = 7;
        player.ResetPitchOnMediaLoad = false;
        player.ResetTempoOnMediaLoad = true;

        Assert.True(player.Load("second.mp3"));
        Assert.Equal(7, player.Effects.Pitch);
        Assert.Equal(100, player.Effects.Tempo);
    }

    [Fact]
    public async Task MediaDiagnostic_ReachesOnlyTheOwningPlayer_UnownedReachesAll()
    {
        using var playerA = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        using var playerB = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        var seenByA = new ConcurrentQueue<string>();
        var seenByB = new ConcurrentQueue<string>();
        using var bothSawUnowned = new CountdownEvent(2);
        void Record(ConcurrentQueue<string> seen, MediaDiagnosticEventArgs e)
        {
            if (e.Category != "OwnershipTest.Player")
                return;
            seen.Enqueue(e.Message);
            if (e.Message == "unowned")
                bothSawUnowned.Signal();
        }

        playerA.MediaDiagnostic += (_, e) => Record(seenByA, e);
        playerB.MediaDiagnostic += (_, e) => Record(seenByB, e);

        using (DiagnosticRouter.EnterOwner(playerA))
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "OwnershipTest.Player", "for A");
        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "OwnershipTest.Player", "unowned");

        // Each player delivers its diagnostics in order on its own worker, so "unowned" arriving
        // means "for A" was already delivered (or dropped).
        Assert.True(bothSawUnowned.Wait(TestEnvironment.EventuallyTimeout));
        await playerA.FlushControlOperationsAsync();
        await playerB.FlushControlOperationsAsync();

        Assert.Equal(["for A", "unowned"], seenByA);
        Assert.Equal(["unowned"], seenByB);
    }

    [Fact]
    public async Task MediaDiagnostic_RaisedForASession_ReachesOnlyThatPlayer_NullOwnerReachesAll()
    {
        // ENG-036: adapters raise outside the engine (the UI thread has no ambient owner) and name
        // the session they belong to; an explicit owner wins over an ambient one.
        using var playerA = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        using var playerB = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        var seenByA = new ConcurrentQueue<string>();
        var seenByB = new ConcurrentQueue<string>();
        using var bothSawUnowned = new CountdownEvent(2);
        void Record(ConcurrentQueue<string> seen, MediaDiagnosticEventArgs e)
        {
            if (e.Category != "OwnershipTest.Adapter")
                return;
            seen.Enqueue(e.Message);
            if (e.Message == "unowned")
                bothSawUnowned.Signal();
        }

        playerA.MediaDiagnostic += (_, e) => Record(seenByA, e);
        playerB.MediaDiagnostic += (_, e) => Record(seenByB, e);

        DiagnosticRouter.Raise(playerA, MediaDiagnosticLevel.Info, "OwnershipTest.Adapter", "surface of A");
        using (DiagnosticRouter.EnterOwner(playerA))
            DiagnosticRouter.Raise(playerB, MediaDiagnosticLevel.Info, "OwnershipTest.Adapter", "surface of B");
        DiagnosticRouter.Raise(null, MediaDiagnosticLevel.Info, "OwnershipTest.Adapter", "unowned");

        Assert.True(bothSawUnowned.Wait(TestEnvironment.EventuallyTimeout));
        await playerA.FlushControlOperationsAsync();
        await playerB.FlushControlOperationsAsync();

        Assert.Equal(["surface of A", "unowned"], seenByA);
        Assert.Equal(["surface of B", "unowned"], seenByB);
    }

    [Fact]
    public async Task SyncControlCall_DiagnosticRaisedByTheEngine_ReachesOnlyItsPlayer()
    {
        // ENG-025: a synchronous call runs on the caller's thread, which has no owner.
        var engineA = new TestMediaEngine();
        using var playerA = new MediaControlPlayer(new TestPlatformFactory(engineA), MediaPlayerConfiguration.Auto, []);
        using var playerB = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        Assert.True(playerA.Load("song.mp3"));
        engineA.OnPlay = static () => DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "OwnershipTest.Sync", "from A");
        (ConcurrentQueue<string> seenByA, ConcurrentQueue<string> seenByB) = await CaptureOwnership(playerA, playerB, "OwnershipTest.Sync", () =>
        {
            Assert.True(playerA.Play());
            return Task.CompletedTask;
        });

        Assert.Equal(["from A", "unowned"], seenByA);
        Assert.Equal(["unowned"], seenByB);
    }

    [Fact]
    public async Task PrepareNext_AnalysisFailure_ReachesOnlyItsPlayer()
    {
        var engineA = new TestMediaEngine { Analyzer = new ThrowingAnalyzer() };
        using var playerA = new MediaControlPlayer(new TestPlatformFactory(engineA), MediaPlayerConfiguration.Auto, []);
        using var playerB = new MediaControlPlayer(new TestPlatformFactory(new TestMediaEngine()), MediaPlayerConfiguration.Auto, []);
        var path = Path.Combine(Path.GetTempPath(), $"fb-prepare-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(path, [0]);
        try
        {
            (ConcurrentQueue<string> seenByA, ConcurrentQueue<string> seenByB) = await CaptureOwnership(playerA, playerB, "PreparedNext", async () =>
                Assert.True(await playerA.PrepareNextAsync(path)));

            Assert.Contains(seenByA, static message => message.StartsWith("Analysis failed", StringComparison.Ordinal));
            Assert.Equal(["unowned"], seenByB);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread with no diagnostic owner, then raises an unowned
    /// marker; returns what each player received in <paramref name="category"/>.
    /// </summary>
    private static async Task<(ConcurrentQueue<string> A, ConcurrentQueue<string> B)> CaptureOwnership(
        MediaControlPlayer playerA, MediaControlPlayer playerB, string category, Func<Task> action)
    {
        var seenByA = new ConcurrentQueue<string>();
        var seenByB = new ConcurrentQueue<string>();
        using var bothSawUnowned = new CountdownEvent(2);
        void Record(ConcurrentQueue<string> seen, MediaDiagnosticEventArgs e)
        {
            if (e.Category != category)
                return;
            seen.Enqueue(e.Message);
            if (e.Message == "unowned")
                bothSawUnowned.Signal();
        }

        playerA.MediaDiagnostic += (_, e) => Record(seenByA, e);
        playerB.MediaDiagnostic += (_, e) => Record(seenByB, e);

        await action();
        DiagnosticRouter.Raise(null, MediaDiagnosticLevel.Info, category, "unowned");

        // Each player delivers in order on its own worker, so "unowned" arriving means the
        // earlier diagnostic was already delivered (or dropped).
        Assert.True(bothSawUnowned.Wait(TestEnvironment.EventuallyTimeout));
        await playerA.FlushControlOperationsAsync();
        await playerB.FlushControlOperationsAsync();
        return (seenByA, seenByB);
    }

    private sealed class ThrowingAnalyzer : IMediaFileAnalyzer
    {
        public Task<MediaAnalysisResult?> AnalyzeAsync(string filePath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("analysis unavailable");
    }

    private sealed class TestPlatformFactory : IPlatformFactory
    {
        private readonly Queue<TestMediaEngine> _engines;
        private readonly TestPresentationCoordinator _coordinator = new();
        private int _createServiceProviderCount;

        public TestPlatformFactory(params TestMediaEngine[] engines)
        {
            _engines = new Queue<TestMediaEngine>(engines);
        }

        public string LastNativeLoadMessage => string.Empty;

        public TestPresentationCoordinator Coordinator => _coordinator;

        public Action<int>? BeforeCreateServiceProvider { get; set; }

        public VideoDecodeCapabilities GetVideoDecodeCapabilities() => VideoDecodeCapabilities.Create([], []);

        public IMediaPresentationCoordinator CreatePresentationCoordinator(
            IMediaEngine mediaEngine,
            MediaPlayerConfiguration configuration) => _coordinator;

        public IServiceProvider CreateServiceProvider(
            MediaPlayerConfiguration configuration,
            IReadOnlyList<string> nativeLibraryPaths)
        {
            var callCount = Interlocked.Increment(ref _createServiceProviderCount);
            BeforeCreateServiceProvider?.Invoke(callCount);
            return new TestServiceProvider(_engines.Dequeue());
        }
    }

    internal sealed class TestPresentationCoordinator : IMediaPresentationCoordinator
    {
        public event EventHandler? FramePresented { add { } remove { } }

        public VideoDisplayMode VideoDisplayMode { get; set; }
        public BaseVisualTransform BaseVisualTransform { get; set; }

        public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => false;
        public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;
        public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;
        public bool DetachPresentationSurface(string surfaceId) => false;
        public int RefreshCount;

        public void RefreshCurrentPosition() => Interlocked.Increment(ref RefreshCount);

        public event EventHandler? DeviceRecovered;

        public void RaiseDeviceRecovered() => DeviceRecovered?.Invoke(this, EventArgs.Empty);
        public void StartRendering() { }
        public void StopRendering() { }
        public void RefreshAttachments() { }
        public void Dispose() { }
    }
}
