using System.Collections.Concurrent;
using System.Reflection;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 046A: Core's surface content tracker (auto-hide plus the Audio-mode signal) that the
/// WPF, Avalonia and WinForms surfaces share.
/// </summary>
public sealed class MediaSurfaceContentTrackerTests
{
    [Fact]
    public void Load_ArmsTheSurface_AndTheFirstFrameShowsIt()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        Assert.False(tracker.ShowsContent);

        fake.Load(hasVisualContent: true);
        Assert.True(tracker.ShowsContent);
        Assert.False(tracker.IsContentVisible);

        fake.PresentFrame();
        Assert.True(tracker.ShowsContent);
        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void FrameBeforeTheLoadNotice_IsReplayedWhenTheLoadArrives()
    {
        // A static image presents once, possibly before MediaLoaded is raised.
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };

        fake.PresentFrame();
        Assert.False(tracker.IsContentVisible);
        fake.Load(hasVisualContent: true);

        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void AudioOnlyMedia_NeverShows_EvenWhenAStaleFrameArrives()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };

        fake.Load(hasVisualContent: false);
        fake.PresentFrame();

        Assert.False(tracker.ShowsContent);
        Assert.False(tracker.IsContentVisible);
    }

    [Fact]
    public void StopEndCloseAndError_Hide_PauseAndLoopingEndKeep()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        ShowContent(fake);

        fake.Raise(nameof(IPlayerSession.MediaPaused), new MediaPausedEventArgs(TimeSpan.Zero));
        Assert.True(tracker.IsContentVisible);
        fake.LoopMedia = true;
        fake.Raise(nameof(IPlayerSession.MediaEnded), EventArgs.Empty);
        Assert.True(tracker.IsContentVisible);

        fake.LoopMedia = false;
        fake.Raise(nameof(IPlayerSession.MediaEnded), EventArgs.Empty);
        Assert.False(tracker.ShowsContent);

        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaStopped), EventArgs.Empty);
        Assert.False(tracker.ShowsContent);

        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaClosed), new MediaClosedEventArgs(Source));
        Assert.False(tracker.ShowsContent);

        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaError), new MediaErrorEventArgs(MediaErrorCode.Unknown, "failed"));
        Assert.False(tracker.ShowsContent);
    }

    [Fact]
    public void StopRaisedWhileTheSessionPlaysAgain_IsIgnored()
    {
        // A replacement stops the previous item internally; the new item already plays.
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        ShowContent(fake);

        fake.State = MediaPlayerState.Playing;
        fake.Raise(nameof(IPlayerSession.MediaStopped), EventArgs.Empty);

        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void PlayAfterStop_RearmsUntilTheNextFrame()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaStopped), EventArgs.Empty);

        fake.Raise(nameof(IPlayerSession.MediaStarted), EventArgs.Empty);
        Assert.True(tracker.ShowsContent);
        Assert.False(tracker.IsContentVisible);

        fake.PresentFrame();
        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void FramePresentedWhileStopped_ShowsAtOnceWhenPlayResumes()
    {
        // Core presents the stopped position; Play then needs no second present to show it.
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaStopped), EventArgs.Empty);
        fake.PresentFrame();
        Assert.False(tracker.IsContentVisible);

        fake.Raise(nameof(IPlayerSession.MediaStarted), EventArgs.Empty);

        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void AutoHideOff_ShowsWhileTheSessionPresents_AndAudioModeClearsAnyway()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker(autoHide: false) { Session = session };

        Assert.True(tracker.ShowsContent);
        Assert.False(tracker.IsContentVisible);

        fake.SwitchMode(MediaPlayerMode.Audio);
        Assert.False(tracker.HasPresentation);
        Assert.False(tracker.ShowsContent);

        fake.SwitchMode(MediaPlayerMode.Full);
        Assert.True(tracker.ShowsContent);
    }

    [Fact]
    public void AudioMode_HidesVisibleContent_AndReadsTheModeLive()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        var changes = 0;
        tracker.Changed += (_, _) => Interlocked.Increment(ref changes);
        ShowContent(fake);
        var before = Volatile.Read(ref changes);

        fake.SwitchMode(MediaPlayerMode.Audio);

        Assert.True(Volatile.Read(ref changes) > before);
        Assert.False(tracker.HasPresentation);
        Assert.False(tracker.IsContentVisible);
        Assert.False(tracker.ShowsContent);

        // A handler that marshals reads the session's mode as it is now.
        fake.Mode = MediaPlayerMode.Full;
        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void TurningAutoHideOn_ArmsAnOpenSessionAndReplaysAPresentedFrame()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker(autoHide: false) { Session = session };
        fake.Load(hasVisualContent: true);
        fake.PresentFrame();

        tracker.AutoHide = true;

        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public void AnOpenSession_IsArmedWhenFollowed()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        fake.IsOpen = true;
        fake.HasVisualContent = true;

        using var tracker = new MediaSurfaceContentTracker { Session = session };

        Assert.True(tracker.ShowsContent);
        Assert.False(tracker.IsContentVisible);
    }

    [Fact]
    public void FollowingTheSameSessionAgain_KeepsTheShownState()
    {
        // The WPF surface follows its Player across Unloaded/Loaded (029C): a paused video shows
        // no new frame, so its state must survive.
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        ShowContent(fake);
        fake.Raise(nameof(IPlayerSession.MediaPaused), new MediaPausedEventArgs(TimeSpan.Zero));
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        tracker.Session = session;

        Assert.True(tracker.IsContentVisible);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ReplacedSession_IsUnsubscribedAndItsEventsAreIgnored()
    {
        (IPlayerSession first, TrackedSession firstFake) = TrackedSession.Create();
        (IPlayerSession second, TrackedSession secondFake) = TrackedSession.Create();
        var tracker = new MediaSurfaceContentTracker { Session = first };
        Assert.True(firstFake.SubscriberCount > 0);

        tracker.Session = second;
        Assert.Equal(0, firstFake.SubscriberCount);
        firstFake.Load(hasVisualContent: true);
        firstFake.PresentFrame();
        Assert.False(tracker.ShowsContent);

        tracker.Dispose();
        Assert.Equal(0, secondFake.SubscriberCount);
        Assert.Null(tracker.Session);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstFrameLoad_IsSetByTheLoadsFirstFrame_AndANewLoadGetsANewNumber(bool autoHide)
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker(autoHide) { Session = session };
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        fake.Load(hasVisualContent: true);
        Assert.Equal(0, tracker.FirstFrameLoad);
        changes = 0;

        fake.PresentFrame();
        var first = tracker.FirstFrameLoad;
        Assert.NotEqual(0, first);
        Assert.Equal(1, changes);

        // Later frames, pause, loop and stop-and-play keep the number and raise nothing new for it.
        fake.PresentFrame();
        fake.Raise(nameof(IPlayerSession.MediaPaused), new MediaPausedEventArgs(TimeSpan.Zero));
        fake.Raise(nameof(IPlayerSession.MediaLooped), new MediaLoopedEventArgs(TimeSpan.Zero));
        Assert.Equal(first, tracker.FirstFrameLoad);

        fake.Raise(nameof(IPlayerSession.MediaClosed), new MediaClosedEventArgs(Source));
        Assert.Equal(0, tracker.FirstFrameLoad);
        fake.Load(hasVisualContent: true);
        fake.PresentFrame();
        Assert.True(tracker.FirstFrameLoad > first);
    }

    [Fact]
    public void FirstFrameLoad_CountsAFrameLatchedBeforeTheLoadNotice()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker(autoHide: false) { Session = session };
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        fake.PresentFrame();
        Assert.Equal(0, tracker.FirstFrameLoad);
        fake.Load(hasVisualContent: true);

        Assert.NotEqual(0, tracker.FirstFrameLoad);
        Assert.True(changes > 0);
    }

    [Fact]
    public void FirstFrameLoad_StaysZeroForAudioOnlyMediaAndAfterAnError()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };

        fake.Load(hasVisualContent: false);
        fake.PresentFrame();
        Assert.Equal(0, tracker.FirstFrameLoad);

        fake.Load(hasVisualContent: true);
        fake.Raise(nameof(IPlayerSession.MediaError), new MediaErrorEventArgs(MediaErrorCode.OpenFailed, "failed"));
        fake.PresentFrame();
        Assert.Equal(0, tracker.FirstFrameLoad);
    }

    [Fact]
    public void FirstFrameLoad_ArmsASessionAlreadyOpen_AndASessionChangeClearsIt()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        fake.IsOpen = true;
        fake.HasVisualContent = true;
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        Assert.Equal(0, tracker.FirstFrameLoad);

        fake.PresentFrame();
        Assert.NotEqual(0, tracker.FirstFrameLoad);

        (IPlayerSession other, _) = TrackedSession.Create();
        tracker.Session = other;
        Assert.Equal(0, tracker.FirstFrameLoad);
    }

    [Fact]
    public async Task FramesFromTheRenderThread_RaiseChangedOnlyOnTheTransition()
    {
        (IPlayerSession session, TrackedSession fake) = TrackedSession.Create();
        using var tracker = new MediaSurfaceContentTracker { Session = session };
        fake.Load(hasVisualContent: true);
        var threads = new ConcurrentQueue<int>();
        tracker.Changed += (_, _) => threads.Enqueue(Environment.CurrentManagedThreadId);

        await Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                fake.PresentFrame();
        });

        Assert.Single(threads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads.Single());
        Assert.True(tracker.IsContentVisible);
    }

    [Fact]
    public async Task Player_RaisesHasPresentationChangedOnlyWhenAudioModeIsEnteredOrLeft()
    {
        var factory = new PlayerModeSwitchTests.SwitchTestPlatformFactory();
        using var player = new MediaControlPlayer(factory, MediaPlayerConfiguration.Auto, []);
        var changes = new ConcurrentQueue<(bool Previous, bool Current)>();
        player.HasPresentationChanged += (_, e) => changes.Enqueue((e.PreviousValue, e.CurrentValue));
        using var tracker = new MediaSurfaceContentTracker(autoHide: false) { Session = player };
        Assert.True(player.HasPresentation);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Background));
        Assert.True(await player.SetModeAsync(MediaPlayerMode.Audio));
        await player.FlushControlOperationsAsync();
        Assert.False(player.HasPresentation);
        Assert.False(((IPlayerSession)player).HasPresentation);
        Assert.False(tracker.ShowsContent);

        Assert.True(await player.SetModeAsync(MediaPlayerMode.Full));
        await player.FlushControlOperationsAsync();

        Assert.Equal([(true, false), (false, true)], changes);
        Assert.True(tracker.ShowsContent);
    }

    [Fact]
    public void PresentationPathText_DescribesEachPath()
    {
        Assert.Equal("Surface #1 'primary': native window path (why)", MediaSurfacePresentationPathText.DescribeChoice("Surface #1 'primary'", MediaSurfacePresentationPath.NativeWindow, "why"));
        Assert.Equal("s: frame sink path", MediaSurfacePresentationPathText.DescribeChoice("s", MediaSurfacePresentationPath.FrameSink, null));
        Assert.Equal("shared GPU image", MediaSurfacePresentationPathText.Describe(MediaSurfacePresentationPath.SharedGpuImage));
        Assert.Equal("none", MediaSurfacePresentationPathText.Describe(MediaSurfacePresentationPath.None));
        Assert.StartsWith("s: the player did not accept the surface", MediaSurfacePresentationPathText.DescribeAttachRefused("s"), StringComparison.Ordinal);
    }

    private static readonly MediaSourceInfo Source = new(MediaInputSource.FromFile("a.mp4"), MediaContent.Video);

    private static void ShowContent(TrackedSession fake)
    {
        fake.State = MediaPlayerState.Stopped;
        fake.Load(hasVisualContent: true);
        fake.PresentFrame();
    }

    /// <summary>
    /// A session double whose events the test raises. Properties read by the tracker are settable;
    /// everything else returns its default.
    /// </summary>
    public class TrackedSession : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, Delegate?> _handlers = new();

        public bool IsOpen { get; set; }

        public bool HasVisualContent { get; set; }

        public bool LoopMedia { get; set; }

        public MediaPlayerState State { get; set; } = MediaPlayerState.Stopped;

        public MediaPlayerMode Mode { get; set; }

        public int SubscriberCount => _handlers.Values.Sum(static handler => handler?.GetInvocationList().Length ?? 0);

        public static (IPlayerSession Session, TrackedSession Fake) Create()
        {
            IPlayerSession session = Create<IPlayerSession, TrackedSession>();
            return (session, (TrackedSession)(object)session);
        }

        public void Load(bool hasVisualContent)
        {
            IsOpen = true;
            HasVisualContent = hasVisualContent;
            Raise(nameof(IPlayerSession.MediaLoaded), new MediaLoadedEventArgs(Source, TimeSpan.Zero));
        }

        public void PresentFrame() => Raise(nameof(IPlayerSession.PresentationFramePresented), EventArgs.Empty);

        public void SwitchMode(MediaPlayerMode mode)
        {
            MediaPlayerMode previous = Mode;
            Mode = mode;
            Raise(nameof(IPlayerSession.ModeChanged), new MediaValueChangedEventArgs<MediaPlayerMode>(previous, mode));
            if ((previous == MediaPlayerMode.Audio) != (mode == MediaPlayerMode.Audio))
                Raise(nameof(IPlayerSession.HasPresentationChanged), new MediaValueChangedEventArgs<bool>(previous != MediaPlayerMode.Audio, mode != MediaPlayerMode.Audio));
        }

        public void Raise(string eventName, object args)
        {
            if (_handlers.TryGetValue(eventName, out Delegate? handler) && handler is not null)
                _ = handler.DynamicInvoke(this, args);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var name = targetMethod.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                _ = _handlers.AddOrUpdate(name[4..], _ => (Delegate)args![0]!, (_, existing) => Delegate.Combine(existing, (Delegate)args![0]!));
                return null;
            }

            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                _ = _handlers.AddOrUpdate(name[7..], _ => null, (_, existing) => Delegate.Remove(existing, (Delegate)args![0]!));
                return null;
            }

            return name switch
            {
                "get_IsOpen" => IsOpen,
                "get_HasVisualContent" => HasVisualContent,
                "get_LoopMedia" => LoopMedia,
                "get_State" => State,
                "get_Mode" => Mode,
                "get_HasPresentation" => Mode != MediaPlayerMode.Audio,
                _ => targetMethod.ReturnType == typeof(void) || !targetMethod.ReturnType.IsValueType
                    ? null
                    : Activator.CreateInstance(targetMethod.ReturnType),
            };
        }
    }
}
